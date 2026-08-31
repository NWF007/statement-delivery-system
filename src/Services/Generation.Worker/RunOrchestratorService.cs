using System.Diagnostics;
using Generation.Worker.Configuration;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using StatementDelivery.Domain.ValueObjects;
using StatementDelivery.Persistence.Leasing;
using StatementDelivery.Persistence.Runs;

namespace Generation.Worker;

/// <summary>
/// The orchestrator: plans runs and monitors their progress. Lease-gated to ONE replica.
/// </summary>
/// <remarks>
/// <para>
/// Every replica hosts this service; only the lease holder acts. Scale to 400 replicas and you
/// get 400 renderers and exactly one orchestrator, with no separate deployment - this is why
/// the Prompt 1 lease abstraction exists. Lose the holder and a standby takes over within one
/// lease TTL, resuming from the database's state rather than from anything held in memory.
/// </para>
/// <para>
/// PLANNING IS RESUMABLE BY CONSTRUCTION. Accounts stream in keyset batches; every batch insert
/// is ON CONFLICT DO NOTHING against UNIQUE(run_id, account_id). An orchestrator that dies at
/// account 12 million restarts, streams from the beginning, and re-inserts nothing - the
/// conflict clause swallows everything already enqueued. No high-water mark to persist, no
/// checkpoint to corrupt.
/// </para>
/// </remarks>
public sealed partial class RunOrchestratorService : BackgroundService
{
    private const string LeaseName = "generation-orchestrator";

    /// <summary>Accounts per planning batch: 10,000 ids in flight at a time, never the table.</summary>
    private const int PlanBatchSize = 10_000;

    private static readonly ActivitySource ActivitySource = new("StatementDelivery.Generation");

    private readonly ILeaseManager _leases;
    private readonly IStatementRunRepository _runs;
    private readonly GenerationMetrics _metrics;
    private readonly CircuitBreakerStateProvider _ledgerCircuit;
    private readonly GenerationWorkerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RunOrchestratorService> _logger;

    // Rolling throughput window: (timestamp, terminalCount) samples over the last few minutes.
    private readonly Queue<(long Timestamp, long Terminal)> _progressWindow = new();

