using System.Globalization;
using Dapper;
using Npgsql;
using StatementDelivery.Domain.ValueObjects;
using StatementDelivery.Persistence.Connections;
using StatementDelivery.Persistence.Repositories;

namespace StatementDelivery.Persistence.Runs;

/// <summary>One batch-generation run.</summary>
/// <param name="Id">The run identifier.</param>
/// <param name="PeriodStart">Period start.</param>
/// <param name="PeriodEnd">Period end.</param>
/// <param name="Status">PLANNING, RUNNING, PAUSED or COMPLETED.</param>
/// <param name="TotalItems">Planned item count; zero while PLANNING.</param>
/// <param name="DeadlineAt">The completion deadline the monitor projects against, if any.</param>
/// <param name="CreatedAt">When the run was requested.</param>
public sealed record StatementRun(
    Guid Id,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string Status,
    long TotalItems,
    DateTimeOffset? DeadlineAt,
    DateTimeOffset CreatedAt);

/// <summary>The run statuses, as stored.</summary>
public static class RunStatus
{
    /// <summary>Created; items not yet fully enqueued.</summary>
    public const string Planning = "PLANNING";

    /// <summary>Workers may claim.</summary>
    public const string Running = "RUNNING";

    /// <summary>Deliberately held (for example: ledger circuit open). Work done is kept.</summary>
    public const string Paused = "PAUSED";

    /// <summary>Terminal. Re-triggering the period is a no-op.</summary>
    public const string Completed = "COMPLETED";
}

/// <summary>The item statuses, as stored.</summary>
public static class RunItemStatus
{
    /// <summary>Claimable.</summary>
    public const string Queued = "QUEUED";

    /// <summary>Claimed. If the claim goes stale the reaper returns it to QUEUED.</summary>
    public const string Rendering = "RENDERING";

    /// <summary>Statement committed.</summary>
    public const string Done = "DONE";

    /// <summary>Last attempt errored. Claimable while attempts remain; quarantined at the ceiling.</summary>
    public const string Failed = "FAILED";
}

/// <summary>One claimed unit of work.</summary>
/// <param name="ItemId">The queue row.</param>
/// <param name="AccountId">The account to render.</param>
/// <param name="Attempts">Attempts INCLUDING this claim.</param>
/// <param name="TraceParent">The traceparent captured at planning time, for trace continuity.</param>
public sealed record ClaimedItem(long ItemId, Guid AccountId, int Attempts, string? TraceParent);

/// <summary>Progress counters for one run, computed from <c>idx_run_item_status</c>.</summary>
/// <param name="Queued">Claimable items.</param>
/// <param name="Rendering">Currently claimed.</param>
/// <param name="Done">Committed.</param>
/// <param name="FailedRetryable">FAILED with attempts remaining - still claimable.</param>
/// <param name="FailedFinal">FAILED at the attempts ceiling - quarantined.</param>
public sealed record RunCounters(long Queued, long Rendering, long Done, long FailedRetryable, long FailedFinal)
{
    /// <summary>Gets the terminal count: items that will never be claimed again.</summary>
    public long Terminal => Done + FailedFinal;
}

/// <summary>One quarantined item, for the failures endpoint.</summary>
/// <param name="ItemId">The queue row - the retry handle.</param>
/// <param name="AccountId">The account that failed.</param>
/// <param name="Attempts">How many attempts were burned.</param>
/// <param name="LastError">Message and exception type. Never a stack trace, never content.</param>
/// <param name="FinishedAt">When the final attempt errored.</param>
public sealed record FailedItem(long ItemId, Guid AccountId, int Attempts, string? LastError, DateTimeOffset? FinishedAt);

