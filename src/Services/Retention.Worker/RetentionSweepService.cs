using System.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;
using Retention.Worker.Configuration;
using StatementDelivery.Domain.Abstractions;
using StatementDelivery.Persistence.Leasing;
using StatementDelivery.Persistence.Retention;

namespace Retention.Worker;

/// <summary>
/// The retention sweep host loop, guarded by distributed leader election.
/// </summary>
/// <remarks>
/// <para>
/// WHY LEADER ELECTION AT ALL. With three replicas, a naive Timer runs the job three times - once
/// per replica, concurrently. For a read-only job that is merely wasteful. For THIS job, whose
/// whole purpose is to delete data and whose service holds the only database role with DELETE
/// rights, three concurrent runs mean three overlapping destructive passes over the same rows.
/// Exactly one replica may run this, and that is a correctness requirement, not an optimisation.
/// </para>
/// <para>
/// WHY A LEASE AND NOT AN ADVISORY LOCK. Every connection goes through PgBouncer in transaction
/// pooling mode, so a session advisory lock is taken on one backend and released against another,
/// and leaks forever. The lease table works through any pooler, is observable with a SELECT, and
/// issues a monotonic fence token. See docs/adr/0008-pgbouncer-transaction-pooling.md.
/// </para>
/// <para>
/// WHY THE FENCE TOKEN MATTERS HERE MORE THAN ANYWHERE. A time-to-live alone cannot stop a leader
/// that was paused by a long garbage collection or a hypervisor stall from waking up after its
/// lease has been taken over and carrying on. It has no way to know it was paused. Passing the
/// fence token to whatever the destructive work writes, and having that reject a token lower than
/// the highest already seen, is the only thing that closes that window - and here the consequence
/// of not closing it is deleted customer data.
/// </para>
/// </remarks>
public sealed partial class RetentionSweepService : BackgroundService
{
    /// <summary>
    /// Activity source for spans this worker starts. Matched by the <c>StatementDelivery.*</c>
    /// wildcard registered in ServiceDefaults.
    /// </summary>
    private static readonly ActivitySource ActivitySource = new("StatementDelivery.Retention");

    private readonly ILeaseManager _leases;
    private readonly RetentionWorkerOptions _options;
    private readonly PurgePass _purge;
    private readonly ErasureExecutor _erasure;
    private readonly RestoreCompleter _restores;
    private readonly ArchivePass _archive;
    private readonly OrphanSweep _orphans;
    private readonly ReconciliationPass _reconciliation;
    private readonly ReconciliationRepository _reconciliationRuns;
    private readonly IIdGenerator _ids;
    private readonly TimeProvider _time;
    private readonly ILogger<RetentionSweepService> _logger;

    // Cadence state, leader-local by design: a new leader running a daily job slightly early
    // after a handover is harmless (every pass is idempotent and bounded), and persisting
    // schedules would add a table to solve a problem idempotency already solved.
    private DateTimeOffset _lastDailyPass = DateTimeOffset.MinValue;
    private DateTimeOffset _lastErasurePass = DateTimeOffset.MinValue;
    private DateTimeOffset _lastOrphanWindow = DateTimeOffset.MinValue;
    private DateTimeOffset _lastReconciliationEnqueue = DateTimeOffset.MinValue;

