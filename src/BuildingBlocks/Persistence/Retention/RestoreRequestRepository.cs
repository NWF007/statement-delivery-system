using Dapper;
using Npgsql;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Persistence.Connections;

namespace StatementDelivery.Persistence.Retention;

/// <summary>A restore request row.</summary>
/// <param name="Id">The request.</param>
/// <param name="StatementId">The archived statement.</param>
/// <param name="PeriodStart">The statement's partition key.</param>
/// <param name="CustomerId">The owning customer.</param>
/// <param name="RequestedBy">Who asked.</param>
/// <param name="RequestedAt">When.</param>
/// <param name="DueAt">The estimate the caller was given.</param>
/// <param name="Status">PENDING, AVAILABLE or FAILED.</param>
/// <param name="AvailableAt">When it completed, if it has.</param>
/// <param name="ExpiresAt">When the restored copy goes cold again. Restored copies are temporary in real Glacier.</param>
public sealed record RestoreRequestRow(
    Guid Id,
    Guid StatementId,
    DateOnly PeriodStart,
    Guid CustomerId,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    DateTimeOffset DueAt,
    string Status,
    DateTimeOffset? AvailableAt,
    DateTimeOffset? ExpiresAt);

/// <summary>The <c>restore_request</c> adapter: 202 now, completion later, expiry after that.</summary>
public sealed class RestoreRequestRepository
{
    private const string InsertSql = """
        INSERT INTO restore_request
            (id, statement_id, period_start, customer_id, requested_by, due_at)
        VALUES (@id, @statementId, @periodStart, @customerId, @requestedBy, @dueAt);
        """;

    private const string FindSql = """
        SELECT id           AS Id,
               statement_id AS StatementId,
               period_start AS PeriodStart,
               customer_id  AS CustomerId,
               requested_by AS RequestedBy,
               requested_at AS RequestedAt,
               due_at       AS DueAt,
               status       AS Status,
               available_at AS AvailableAt,
               expires_at   AS ExpiresAt
          FROM restore_request
         WHERE id = @id AND statement_id = @statementId;
        """;

    // An unexpired AVAILABLE restore is what lets an ARCHIVED statement download. Newest wins
    // when several exist.
    private const string LiveForStatementSql = """
        SELECT id           AS Id,
               statement_id AS StatementId,
               period_start AS PeriodStart,
               customer_id  AS CustomerId,
               requested_by AS RequestedBy,
               requested_at AS RequestedAt,
               due_at       AS DueAt,
               status       AS Status,
               available_at AS AvailableAt,
               expires_at   AS ExpiresAt
          FROM restore_request
         WHERE statement_id = @statementId
           AND status = 'AVAILABLE'
           AND (expires_at IS NULL OR expires_at > now())
         ORDER BY available_at DESC
         LIMIT 1;
        """;

    private const string PendingForStatementSql = """
        SELECT id           AS Id,
               statement_id AS StatementId,
               period_start AS PeriodStart,
               customer_id  AS CustomerId,
               requested_by AS RequestedBy,
               requested_at AS RequestedAt,
               due_at       AS DueAt,
               status       AS Status,
               available_at AS AvailableAt,
               expires_at   AS ExpiresAt
          FROM restore_request
         WHERE statement_id = @statementId AND status = 'PENDING'
         ORDER BY requested_at DESC
         LIMIT 1;
        """;

    private const string DueSql = """
        SELECT id           AS Id,
               statement_id AS StatementId,
               period_start AS PeriodStart,
               customer_id  AS CustomerId,
               requested_by AS RequestedBy,
               requested_at AS RequestedAt,
               due_at       AS DueAt,
               status       AS Status,
               available_at AS AvailableAt,
               expires_at   AS ExpiresAt
          FROM restore_request
         WHERE status = 'PENDING' AND due_at <= now()
         ORDER BY due_at
         LIMIT @limit;
        """;

    // status = 'PENDING' in the WHERE makes completion idempotent under the worker's retries.
    private const string MarkAvailableSql = """
        UPDATE restore_request
           SET status = 'AVAILABLE', available_at = now(), expires_at = @expiresAt
         WHERE id = @id AND status = 'PENDING';
        """;

    private readonly IDbConnectionFactory _connections;

    /// <summary>Initialises a new instance of the <see cref="RestoreRequestRepository"/> class.</summary>
    /// <param name="connections">Connection factory.</param>
    public RestoreRequestRepository(IDbConnectionFactory connections) => _connections = connections;

    /// <summary>Creates a request, in the caller's transaction (the audit joins it).</summary>
    /// <param name="request">The request.</param>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task CreateAsync(
        RestoreRequestRow request, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(transaction);

        _ = await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            InsertSql,
            new
            {
                id = request.Id,
                statementId = request.StatementId,
                periodStart = request.PeriodStart,
                customerId = request.CustomerId,
                requestedBy = request.RequestedBy,
                dueAt = request.DueAt,
            },
            transaction,
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Reads one request, scoped to its statement so the route cannot read across statements.</summary>
    /// <param name="restoreId">The request.</param>
    /// <param name="statementId">The statement in the route.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The row, or null.</returns>
    public async Task<RestoreRequestRow?> FindAsync(
        Guid restoreId, StatementId statementId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<RestoreRequestRow>(new CommandDefinition(
            FindSql,
            new { id = restoreId, statementId = statementId.Value },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Finds an unexpired completed restore for a statement, or null.</summary>
    /// <param name="statementId">The statement.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The row, or null.</returns>
    public async Task<RestoreRequestRow?> FindLiveAsync(StatementId statementId, CancellationToken cancellationToken)
    {
        // ReadStrong: this read gates a download of archived content; a lagging replica that has
        // not seen the completion would 409 a restore the customer was just told is ready.
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<RestoreRequestRow>(new CommandDefinition(
            LiveForStatementSql,
            new { statementId = statementId.Value },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Finds a still-pending restore for a statement, so repeat requests reuse it.</summary>
    /// <param name="statementId">The statement.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The row, or null.</returns>
    public async Task<RestoreRequestRow?> FindPendingAsync(StatementId statementId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<RestoreRequestRow>(new CommandDefinition(
            PendingForStatementSql,
            new { statementId = statementId.Value },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>The completion job's work list.</summary>
    /// <param name="limit">Batch bound.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Pending requests whose simulated (or real) retrieval has elapsed.</returns>
    public async Task<IReadOnlyList<RestoreRequestRow>> ListDueAsync(int limit, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        IEnumerable<RestoreRequestRow> rows = await connection.QueryAsync<RestoreRequestRow>(new CommandDefinition(
            DueSql,
            new { limit },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows];
    }

    /// <summary>Marks a restore AVAILABLE, in the caller's transaction (audit and outbox join it).</summary>
    /// <param name="restoreId">The request.</param>
    /// <param name="expiresAt">When the restored copy goes cold again.</param>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when this call completed it; false when it was not pending.</returns>
    public async Task<bool> MarkAvailableAsync(
        Guid restoreId, DateTimeOffset? expiresAt, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        int updated = await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            MarkAvailableSql,
            new { id = restoreId, expiresAt },
            transaction,
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return updated == 1;
    }
}
