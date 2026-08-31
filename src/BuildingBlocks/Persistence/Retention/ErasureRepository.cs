using Dapper;
using Npgsql;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Persistence.Connections;

namespace StatementDelivery.Persistence.Retention;

/// <summary>An erasure request row.</summary>
/// <param name="Id">The request.</param>
/// <param name="CustomerId">The customer to erase.</param>
/// <param name="Reason">The stated basis, e.g. "POPIA s24 data subject request".</param>
/// <param name="RequestReference">The DSR reference.</param>
/// <param name="RequestedBy">Who lodged it.</param>
/// <param name="RequestedAt">When.</param>
/// <param name="DueAt">When the cooling-off window closes.</param>
/// <param name="Status">SCHEDULED, CANCELLED or COMPLETED.</param>
/// <param name="LastBlockedReason">The decision type that last blocked execution, for transition-only auditing (V022).</param>
/// <param name="LastBlockedAt">When the block was last audited.</param>
public sealed record ErasureRequestRow(
    Guid Id,
    Guid CustomerId,
    string Reason,
    string RequestReference,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    DateTimeOffset DueAt,
    string Status,
    string? LastBlockedReason = null,
    DateTimeOffset? LastBlockedAt = null);

/// <summary>A customer key row's lifecycle state, without any crypto material.</summary>
/// <remarks>
/// Deliberately NOT <c>CustomerKeyRecord</c>: that type lives in the Crypto project and carries
/// wrapped key material, and the delivery API is architecturally banned from referencing
/// anything in <c>StatementDelivery.Crypto.Keys</c> (CryptoBoundaryTests). The erasure endpoint
/// needs two facts - is there a key, and is it destroyed - and this shape carries exactly those.
/// </remarks>
/// <param name="Status">ACTIVE, ROTATING, SCHEDULED_DESTRUCTION or DESTROYED.</param>
/// <param name="DestroyedAt">When destruction happened, when it has.</param>
public sealed record CustomerKeyState(string Status, DateTimeOffset? DestroyedAt);

/// <summary>The erasure lifecycle's database adapter: schedule, cancel, find due, complete.</summary>
/// <remarks>
/// Scheduling and cancellation flip BOTH the request row and <c>customer_key</c> in one
/// transaction — the request is the record, the key row is the fuse, and they must never
/// disagree. Destruction itself is NOT here: that is <c>ICustomerKeyStore.DestroyAsync</c>,
/// called by the executor after re-evaluating the legal position.
/// </remarks>
public sealed class ErasureRepository
{
    private const string InsertRequestSql = """
        INSERT INTO erasure_request
            (id, customer_id, reason, request_reference, requested_by, due_at)
        VALUES (@id, @customerId, @reason, @requestReference, @requestedBy, @dueAt);
        """;

    // Guarded on ACTIVE: a customer already scheduled (unique partial index also enforces one
    // live request), already destroyed, or mid-rotation must not be silently re-armed.
    private const string ScheduleKeySql = """
        UPDATE customer_key
           SET status = 'SCHEDULED_DESTRUCTION',
               destruction_due_at = @dueAt,
               destruction_reason = @reason
         WHERE customer_id = @customerId AND status = 'ACTIVE';
        """;

    private const string CancelRequestSql = """
        UPDATE erasure_request
           SET status = 'CANCELLED', cancelled_at = now(), cancelled_by = @cancelledBy
         WHERE customer_id = @customerId AND status = 'SCHEDULED'
        RETURNING id;
        """;

    private const string CancelKeySql = """
        UPDATE customer_key
           SET status = 'ACTIVE', destruction_due_at = NULL, destruction_reason = NULL
         WHERE customer_id = @customerId AND status = 'SCHEDULED_DESTRUCTION';
        """;

    private const string FindActiveSql = """
        SELECT id                AS Id,
               customer_id       AS CustomerId,
               reason            AS Reason,
               request_reference AS RequestReference,
               requested_by      AS RequestedBy,
               requested_at      AS RequestedAt,
               due_at            AS DueAt,
               status            AS Status
          FROM erasure_request
         WHERE customer_id = @customerId AND status = 'SCHEDULED';
        """;

    // The executor's work list: cooling-off elapsed, still scheduled. Bounded, because every
    // sweep in this codebase is bounded.
    private const string DueSql = """
        SELECT id                AS Id,
               customer_id       AS CustomerId,
               reason            AS Reason,
               request_reference AS RequestReference,
               requested_by      AS RequestedBy,
               requested_at      AS RequestedAt,
               due_at            AS DueAt,
               status            AS Status,
               last_blocked_reason AS LastBlockedReason,
               last_blocked_at   AS LastBlockedAt
          FROM erasure_request
         WHERE status = 'SCHEDULED' AND due_at <= now()
         ORDER BY due_at
         LIMIT @limit;
        """;

