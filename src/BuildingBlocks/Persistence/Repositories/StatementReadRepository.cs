using Dapper;
using Npgsql;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Domain.Statements;
using StatementDelivery.Domain.ValueObjects;
using StatementDelivery.Persistence.Connections;

namespace StatementDelivery.Persistence.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IStatementReadRepository"/>.
/// </summary>
/// <remarks>
/// Strongly-typed identifiers are unwrapped to raw <see cref="Guid"/> at the parameter boundary
/// rather than through global Dapper type handlers. Handlers are process-wide mutable state that
/// changes how every query in the process behaves; unwrapping at the two places it is needed is
/// more code and far less surprising.
/// </remarks>
public sealed class StatementReadRepository : IStatementReadRepository
{
    /// <summary>The server-side page cap, applied regardless of what the caller asks for.</summary>
    /// <remarks>
    /// A client asking for 10,000 rows is either mistaken or probing. Capping here rather than in
    /// the endpoint means every caller is capped, including one added later that forgets to
    /// validate.
    /// </remarks>
    public const int MaxPageSize = 100;

    // Explicit column list, never SELECT *. Adding a column to `statement` must not silently change
    // the shape of this result.
    private const string Columns = """
        id, account_id AS AccountId, customer_id AS CustomerId, period_start AS PeriodStart,
        period_end AS PeriodEnd, version, status, storage_key AS StorageKey,
        storage_tier AS StorageTier, size_bytes AS SizeBytes, retain_until AS RetainUntil,
        generated_at AS GeneratedAt, purged_at AS PurgedAt
        """;

    // THE CRYPTO COLUMNS ARE ON THE SINGLE-ROW LOOKUP ONLY, NEVER ON THE LIST.
    //
    // The list serves the customer-facing catalogue: dozens of rows, none of which is about to be
    // decrypted. Selecting key material there would put a wrapped DEK for every statement a customer
    // owns into a response path that has no use for one, and the cheapest way to keep an envelope out
    // of somewhere it does not belong is not to fetch it.
    //
    // The single-row lookup is the download path. It fetches exactly the one envelope it is about to
    // use, for exactly the one object it is about to open.
    private const string FindColumns = $"""
        {Columns}, content_sha256 AS ContentSha256, wrapped_dek AS WrappedDek,
        dek_algorithm AS DekAlgorithm, kek_id AS KekId
        """;

    private const string FindSql = $"""
        SELECT {FindColumns}
          FROM statement
         WHERE id = @id
           AND period_start = @periodStart
           AND customer_id = @owner;
        """;

    /// <summary>
    /// First page. No cursor predicate at all, rather than a null-tolerant one.
    /// </summary>
    /// <!--
    /// WHY THREE STATUSES (Prompt 6, E4): an ARCHIVED statement must be listable or the restore
    /// flow is unreachable from the UI, and a PURGED one must appear - with its status and no
    /// download offered - because "this existed and was destroyed" is a fact the customer is
    /// entitled to see. PENDING and FAILED stay hidden: unfinished work is not a customer fact.
    /// Served by idx_statement_customer_period_visible (V018).
    /// -->
    /// <remarks>
    /// Two statements instead of one with <c>(@cursor IS NULL OR ...)</c>. That form forces the
    /// planner to produce a plan that works for both cases, which in practice means the worse plan
    /// for both. Two explicit statements each get an optimal plan and each get their own prepared
    /// statement.
    /// </remarks>
    private const string ListFirstPageSql = $"""
        SELECT {Columns}
          FROM statement
         WHERE customer_id = @customerId
           AND status IN ('AVAILABLE', 'ARCHIVED', 'PURGED')
           AND period_start >= @from
           AND period_start <  @to
         ORDER BY period_start DESC, id DESC
         LIMIT @limit;
        """;

    /// <summary>
    /// Subsequent pages, using a ROW-VALUE comparison.
    /// </summary>
    /// <remarks>
    /// <c>(period_start, id) &lt; (@cursorPeriod, @cursorId)</c> is one predicate PostgreSQL can
    /// satisfy from <c>idx_statement_customer_period</c> in a single range scan. Written out as
    /// <c>period_start &lt; @p OR (period_start = @p AND id &lt; @i)</c> - which looks equivalent -
    /// the planner usually cannot use the index and falls back to a scan plus a sort.
    /// </remarks>
    private const string ListNextPageSql = $"""
        SELECT {Columns}
          FROM statement
         WHERE customer_id = @customerId
           AND status IN ('AVAILABLE', 'ARCHIVED', 'PURGED')
           AND period_start >= @from
           AND period_start <  @to
           AND (period_start, id) < (@cursorPeriod, @cursorId)
         ORDER BY period_start DESC, id DESC
         LIMIT @limit;
        """;

    private readonly IDbConnectionFactory _connections;

    /// <summary>Initialises a new instance of the <see cref="StatementReadRepository"/> class.</summary>
    /// <param name="connections">Connection factory.</param>
    public StatementReadRepository(IDbConnectionFactory connections) => _connections = connections;

    /// <inheritdoc />
    public async Task<Statement?> FindAsync(
        StatementId id,
        DateOnly periodStart,
        CustomerId owner,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadEventual, cancellationToken).ConfigureAwait(false);

        StatementRow? row = await connection.QuerySingleOrDefaultAsync<StatementRow>(new CommandDefinition(
            FindSql,
            new { id = id.Value, periodStart, owner = owner.Value },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadEventual),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row?.ToDomain();
    }