/// <summary>
/// The batch-generation run store: the plan, and the PostgreSQL work queue over it.
/// </summary>
public interface IStatementRunRepository
{
    /// <summary>Creates the run for a period, or returns the existing one. Idempotent.</summary>
    /// <param name="runId">Identifier to use IF a run is created. Ignored when one exists.</param>
    /// <param name="period">The statement period.</param>
    /// <param name="deadlineAt">Completion deadline, or null for none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The run - freshly created in PLANNING, or whatever already existed.</returns>
    Task<StatementRun> CreateOrGetAsync(
        Guid runId, StatementPeriod period, DateTimeOffset? deadlineAt, CancellationToken cancellationToken);

    /// <summary>Loads one run.</summary>
    /// <param name="runId">The run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The run, or null.</returns>
    Task<StatementRun?> FindAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>Loads the runs the orchestrator must act on: PLANNING, RUNNING and PAUSED.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Active runs, oldest first.</returns>
    Task<IReadOnlyList<StatementRun>> ListActiveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Streams eligible accounts for a period, one keyset batch at a time.
    /// </summary>
    /// <remarks>
    /// NEVER the whole table. Keyset on <c>account.id</c>, one batch held at a time - the hard
    /// constraint this subsystem lives under is that nothing loads a full result set into memory.
    /// </remarks>
    /// <param name="period">The statement period, driving eligibility.</param>
    /// <param name="batchSize">Rows per batch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Batches of account identifiers, in id order.</returns>
    IAsyncEnumerable<IReadOnlyList<Guid>> StreamEligibleAccountsAsync(
        StatementPeriod period, int batchSize, CancellationToken cancellationToken);

    /// <summary>Enqueues one batch of run items. Safe to repeat: conflicts are skipped.</summary>
    /// <param name="runId">The run.</param>
    /// <param name="accountIds">The accounts in this batch.</param>
    /// <param name="traceParent">The planning trace, stamped on every item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many rows were actually inserted (conflicts excluded).</returns>
    Task<int> EnqueueBatchAsync(
        Guid runId, IReadOnlyList<Guid> accountIds, string? traceParent, CancellationToken cancellationToken);

    /// <summary>Finishes planning: records the total and opens the run for claiming.</summary>
    /// <param name="runId">The run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The total item count recorded.</returns>
    Task<long> MarkRunningAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>Moves a run between RUNNING, PAUSED and COMPLETED.</summary>
    /// <param name="runId">The run.</param>
    /// <param name="fromStatus">Expected current status - the transition is a predicate, not a hope.</param>
    /// <param name="toStatus">Target status.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the transition happened.</returns>
    Task<bool> TransitionAsync(Guid runId, string fromStatus, string toStatus, CancellationToken cancellationToken);

    /// <summary>Claims up to <paramref name="batchSize"/> items for one worker.</summary>
    /// <param name="runId">The run to claim from.</param>
    /// <param name="workerId">This worker's identity, recorded on the claim.</param>
    /// <param name="batchSize">Maximum items per claim round trip.</param>
    /// <param name="maxAttempts">The poison ceiling; items at or past it are never claimed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The claimed items - possibly empty, which means the queue is drained (or paused).</returns>
    Task<IReadOnlyList<ClaimedItem>> ClaimBatchAsync(
        Guid runId, string workerId, int batchSize, int maxAttempts, CancellationToken cancellationToken);

    /// <summary>Marks an item DONE, inside the caller's statement-commit transaction.</summary>
    /// <param name="itemId">The claimed item.</param>
    /// <param name="statementId">The statement the item produced.</param>
    /// <param name="workerId">THIS worker. Zero rows back means the claim was reaped from under it.</param>
    /// <param name="transaction">The caller's transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Rows affected: 1, or 0 when a reaped-and-reclaimed successor now owns the item.</returns>
    Task<int> CompleteItemAsync(
        long itemId, Guid statementId, string workerId, NpgsqlTransaction transaction, CancellationToken cancellationToken);

