using Dapper;
using Npgsql;
using StatementDelivery.Persistence.Connections;

namespace StatementDelivery.Persistence.Retention;

/// <summary>A reconciliation run.</summary>
/// <param name="Id">The run.</param>
/// <param name="RequestedBy">Who asked; null means the daily schedule.</param>
/// <param name="RequestedAt">When.</param>
/// <param name="StartedAt">When the worker picked it up.</param>
/// <param name="CompletedAt">When it finished.</param>
/// <param name="Status">REQUESTED, RUNNING, COMPLETED or FAILED.</param>
public sealed record ReconciliationRunRow(
    Guid Id,
    string? RequestedBy,
    DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string Status);

/// <summary>One finding.</summary>
/// <param name="CheckName">Which of the six checks.</param>
/// <param name="Severity">CRITICAL, WARNING or INFO.</param>
/// <param name="Subject">What it is about — a statement id, a storage key, a chain id.</param>
/// <param name="Detail">The finding, in one sentence.</param>
public sealed record ReconciliationFinding(string CheckName, string Severity, string Subject, string Detail);

/// <summary>The reconciliation run queue and findings store.</summary>
public sealed class ReconciliationRepository
{
    private const string EnqueueSql = """
        INSERT INTO reconciliation_run (id, requested_by)
        VALUES (@id, @requestedBy);
        """;

    // One run at a time, claimed atomically: the RUNNING guard means two leaders across a lease
    // handover cannot both execute the same request.
    private const string ClaimNextSql = """
        UPDATE reconciliation_run
           SET status = 'RUNNING', started_at = now()
         WHERE id = (SELECT id FROM reconciliation_run
                      WHERE status = 'REQUESTED'
                      ORDER BY requested_at
                      LIMIT 1
                        FOR UPDATE SKIP LOCKED)
        RETURNING id           AS Id,
                  requested_by AS RequestedBy,
                  requested_at AS RequestedAt,
                  started_at   AS StartedAt,
                  completed_at AS CompletedAt,
                  status       AS Status;
        """;

    private const string CompleteSql = """
        UPDATE reconciliation_run
           SET status = @status, completed_at = now()
         WHERE id = @id AND status = 'RUNNING';
        """;

    private const string InsertFindingSql = """
        INSERT INTO reconciliation_finding (run_id, check_name, severity, subject, detail)
        VALUES (@runId, @checkName, @severity, @subject, @detail);
        """;

    // H1: a leader that died (or was cancelled) mid-pass leaves its run RUNNING forever - never
    // completed, never re-queued. The reaper marks such runs FAILED so GET /latest and the
    // metrics tell the truth. One hour is generous: a pass is bounded samples, not a bucket walk.
    private const string ReapStaleRunsSql = """
        UPDATE reconciliation_run
           SET status = 'FAILED', completed_at = now()
         WHERE status = 'RUNNING' AND started_at < now() - INTERVAL '1 hour';
        """;

    private const string LatestRunSql = """
        SELECT id           AS Id,
               requested_by AS RequestedBy,
               requested_at AS RequestedAt,
               started_at   AS StartedAt,
               completed_at AS CompletedAt,
               status       AS Status
          FROM reconciliation_run
         WHERE status IN ('COMPLETED', 'FAILED')
         ORDER BY completed_at DESC
         LIMIT 1;
        """;

    private const string FindingsSql = """
        SELECT check_name AS CheckName,
               severity   AS Severity,
               subject    AS Subject,
               detail     AS Detail
          FROM reconciliation_finding
         WHERE run_id = @runId
         ORDER BY id
         LIMIT @limit;
        """;

    // Check 6, both halves. The customer_key half SHOULD be impossible (V013's constraint), and
    // that is exactly why reconciliation checks it: an erasure the system believes completed but
    // did not is invisible until someone audits it - the failure mode this job exists to catch.
    private const string IncompleteErasureKeysSql = """
        SELECT count(*) FROM customer_key
         WHERE status = 'DESTROYED' AND wrapped_cek IS NOT NULL;
        """;