    private const string RecordBlockedSql = """
        UPDATE erasure_request
           SET last_blocked_reason = @reason, last_blocked_at = now()
         WHERE id = @id AND status = 'SCHEDULED';
        """;

    private const string KeyStateSql = """
        SELECT status AS Status, destroyed_at AS DestroyedAt
          FROM customer_key
         WHERE customer_id = @customerId;
        """;

    private const string CompleteRequestSql = """
        UPDATE erasure_request
           SET status = 'COMPLETED', completed_at = now()
         WHERE id = @id AND status = 'SCHEDULED';
        """;

    private readonly IDbConnectionFactory _connections;

    /// <summary>Initialises a new instance of the <see cref="ErasureRepository"/> class.</summary>
    /// <param name="connections">Connection factory.</param>
    public ErasureRepository(IDbConnectionFactory connections) => _connections = connections;

    /// <summary>Schedules an erasure: request row plus the customer key's cooling-off state, one transaction.</summary>
    /// <param name="request">The request. <see cref="ErasureRequestRow.Status"/> is ignored; it inserts SCHEDULED.</param>
    /// <param name="transaction">The transaction the ERASURE_SCHEDULED audit also joins.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the key row moved to SCHEDULED_DESTRUCTION; false when it was not ACTIVE (no key yet, already scheduled, or destroyed).</returns>
    public async Task<bool> ScheduleAsync(
        ErasureRequestRow request, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(transaction);

        _ = await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            InsertRequestSql,
            new
            {
                id = request.Id,
                customerId = request.CustomerId,
                reason = request.Reason,
                requestReference = request.RequestReference,
                requestedBy = request.RequestedBy,
                dueAt = request.DueAt,
            },
            transaction,
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        int armed = await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            ScheduleKeySql,
            new { customerId = request.CustomerId, dueAt = request.DueAt, reason = request.Reason },
            transaction,
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return armed == 1;
    }

    /// <summary>Cancels a scheduled erasure while the cooling-off window is open, one transaction.</summary>
    /// <param name="customerId">The customer.</param>
    /// <param name="cancelledBy">Who cancelled.</param>
    /// <param name="transaction">The transaction the ERASURE_CANCELLED audit also joins.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cancelled request's id, or null when nothing was scheduled.</returns>
    public async Task<Guid?> CancelAsync(
        CustomerId customerId, string cancelledBy, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        Guid? cancelled = await transaction.Connection!.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            CancelRequestSql,
            new { customerId = customerId.Value, cancelledBy },
            transaction,
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (cancelled is null)
        {
            return null;
        }

        _ = await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            CancelKeySql,
            new { customerId = customerId.Value },
            transaction,
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return cancelled;
    }

    /// <summary>Reads the customer key row's state, or null when the customer has no key.</summary>
    /// <param name="customerId">The customer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The state, or null.</returns>
    public async Task<CustomerKeyState?> FindKeyStateAsync(
        CustomerId customerId, CancellationToken cancellationToken)
    {
        // ReadStrong: this read gates an erasure decision (ADR-0024's rule).
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<CustomerKeyState>(new CommandDefinition(
            KeyStateSql,
            new { customerId = customerId.Value },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Finds a customer's live (scheduled) request, or null.</summary>
    /// <param name="customerId">The customer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The row, or null.</returns>
    public async Task<ErasureRequestRow?> FindScheduledAsync(CustomerId customerId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<ErasureRequestRow>(new CommandDefinition(
            FindActiveSql,
            new { customerId = customerId.Value },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>The executor's work list: scheduled requests whose cooling-off has elapsed.</summary>
    /// <param name="limit">Batch bound.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Due requests, oldest first.</returns>
    public async Task<IReadOnlyList<ErasureRequestRow>> ListDueAsync(int limit, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        IEnumerable<ErasureRequestRow> rows = await connection.QueryAsync<ErasureRequestRow>(new CommandDefinition(
            DueSql,
            new { limit },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows];
    }

    /// <summary>Records the blocking reason and timestamp, in the caller's transaction.</summary>
    /// <param name="requestId">The request.</param>
    /// <param name="reason">The decision type that blocked.</param>
    /// <param name="transaction">The transaction the ERASURE_BLOCKED audit (when one is due) also joins.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RecordBlockedAsync(
        Guid requestId, string reason, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        _ = await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            RecordBlockedSql,
            new { id = requestId, reason },
            transaction,
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Marks a request completed, in the caller's transaction.</summary>
    /// <param name="requestId">The request.</param>
    /// <param name="transaction">The transaction the ERASURE_COMPLETED audit also joins.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task CompleteAsync(Guid requestId, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        _ = await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            CompleteRequestSql,
            new { id = requestId },
            transaction,
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
