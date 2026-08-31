using Dapper;
using Npgsql;
using StatementDelivery.Crypto.Keys;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Persistence.Connections;

namespace StatementDelivery.Persistence.Keys;

/// <summary>
/// The <c>customer_key</c> adapter behind <see cref="ICustomerKeyStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// The port lives in the Crypto project so that the cipher and its tests never see Npgsql; this is
/// the adapter, and it is the only place a wrapped CEK is read from or written to a row.
/// </para>
/// <para>
/// NOTE WHAT IS NOT HERE: no DELETE, and no UPDATE. Destroying key material is Prompt 6's work and
/// belongs with the code that decides whether destruction is lawful yet. The database agrees -
/// V009 revokes DELETE from every role, and V013 grants app_generation INSERT only.
/// </para>
/// </remarks>
public sealed class CustomerKeyRepository : ICustomerKeyStore
{
    private const string FindSql =
        """
        SELECT customer_id   AS CustomerId,
               cohort_id     AS CohortId,
               kek_id        AS KekId,
               wrapped_cek   AS WrappedCek,
               status        AS Status,
               destroyed_at  AS DestroyedAt
          FROM customer_key
         WHERE customer_id = @customer;
        """;

    // ON CONFLICT DO NOTHING, and the RETURNING is what makes the outcome observable: a row that
    // conflicted returns nothing, so `inserted` is false and the caller re-reads the winner.
    //
    // Four hundred generation replicas can reach a customer with no key in the same millisecond.
    // Without this, the last writer would win and a customer would end up with statements encrypted
    // under a CEK no row records - unreadable forever, discovered on the first download attempt.
    private const string InsertSql =
        """
        INSERT INTO customer_key (customer_id, cohort_id, kek_id, wrapped_cek, cek_algorithm, status)
        VALUES (@customer, @cohort, @kekId, @wrappedCek, @algorithm, 'ACTIVE')
        ON CONFLICT (customer_id) DO NOTHING
        RETURNING customer_id;
        """;

    // status <> 'DESTROYED' makes retries no-ops, and the WHERE beats a blind UPDATE for one
    // more reason: the RETURNING tells the caller whether THIS call did the destruction, which is
    // what decides whether ERASURE_COMPLETED gets audited once or twice.
    //
    // destruction_due_at is cleared in the same statement: V018's ck_customer_key_scheduled_has_due
    // ties a due date to SCHEDULED_DESTRUCTION and nothing else - a destroyed row with a live
    // fuse would fail the constraint, and rightly.
    private const string DestroySql =
        """
        UPDATE customer_key
           SET wrapped_cek        = NULL,
               status             = 'DESTROYED',
               destroyed_at       = now(),
               destruction_reason = @reason,
               destruction_due_at = NULL
         WHERE customer_id = @customer
           AND status <> 'DESTROYED'
        RETURNING customer_id;
        """;

    private readonly IDbConnectionFactory _connections;

    /// <summary>Initialises a new instance of the <see cref="CustomerKeyRepository"/> class.</summary>
    /// <param name="connections">Connection factory.</param>
    public CustomerKeyRepository(IDbConnectionFactory connections) => _connections = connections;

    /// <inheritdoc />
    public async Task<CustomerKeyRecord?> FindAsync(CustomerId customer, CancellationToken ct)
    {
        // ReadStrong. A replica lagging behind an insert that just happened would report "no key"
        // for a customer who has one, and the caller would mint a second - the exact outcome the
        // conflict clause above exists to prevent.
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, ct).ConfigureAwait(false);

        Row? row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            FindSql,
            new { customer = customer.Value },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: ct)).ConfigureAwait(false);

        return row?.ToRecord();
    }

    /// <inheritdoc />
    public async Task<bool> TryInsertAsync(CustomerKeyRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.Write, ct).ConfigureAwait(false);

        Guid? inserted = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            InsertSql,
            new
            {
                customer = record.CustomerId.Value,
                cohort = record.CohortId,
                kekId = record.KekId,
                wrappedCek = record.WrappedCek,
                algorithm = KeyWrap.AlgorithmName,
            },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
            cancellationToken: ct)).ConfigureAwait(false);

        return inserted is not null;
    }

    /// <inheritdoc />
    public async Task<bool> DestroyAsync(CustomerId customer, string reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.Write, ct).ConfigureAwait(false);

        Guid? destroyed = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            DestroySql,
            new { customer = customer.Value, reason },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
            cancellationToken: ct)).ConfigureAwait(false);

        if (destroyed is null)
        {
            return false;
        }

        // VACUUM, because the UPDATE alone has not removed the wrapped CEK from disk: MVCC keeps
        // the old row version - key material included - on the page until vacuumed. Plain VACUUM
        // (app_retention holds MAINTAIN, PostgreSQL 17) removes the dead tuple; it cannot run
        // inside a transaction, hence a separate command on the same connection. Honest limit:
        // freed page space is reused, not zeroed, and filesystem journals are beyond SQL's reach -
        // the defence in depth for those layers is full-disk encryption, per the threat model.
        _ = await connection.ExecuteAsync(new CommandDefinition(
            "VACUUM customer_key;",
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
            cancellationToken: ct)).ConfigureAwait(false);

        return true;
    }

    private sealed record Row
    {
        public Guid CustomerId { get; init; }

        public short? CohortId { get; init; }

        public string KekId { get; init; } = string.Empty;

        public byte[]? WrappedCek { get; init; }

        public string Status { get; init; } = string.Empty;

        public DateTime? DestroyedAt { get; init; }

        public CustomerKeyRecord ToRecord() => new(
            new CustomerId(CustomerId),
            CohortId ?? 0,
            KekId,

            // Null rather than empty is possible on a DESTROYED row, which is the whole point of a
            // destroyed row. The service turns that into CryptoErasedException rather than into a
            // decryption failure, because "erased by design" and "corrupt" must not be confused.
            WrappedCek ?? [],
            Status,
            DestroyedAt is null ? null : new DateTimeOffset(DestroyedAt.Value, TimeSpan.Zero));
    }
}