    /// <summary>Records a failed attempt. The item stays claimable while attempts remain.</summary>
    /// <param name="itemId">The claimed item.</param>
    /// <param name="failureReason">Message and exception type. The implementation truncates defensively.</param>
    /// <param name="workerId">THIS worker; the failure is scoped to its own claim.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Rows affected: 0 when the claim was reaped from under this worker.</returns>
    Task<int> FailItemAsync(long itemId, string failureReason, string workerId, CancellationToken cancellationToken);

    /// <summary>
    /// Records a DETERMINISTIC failure: FAILED with attempts raised to the ceiling, so the item
    /// is never claimed again. For conditions no retry can change (a destroyed customer key).
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="failureReason">Exception type and message. Never a stack trace or content.</param>
    /// <param name="workerId">This worker; completion is claimant-scoped.</param>
    /// <param name="maxAttempts">The poison ceiling to raise attempts to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Rows affected: 0 means the claim was reaped from under this worker.</returns>
    Task<int> FailItemTerminallyAsync(long itemId, string failureReason, string workerId, int maxAttempts, CancellationToken cancellationToken);

    /// <summary>Returns claimed-but-unstarted items to QUEUED on graceful shutdown.</summary>
    /// <remarks>Attempts stay incremented - see the claim-time increment rule.</remarks>
    /// <param name="itemIds">The items this worker claimed and will not process.</param>
    /// <param name="workerId">This worker; the release is scoped to its own claims.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many were released.</returns>
    Task<int> ReleaseClaimsAsync(IReadOnlyList<long> itemIds, string workerId, CancellationToken cancellationToken);

    /// <summary>Returns stale RENDERING claims to QUEUED. The reaper's one statement.</summary>
    /// <param name="staleAfter">How long a claim may sit in RENDERING before it is presumed dead.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many claims were reaped.</returns>
    Task<int> ReapStaleClaimsAsync(TimeSpan staleAfter, CancellationToken cancellationToken);

    /// <summary>Reads progress counters from the status index.</summary>
    /// <param name="runId">The run.</param>
    /// <param name="maxAttempts">The ceiling that splits FAILED into retryable and final.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The counters.</returns>
    Task<RunCounters> CountersAsync(Guid runId, int maxAttempts, CancellationToken cancellationToken);

    /// <summary>Lists quarantined items, one keyset page at a time.</summary>
    /// <param name="runId">The run.</param>
    /// <param name="maxAttempts">The quarantine ceiling.</param>
    /// <param name="afterItemId">Keyset cursor: return items with an id greater than this.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One page of failures.</returns>
    Task<IReadOnlyList<FailedItem>> ListFailuresAsync(
        Guid runId, int maxAttempts, long afterItemId, int limit, CancellationToken cancellationToken);

    /// <summary>Resets quarantined items for another round of attempts. A deliberate operator action.</summary>
    /// <param name="runId">The run.</param>
    /// <param name="itemIds">Specific items, or null for every quarantined item in the run.</param>
    /// <param name="transaction">The caller's transaction, so the reset and its audit commit together.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many items were reset.</returns>
    Task<int> RetryFailuresAsync(
        Guid runId, IReadOnlyList<long>? itemIds, NpgsqlTransaction transaction, CancellationToken cancellationToken);
}

