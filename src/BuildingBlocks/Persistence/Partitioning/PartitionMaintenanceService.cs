using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using StatementDelivery.Persistence.Connections;
using StatementDelivery.Persistence.Leasing;

namespace StatementDelivery.Persistence.Partitioning;

/// <summary>
/// Pre-creates range partitions ahead of time so that an insert never arrives at a table with
/// nowhere to put it.
/// </summary>
/// <remarks>
/// <para>
/// Runs under <see cref="ILeaseManager"/>. Partition creation is idempotent, so concurrent runs
/// would be safe, but each one takes an ACCESS EXCLUSIVE lock on the parent table and N replicas
/// queueing for that lock every interval is a self-inflicted stall.
/// </para>
/// <para>
/// This service creates partitions. It does not verify them - that is
/// <see cref="PartitionHealthCheck"/>, which runs whether or not this service is enabled,
/// because a check that trusts the mechanism it is checking is not a check.
/// </para>
/// </remarks>
public sealed partial class PartitionMaintenanceService : BackgroundService
{
    private const string EnsureSql =
        "SELECT ensure_range_partitions(@table::regclass, @granularity, @periodsAhead);";

    private readonly IDbConnectionFactory _connections;
    private readonly ILeaseManager _leases;
    private readonly PartitionOptions _options;
    private readonly PartitionMetrics _metrics;
    private readonly ILogger<PartitionMaintenanceService> _logger;

    /// <summary>Initialises a new instance of the <see cref="PartitionMaintenanceService"/> class.</summary>
    /// <param name="connections">Connection factory.</param>
    /// <param name="leases">Lease manager used to elect a single maintainer.</param>
    /// <param name="options">Partition options.</param>
    /// <param name="metrics">Partition metrics.</param>
    /// <param name="logger">Logger.</param>
    public PartitionMaintenanceService(
        IDbConnectionFactory connections,
        ILeaseManager leases,
        IOptions<PartitionOptions> options,
        PartitionMetrics metrics,
        ILogger<PartitionMaintenanceService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _connections = connections;
        _leases = leases;
        _options = options.Value;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.MaintenanceEnabled)
        {
            LogMaintenanceDisabled(_logger);
            return;
        }

        if (_options.Tables.Count == 0)
        {
            LogNoTablesConfigured(_logger);
            return;
        }

        using var ticker = new PeriodicTimer(_options.Interval);

        do
        {
            try
            {
                await RunOnceUnderLeaseAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A scheduled maintenance loop must survive one bad run; the next tick retries.
            catch (Exception ex)
            {
                LogMaintenanceRunFailed(_logger, ex);
            }
#pragma warning restore CA1031
        }
        while (await ticker.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task RunOnceUnderLeaseAsync(CancellationToken stoppingToken)
    {
        await using ILeaseHandle? lease =
            await _leases.TryAcquireAsync(_options.LeaseName, stoppingToken).ConfigureAwait(false);

        if (lease is null)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.LeaseLost);

        foreach (PartitionedTableOptions table in _options.Tables)
        {
            linked.Token.ThrowIfCancellationRequested();

            await using NpgsqlConnection connection =
                await _connections.OpenAsync(ConnectionIntent.Write, linked.Token).ConfigureAwait(false);

            var command = new CommandDefinition(
                EnsureSql,
                new { table = table.Table, granularity = table.Granularity, periodsAhead = table.PeriodsAhead },
                commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
                cancellationToken: linked.Token);

            int created = await connection.ExecuteScalarAsync<int>(command).ConfigureAwait(false);
            _metrics.RecordCreated(table.Table, created);

            if (created > 0)
            {
                LogPartitionsCreated(_logger, created, table.Granularity, table.Table, table.PeriodsAhead);
            }
            else
            {
                LogPartitionsAlreadyAhead(_logger, table.Table, table.PeriodsAhead);
            }
        }
    }

    [LoggerMessage(
        EventId = 1300,
        Level = LogLevel.Information,
        Message = "Partition maintenance is disabled. The partitions-ready health check still runs, so a missing partition will still fail readiness.")]
    private static partial void LogMaintenanceDisabled(ILogger logger);

    [LoggerMessage(
        EventId = 1301,
        Level = LogLevel.Warning,
        Message = "Partition maintenance is enabled but no tables are configured. Nothing will be pre-created.")]
    private static partial void LogNoTablesConfigured(ILogger logger);

    [LoggerMessage(
        EventId = 1302,
        Level = LogLevel.Error,
        Message = "Partition maintenance run failed. Retrying at the next interval.")]
    private static partial void LogMaintenanceRunFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1303,
        Level = LogLevel.Information,
        Message = "Created {Created} {Granularity} partition(s) for {Table}, now covering {PeriodsAhead} period(s) ahead.")]
    private static partial void LogPartitionsCreated(ILogger logger, int created, string granularity, string table, int periodsAhead);

    [LoggerMessage(
        EventId = 1304,
        Level = LogLevel.Debug,
        Message = "Partitions for {Table} are already {PeriodsAhead} period(s) ahead.")]
    private static partial void LogPartitionsAlreadyAhead(ILogger logger, string table, int periodsAhead);
}
