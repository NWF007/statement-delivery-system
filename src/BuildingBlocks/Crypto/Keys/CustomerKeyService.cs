using System.Globalization;
using Microsoft.Extensions.Logging;
using StatementDelivery.Domain.Identifiers;

namespace StatementDelivery.Crypto.Keys;

/// <summary>The stored state of one customer's key material.</summary>
/// <param name="CustomerId">The customer.</param>
/// <param name="CohortId">Which cohort KEK wraps the CEK. Fixed for the life of the row.</param>
/// <param name="KekId">The cohort KEK identifier.</param>
/// <param name="WrappedCek">The CEK, wrapped. NEVER the plaintext.</param>
/// <param name="Status">ACTIVE, ROTATING or DESTROYED.</param>
/// <param name="DestroyedAt">When the CEK was destroyed, if it was.</param>
public sealed record CustomerKeyRecord(
    CustomerId CustomerId,
    short CohortId,
    string KekId,
    byte[] WrappedCek,
    string Status,
    DateTimeOffset? DestroyedAt);

/// <summary>
/// Persistence for <see cref="CustomerKeyRecord"/>.
/// </summary>
/// <remarks>
/// A port, so that this project stays free of Npgsql and the tamper tests stay free of a database.
/// The adapter lives in <c>StatementDelivery.Persistence</c>.
/// </remarks>
public interface ICustomerKeyStore
{
    /// <summary>Reads a customer's key row, or null when they have none yet.</summary>
    /// <param name="customer">The customer.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The row, or null.</returns>
    Task<CustomerKeyRecord?> FindAsync(CustomerId customer, CancellationToken ct);

    /// <summary>
    /// Inserts a key row, doing nothing if one already exists.
    /// </summary>
    /// <remarks>
    /// INSERT ... ON CONFLICT DO NOTHING, and the boolean matters. Four hundred generation replicas
    /// can reach a customer with no key at the same instant; all of them mint a candidate CEK and
    /// exactly one wins. The losers must DISCARD theirs and re-read, because a customer with two
    /// CEKs is a customer half of whose statements are unreadable after erasure.
    /// </remarks>
    /// <param name="record">The candidate row.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if this call inserted the row; false if one already existed.</returns>
    Task<bool> TryInsertAsync(CustomerKeyRecord record, CancellationToken ct);

    /// <summary>
    /// Destroys the customer's wrapped CEK: nulls the material, marks the row DESTROYED, and
    /// scrubs the dead tuple so the bytes do not survive in MVCC history.
    /// </summary>
    /// <remarks>
    /// The store performs the mechanics ONLY. Whether destruction is lawful - retention expired,
    /// no legal hold, cooling-off elapsed and re-checked - is the caller's decision, made through
    /// the retention decision engine and audited. This method must stay dumb, because a clever
    /// store is a second place for legal logic to disagree with the first.
    /// </remarks>
    /// <param name="customer">The customer.</param>
    /// <param name="reason">Recorded on the row, e.g. the POPIA s24 request reference.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if this call destroyed the key; false if it was already destroyed.</returns>
    Task<bool> DestroyAsync(CustomerId customer, string reason, CancellationToken ct);
}

/// <summary>Raised when a customer's key material has been destroyed.</summary>
/// <remarks>
/// A distinct type because the operational meaning is distinct: this is not corruption and not an
/// outage, it is the system working. The statements are gone by design and no retry will help.
/// </remarks>
public sealed class CryptoErasedException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="CryptoErasedException"/> class.</summary>
    public CryptoErasedException()
        : base("The key material for this customer has been destroyed.")
    {
    }

    /// <summary>Initialises a new instance of the <see cref="CryptoErasedException"/> class.</summary>
    /// <param name="message">The message.</param>
    public CryptoErasedException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance of the <see cref="CryptoErasedException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public CryptoErasedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The middle tier of the key hierarchy: one customer encryption key per customer.
/// </summary>
public interface ICustomerKeyService
{
    /// <summary>Returns the customer's CEK, minting one on first use.</summary>
    /// <param name="customer">The customer.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The plaintext CEK. The caller owns it and must dispose it.</returns>
    Task<DataKey> GetOrCreateCekAsync(CustomerId customer, CancellationToken ct);

    /// <summary>Returns an existing CEK. Does not create one.</summary>
    /// <param name="customer">The customer.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The plaintext CEK. The caller owns it and must dispose it.</returns>
    /// <exception cref="CryptoErasedException">The key has been destroyed.</exception>
    Task<DataKey> UnwrapCekAsync(CustomerId customer, CancellationToken ct);

    /// <summary>Destroys a customer's CEK, crypto-erasing every statement they have.</summary>
    /// <param name="customer">The customer.</param>
    /// <param name="reason">Why, for the audit trail.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task DestroyCekAsync(CustomerId customer, string reason, CancellationToken ct);
}