/// <summary>PostgreSQL implementation of <see cref="IStatementRunRepository"/>.</summary>
public sealed class StatementRunRepository : IStatementRunRepository
{
    // =========================================================================================
    //  THE BATCH CLAIM. The canonical PostgreSQL queue pattern, and the query this whole
    //  subsystem stands on - a bug here produces duplicate statements, and duplicate statements
    //  live under a Compliance-mode Object Lock for seven years.
    //
    //  FOR UPDATE SKIP LOCKED is what makes this a queue. Each worker's SELECT locks the rows it
    //  is about to take; a concurrent worker's SELECT SKIPS those rows instead of blocking on
    //  them, and takes the next unlocked ones. Without SKIP LOCKED, 400 workers serialise into
    //  one - every claim waits for every earlier claim's transaction. With it, claims interleave
    //  and the only serialisation left is the row lock itself, which is exactly the exclusivity
    //  the queue needs: a row is claimed by whoever locked it, once.
    //
    //  BATCH SIZE 50, NOT 1 (configurable; see GenerationOptions.ClaimBatchSize for the sizing
    //  reasoning). At ~1,400 items/second, claiming singly is 1,400 round trips per second
    //  against one hot table; claiming 50 at a time is ~28. The queue table's write rate is the
    //  same either way - the difference is pure per-statement overhead.
    //
    //  attempts INCREMENTS HERE, ON CLAIM - not on completion. A worker that crashes mid-render
    //  never reaches completion code, so a completion-time increment records nothing and the
    //  item comes back pristine: a poison item that kills its worker would loop forever. Burning
    //  the attempt at claim time means every claim - crashed or not - moves the item toward
    //  maxAttempts, where the WHERE clause quarantines it by exclusion. The reaper depends on
    //  this: it resets status and claim columns but NEVER attempts.
    //
    //  The CTE orders by id (dense, insertion-ordered) and the UPDATE joins back on the primary
    //  key. RETURNING hands the worker everything it needs - including the traceparent stamped
    //  at planning time, so the render continues the planning trace.
    // =========================================================================================
    private const string ClaimSql = """
        WITH claimed AS (
            SELECT id
              FROM statement_run_item
             WHERE run_id = @runId
               AND status IN ('QUEUED', 'FAILED')
               AND attempts < @maxAttempts
             ORDER BY id
               FOR UPDATE SKIP LOCKED
             LIMIT @batchSize
        )
        UPDATE statement_run_item i
           SET status     = 'RENDERING',
               claimed_at = now(),
               claimed_by = @workerId,
               started_at = now(),
               attempts   = i.attempts + 1
          FROM claimed c
         WHERE i.id = c.id
        RETURNING i.id, i.account_id AS AccountId, i.attempts, i.trace_parent AS TraceParent;
        """;

    private const string CreateRunSql = """
        INSERT INTO statement_run (id, period_start, period_end, status, deadline_at)
        VALUES (@id, @periodStart, @periodEnd, 'PLANNING', @deadlineAt)
        ON CONFLICT (period_start, period_end) DO NOTHING;
        """;

    private const string FindRunByPeriodSql = """
        SELECT id, period_start AS PeriodStart, period_end AS PeriodEnd, status,
               total_items AS TotalItems, deadline_at AS DeadlineAt, created_at AS CreatedAt
          FROM statement_run
         WHERE period_start = @periodStart AND period_end = @periodEnd;
        """;

    private const string FindRunSql = """
        SELECT id, period_start AS PeriodStart, period_end AS PeriodEnd, status,
               total_items AS TotalItems, deadline_at AS DeadlineAt, created_at AS CreatedAt
          FROM statement_run
         WHERE id = @id;
        """;

    private const string ListActiveSql = """
        SELECT id, period_start AS PeriodStart, period_end AS PeriodEnd, status,
               total_items AS TotalItems, deadline_at AS DeadlineAt, created_at AS CreatedAt
          FROM statement_run
         WHERE status IN ('PLANNING', 'RUNNING', 'PAUSED')
         ORDER BY created_at;
        """;

    // Keyset on id, never OFFSET. Eligibility per the specification: ACTIVE, open before the period
    // ended, and not closed before it started. The dormant are still statemented; the closed
    // stop receiving statements after their closing period.
    private const string StreamAccountsSql = """
        SELECT id
          FROM account
         WHERE id > @afterId
           AND status = 'ACTIVE'
           AND opened_at <= @periodEnd
           AND (closed_at IS NULL OR closed_at > @periodStart)
         ORDER BY id
         LIMIT @batchSize;
        """;