    /// <summary>Initialises a new instance of the <see cref="RunOrchestratorService"/> class.</summary>
    /// <param name="leases">Lease manager.</param>
    /// <param name="runs">The run store.</param>
    /// <param name="metrics">Batch metrics.</param>
    /// <param name="ledgerCircuit">The ledger breaker's state, for pause decisions.</param>
    /// <param name="options">Worker options.</param>
    /// <param name="time">Time source.</param>
    /// <param name="logger">Logger.</param>
    public RunOrchestratorService(
        ILeaseManager leases,
        IStatementRunRepository runs,
        GenerationMetrics metrics,
        CircuitBreakerStateProvider ledgerCircuit,
        IOptions<GenerationWorkerOptions> options,
        TimeProvider time,
        ILogger<RunOrchestratorService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _leases = leases;
        _runs = runs;
        _metrics = metrics;
        _ledgerCircuit = ledgerCircuit;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ILeaseHandle? lease = await _leases.TryAcquireAsync(LeaseName, stoppingToken).ConfigureAwait(false);

                if (lease is null)
                {
                    // Another replica orchestrates. Ordinary, not an error; renderer-only mode.
                    await Task.Delay(_options.MonitorInterval, _time, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await using (lease.ConfigureAwait(false))
                {
                    using CancellationTokenSource linked = CancellationTokenSource
                        .CreateLinkedTokenSource(stoppingToken, lease.LeaseLost);

                    await OrchestrateAsync(linked.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // Lease lost, not shutdown: stop acting instantly and rejoin the standby pool.
                LogLeaseLost(_logger);
            }
            catch (Exception ex)
            {
                LogOrchestratorError(_logger, ex);
                await Task.Delay(_options.MonitorInterval, _time, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task OrchestrateAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<StatementRun> active = await _runs.ListActiveAsync(cancellationToken).ConfigureAwait(false);

            foreach (StatementRun run in active)
            {
                switch (run.Status)
                {
                    case RunStatus.Planning:
                        await PlanAsync(run, cancellationToken).ConfigureAwait(false);
                        break;

                    case RunStatus.Running:
                    case RunStatus.Paused:
                        await MonitorAsync(run, cancellationToken).ConfigureAwait(false);
                        break;

                    default:
                        break;
                }
            }

            if (active.Count == 0)
            {
                _metrics.Projection(-1, 0);
            }

            await Task.Delay(_options.MonitorInterval, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PlanAsync(StatementRun run, CancellationToken cancellationToken)
    {
        using Activity? activity = ActivitySource.StartActivity("generation.plan-run");
        _ = activity?.SetTag("generation.run_id", run.Id);

        StatementPeriod period = StatementPeriod.Create(run.PeriodStart, run.PeriodEnd);
        string? traceParent = Activity.Current?.Id;
        long enqueued = 0;

        await foreach (IReadOnlyList<Guid> batch in _runs
            .StreamEligibleAccountsAsync(period, PlanBatchSize, cancellationToken).ConfigureAwait(false))
        {
            enqueued += await _runs.EnqueueBatchAsync(run.Id, batch, traceParent, cancellationToken)
                .ConfigureAwait(false);
        }

        long total = await _runs.MarkRunningAsync(run.Id, cancellationToken).ConfigureAwait(false);
        LogRunPlanned(_logger, run.Id, total, enqueued);
    }

    private async Task MonitorAsync(StatementRun run, CancellationToken cancellationToken)
    {
        RunCounters counters = await _runs
            .CountersAsync(run.Id, _options.MaxAttempts, cancellationToken).ConfigureAwait(false);

        // ---- Completion ------------------------------------------------------------------------
        if (run.TotalItems > 0 && counters.Terminal >= run.TotalItems)
        {
            if (await _runs.TransitionAsync(run.Id, run.Status, RunStatus.Completed, cancellationToken)
                .ConfigureAwait(false))
            {
                _metrics.Projection(-1, 0);
                _progressWindow.Clear();
                LogRunCompleted(_logger, run.Id, counters.Done, counters.FailedFinal);
            }

            return;
        }

        // ---- Pause / resume on the ledger breaker ----------------------------------------------
        // The breaker observed here is THIS replica's - the orchestrator shares a process with a
        // renderer, so it sees a representative ledger. Claim loops on every replica also stop
        // locally on their own breakers; the run-status flip is the operator-facing signal, not
        // the enforcement. ADR-0030 records this honestly.
        CircuitState circuit = _ledgerCircuit.CircuitState;

        if (run.Status == RunStatus.Running && circuit == CircuitState.Open)
        {
            if (await _runs.TransitionAsync(run.Id, RunStatus.Running, RunStatus.Paused, cancellationToken)
                .ConfigureAwait(false))
            {
                _metrics.RunPaused("ledger_circuit_open");
                LogRunPaused(_logger, run.Id);
            }

            return;
        }

        if (run.Status == RunStatus.Paused && circuit == CircuitState.Closed)
        {
            if (await _runs.TransitionAsync(run.Id, RunStatus.Paused, RunStatus.Running, cancellationToken)
                .ConfigureAwait(false))
            {
                _progressWindow.Clear(); // stale samples would poison the projection
                LogRunResumed(_logger, run.Id);
            }

            return;
        }

        // ---- Throughput and projection ---------------------------------------------------------
        long now = _time.GetTimestamp();
        _progressWindow.Enqueue((now, counters.Terminal));

        // Keep ~10 minutes of samples.
        while (_progressWindow.Count > 0
            && _time.GetElapsedTime(_progressWindow.Peek().Timestamp, now) > TimeSpan.FromMinutes(10))
        {
            _ = _progressWindow.Dequeue();
        }

        double itemsPerSecond = 0;
        if (_progressWindow.Count >= 2)
        {
            (long oldestTs, long oldestDone) = _progressWindow.Peek();
            double windowSeconds = _time.GetElapsedTime(oldestTs, now).TotalSeconds;
            if (windowSeconds > 0)
            {
                itemsPerSecond = (counters.Terminal - oldestDone) / windowSeconds;
            }
        }

        long remaining = Math.Max(0, run.TotalItems - counters.Terminal);
        long projectedSeconds = itemsPerSecond > 0 ? (long)(remaining / itemsPerSecond) : -1;
        _metrics.Projection(projectedSeconds, itemsPerSecond);

        // ---- Deadline alert --------------------------------------------------------------------
        // "62% done" tells an on-call nothing; "will finish 90 minutes late" tells them
        // everything. The alert is the projection crossing the deadline, not any percentage.
        DateTimeOffset deadline = run.DeadlineAt ?? run.CreatedAt.AddHours(_options.RunDeadlineHours);

        if (projectedSeconds >= 0)
        {
            DateTimeOffset projectedFinish = _time.GetUtcNow().AddSeconds(projectedSeconds);
            if (projectedFinish > deadline)
            {
                LogDeadlineAtRisk(
                    _logger, run.Id, (long)(projectedFinish - deadline).TotalMinutes,
                    counters.Terminal, run.TotalItems);
            }
        }
    }

    [LoggerMessage(EventId = 5020, Level = LogLevel.Information,
        Message = "Run {RunId} planned: {Total} items ({NewlyEnqueued} newly enqueued this pass)")]
    private static partial void LogRunPlanned(ILogger logger, Guid runId, long total, long newlyEnqueued);

    [LoggerMessage(EventId = 5021, Level = LogLevel.Information,
        Message = "Run {RunId} completed: {Done} done, {FailedFinal} quarantined")]
    private static partial void LogRunCompleted(ILogger logger, Guid runId, long done, long failedFinal);

    [LoggerMessage(EventId = 5022, Level = LogLevel.Warning,
        Message = "Run {RunId} PAUSED: ledger circuit open. Completed work is preserved; resuming when the breaker closes")]
    private static partial void LogRunPaused(ILogger logger, Guid runId);

    [LoggerMessage(EventId = 5023, Level = LogLevel.Information,
        Message = "Run {RunId} resumed: ledger circuit closed")]
    private static partial void LogRunResumed(ILogger logger, Guid runId);

    [LoggerMessage(EventId = 5024, Level = LogLevel.Warning,
        Message = "Run {RunId} projected to miss its deadline by {MinutesLate} minutes ({Terminal}/{Total} items)")]
    private static partial void LogDeadlineAtRisk(
        ILogger logger, Guid runId, long minutesLate, long terminal, long total);

    [LoggerMessage(EventId = 5025, Level = LogLevel.Warning,
        Message = "Orchestrator lease lost; rejoining the standby pool")]
    private static partial void LogLeaseLost(ILogger logger);

    [LoggerMessage(EventId = 5026, Level = LogLevel.Error, Message = "Orchestrator error; retrying")]
    private static partial void LogOrchestratorError(ILogger logger, Exception exception);
}