/// <summary>
/// The three-tier hierarchy in code: cohort KEK wraps CEK, CEK wraps DEK, DEK encrypts bytes.
/// </summary>
/// <remarks>
/// <para>
/// WHY THE MIDDLE TIER EXISTS AT ALL is an arithmetic argument, set out in full in ADR-0020 and
/// summarised here because the code is where somebody will be standing when they wonder.
/// </para>
/// <para>
/// The clean design for crypto-erasure is one KMS key per customer: destroy the key, the customer's
/// data is gone, done. AWS KMS charges roughly one dollar per customer-managed key per month. At 26
/// million customers that is $26 MILLION A MONTH, which does not eliminate the design on grounds of
/// taste - it eliminates it outright.
/// </para>
/// <para>
/// Cohort keys alone are affordable (1,024 x $1 = about $1,024/month) and useless for erasure:
/// destroying one would erase roughly 25,000 customers at once. Erasure has to be per-customer or it
/// is not erasure.
/// </para>
/// <para>
/// So the hierarchy splits the difference. Cohort KEKs live in KMS, where an HSM protects them and
/// the bill is four figures. Per-customer CEKs live WRAPPED IN OUR OWN DATABASE, at zero marginal
/// cost, one row each. Erasure deletes one row.
/// </para>
/// <para>
/// THE TRADE-OFF, STATED PLAINLY: a CEK is protected by this database's access controls rather than
/// by an HSM. That is a weaker guarantee, and it is accepted because the alternative is
/// economically impossible rather than because it is equivalent. What compensates: the wrapped CEK
/// column is readable only by the roles that decrypt (app_generation, app_download, app_retention -
/// never app_delivery), every access is audited, and the wrapped CEK is USELESS WITHOUT THE COHORT
/// KEK IN KMS. An attacker who exfiltrates the entire database still holds nothing but ciphertext.
/// They need both, and the two live behind different credentials in different systems.
/// </para>
/// </remarks>
public sealed partial class CustomerKeyService : ICustomerKeyService
{
    private readonly IKeyProvider _keys;
    private readonly ICustomerKeyStore _store;
    private readonly ILogger<CustomerKeyService> _logger;

    /// <summary>Initialises a new instance of the <see cref="CustomerKeyService"/> class.</summary>
    /// <param name="keys">The key provider holding the cohort KEKs.</param>
    /// <param name="store">Persistence for customer key rows.</param>
    /// <param name="logger">Logger.</param>
    public CustomerKeyService(IKeyProvider keys, ICustomerKeyStore store, ILogger<CustomerKeyService> logger)
    {
        _keys = keys;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DataKey> GetOrCreateCekAsync(CustomerId customer, CancellationToken ct)
    {
        CustomerKeyRecord? existing = await _store.FindAsync(customer, ct).ConfigureAwait(false);

        if (existing is not null)
        {
            return await UnwrapRecordAsync(existing, ct).ConfigureAwait(false);
        }

        short cohort = CohortAssignment.ForCustomer(customer);
        string kekId = CohortAssignment.KekIdFor(cohort);

        using GeneratedDataKey candidate = await _keys.GenerateDataKeyAsync(kekId, ct).ConfigureAwait(false);

        var record = new CustomerKeyRecord(
            customer, cohort, kekId, candidate.Wrapped, "ACTIVE", DestroyedAt: null);

        bool inserted = await _store.TryInsertAsync(record, ct).ConfigureAwait(false);

        if (inserted)
        {
            CekMinted(_logger, customer.Value, cohort);

            // A separate copy, because `candidate` is disposed by the using block at the end of this
            // method and the caller needs a key that outlives it.
            return DataKey.CopyFrom(candidate.Plaintext.Span);
        }

        // LOST THE RACE. Another replica inserted first, so this candidate is discarded unused and
        // the winner's row is what everything from here on uses. Returning the losing key instead
        // would encrypt this statement under a CEK no row records - unreadable forever, and only
        // discovered on the first download.
        CekInsertRaceLost(_logger, customer.Value);

        CustomerKeyRecord winner = await _store.FindAsync(customer, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"Customer key row for {customer} vanished after a conflicting insert."));

        return await UnwrapRecordAsync(winner, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DataKey> UnwrapCekAsync(CustomerId customer, CancellationToken ct)
    {
        CustomerKeyRecord record = await _store.FindAsync(customer, ct).ConfigureAwait(false)
            ?? throw new CryptoErasedException(
                string.Create(CultureInfo.InvariantCulture, $"No key material exists for customer {customer}."));

        return await UnwrapRecordAsync(record, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DestroyCekAsync(CustomerId customer, string reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        // Deliberately thin. The half that decides WHETHER destruction is lawful - retention
        // expired, no legal hold, the cooling-off window elapsed and the conflicts RE-CHECKED at
        // execution time - lives in the erasure executor, which runs the retention decision
        // engine and audits ERASURE_COMPLETED in the same pass. This method is the mechanics:
        // null the material, scrub the MVCC history. Idempotent, because the executor retries.
        _ = await _store.DestroyAsync(customer, reason, ct).ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 4010,
        Level = LogLevel.Information,
        Message = "Minted a customer encryption key for {CustomerId} in cohort {CohortId}.")]
    private static partial void CekMinted(ILogger logger, Guid customerId, short cohortId);

    [LoggerMessage(
        EventId = 4011,
        Level = LogLevel.Debug,
        Message = "Discarded a candidate CEK for {CustomerId}: another replica inserted first. "
                + "Expected under concurrent generation; the winner's key is used.")]
    private static partial void CekInsertRaceLost(ILogger logger, Guid customerId);

    private async Task<DataKey> UnwrapRecordAsync(CustomerKeyRecord record, CancellationToken ct)
    {
        if (string.Equals(record.Status, "DESTROYED", StringComparison.Ordinal) || record.DestroyedAt is not null)
        {
            throw new CryptoErasedException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Key material for customer {record.CustomerId} was destroyed on {record.DestroyedAt:O}."));
        }

        if (record.WrappedCek.Length == 0)
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"Customer {record.CustomerId} has a key row with no wrapped CEK."));
        }

        // Deliberately NOT cached. Caching CEKs would keep customer key material resident far
        // longer than the DEK cache keeps a DEK, for a far smaller saving - the throughput problem
        // that justifies caching lives one tier down, at the DEK. See DataKeyCache.
        return await _keys.UnwrapAsync(record.KekId, record.WrappedCek, ct).ConfigureAwait(false);
    }
}
