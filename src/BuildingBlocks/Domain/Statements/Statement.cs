using StatementDelivery.Domain.Exceptions;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Domain.ValueObjects;

namespace StatementDelivery.Domain.Statements;

/// <summary>The lifecycle of a statement.</summary>
public enum StatementStatus
{
    /// <summary>Scheduled for generation; no bytes exist yet.</summary>
    Pending = 0,

    /// <summary>Rendered, stored and downloadable.</summary>
    Available = 1,

    /// <summary>Moved to cold storage. Still retained, slower to retrieve.</summary>
    Archived = 2,

    /// <summary>Bytes destroyed after retention expiry. TERMINAL.</summary>
    Purged = 3,

    /// <summary>Generation failed. Retryable.</summary>
    Failed = 4,
}

/// <summary>
/// The envelope needed to decrypt one stored object.
/// </summary>
/// <remarks>
/// <para>
/// EVERYTHING HERE IS SAFE TO PERSIST AND SAFE TO PASS AROUND. <see cref="WrappedDek"/> is the data
/// key ENCRYPTED by the customer's CEK, which is itself encrypted by a cohort KEK in the key
/// management service. Holding all of it and nothing else, an attacker has ciphertext and no way to
/// turn it into a key.
/// </para>
/// <para>
/// NOTE WHAT IS ABSENT: no IV and no auth tag. V006 created columns for both, on the assumption of
/// a single one-shot encryption per object. The framed format made them obsolete before they were
/// ever written to - each frame carries its own nonce, derived structurally from a per-object prefix
/// in the object header, and its own tag. There is no single IV to record. The columns stay NULL and
/// V013 does not add a constraint pretending otherwise.
/// </para>
/// </remarks>
/// <param name="WrappedDek">The data key, wrapped by the customer encryption key.</param>
/// <param name="KekId">The cohort KEK alias in force when the object was written.</param>
/// <param name="Algorithm">How the data key was wrapped, so an algorithm change stays decodable.</param>
/// <param name="ContentSha256">
/// The digest of the PLAINTEXT, recorded at write time and verified on the way back out.
/// </param>
/// <param name="Binding">
/// The identity authenticated alongside every frame. SUPPLIED BY THE READER, FROM THE DATABASE.
/// </param>
public sealed record CryptoEnvelope(
    byte[] WrappedDek,
    string KekId,
    string Algorithm,
    byte[]? ContentSha256,
    ContentBinding Binding);

/// <summary>
/// The statement identity that is cryptographically bound into a ciphertext.
/// </summary>
/// <remarks>
/// <para>
/// THIS MUST TRAVEL WITH THE ENVELOPE, AND IT MUST COME FROM THE ROW RATHER THAN FROM THE OBJECT.
/// That is the whole mechanism, and it is easy to leave out by accident.
/// </para>
/// <para>
/// These three values go into the additional authenticated data of every frame at write time. On
/// read they are supplied AGAIN, independently, from the statement row - and the decryption fails if
/// they disagree with what was signed. That is what defeats an attacker who can write to the
/// database: pointing customer A's <c>storage_key</c> at customer B's object makes the fetch succeed
/// and the decryption fail.
/// </para>
/// <para>
/// If the reader took the identity from inside the object instead, the check would compare the
/// object against itself and detect nothing at all - a substituted object carries a substituted
/// identity, and the two would agree perfectly. An independent source is the entire point.
/// </para>
/// </remarks>
/// <param name="StatementId">Which statement these bytes are.</param>
/// <param name="CustomerId">Who owns them.</param>
/// <param name="Version">Which generation. A regenerated statement is a different object.</param>
public readonly record struct ContentBinding(Guid StatementId, Guid CustomerId, int Version);

/// <summary>
/// Where a rendered statement's bytes live, and what is needed to read them.
/// </summary>
/// <remarks>
/// <para>
/// THIS VALUE OBJECT GREW IN PROMPT 4; THE PORT AROUND IT DID NOT. <see cref="Envelope"/> is new,
/// and <c>IStatementContentStore</c> - the seam the encrypting adapter went behind - is unchanged,
/// as is every line of request handling in the download gateway.
/// </para>
/// <para>
/// That distinction is worth being precise about, because "zero changes" would be a false claim and
/// "no logic changes" is a true one. Swapping an adapter is expected to change what the data
/// carries: the gateway now reads four more columns from a row and hands them to the store. It is
/// not expected to change how the caller BEHAVES, and it did not - no branch, no condition and no
/// ordering in the redemption path moved. That is the property the port was defined to buy.
/// </para>
/// <para>
/// Null only for statements written before encryption existed. The encrypting adapter refuses to
/// serve a location without one rather than guessing the bytes are plaintext - an adapter that
/// requires an envelope says so by failing when it is absent, rather than by making the type
/// impossible to construct without one.
/// </para>
/// </remarks>
/// <param name="Key">The object key. Computed from the database, never discovered by listing.</param>
/// <param name="Tier">The storage tier, for example STANDARD or GLACIER.</param>
/// <param name="SizeBytes">The stored size.</param>
/// <param name="Envelope">The crypto envelope, or null for unencrypted content.</param>
public sealed record StorageLocation(string Key, string Tier, long SizeBytes, CryptoEnvelope? Envelope = null)
{
    /// <summary>Gets the object key.</summary>
    public string Key { get; } = string.IsNullOrWhiteSpace(Key)
        ? throw new InvariantViolationException("Storage key must not be empty.")
        : Key;

