using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Retention.Worker.Configuration;
using StatementDelivery.Persistence.Connections;
using StatementDelivery.Persistence.Leasing;

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

    private readonly IDbConnectionFactory _connections;
    private readonly ILeaseManager _leases;
    private readonly RetentionWorkerOptions _options;
    private readonly ILogger<RetentionSweepService> _logger;

    /// <summary>Initialises a new instance of the <see cref="RetentionSweepService"/> class.</summary>
    /// <param name="connections">Connection factory.</param>
    /// <param name="leases">Lease manager used to elect a single leader.</param>
    /// <param name="options">Worker options.</param>
    /// <param name="logger">Logger.</param>
    public RetentionSweepService(
        IDbConnectionFactory connections,
        ILeaseManager leases,
        IOptions<RetentionWorkerOptions> options,
        ILogger<RetentionSweepService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _connections = connections;
        _leases = leases;
        _options = options.Value;
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

        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.Write, cancellationToken).ConfigureAwait(false);

        // TODO(retention): drop expired partitions, honour legal holds, and process erasure
        // requests - each one guarded by lease.FenceToken so a stalled former leader cannot act on
        // a lease it no longer holds. Until then the sweep proves the leader-elected path works end
        // to end: exactly one replica reaches this line, on a connection through PgBouncer.
        int probe = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT 1;",
                commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        LogSweepCompleted(_logger, lease.FenceToken, probe);
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
        Message = "Retention sweep completed under fence token {FenceToken} (probe={Probe}). Nothing is due for retention yet.")]
    private static partial void LogSweepCompleted(ILogger logger, long fenceToken, int probe);

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