    /// <inheritdoc />
    public async Task<Statement?> FindAsync(
        StatementId id,
        DateOnly periodStart,
        CustomerId owner,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        // NO CONNECTION FACTORY CALL HERE, AND THAT IS THE ENTIRE POINT OF THIS OVERLOAD.
        //
        // Reaching for _connections would open a second connection while the caller holds a write
        // transaction on the first - which is the pool-deadlock shape this overload exists to
        // remove. The command runs on the transaction's own connection, so it sees the caller's
        // uncommitted work, cannot be routed to a replica, and costs no extra pool slot.
        NpgsqlConnection connection = transaction.Connection
            ?? throw new InvalidOperationException("The statement lookup transaction has no connection.");

        StatementRow? row = await connection.QuerySingleOrDefaultAsync<StatementRow>(new CommandDefinition(
            FindSql,
            new { id = id.Value, periodStart, owner = owner.Value },
            transaction: transaction,

            // The WRITE budget, not a read one: this command is part of a write transaction, and a
            // read that outlives its transaction's budget holds the transaction open past it.
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row?.ToDomain();
    }

    /// <inheritdoc />
    public async Task<CursorPage<Statement>> ListForCustomerAsync(
        CustomerId customer,
        DateOnly fromInclusive,
        DateOnly toExclusive,
        Cursor? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        int pageSize = Math.Clamp(limit, 1, MaxPageSize);

        // FETCH ONE MORE THAN ASKED FOR. The extra row answers "is there another page?" for free.
        // A second COUNT query would cost a full scan of the matching range to produce a number
        // that is stale before it reaches the client.
        int fetch = pageSize + 1;

        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadEventual, cancellationToken).ConfigureAwait(false);

        object parameters = cursor is null
            ? new { customerId = customer.Value, from = fromInclusive, to = toExclusive, limit = fetch }
            : new
            {
                customerId = customer.Value,
                from = fromInclusive,
                to = toExclusive,
                limit = fetch,
                cursorPeriod = cursor.Value.PeriodStart,
                cursorId = cursor.Value.Id,
            };

        IEnumerable<StatementRow> rows = await connection.QueryAsync<StatementRow>(new CommandDefinition(
            cursor is null ? ListFirstPageSql : ListNextPageSql,
            parameters,
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadEventual),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        List<StatementRow> materialised = [.. rows];

        bool hasMore = materialised.Count > pageSize;
        if (hasMore)
        {
            materialised.RemoveAt(materialised.Count - 1);
        }

        List<Statement> items = [.. materialised.Select(row => row.ToDomain())];

        Cursor? next = hasMore && items.Count > 0
            ? new Cursor(items[^1].Period.Start, items[^1].Id.Value)
            : null;

        return new CursorPage<Statement>(items, next, hasMore);
    }

    /// <summary>
    /// The raw row shape. Mapped by hand into the aggregate.
    /// </summary>
    /// <remarks>
    /// Hand-mapped rather than materialised straight into <see cref="Statement"/>, because the
    /// aggregate has no public constructor and no settable properties - which is exactly what stops
    /// a caller from bypassing the state machine. Making it Dapper-friendly would mean giving that
    /// up for the convenience of one method.
    /// </remarks>
    private sealed record StatementRow
    {
        public Guid Id { get; init; }

        public Guid AccountId { get; init; }

        public Guid CustomerId { get; init; }

        public DateOnly PeriodStart { get; init; }

        public DateOnly PeriodEnd { get; init; }

        public int Version { get; init; }

        public string Status { get; init; } = string.Empty;

        public string? StorageKey { get; init; }

        public string StorageTier { get; init; } = "STANDARD";

        public long? SizeBytes { get; init; }

        public DateOnly RetainUntil { get; init; }

        public DateTime? GeneratedAt { get; init; }

        public DateTime? PurgedAt { get; init; }

        // Null on every row that came from the list query, which does not select them. See the
        // comment on FindColumns.
        public byte[]? ContentSha256 { get; init; }

        public byte[]? WrappedDek { get; init; }

        public string? DekAlgorithm { get; init; }

        public string? KekId { get; init; }

        public Statement ToDomain() => Statement.Rehydrate(
            new StatementId(Id),
            new AccountId(AccountId),
            new StatementDelivery.Domain.Identifiers.CustomerId(CustomerId),
            StatementPeriod.Create(PeriodStart, PeriodEnd),
            Version,
            Enum.Parse<StatementStatus>(Status, ignoreCase: true),
            RetainUntil,
            GeneratedAt is null ? null : new DateTimeOffset(GeneratedAt.Value, TimeSpan.Zero),
            StorageKey is null ? null : new StorageLocation(StorageKey, StorageTier, SizeBytes ?? 0, ToEnvelope()),
            PurgedAt is null ? null : new DateTimeOffset(PurgedAt.Value, TimeSpan.Zero));

        // Null unless BOTH halves are present. A wrapped DEK with no KEK identifier cannot be
        // unwrapped, and a KEK identifier with no wrapped DEK names a key for nothing - either alone
        // is a half-written row, and returning a partial envelope would push the discovery of that
        // into the middle of a download instead of keeping it here.
        private CryptoEnvelope? ToEnvelope() =>
            WrappedDek is null || KekId is null
                ? null
                : new CryptoEnvelope(
                    WrappedDek,
                    KekId,
                    DekAlgorithm ?? "AES-256-GCM",
                    ContentSha256,

                    // FROM THE ROW, NOT FROM THE OBJECT. See ContentBinding: taking these from
                    // inside the ciphertext would compare the object against itself and catch
                    // nothing.
                    new ContentBinding(Id, CustomerId, Version));
    }
}
