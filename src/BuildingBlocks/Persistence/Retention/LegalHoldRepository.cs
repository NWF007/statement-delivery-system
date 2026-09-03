using Dapper;
using Npgsql;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Persistence.Connections;

namespace StatementDelivery.Persistence.Retention;

/// <summary>A legal hold row.</summary>
/// <param name="Id">The hold.</param>
/// <param name="StatementId">Null for a customer-scoped hold; set for a statement-scoped one. Scope is THIS column alone (V021).</param>
/// <param name="CustomerId">Always set (V021, ADR-0040): the single indexed predicate the erasure gate relies on.</param>
/// <param name="CaseReference">The matter. Mandatory at placement — a hold nobody can trace is a hold nobody dares release.</param>
/// <param name="Reason">Why the hold was placed.</param>
/// <param name="PlacedBy">Who placed it.</param>
/// <param name="PlacedAt">When.</param>
/// <param name="ReleasedAt">Null while active. Releasing sets this rather than deleting — the row is evidence.</param>
public sealed record LegalHoldRow(
    Guid Id,
    Guid? StatementId,
    Guid? CustomerId,
    string CaseReference,
    string? Reason,
    string PlacedBy,
    DateTimeOffset PlacedAt,
    DateTimeOffset? ReleasedAt);

/// <summary>The <c>legal_hold</c> adapter.</summary>
/// <remarks>
/// Placement and release run in the CALLER's transaction, because ADR-0025 applies: the audit
/// event describing the hold binds to the same transaction as the hold itself. The object-store
/// half of the dual-layer hold (ADR-0037) is deliberately NOT here — storage cannot join a
/// database transaction, and the endpoint sequences storage-first explicitly.
/// </remarks>
public sealed class LegalHoldRepository
{
    private const string InsertSql = """
        INSERT INTO legal_hold (id, statement_id, customer_id, case_reference, reason, placed_by)
        VALUES (@id, @statementId, @customerId, @caseReference, @reason, @placedBy);
        """;

    // released_at IS NULL in the WHERE makes release idempotent and race-safe: two concurrent
    // releases update one row once, and the loser learns it lost from the rowcount.
    private const string ReleaseSql = """
        UPDATE legal_hold
           SET released_at = now(), released_by = @releasedBy, release_reason = @releaseReason
         WHERE id = @id AND released_at IS NULL
        RETURNING statement_id AS StatementId, customer_id AS CustomerId, case_reference AS CaseReference;
        """;

    private const string FindSql = """
        SELECT id            AS Id,
               statement_id  AS StatementId,
               customer_id   AS CustomerId,
               case_reference AS CaseReference,
               reason        AS Reason,
               placed_by     AS PlacedBy,
               placed_at     AS PlacedAt,
               released_at   AS ReleasedAt
          FROM legal_hold
         WHERE id = @id;
        """;

    // "Is anything holding this statement?" — either a hold on the statement itself or one on
    // its owning customer. Both partial indexes from V008 serve this. Checked AT PURGE TIME and
    // at erasure time, never at generation time: that is what makes a customer-scoped hold cover
    // statements generated after it was placed.
    // The customer branch requires statement_id IS NULL: since V021 every row carries
    // customer_id, and without that predicate a statement-scoped hold on one statement would
    // block purging its SIBLINGS - over-protection that silently repeals retention for the
    // whole customer. Scope is statement_id alone (ADR-0040).
    private const string ActiveForStatementSql = """
        SELECT case_reference
          FROM legal_hold
         WHERE released_at IS NULL
           AND (statement_id = @statementId
                OR (customer_id = @customerId AND statement_id IS NULL))
         ORDER BY placed_at
         LIMIT 1;
        """;

    // EVERY hold affecting the customer, both scopes - the erasure gate. This is the query the
    // most serious retention defect to date lived in: before V021 a statement-scoped hold carried
    // no customer_id and was invisible here, so a hold on one statement did not block the erasure
    // that would destroy that statement's readability. The data model now guarantees this single
    // indexed predicate is complete (ADR-0040).
    private const string ActiveForCustomerSql = """
        SELECT case_reference
          FROM legal_hold
         WHERE released_at IS NULL AND customer_id = @customerId
         ORDER BY placed_at
         LIMIT 1;
        """;

    // Keyset pagination on (placed_at, id), like every list in this codebase — OFFSET walks the
    // whole prefix and gets slower per page.
    private const string ListSql = """
        SELECT id            AS Id,
               statement_id  AS StatementId,
               customer_id   AS CustomerId,
               case_reference AS CaseReference,
               reason        AS Reason,
               placed_by     AS PlacedBy,
               placed_at     AS PlacedAt,
               released_at   AS ReleasedAt
          FROM legal_hold
         WHERE (@activeOnly = FALSE OR released_at IS NULL)
           AND (@afterPlacedAt::timestamptz IS NULL
                OR (placed_at, id) > (@afterPlacedAt, @afterId))
         ORDER BY placed_at, id
         LIMIT @limit;
        """;