    /// <summary>Gets the stored size in bytes.</summary>
    public long SizeBytes { get; } = SizeBytes < 0
        ? throw new InvariantViolationException("Storage size must not be negative.")
        : SizeBytes;
}

/// <summary>
/// A customer's account statement for one calendar month. The aggregate root.
/// </summary>
/// <remarks>
/// <para>
/// IMMUTABLE. Every transition returns a NEW instance rather than mutating this one. There are no
/// public setters anywhere, which is what makes the state machine below the only way the status can
/// change. A public setter would let a caller move a statement to Purged without going through the
/// rule that says Purged is terminal.
/// </para>
/// <para>
/// THE LEGAL TRANSITIONS, in full:
/// </para>
/// <code>
///   FROM       -> Available  Archived  Purged  Failed
///   Pending         yes        no        no      yes
///   Available       no         yes       yes     yes
///   Archived        yes        no        yes     no      (Available = restore from cold)
///   Purged          no         no        no      no      (TERMINAL)
///   Failed          yes        no        yes     no      (Available = retry succeeded)
/// </code>
/// <para>
/// Self-transitions are illegal on purpose. Marking an already-Available statement Available again
/// is a double-processing bug - a worker that lost its lease and resumed, or a message delivered
/// twice - and it should be loud rather than idempotent, because the second render may have
/// produced different bytes.
/// </para>
/// </remarks>
public sealed record Statement
{
    private Statement()
    {
    }

    /// <summary>Gets the statement identifier. UUIDv7, application-supplied.</summary>
    public required StatementId Id { get; init; }

    /// <summary>Gets the owning account.</summary>
    public required AccountId AccountId { get; init; }

    /// <summary>
    /// Gets the owning customer.
    /// </summary>
    /// <remarks>
    /// DENORMALISED DELIBERATELY. The owner is reachable by joining statement to account, so this
    /// column is redundant - and it is here anyway, because authorisation runs on EVERY request and
    /// that join would be against a 2.5-billion-row table on the hot path of a SECURITY decision.
    /// Denormalising the owner turns "does this person own this?" into a single-column predicate
    /// that goes straight into the WHERE clause.
    /// <para>
    /// The cost is real and accepted: moving an account between customers requires backfilling
    /// every statement row for that account. That is a rare, batchable operation; authorisation is
    /// not. See docs/adr/0009-denormalised-customer-id-on-statement.md.
    /// </para>
    /// </remarks>
    public required CustomerId CustomerId { get; init; }

    /// <summary>Gets the covered month. <c>Period.Start</c> is the RANGE partition key.</summary>
    public required StatementPeriod Period { get; init; }

    /// <summary>
    /// Gets the generation version, starting at 1.
    /// </summary>
    /// <remarks>
    /// A regenerated statement is a NEW row with an incremented version, never an update in place.
    /// The unique constraint is on (account_id, period_start, version), so both survive - which
    /// matters when a customer disputes what a statement said before it was corrected.
    /// </remarks>
    public required int Version { get; init; }

    /// <summary>Gets the lifecycle status.</summary>
    public required StatementStatus Status { get; init; }

    /// <summary>
    /// Gets the last date this statement must still exist.
    /// </summary>
    /// <remarks>
    /// Derived from the period END, not from the generation date, so a statement regenerated years
    /// late does not thereby earn extra retention.
    /// </remarks>
    public required DateOnly RetainUntil { get; init; }

    /// <summary>Gets when the bytes were produced, or null while pending or failed.</summary>
    public DateTimeOffset? GeneratedAt { get; init; }

    /// <summary>Gets where the bytes live, or null unless the status is Available or Archived.</summary>
    public StorageLocation? Storage { get; init; }

    /// <summary>Gets why generation failed. Diagnostic only; never returned to a customer.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Gets when the bytes were destroyed.</summary>
    public DateTimeOffset? PurgedAt { get; init; }

    /// <summary>
    /// Creates a statement scheduled for generation.
    /// </summary>
    /// <param name="id">The identifier.</param>
    /// <param name="accountId">The owning account.</param>
    /// <param name="customerId">The owning customer.</param>
    /// <param name="period">The covered month.</param>
    /// <param name="retention">The retention policy to apply.</param>
    /// <param name="version">The generation version; 1 unless this is a regeneration.</param>
    /// <returns>A statement in <see cref="StatementStatus.Pending"/>.</returns>
    public static Statement Create(
        StatementId id,
        AccountId accountId,
        CustomerId customerId,
        StatementPeriod period,
        RetentionPolicy retention,
        int version = 1)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(retention);

        if (version < 1)
        {
            throw new InvariantViolationException($"Statement version must be at least 1, got {version}.");
        }