    /// <summary>Initialises a new instance of the <see cref="RetentionSweepService"/> class.</summary>
    /// <param name="leases">Lease manager used to elect a single leader.</param>
    /// <param name="options">Worker options.</param>
    /// <param name="purge">The daily purge.</param>
    /// <param name="erasure">The erasure executor.</param>
    /// <param name="restores">The restore completer.</param>
    /// <param name="archive">The archive transition.</param>
    /// <param name="orphans">The orphan sweep.</param>
    /// <param name="reconciliation">The reconciliation pass.</param>
    /// <param name="reconciliationRuns">The run queue, for the daily auto-enqueue.</param>
    /// <param name="ids">Id generator.</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Logger.</param>
    public RetentionSweepService(
        ILeaseManager leases,
        IOptions<RetentionWorkerOptions> options,
        PurgePass purge,
        ErasureExecutor erasure,
        RestoreCompleter restores,
        ArchivePass archive,
        OrphanSweep orphans,
        ReconciliationPass reconciliation,
        ReconciliationRepository reconciliationRuns,
        IIdGenerator ids,
        TimeProvider time,
        ILogger<RetentionSweepService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _leases = leases;
        _options = options.Value;
        _purge = purge;
        _erasure = erasure;
        _restores = restores;
        _archive = archive;
        _orphans = orphans;
        _reconciliation = reconciliation;
        _reconciliationRuns = reconciliationRuns;
        _ids = ids;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(_logger, _options.LeaseName, _options.SweepIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            ILeaseHandle? lease = await AcquireLeadershipAsync(stoppingToken).ConfigureAwait(false);
            if (lease is null)
            {
                return;
            }

            await using (lease.ConfigureAwait(false))
            {
                // Linked, so the sweep loop stops for either reason: the host is shutting down, OR
                // this replica has lost leadership and another one has taken over. Watching only
                // the stopping token would leave two leaders running concurrently, which is the
                // exact failure the lease exists to prevent.
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.LeaseLost);

                await RunSweepsAsync(lease, linked.Token).ConfigureAwait(false);
            }

            // Falling through here means leadership was lost rather than the host stopping. Going
            // back to standby rather than exiting matters: a replica that gives up on its first
            // lost lease is a replica that can never become leader again, so a cluster that has
            // flapped once ends up with nobody eligible.
            if (!stoppingToken.IsCancellationRequested)
            {
                LogReturningToStandby(_logger);
            }
        }
    }

    private async Task<ILeaseHandle?> AcquireLeadershipAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            ILeaseHandle? lease;
            try
            {
                lease = await _leases.TryAcquireAsync(_options.LeaseName, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (NpgsqlException ex)
            {
                // Not fatal. The database being briefly unreachable is not evidence that this
                // replica should stop trying to become leader.
                LogElectionUnreachable(_logger, ex, _options.LeaderElectionRetrySeconds);
                lease = null;
            }

            if (lease is not null)
            {
                return lease;
            }

            LogStandby(_logger, _options.LeaseName, _options.LeaderElectionRetrySeconds);

            try
            {
                await Task.Delay(_options.LeaderElectionRetry, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        return null;
    }

    private async Task RunSweepsAsync(ILeaseHandle lease, CancellationToken cancellationToken)
    {
        using var ticker = new PeriodicTimer(_options.SweepInterval);

        do
        {
            try
            {
                await RunSweepAsync(lease, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
#pragma warning disable CA1031 // A scheduled sweep must survive one bad run; the next tick retries.
            catch (Exception ex)
            {
                LogSweepFailed(_logger, ex);
            }
#pragma warning restore CA1031

            try
            {
                if (!await ticker.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        while (true);
    }

    private async Task RunSweepAsync(ILeaseHandle lease, CancellationToken cancellationToken)
    {
        using Activity? activity = ActivitySource.StartActivity("retention.sweep", ActivityKind.Internal);
        activity?.SetTag("retention.lease_name", lease.LeaseName);
        activity?.SetTag("retention.fence_token", lease.FenceToken);

        DateTimeOffset now = _time.GetUtcNow();

        // EVERY TICK: a due restore completes on schedule; a staff-requested reconciliation
        // run does not wait for tomorrow.
        int restored = await _restores.RunAsync(lease.FenceToken, cancellationToken).ConfigureAwait(false);
        _ = await _reconciliation.RunAsync(cancellationToken).ConfigureAwait(false);

        // DAILY, ON ITS OWN INTERVAL: the erasure executor. The brief calls it a daily job and
        // the blocked-request audit cadence assumes it (V022) - running it every tick was 288
        // ERASURE_BLOCKED appends per day per blocked request. The cooling-off window is seven
        // days; up to a day of execution slack is noise.
        int erased = 0;
        if (now - _lastErasurePass >= TimeSpan.FromHours(_options.ErasureIntervalHours))
        {
            erased = await _erasure.RunAsync(lease.FenceToken, cancellationToken).ConfigureAwait(false);
            _lastErasurePass = now;
        }

        // DAILY: the purge and the archive transition. Idempotent and bounded, so a leadership
        // handover running them early costs nothing but a small batch.
        int purged = 0;
        int archived = 0;
        if (now - _lastDailyPass >= TimeSpan.FromHours(_options.DailyJobIntervalHours))
        {
            purged = await _purge.RunAsync(lease.FenceToken, cancellationToken).ConfigureAwait(false);
            archived = await _archive.RunAsync(lease.FenceToken, cancellationToken).ConfigureAwait(false);
            _lastDailyPass = now;
        }

        // DAILY: the scheduled reconciliation run, queued through the same queue the API uses so
        // GET /latest cannot tell them apart.
        if (now - _lastReconciliationEnqueue >= TimeSpan.FromHours(_options.DailyJobIntervalHours))
        {
            await _reconciliationRuns.EnqueueAsync(_ids.NewId(), requestedBy: null, cancellationToken)
                .ConfigureAwait(false);
            _lastReconciliationEnqueue = now;
        }

        // WEEKLY window: the orphan sweep, resuming its saved cursor; a bounded number of pages
        // per tick, so one full 256-shard cycle spreads across the window.
        int orphans = 0;
        if (now - _lastOrphanWindow >= TimeSpan.FromDays(_options.OrphanSweepIntervalDays))
        {
            orphans = await _orphans.RunAsync(cancellationToken).ConfigureAwait(false);
            _lastOrphanWindow = now;
        }

        LogSweepCompleted(_logger, lease.FenceToken, purged, archived, erased, restored, orphans);
    }

    [LoggerMessage(
        EventId = 4000,
        Level = LogLevel.Information,
        Message = "Retention worker started. Contending for lease {LeaseName}; sweeps run every {SweepIntervalSeconds}s once elected.")]
    private static partial void LogStarted(ILogger logger, string leaseName, int sweepIntervalSeconds);

    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Information,
        Message = "Standby: lease {LeaseName} is held by another replica. Retrying in {RetrySeconds}s. This replica will run no retention work.")]
    private static partial void LogStandby(ILogger logger, string leaseName, int retrySeconds);

    [LoggerMessage(
        EventId = 4002,
        Level = LogLevel.Debug,
        Message = "Retention sweep completed under fence token {FenceToken}: purged={Purged}, archived={Archived}, erased={Erased}, restored={Restored}, orphansReported={Orphans}.")]
    private static partial void LogSweepCompleted(
        ILogger logger, long fenceToken, int purged, int archived, int erased, int restored, int orphans);

    [LoggerMessage(
        EventId = 4003,
        Level = LogLevel.Error,
        Message = "Retention sweep failed. Retrying at the next interval.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 4004,
        Level = LogLevel.Warning,
        Message = "Leadership lost. Returning to standby and contending for the lease again.")]
    private static partial void LogReturningToStandby(ILogger logger);

    [LoggerMessage(
        EventId = 4005,
        Level = LogLevel.Warning,
        Message = "Could not reach the database to contend for leadership. Retrying in {RetrySeconds}s.")]
    private static partial void LogElectionUnreachable(ILogger logger, Exception exception, int retrySeconds);
}