    // =========================================================================================
    //  PLANNING INSERT: unnest + ON CONFLICT, not binary COPY.
    //
    //  ⚠ DEVIATION FROM THE SPECIFICATION, which said "batch-INSERT run items via IBulkWriter". The bulk
    //  writer is binary COPY, and COPY cannot express ON CONFLICT DO NOTHING - the very clause
    //  the same specification calls "what makes planning resumable". Of the two instructions, the
    //  resumability one is load-bearing, so it wins. unnest keeps the batch a single round trip
    //  (one statement per 10,000 accounts, not 10,000 statements) and conflicts skip silently.
    //  The COPY alternative - staging table plus INSERT..SELECT..ON CONFLICT - works under
    //  transaction pooling only if the temp table lives and dies inside one transaction, and
    //  buys nothing at one statement per batch either way.
    // =========================================================================================
    private const string EnqueueSql = """
        INSERT INTO statement_run_item (run_id, account_id, trace_parent)
        SELECT @runId, a, @traceParent
          FROM unnest(@accountIds) AS a
        ON CONFLICT (run_id, account_id) DO NOTHING;
        """;

    private const string MarkRunningSql = """
        UPDATE statement_run
           SET status      = 'RUNNING',
               total_items = (SELECT count(*) FROM statement_run_item WHERE run_id = @runId),
               updated_at  = now()
         WHERE id = @runId
           AND status IN ('PLANNING', 'RUNNING')
        RETURNING total_items;
        """;

    // The FROM status is a predicate: a transition races with nobody, it either applies to the
    // expected state or reports that it did not. Callers decide what a lost race means.
    private const string TransitionSql = """
        UPDATE statement_run
           SET status = @toStatus, updated_at = now()
         WHERE id = @runId AND status = @fromStatus;
        """;

    // claimed_by IS IN THE PREDICATE (a design review's low-severity note, promoted to a real fix
    // here). A render outliving the stale window can interleave with its reaped-and-reclaimed
    // successor; without the scope, the original worker could mark DONE a row its successor now
    // owns, and the two would converge only by tripping the statement version unique constraint.
    // Converging by constraint violation is worse than not racing. With the scope, zero rows
    // means exactly one thing - "this claim was reaped from under me" - which the caller reports
    // as generation_stale_completion_total: a non-zero rate says the stale window is shorter
    // than real render times, a tuning signal that was previously invisible.
    private const string CompleteItemSql = """
        UPDATE statement_run_item
           SET status = 'DONE', finished_at = now(), statement_id = @statementId, last_error = NULL
         WHERE id = @itemId AND status = 'RENDERING' AND claimed_by = @workerId;
        """;

    private const string FailItemSql = """
        UPDATE statement_run_item
           SET status = 'FAILED', finished_at = now(), last_error = @error
         WHERE id = @itemId AND status = 'RENDERING' AND claimed_by = @workerId;
        """;

    // attempts jumps to the ceiling so the claim query never hands this item out again: the
    // failure is DETERMINISTIC (the customer's key is destroyed or scheduled, so the destroyed-key
    // write guard refuses the publish every time), and a retry would burn a claim to hit the same
    // wall. GREATEST keeps a higher recorded attempt count intact.
    private const string FailItemTerminallySql = """
        UPDATE statement_run_item
           SET status = 'FAILED', finished_at = now(), last_error = @error,
               attempts = GREATEST(attempts, @maxAttempts)
         WHERE id = @itemId AND status = 'RENDERING' AND claimed_by = @workerId;
        """;

    // Scoped to THIS worker's claims: a graceful shutdown must not release rows some other
    // worker claimed a millisecond ago. attempts stays - the claim was made, the attempt burned.
    private const string ReleaseClaimsSql = """
        UPDATE statement_run_item
           SET status = 'QUEUED', claimed_at = NULL, claimed_by = NULL, started_at = NULL
         WHERE id = ANY(@itemIds)
           AND status = 'RENDERING'
           AND claimed_by = @workerId;
        """;