        return new Statement
        {
            Id = id,
            AccountId = accountId,
            CustomerId = customerId,
            Period = period,
            Version = version,
            Status = StatementStatus.Pending,
            RetainUntil = retention.RetainUntil(period),
        };
    }

    /// <summary>
    /// Rehydrates a statement from storage without re-running creation rules.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Create"/> because loading a row is not the same event as minting a
    /// statement: a row written years ago under a different retention policy must load as it was
    /// stored, not be silently recomputed to today's rules.
    /// </remarks>
    /// <param name="id">The identifier.</param>
    /// <param name="accountId">The owning account.</param>
    /// <param name="customerId">The owning customer.</param>
    /// <param name="period">The covered month.</param>
    /// <param name="version">The generation version.</param>
    /// <param name="status">The stored status.</param>
    /// <param name="retainUntil">The stored retention deadline.</param>
    /// <param name="generatedAt">When the bytes were produced.</param>
    /// <param name="storage">Where the bytes live.</param>
    /// <param name="purgedAt">When the bytes were destroyed.</param>
    /// <returns>The rehydrated statement.</returns>
    public static Statement Rehydrate(
        StatementId id,
        AccountId accountId,
        CustomerId customerId,
        StatementPeriod period,
        int version,
        StatementStatus status,
        DateOnly retainUntil,
        DateTimeOffset? generatedAt,
        StorageLocation? storage,
        DateTimeOffset? purgedAt) =>
        new()
        {
            Id = id,
            AccountId = accountId,
            CustomerId = customerId,
            Period = period,
            Version = version,
            Status = status,
            RetainUntil = retainUntil,
            GeneratedAt = generatedAt,
            Storage = storage,
            PurgedAt = purgedAt,
        };

    /// <summary>Marks the statement rendered, stored and downloadable.</summary>
    /// <param name="location">Where the bytes were written.</param>
    /// <param name="at">When generation completed.</param>
    /// <returns>A new instance in <see cref="StatementStatus.Available"/>.</returns>
    /// <exception cref="InvalidStateTransitionException">The current status forbids this.</exception>
    public Statement MarkAvailable(StorageLocation location, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(location);

        // Pending -> generated. Failed -> a retry succeeded. Archived -> restored from cold.
        Require(StatementStatus.Available, StatementStatus.Pending, StatementStatus.Failed, StatementStatus.Archived);

        return this with
        {
            Status = StatementStatus.Available,
            Storage = location,
            GeneratedAt = GeneratedAt ?? at,
            FailureReason = null,
        };
    }

    /// <summary>Moves the statement to cold storage. Still retained.</summary>
    /// <param name="at">When the transition occurred.</param>
    /// <returns>A new instance in <see cref="StatementStatus.Archived"/>.</returns>
    /// <exception cref="InvalidStateTransitionException">The current status forbids this.</exception>
    public Statement MarkArchived(DateTimeOffset at)
    {
        // Only from Available. Archiving something that was never rendered would produce a row
        // claiming cold storage holds bytes that do not exist.
        Require(StatementStatus.Archived, StatementStatus.Available);

        _ = at;
        return this with { Status = StatementStatus.Archived };
    }

    /// <summary>
    /// Records that the bytes have been destroyed after retention expiry. TERMINAL.
    /// </summary>
    /// <remarks>
    /// The row survives the bytes on purpose. A purged statement must remain provably
    /// accounted for - "this existed and was destroyed on this date" is the answer an auditor
    /// needs, and deleting the row instead makes it indistinguishable from one that never existed.
    /// </remarks>
    /// <param name="at">When the bytes were destroyed.</param>
    /// <returns>A new instance in <see cref="StatementStatus.Purged"/>.</returns>
    /// <exception cref="InvalidStateTransitionException">The current status forbids this.</exception>
    public Statement MarkPurged(DateTimeOffset at)
    {
        Require(StatementStatus.Purged, StatementStatus.Available, StatementStatus.Archived, StatementStatus.Failed);

        return this with
        {
            Status = StatementStatus.Purged,
            Storage = null,
            PurgedAt = at,
        };
    }

    /// <summary>Records that generation failed. Retryable.</summary>
    /// <param name="reason">Diagnostic detail. Never returned to a customer.</param>
    /// <param name="at">When the failure occurred.</param>
    /// <returns>A new instance in <see cref="StatementStatus.Failed"/>.</returns>
    /// <exception cref="InvalidStateTransitionException">The current status forbids this.</exception>
    public Statement MarkFailed(string reason, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        Require(StatementStatus.Failed, StatementStatus.Pending, StatementStatus.Available);

        _ = at;
        return this with { Status = StatementStatus.Failed, FailureReason = reason };
    }

    /// <summary>
    /// Guards a transition, treating a self-transition as illegal.
    /// </summary>
    private void Require(StatementStatus target, params ReadOnlySpan<StatementStatus> legalSources)
    {
        foreach (StatementStatus source in legalSources)
        {
            if (Status == source)
            {
                return;
            }
        }

        throw new InvalidStateTransitionException(nameof(Statement), Status.ToString(), target.ToString());
    }
}
