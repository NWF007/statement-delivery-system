using System.Diagnostics.Metrics;

namespace Retention.Worker;

/// <summary>Every signal the retention lifecycle emits.</summary>
/// <remarks>
/// The gauge to alert on is <c>statement_eligible_for_purge_total{blocked="none"}</c>: climbing
/// steadily means the purge worker has stalled, and the symptom without it is a slowly rising
/// storage bill rather than an error anywhere. The legal_hold and object_lock series are the
/// system OBEYING the law - a large litigation hold must not read as a stalled worker (H2).
/// Lock expiry is not deletion; this worker is the deleter, and this gauge is how you notice it
/// stopped.
/// </remarks>
public sealed class RetentionMetrics : IDisposable
{
    /// <summary>The meter name, matched by the ServiceDefaults wildcard registration.</summary>
    public const string MeterName = "StatementDelivery.Retention";

    /// <summary>Purge outcome labels — a closed set.</summary>
    public static class PurgeOutcome
    {
        /// <summary>Versions deleted, row marked.</summary>
        public const string Purged = "purged";

        /// <summary>Skipped: an active legal hold.</summary>
        public const string SkippedLegalHold = "skipped_legal_hold";

        /// <summary>Skipped: the object store's lock has not expired.</summary>
        public const string SkippedObjectLock = "skipped_object_lock";

        /// <summary>The database date and the engine disagreed. A data-integrity page.</summary>
        public const string DateDisagreement = "date_disagreement";

        /// <summary>The key was already destroyed; bookkeeping only.</summary>
        public const string AlreadyErased = "already_erased";
    }

    private readonly Meter _meter;
    private readonly Counter<long> _purged;
    private readonly Counter<long> _erasures;
    private readonly Counter<long> _restores;
    private readonly Counter<long> _archived;
    private readonly Counter<long> _orphans;
    private readonly Counter<long> _orphanBytes;
    private readonly Counter<long> _reconciliationFindings;
    private readonly Counter<long> _erasedUnderHold;
    private readonly Counter<long> _staleReconciliationRuns;
    private long _eligibleUnblocked;
    private long _eligibleHoldBlocked;
    private long _eligibleLockBlocked;

    /// <summary>Initialises a new instance of the <see cref="RetentionMetrics"/> class.</summary>
    /// <param name="meterFactory">Meter factory.</param>
    public RetentionMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _meter = meterFactory.Create(MeterName);

        _purged = _meter.CreateCounter<long>(
            "statement_purged_total",
            unit: "{statement}",
            description: "Purge sweep outcomes, by outcome label. Skips are outcomes too - a refusal is a decision.");

        _erasures = _meter.CreateCounter<long>(
            "erasure_completed_total",
            unit: "{customer}",
            description: "Crypto-erasures executed. Each one is irreversible and audited.");

        _restores = _meter.CreateCounter<long>(
            "restore_completed_total",
            unit: "{restore}",
            description: "Archive restores completed.");

        _archived = _meter.CreateCounter<long>(
            "statement_archived_total",
            unit: "{statement}",
            description: "Statements transitioned to the cold tier.");

        _orphans = _meter.CreateCounter<long>(
            "storage_orphan_total",
            unit: "{object}",
            description: "Objects no statement row accounts for. Report-only; see ADR-0039.");

        _orphanBytes = _meter.CreateCounter<long>(
            "storage_orphan_bytes",
            unit: "By",
            description: "Bytes of unaccounted storage - the quantified exposure, not a shrug.");

        _reconciliationFindings = _meter.CreateCounter<long>(
            "reconciliation_findings_total",
            unit: "{finding}",
            description: "Reconciliation findings by check and severity. Alert on any CRITICAL.");

        _erasedUnderHold = _meter.CreateCounter<long>(
            "retention_erased_under_hold_total",
            unit: "{statement}",
            description: "Statements observed ERASED while under an ACTIVE hold. Should be impossible (erasure is hold-blocked upstream); ANY non-zero value pages.");

        _staleReconciliationRuns = _meter.CreateCounter<long>(
            "reconciliation_run_stale_total",
            unit: "{run}",
            description: "Reconciliation runs found stuck RUNNING and reaped to FAILED. A dead or cancelled leader left them behind.");

        // Split by block reason (H2): a large litigation hold used to read as a stalled purge
        // worker. Alert on blocked="none" ONLY - the held and locked series are the system
        // obeying the law, not failing to work.
        _ = _meter.CreateObservableGauge(
            "statement_eligible_for_purge_total",
            () => new[]
            {
                new Measurement<long>(
                    Volatile.Read(ref _eligibleUnblocked),
                    new KeyValuePair<string, object?>("blocked", "none")),
                new Measurement<long>(
                    Volatile.Read(ref _eligibleHoldBlocked),
                    new KeyValuePair<string, object?>("blocked", "legal_hold")),
                new Measurement<long>(
                    Volatile.Read(ref _eligibleLockBlocked),
                    new KeyValuePair<string, object?>("blocked", "object_lock")),
            },
            unit: "{statement}",
            description: "Statements past retain_until and not yet purged, by why they remain. blocked=none climbing steadily = the purge worker has stalled (ALERT); legal_hold comes from the database; object_lock is observed by the most recent pass, since only the store knows its locks.");
    }

    /// <summary>Records one purge-sweep outcome.</summary>
    /// <param name="outcome">One of <see cref="PurgeOutcome"/>.</param>
    public void Purged(string outcome) =>
        _purged.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>Records a completed erasure.</summary>
    public void ErasureCompleted() => _erasures.Add(1);

    /// <summary>Records a completed restore.</summary>
    public void RestoreCompleted() => _restores.Add(1);

    /// <summary>Records archived statements.</summary>
    /// <param name="count">How many.</param>
    public void Archived(int count) => _archived.Add(count);

    /// <summary>Records one orphan.</summary>
    /// <param name="sizeBytes">Its size.</param>
    public void Orphan(long sizeBytes)
    {
        _orphans.Add(1);
        _orphanBytes.Add(sizeBytes);
    }

    /// <summary>Records the impossible state: an erased statement under an active hold.</summary>
    public void ErasedUnderHold() => _erasedUnderHold.Add(1);

    /// <summary>Records one reconciliation finding.</summary>
    /// <param name="check">The check name.</param>
    /// <param name="severity">CRITICAL, WARNING or INFO.</param>
    public void ReconciliationFinding(string check, string severity) =>
        _reconciliationFindings.Add(
            1,
            new KeyValuePair<string, object?>("check", check),
            new KeyValuePair<string, object?>("severity", severity));

    /// <summary>Publishes the eligible-for-purge gauge, split by block reason.</summary>
    /// <param name="unblocked">Eligible with no known block - the series to alert on.</param>
    /// <param name="holdBlocked">Eligible but under an active hold (database).</param>
    /// <param name="lockBlocked">Eligible but refused by the store's lock in the most recent pass.</param>
    public void EligibleForPurge(long unblocked, long holdBlocked, long lockBlocked)
    {
        Volatile.Write(ref _eligibleUnblocked, unblocked);
        Volatile.Write(ref _eligibleHoldBlocked, holdBlocked);
        Volatile.Write(ref _eligibleLockBlocked, lockBlocked);
    }

    /// <summary>Records reaped stale reconciliation runs.</summary>
    /// <param name="count">How many.</param>
    public void ReconciliationRunsStale(int count) => _staleReconciliationRuns.Add(count);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