    private const string IncompleteErasureStatementsSql = """
        SELECT count(*)
          FROM statement s
         WHERE s.wrapped_dek IS NOT NULL
           AND EXISTS (SELECT 1 FROM customer_key k
                        WHERE k.customer_id = s.customer_id AND k.status = 'DESTROYED');
        """;

    private const string RecentOrphansSql = """
        SELECT count(*) FROM orphan_report WHERE seen_at >= @since;
        """;

    private readonly IDbConnectionFactory _connections;

    /// <summary>Initialises a new instance of the <see cref="ReconciliationRepository"/> class.</summary>
    /// <param name="connections">Connection factory.</param>
    public ReconciliationRepository(IDbConnectionFactory connections) => _connections = connections;

    /// <summary>Enqueues a run (the API's POST, or the daily schedule).</summary>
    /// <param name="runId">The new run's id.</param>
    /// <param name="requestedBy">Who asked; null for the schedule.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task EnqueueAsync(Guid runId, string? requestedBy, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.Write, cancellationToken).ConfigureAwait(false);

        _ = await connection.ExecuteAsync(new CommandDefinition(
            EnqueueSql,
            new { id = runId, requestedBy },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Claims the oldest requested run, or null when none waits.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The claimed run, now RUNNING.</returns>
    public async Task<ReconciliationRunRow?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.Write, cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<ReconciliationRunRow>(new CommandDefinition(
            ClaimNextSql,
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Records one finding.</summary>
    /// <param name="runId">The run.</param>
    /// <param name="finding">The finding.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task AddFindingAsync(
        Guid runId, ReconciliationFinding finding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(finding);

        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.Write, cancellationToken).ConfigureAwait(false);

        _ = await connection.ExecuteAsync(new CommandDefinition(
            InsertFindingSql,
            new
            {
                runId,
                checkName = finding.CheckName,
                severity = finding.Severity,
                subject = finding.Subject,
                detail = finding.Detail,
            },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Marks a run COMPLETED or FAILED.</summary>
    /// <param name="runId">The run.</param>
    /// <param name="failed">True when the pass itself errored (distinct from finding problems).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task CompleteAsync(Guid runId, bool failed, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.Write, cancellationToken).ConfigureAwait(false);

        _ = await connection.ExecuteAsync(new CommandDefinition(
            CompleteSql,
            new { id = runId, status = failed ? "FAILED" : "COMPLETED" },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Marks runs stuck RUNNING for over an hour as FAILED. See the SQL's comment.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many stale runs were reaped.</returns>
    public async Task<int> ReapStaleRunsAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.Write, cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteAsync(new CommandDefinition(
            ReapStaleRunsSql,
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>The latest finished run, or null when none has run yet.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The run, or null.</returns>
    public async Task<ReconciliationRunRow?> FindLatestAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<ReconciliationRunRow>(new CommandDefinition(
            LatestRunSql,
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Check 6: destroyed keys still holding material, and statements of destroyed customers still holding DEKs.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The two counts.</returns>
    public async Task<(long Keys, long Statements)> CountIncompleteErasuresAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        long keys = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            IncompleteErasureKeysSql,
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        long statements = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            IncompleteErasureStatementsSql,
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return (keys, statements);
    }

    /// <summary>Check 2: orphans reported since a point in time.</summary>
    /// <param name="since">The window start.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The count.</returns>
    public async Task<long> CountRecentOrphansAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            RecentOrphansSql,
            new { since },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>A run's findings, bounded.</summary>
    /// <param name="runId">The run.</param>
    /// <param name="limit">Cap on rows returned.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The findings, in discovery order.</returns>
    public async Task<IReadOnlyList<ReconciliationFinding>> ListFindingsAsync(
        Guid runId, int limit, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        IEnumerable<ReconciliationFinding> rows = await connection.QueryAsync<ReconciliationFinding>(
            new CommandDefinition(
                FindingsSql,
                new { runId, limit },
                commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows];
    }
}
