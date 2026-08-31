using System.Diagnostics.Metrics;
using Polly.CircuitBreaker;

namespace Generation.Worker;

/// <summary>
/// Every signal the batch subsystem emits. Part I of the brief, instrument for instrument.
/// </summary>
/// <remarks>
/// <para>
/// CARDINALITY: <c>run_id</c> is a safe label - twelve values a year. <c>account_id</c> and
/// <c>statement_id</c> are NOT and never appear on a metric; thirty million label values is a
/// telemetry-backend outage with extra steps. Stage and reason labels come from small closed
/// sets defined here.
/// </para>
/// <para>
/// Stage-level timing is the operable part: "renders are slow" could be the ledger, QuestPDF,
/// KMS or S3, and without per-stage histograms every incident starts with a wrong guess.
/// </para>
/// </remarks>
public sealed class GenerationMetrics : IDisposable
{
    /// <summary>The meter name, matched by the ServiceDefaults wildcard registration.</summary>
    public const string MeterName = "StatementDelivery.Generation";

    /// <summary>The pipeline stages, as duration labels.</summary>
    public static class Stage
    {
        /// <summary>Fetching transactions from the ledger.</summary>
        public const string Ledger = "ledger";

        /// <summary>Rendering the PDF.</summary>
        public const string Render = "render";

        /// <summary>Encrypting and uploading. One stage, because the pipeline streams them as one pass.</summary>
        public const string EncryptUpload = "encrypt_upload";

        /// <summary>The finalising database transaction.</summary>
        public const string Finalize = "finalize";
    }

    private readonly Meter _meter;
    private readonly Counter<long> _itemsCompleted;
    private readonly Counter<long> _itemsFailed;
    private readonly Counter<long> _itemsReaped;
    private readonly Counter<long> _runsPaused;
    private readonly Counter<long> _staleCompletions;
    private readonly Histogram<double> _stageDuration;
    private readonly Histogram<double> _ledgerDuration;

    private long _projectedCompletionSeconds = -1;
    private double _throughput;
    private long _outboxOldestUnpublishedSeconds;

    /// <summary>Initialises a new instance of the <see cref="GenerationMetrics"/> class.</summary>
    /// <param name="meterFactory">Meter factory.</param>
    /// <param name="circuitState">The ledger breaker's state, observed as a gauge.</param>
    public GenerationMetrics(IMeterFactory meterFactory, CircuitBreakerStateProvider circuitState)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        ArgumentNullException.ThrowIfNull(circuitState);

        _meter = meterFactory.Create(MeterName);

        _itemsCompleted = _meter.CreateCounter<long>(
            "generation_run_items_completed_total",
            unit: "{item}",
            description: "Run items that reached DONE. Labelled by run_id (bounded: 12/year).");

        _itemsFailed = _meter.CreateCounter<long>(
            "generation_item_failed_total",
            unit: "{item}",
            description: "Failed attempts, by reason. A rising single reason is a systemic cause, not bad luck.");

        _itemsReaped = _meter.CreateCounter<long>(
            "generation_items_reaped_total",
            unit: "{item}",
            description: "Stale claims returned to the queue. A steady non-zero rate means workers are dying - alert on it independently of run progress.");

        _runsPaused = _meter.CreateCounter<long>(
            "generation_run_paused_total",
            unit: "{pause}",
            description: "Times a run was paused, by reason. Pausing preserves completed work; failing throws it away.");

        _staleCompletions = _meter.CreateCounter<long>(
            "generation_stale_completion_total",
            unit: "{item}",
            description: "Renders that finished after their claim was reaped and reclaimed. A non-zero rate means the stale-claim window is shorter than real render times - lengthen Generation:StaleClaimMinutes.");

        _stageDuration = _meter.CreateHistogram<double>(
            "generation_item_duration_seconds",
            unit: "s",
            description: "Per-item time by stage: ledger | render | encrypt_upload | finalize. The where-is-it-slow signal.");

        _ledgerDuration = _meter.CreateHistogram<double>(
            "ledger_request_duration_seconds",
            unit: "s",
            description: "Ledger round trips as the worker experienced them, retries included.");

        _ = _meter.CreateObservableGauge(
            "generation_run_projected_completion_seconds",
            () => Volatile.Read(ref _projectedCompletionSeconds),
            unit: "s",
            description: "Seconds until the active run finishes at current throughput. THE operable number: '62% done' tells an on-call nothing; 'finishes 90 minutes late' tells them everything. -1 = no active run.");

        _ = _meter.CreateObservableGauge(
            "generation_throughput_items_per_second",
            () => Volatile.Read(ref _throughput),
            unit: "{item}/s",
            description: "Rolling completion rate the projection is computed from.");

        _ = _meter.CreateObservableGauge(
            "ledger_circuit_state",
            () => (long)circuitState.CircuitState,
            description: "0 closed, 1 open, 2 half-open, 3 isolated (Polly's enum order). Alert on any sustained non-zero.");

        _ = _meter.CreateObservableGauge(
            "outbox_unpublished_age_seconds",
            () => Volatile.Read(ref _outboxOldestUnpublishedSeconds),
            unit: "s",
            description: "Age of the oldest unpublished outbox row. Rising = the relay has stalled - the earliest signal, well before anyone misses a notification.");
    }

    /// <summary>Records a completed item.</summary>
    /// <param name="runId">The run. Bounded cardinality: monthly.</param>
    public void ItemCompleted(Guid runId) =>
        _itemsCompleted.Add(1, new KeyValuePair<string, object?>("run_id", runId.ToString("D")));

    /// <summary>Records a failed attempt, by closed-set reason.</summary>
    /// <param name="reason">The failure class - an exception TYPE name, never a message.</param>
    public void ItemFailed(string reason) =>
        _itemsFailed.Add(1, new KeyValuePair<string, object?>("reason", reason));

    /// <summary>Records a render that outlived its own claim.</summary>
    public void StaleCompletion() => _staleCompletions.Add(1);

    /// <summary>Records reaped claims.</summary>
    /// <param name="count">How many.</param>
    public void ItemsReaped(int count) => _itemsReaped.Add(count);

    /// <summary>Records a run pause.</summary>
    /// <param name="reason">Why, for example ledger_circuit_open.</param>
    public void RunPaused(string reason) =>
        _runsPaused.Add(1, new KeyValuePair<string, object?>("reason", reason));

    /// <summary>Records one stage's duration for one item.</summary>
    /// <param name="stage">One of <see cref="Stage"/>.</param>
    /// <param name="seconds">Elapsed seconds.</param>
    public void StageCompleted(string stage, double seconds) =>
        _stageDuration.Record(seconds, new KeyValuePair<string, object?>("stage", stage));

    /// <summary>Records one ledger conversation's duration.</summary>
    /// <param name="seconds">Elapsed seconds, retries included.</param>
    public void LedgerRequest(double seconds) => _ledgerDuration.Record(seconds);

    /// <summary>Publishes the monitor's latest projection.</summary>
    /// <param name="projectedSeconds">Seconds to completion, or -1 for no active run.</param>
    /// <param name="itemsPerSecond">The rolling throughput behind it.</param>
    public void Projection(long projectedSeconds, double itemsPerSecond)
    {
        Volatile.Write(ref _projectedCompletionSeconds, projectedSeconds);
        Volatile.Write(ref _throughput, itemsPerSecond);
    }

    /// <summary>Publishes the relay's oldest-unpublished age.</summary>
    /// <param name="seconds">Age in seconds; zero when the outbox is drained.</param>
    public void OutboxAge(long seconds) => Volatile.Write(ref _outboxOldestUnpublishedSeconds, seconds);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