    // The reaper. attempts is deliberately untouched: it was incremented at claim time, which is
    // the entire reason a worker that keeps dying on one item still drives it to the poison
    // ceiling instead of cycling forever. Belt and braces with graceful shutdown - a SIGKILLed
    // pod never runs any release path, and this is what cleans up after it.
    private const string ReapSql = """
        UPDATE statement_run_item
           SET status = 'QUEUED', claimed_by = NULL, claimed_at = NULL, started_at = NULL
         WHERE status = 'RENDERING'
           AND claimed_at < now() - @staleAfter;
        """;

    private const string CountersSql = """
        SELECT count(*) FILTER (WHERE status = 'QUEUED')                                    AS Queued,
               count(*) FILTER (WHERE status = 'RENDERING')                                 AS Rendering,
               count(*) FILTER (WHERE status = 'DONE')                                      AS Done,
               count(*) FILTER (WHERE status = 'FAILED' AND attempts <  @maxAttempts)       AS FailedRetryable,
               count(*) FILTER (WHERE status = 'FAILED' AND attempts >= @maxAttempts)       AS FailedFinal
          FROM statement_run_item
         WHERE run_id = @runId;
        """;

    private const string ListFailuresSql = """
        SELECT id AS ItemId, account_id AS AccountId, attempts AS Attempts,
               last_error AS LastError, finished_at AS FinishedAt
          FROM statement_run_item
         WHERE run_id = @runId
           AND status = 'FAILED'
           AND attempts >= @maxAttempts
           AND id > @afterItemId
         ORDER BY id
         LIMIT @limit;
        """;

    // Reset resets attempts to ZERO - a deliberate operator action taken after the underlying
    // cause is fixed, granting a full fresh round rather than one more try.
    private const string RetryAllSql = """
        UPDATE statement_run_item
           SET status = 'QUEUED', attempts = 0, last_error = NULL,
               claimed_at = NULL, claimed_by = NULL, started_at = NULL, finished_at = NULL
         WHERE run_id = @runId
           AND status = 'FAILED'
           AND attempts >= @maxAttemptsFloor;
        """;

    private const string RetrySomeSql = """
        UPDATE statement_run_item
           SET status = 'QUEUED', attempts = 0, last_error = NULL,
               claimed_at = NULL, claimed_by = NULL, started_at = NULL, finished_at = NULL
         WHERE run_id = @runId
           AND id = ANY(@itemIds)
           AND status = 'FAILED';
        """;

    /// <summary>last_error ceiling. Anything longer is truncated - it is a label, not a log.</summary>
    private const int MaxErrorLength = 500;

    private readonly IDbConnectionFactory _connections;

    /// <summary>Initialises a new instance of the <see cref="StatementRunRepository"/> class.</summary>
    /// <param name="connections">Connection factory.</param>
    public StatementRunRepository(IDbConnectionFactory connections) => _connections = connections;

    private int WriteTimeout => _connections.CommandTimeoutSeconds(ConnectionIntent.Write);

    /// <inheritdoc />
    public async Task<StatementRun> CreateOrGetAsync(
        Guid runId, StatementPeriod period, DateTimeOffset? deadlineAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(period);

        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.Write, cancellationToken).ConfigureAwait(false);