    private readonly IDbConnectionFactory _connections;

    /// <summary>Initialises a new instance of the <see cref="LegalHoldRepository"/> class.</summary>
    /// <param name="connections">Connection factory.</param>
    public LegalHoldRepository(IDbConnectionFactory connections) => _connections = connections;

    /// <summary>Inserts a hold, in the caller's transaction.</summary>
    /// <param name="hold">The hold to place. <see cref="LegalHoldRow.ReleasedAt"/> must be null.</param>
    /// <param name="transaction">The transaction the audit append also joins.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task PlaceAsync(LegalHoldRow hold, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hold);
        ArgumentNullException.ThrowIfNull(transaction);

        _ = await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            InsertSql,
            new
            {
                id = hold.Id,
                statementId = hold.StatementId,
                customerId = hold.CustomerId,
                caseReference = hold.CaseReference,
                reason = hold.Reason,
                placedBy = hold.PlacedBy,
            },
            transaction,
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Releases a hold, in the caller's transaction.</summary>
    /// <param name="holdId">The hold.</param>
    /// <param name="releasedBy">Who released it.</param>
    /// <param name="releaseReason">Why.</param>
    /// <param name="transaction">The transaction the audit append also joins.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The released hold's scope, or null when it did not exist or was already released.</returns>
    public async Task<(Guid? StatementId, Guid? CustomerId, string CaseReference)?> ReleaseAsync(
        Guid holdId, string releasedBy, string? releaseReason,
        NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        (Guid?, Guid?, string)? released = await transaction.Connection!
            .QuerySingleOrDefaultAsync<(Guid?, Guid?, string)?>(new CommandDefinition(
                ReleaseSql,
                new { id = holdId, releasedBy, releaseReason },
                transaction,
                commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return released;
    }

    /// <summary>Reads one hold.</summary>
    /// <param name="holdId">The hold.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The row, or null.</returns>
    public async Task<LegalHoldRow?> FindAsync(Guid holdId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        HoldRow? row = await connection.QuerySingleOrDefaultAsync<HoldRow>(new CommandDefinition(
            FindSql,
            new { id = holdId },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row?.ToRecord();
    }

    /// <summary>Finds the case reference of an active hold covering a statement, or null.</summary>
    /// <param name="statementId">The statement.</param>
    /// <param name="customerId">Its owning customer, so customer-scoped holds are honoured.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The case reference, or null when nothing holds it.</returns>
    public async Task<string?> ActiveCaseReferenceForStatementAsync(
        StatementId statementId, CustomerId customerId, CancellationToken cancellationToken)
    {
        // ReadStrong: this read GATES destruction. A lagging replica that has not yet seen a
        // hold placed seconds ago must not clear the way for a purge (ADR-0024's rule).
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            ActiveForStatementSql,
            new { statementId = statementId.Value, customerId = customerId.Value },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Finds the case reference of an active customer-scoped hold, or null.</summary>
    /// <param name="customerId">The customer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The case reference, or null.</returns>
    public async Task<string?> ActiveCaseReferenceForCustomerAsync(
        CustomerId customerId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            ActiveForCustomerSql,
            new { customerId = customerId.Value },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Lists holds, keyset-paginated.</summary>
    /// <param name="activeOnly">True to list only unreleased holds.</param>
    /// <param name="afterPlacedAt">Cursor: the last row's placed-at, or null for the first page.</param>
    /// <param name="afterId">Cursor: the last row's id.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One page.</returns>
    public async Task<IReadOnlyList<LegalHoldRow>> ListAsync(
        bool activeOnly, DateTimeOffset? afterPlacedAt, Guid afterId, int limit,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadEventual, cancellationToken).ConfigureAwait(false);

        IEnumerable<HoldRow> rows = await connection.QueryAsync<HoldRow>(new CommandDefinition(
            ListSql,
            new { activeOnly, afterPlacedAt, afterId, limit },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadEventual),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(static r => r.ToRecord())];
    }
    /// <summary>
    /// Dapper-facing shape: the public record's positional constructor takes DateTimeOffset and
    /// Dapper materialising timestamptz hands the constructor-matcher a DateTime, so no
    /// signature matches (see ErasureRepository.RequestRow). Init properties in DateTime,
    /// converted at the edge.
    /// </summary>
    private sealed record HoldRow
    {
        public Guid Id { get; init; }

        public Guid? StatementId { get; init; }

        public Guid? CustomerId { get; init; }

        public string CaseReference { get; init; } = string.Empty;

        public string? Reason { get; init; }

        public string PlacedBy { get; init; } = string.Empty;

        public DateTime PlacedAt { get; init; }

        public DateTime? ReleasedAt { get; init; }

        public LegalHoldRow ToRecord() => new(
            Id, StatementId, CustomerId, CaseReference, Reason, PlacedBy,
            new DateTimeOffset(PlacedAt, TimeSpan.Zero),
            ReleasedAt is { } releasedAt ? new DateTimeOffset(releasedAt, TimeSpan.Zero) : null);
    }
}
