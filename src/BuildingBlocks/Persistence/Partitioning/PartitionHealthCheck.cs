using System.Globalization;
using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using StatementDelivery.Persistence.Connections;

namespace StatementDelivery.Persistence.Partitioning;

/// <summary>
/// Readiness check asserting that every configured range-partitioned table has a partition
/// covering one full period beyond now.
/// </summary>
/// <remarks>
/// <para>
/// Registered with the <c>ready</c> tag, never <c>live</c>. A missing partition means writes are
/// about to start failing, so this instance should be taken out of the load balancer - but the
/// process is perfectly healthy and restarting it would fix nothing while making the outage
/// louder.
/// </para>
/// <para>
/// This verifies the OUTCOME rather than trusting the mechanism. It stays in place whether
/// partitions are created by <see cref="PartitionMaintenanceService"/>, by pg_partman, or by an
/// operator with psql.
/// </para>
/// </remarks>
public sealed class PartitionHealthCheck : IHealthCheck
{
    /// <summary>The registered name of this check.</summary>
    public const string Name = "partitions-ready";

    private const string ExistsSql =
        "SELECT range_partition_exists(@table::regclass, @granularity, @at);";

    private readonly IDbConnectionFactory _connections;
    private readonly PartitionOptions _options;
    private readonly PartitionMetrics _metrics;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initialises a new instance of the <see cref="PartitionHealthCheck"/> class.</summary>
    /// <param name="connections">Connection factory.</param>
    /// <param name="options">Partition options.</param>
    /// <param name="metrics">Partition metrics.</param>
    /// <param name="timeProvider">Time source, substitutable in tests.</param>
    public PartitionHealthCheck(
        IDbConnectionFactory connections,
        IOptions<PartitionOptions> options,
        PartitionMetrics metrics,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);

        _connections = connections;
        _options = options.Value;
        _metrics = metrics;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (_options.Tables.Count == 0)
        {
            return HealthCheckResult.Healthy("No partitioned tables are configured.");
        }

        var missing = new List<string>();
        DateTimeOffset now = _timeProvider.GetUtcNow();

        try
        {
            await using NpgsqlConnection connection =
                await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

            foreach (PartitionedTableOptions table in _options.Tables)
            {
                DateTimeOffset probeAt = table.Granularity switch
                {
                    "day" => now.AddDays(1),
                    "month" => now.AddMonths(1),
                    _ => now,
                };

                var command = new CommandDefinition(
                    ExistsSql,
                    new { table = table.Table, granularity = table.Granularity, at = probeAt },
                    commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
                    cancellationToken: cancellationToken);

                bool exists = await connection.ExecuteScalarAsync<bool>(command).ConfigureAwait(false);
                if (!exists)
                {
                    _metrics.RecordMissing(table.Table);
                    missing.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{table.Table} has no {table.Granularity} partition covering {probeAt:yyyy-MM-dd}"));
                }
            }
        }
        catch (NpgsqlException ex)
        {
            return HealthCheckResult.Unhealthy("Could not verify range partitions.", ex);
        }

        return missing.Count == 0
            ? HealthCheckResult.Healthy("All configured range partitions cover the next period.")
            : HealthCheckResult.Unhealthy(
                "Range partitions are missing; writes to these tables will fail. " + string.Join("; ", missing));
    }
}
