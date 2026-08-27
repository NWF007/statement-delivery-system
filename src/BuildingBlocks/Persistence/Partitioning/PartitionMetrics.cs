using System.Diagnostics.Metrics;

namespace StatementDelivery.Persistence.Partitioning;

/// <summary>
/// Instruments for range-partition maintenance.
/// </summary>
/// <remarks>
/// Registered with the OpenTelemetry meter provider in ServiceDefaults via
/// <see cref="MeterName"/>. A metric nobody exports is a metric nobody alerts on.
/// </remarks>
public sealed class PartitionMetrics : IDisposable
{
    /// <summary>
    /// The meter name. ServiceDefaults adds this to the OpenTelemetry metrics pipeline.
    /// </summary>
    public const string MeterName = "StatementDelivery.Persistence";

    private readonly Meter _meter;
    private readonly Counter<long> _missing;
    private readonly Counter<long> _created;

    /// <summary>Initialises a new instance of the <see cref="PartitionMetrics"/> class.</summary>
    /// <param name="meterFactory">Meter factory from dependency injection.</param>
    public PartitionMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _meter = meterFactory.Create(MeterName);

        // Named exactly partition_missing_total because that is the name the runbook and the
        // alert rule use. Note for whoever wires a Prometheus scrape later: the OTLP-to-Prometheus
        // translation appends _total to counters, so pin the exported name rather than letting it
        // become partition_missing_total_total.
        _missing = _meter.CreateCounter<long>(
            "partition_missing_total",
            unit: "{partition}",
            description: "Range partitions found missing by the readiness health check. Any non-zero value is an impending write outage.");

        _created = _meter.CreateCounter<long>(
            "partition_created_total",
            unit: "{partition}",
            description: "Range partitions created by the maintenance service.");
    }

    /// <summary>Records that a required partition was absent.</summary>
    /// <param name="table">The parent table.</param>
    public void RecordMissing(string table) =>
        _missing.Add(1, new KeyValuePair<string, object?>("table", table));

    /// <summary>Records partitions created for a table.</summary>
    /// <param name="table">The parent table.</param>
    /// <param name="count">How many partitions were created.</param>
    public void RecordCreated(string table, int count)
    {
        if (count > 0)
        {
            _created.Add(count, new KeyValuePair<string, object?>("table", table));
        }
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