        // INSERT then SELECT, both against the primary. ON CONFLICT DO NOTHING swallows the
        // duplicate; the SELECT then returns whichever row won - ours or a predecessor's. The
        // two statements need no shared transaction: the UNIQUE constraint is the arbiter.
        _ = await connection.ExecuteAsync(new CommandDefinition(
            CreateRunSql,
            new { id = runId, periodStart = period.Start, periodEnd = period.End, deadlineAt },
            commandTimeout: WriteTimeout,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        RunRow row = await connection.QuerySingleAsync<RunRow>(new CommandDefinition(
            FindRunByPeriodSql,
            new { periodStart = period.Start, periodEnd = period.End },
            commandTimeout: WriteTimeout,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row.ToRecord();
    }

    /// <inheritdoc />
    public async Task<StatementRun?> FindAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        RunRow? row = await connection.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition(
            FindRunSql,
            new { id = runId },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row?.ToRecord();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StatementRun>> ListActiveAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        IEnumerable<RunRow> rows = await connection.QueryAsync<RunRow>(new CommandDefinition(
            ListActiveSql,
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(static r => r.ToRecord())];
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<IReadOnlyList<Guid>> StreamEligibleAccountsAsync(
        StatementPeriod period,
        int batchSize,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        // One batch in memory at a time, ever. The keyset cursor is the last id of the previous
        // batch; Guid.Empty sorts before every UUIDv7, so the first batch starts at the beginning.
        Guid afterId = Guid.Empty;
        DateTimeOffset periodStart = new(period.Start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        DateTimeOffset periodEnd = new(period.End.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);

        while (true)
        {
            List<Guid> batch;

            // A fresh short-lived connection per batch, not one connection held across the whole
            // stream: behind PgBouncer a connection held open between transactions pins a server
            // slot the whole fleet shares, and planning 30 million accounts takes minutes.
            await using (NpgsqlConnection connection =
                await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false))
            {
                IEnumerable<Guid> rows = await connection.QueryAsync<Guid>(new CommandDefinition(
                    StreamAccountsSql,
                    new { afterId, periodStart, periodEnd, batchSize },
                    commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

                batch = [.. rows];
            }

            if (batch.Count == 0)
            {
                yield break;
            }

            afterId = batch[^1];
            yield return batch;

            if (batch.Count < batchSize)
            {
                yield break;
            }
        }
    }

    /// <inheritdoc />
    public async Task<int> EnqueueBatchAsync(
        Guid runId, IReadOnlyList<Guid> accountIds, string? traceParent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accountIds);

        if (accountIds.Count == 0)
        {
            return 0;
        }

        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.Write, cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteAsync(new CommandDefinition(
            EnqueueSql,
            new { runId, accountIds = accountIds.ToArray(), traceParent },
            commandTimeout: WriteTimeout,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> MarkRunningAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.Write, cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            MarkRunningSql,
            new { runId },
            commandTimeout: WriteTimeout,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> TransitionAsync(
        Guid runId, string fromStatus, string toStatus, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.Write, cancellationToken).ConfigureAwait(false);

        int affected = await connection.ExecuteAsync(new CommandDefinition(
            TransitionSql,
            new { runId, fromStatus, toStatus },
            commandTimeout: WriteTimeout,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected == 1;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ClaimedItem>> ClaimBatchAsync(
        Guid runId, string workerId, int batchSize, int maxAttempts, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.Write, cancellationToken).ConfigureAwait(false);

        IEnumerable<ClaimRow> rows = await connection.QueryAsync<ClaimRow>(new CommandDefinition(
            ClaimSql,
            new { runId, workerId, batchSize, maxAttempts },
            commandTimeout: WriteTimeout,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(static r => new ClaimedItem(r.Id, r.AccountId, r.Attempts, r.TraceParent))];
    }

    /// <inheritdoc />
    public async Task<int> CompleteItemAsync(
        long itemId, Guid statementId, string workerId, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        return await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            CompleteItemSql,
            new { itemId, statementId, workerId },
            transaction: transaction,
            commandTimeout: WriteTimeout,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> FailItemAsync(
        long itemId, string failureReason, string workerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        string bounded = failureReason.Length <= MaxErrorLength
            ? failureReason
            : failureReason[..MaxErrorLength];

        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.Write, cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteAsync(new CommandDefinition(
            FailItemSql,
            new { itemId, error = bounded, workerId },
            commandTimeout: WriteTimeout,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> FailItemTerminallyAsync(
        long itemId, string failureReason, string workerId, int maxAttempts, CancellationToken cancellationToken)
    {
        string bounded = failureReason.Length <= 500 ? failureReason : failureReason[..500];

        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.Write, cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteAsync(new CommandDefinition(
            FailItemTerminallySql,
            new { itemId, error = bounded, workerId, maxAttempts },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> ReleaseClaimsAsync(
        IReadOnlyList<long> itemIds, string workerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        if (itemIds.Count == 0)
        {
            return 0;
        }

        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.Write, cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteAsync(new CommandDefinition(
            ReleaseClaimsSql,
            new { itemIds = itemIds.ToArray(), workerId },
            commandTimeout: WriteTimeout,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> ReapStaleClaimsAsync(TimeSpan staleAfter, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.Write, cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteAsync(new CommandDefinition(
            ReapSql,
            new { staleAfter },
            commandTimeout: WriteTimeout,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RunCounters> CountersAsync(Guid runId, int maxAttempts, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleAsync<RunCounters>(new CommandDefinition(
            CountersSql,
            new { runId, maxAttempts },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FailedItem>> ListFailuresAsync(
        Guid runId, int maxAttempts, long afterItemId, int limit, CancellationToken cancellationToken)
    {
        int pageSize = Math.Clamp(limit, 1, 500);

        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        IEnumerable<FailedItemRow> rows = await connection.QueryAsync<FailedItemRow>(new CommandDefinition(
            ListFailuresSql,
            new { runId, maxAttempts, afterItemId, limit = pageSize },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(static r => r.ToRecord())];
    }

    /// <inheritdoc />
    public async Task<int> RetryFailuresAsync(
        Guid runId, IReadOnlyList<long>? itemIds, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (itemIds is { Count: 0 })
        {
            return 0;
        }

        CommandDefinition command = itemIds is null
            ? new CommandDefinition(
                RetryAllSql,
                new { runId, maxAttemptsFloor = 1 },
                transaction: transaction,
                commandTimeout: WriteTimeout,
                cancellationToken: cancellationToken)
            : new CommandDefinition(
                RetrySomeSql,
                new { runId, itemIds = itemIds.ToArray() },
                transaction: transaction,
                commandTimeout: WriteTimeout,
                cancellationToken: cancellationToken);

        return await transaction.Connection!.ExecuteAsync(command).ConfigureAwait(false);
    }

    private sealed record RunRow
    {
        public Guid Id { get; init; }

        public DateOnly PeriodStart { get; init; }

        public DateOnly PeriodEnd { get; init; }

        public string Status { get; init; } = string.Empty;

        public long TotalItems { get; init; }

        public DateTime? DeadlineAt { get; init; }

        public DateTime CreatedAt { get; init; }

        public StatementRun ToRecord() => new(
            Id, PeriodStart, PeriodEnd, Status, TotalItems,
            DeadlineAt is { } d ? new DateTimeOffset(d, TimeSpan.Zero) : null,
            new DateTimeOffset(CreatedAt, TimeSpan.Zero));
    }

    /// <summary>
    /// Dapper-facing shape: the public record's positional constructor takes DateTimeOffset and
    /// Dapper materialising timestamptz hands the constructor-matcher a DateTime, so no
    /// signature matches (see ErasureRepository.RequestRow). Init properties in DateTime,
    /// converted at the edge.
    /// </summary>
    private sealed record FailedItemRow
    {
        public long ItemId { get; init; }

        public Guid AccountId { get; init; }

        public int Attempts { get; init; }

        public string? LastError { get; init; }

        public DateTime? FinishedAt { get; init; }

        public FailedItem ToRecord() => new(
            ItemId, AccountId, Attempts, LastError,
            FinishedAt is { } finishedAt ? new DateTimeOffset(finishedAt, TimeSpan.Zero) : null);
    }

    private sealed record ClaimRow
    {
        public long Id { get; init; }

        public Guid AccountId { get; init; }

        public int Attempts { get; init; }

        public string? TraceParent { get; init; }
    }
}
