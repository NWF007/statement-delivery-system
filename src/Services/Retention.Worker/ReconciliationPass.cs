using System.Globalization;
using Microsoft.Extensions.Options;
using Retention.Worker.Configuration;
using StatementDelivery.Domain.Auditing;
using StatementDelivery.Persistence.Auditing;
using StatementDelivery.Persistence.Retention;
using StatementDelivery.ServiceDefaults.Storage;

namespace Retention.Worker;

/// <summary>The daily proof that the database and the object store still agree.</summary>
/// <remarks>
/// Six checks, every one bounded. Check 6 is the one that matters most: an erasure the system
/// BELIEVES completed but did not is a regulatory failure invisible until someone audits it —
/// exactly the failure mode a reconciliation job exists to catch. Findings are rows, not log
/// lines, because <c>GET /v1/admin/reconciliation/latest</c> has to show them; the metric fires
/// per finding so any CRITICAL alerts without anyone polling.
/// </remarks>
public sealed partial class ReconciliationPass
{
    private static class Check
    {
        public const string MissingObject = "MISSING_OBJECT";
        public const string OrphanedObject = "ORPHANED_OBJECT";
        public const string LegalHoldDrift = "LEGAL_HOLD_DRIFT";
        public const string RetentionDrift = "RETENTION_DRIFT";
        public const string AuditChain = "AUDIT_CHAIN";
        public const string IncompleteErasure = "INCOMPLETE_ERASURE";
    }

    private readonly ReconciliationRepository _repository;
    private readonly RetentionSweepRepository _statements;
    private readonly LegalHoldRepository _holds;
    private readonly IStatementObjectAdmin _objects;
    private readonly IAuditVerifier _auditVerifier;
    private readonly AuditOptions _auditOptions;
    private readonly RetentionMetrics _metrics;
    private readonly RetentionWorkerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ReconciliationPass> _logger;

    /// <summary>Initialises a new instance of the <see cref="ReconciliationPass"/> class.</summary>
    /// <param name="repository">Run queue and findings.</param>
    /// <param name="statements">Statement samples.</param>
    /// <param name="holds">Hold listings for the drift check.</param>
    /// <param name="objects">The object store's admin surface.</param>
    /// <param name="auditVerifier">The chain verifier.</param>
    /// <param name="auditOptions">Chain count.</param>
    /// <param name="metrics">Metrics.</param>
    /// <param name="options">Worker options.</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Logger.</param>
    public ReconciliationPass(
        ReconciliationRepository repository,
        RetentionSweepRepository statements,
        LegalHoldRepository holds,
        IStatementObjectAdmin objects,
        IAuditVerifier auditVerifier,
        IOptions<AuditOptions> auditOptions,
        RetentionMetrics metrics,
        IOptions<RetentionWorkerOptions> options,
        TimeProvider time,
        ILogger<ReconciliationPass> logger)
    {
        ArgumentNullException.ThrowIfNull(auditOptions);
        ArgumentNullException.ThrowIfNull(options);
        _repository = repository;
        _statements = statements;
        _holds = holds;
        _objects = objects;
        _auditVerifier = auditVerifier;
        _auditOptions = auditOptions.Value;
        _metrics = metrics;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>Claims and executes at most one requested run.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when a run executed.</returns>
    public async Task<bool> RunAsync(CancellationToken cancellationToken)
    {
        // H1 first: reap runs a dead or cancelled leader left RUNNING, so the queue and
        // GET /latest tell the truth before this pass adds to them.
        int stale = await _repository.ReapStaleRunsAsync(cancellationToken).ConfigureAwait(false);
        if (stale > 0)
        {
            _metrics.ReconciliationRunsStale(stale);
        }

        ReconciliationRunRow? run = await _repository.ClaimNextAsync(cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return false;
        }

        int critical = 0;
        try
        {
            critical += await CheckObjectsAsync(run.Id, cancellationToken).ConfigureAwait(false);
            critical += await CheckOrphansAsync(run.Id, cancellationToken).ConfigureAwait(false);
            critical += await CheckLegalHoldDriftAsync(run.Id, cancellationToken).ConfigureAwait(false);
            critical += await CheckAuditChainsAsync(run.Id, cancellationToken).ConfigureAwait(false);
            critical += await CheckIncompleteErasureAsync(run.Id, cancellationToken).ConfigureAwait(false);

            await _repository.CompleteAsync(run.Id, failed: false, cancellationToken).ConfigureAwait(false);
            LogCompleted(_logger, run.Id, critical);
        }
        catch (OperationCanceledException)
        {
            // Shutdown mid-pass: record the abort so the run is not stuck RUNNING until the
            // stale reaper notices it an hour later. CancellationToken.None deliberately - the
            // bookkeeping write must survive the very cancellation it records.
            await _repository.CompleteAsync(run.Id, failed: true, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
#pragma warning disable CA1031 // A failed pass is recorded FAILED and retried tomorrow; it must not kill the sweep loop.
        catch (Exception ex)
        {
            LogFailed(_logger, ex, run.Id);
            await _repository.CompleteAsync(run.Id, failed: true, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning restore CA1031

        return true;
    }

    /// <summary>Checks 1 and 4 in one sampled walk: existence, then the retention dates.</summary>
    private async Task<int> CheckObjectsAsync(Guid runId, CancellationToken ct)
    {
        int critical = 0;
        IReadOnlyList<PurgeCandidate> sample = await _statements.ListAvailableSampleAsync(
            _options.ReconciliationSampleSize, ct).ConfigureAwait(false);

        foreach (PurgeCandidate statement in sample)
        {
            ct.ThrowIfCancellationRequested();
            ObjectRetentionInfo info = await _objects.GetRetentionAsync(statement.StorageKey!, ct)
                .ConfigureAwait(false);

            if (!info.Exists)
            {
                // CHECK 1. A customer clicking download on this statement gets a 500.
                await ReportAsync(runId, Check.MissingObject, "CRITICAL", statement.Id.ToString("D"),
                    string.Create(CultureInfo.InvariantCulture,
                        $"Statement is AVAILABLE but object '{statement.StorageKey}' does not exist in storage."),
                    ct).ConfigureAwait(false);
                critical++;
                continue;
            }

            // CHECK 4. The two dates were written together at PUT; divergence means tampering
            // or a bug, and either deserves a person. (The store's date may legitimately be
            // LATER if retention was extended in place; earlier never.)
            if (info.RetainUntil is { } storeDate && storeDate < statement.RetainUntil)
            {
                await ReportAsync(runId, Check.RetentionDrift, "CRITICAL", statement.Id.ToString("D"),
                    string.Create(CultureInfo.InvariantCulture,
                        $"Object lock ends {storeDate:O} but the database requires {statement.RetainUntil:O} - the physical protection undershoots the legal obligation."),
                    ct).ConfigureAwait(false);
                critical++;
            }
            else if (info.RetainUntil is null)
            {
                await ReportAsync(runId, Check.RetentionDrift, "WARNING", statement.Id.ToString("D"),
                    "Object has no retention lock at all; every statement is written with one.",
                    ct).ConfigureAwait(false);
            }
        }

        return critical;
    }

    /// <summary>Check 2: the orphan sweep's recent reports, summarised.</summary>
    private async Task<int> CheckOrphansAsync(Guid runId, CancellationToken ct)
    {
        long recent = await _repository.CountRecentOrphansAsync(
            _time.GetUtcNow().AddDays(-8), ct).ConfigureAwait(false);
        if (recent > 0)
        {
            await ReportAsync(runId, Check.OrphanedObject, "WARNING", "orphan_report",
                string.Create(CultureInfo.InvariantCulture,
                    $"{recent} orphaned objects reported in the last 8 days. See orphan_report; nothing is deleted automatically (ADR-0039)."),
                ct).ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>Check 3: DB-active holds whose objects lack the physical hold.</summary>
    /// <remarks>
    /// One direction checked exhaustively (bounded); the reverse — store holds with no DB record
    /// — cannot be enumerated without walking the bucket, so it is caught where it bites: the
    /// purge pass treats an unexpected store hold as a blocking drift and audits it.
    /// </remarks>
    private async Task<int> CheckLegalHoldDriftAsync(Guid runId, CancellationToken ct)
    {
        int critical = 0;
        IReadOnlyList<LegalHoldRow> active = await _holds.ListAsync(
            activeOnly: true, null, Guid.Empty, _options.ReconciliationSampleSize, ct).ConfigureAwait(false);

        foreach (LegalHoldRow hold in active)
        {
            ct.ThrowIfCancellationRequested();

            if (hold.StatementId is { } statementId)
            {
                PurgeCandidate? statement = await _statements.FindStatementRefAsync(statementId, ct)
                    .ConfigureAwait(false);
                if (statement?.StorageKey is null)
                {
                    continue;
                }

                ObjectRetentionInfo info = await _objects.GetRetentionAsync(statement.StorageKey, ct)
                    .ConfigureAwait(false);
                if (info.Exists && !info.LegalHold)
                {
                    await ReportAsync(runId, Check.LegalHoldDrift, "CRITICAL", hold.Id.ToString("D"),
                        string.Create(CultureInfo.InvariantCulture,
                            $"Hold {hold.CaseReference} is active in the database but object '{statement.StorageKey}' carries no store-side hold. The physical layer is not enforcing the legal record."),
                        ct).ConfigureAwait(false);
                    critical++;
                }
            }
            else if (hold.CustomerId is { } customerId)
            {
                // Sample the customer's first page rather than every object: systemic drift
                // (the placement loop broke) shows up in any page; single-object drift on a
                // 10,000-statement customer is the statement-scoped check's job when it matters.
                IReadOnlyList<StatementStorageRef> page = await _statements.ListStorageRefsForCustomerAsync(
                    new StatementDelivery.Domain.Identifiers.CustomerId(customerId),
                    Guid.Empty, DateOnly.MinValue, 50, ct).ConfigureAwait(false);

                foreach (StatementStorageRef reference in page)
                {
                    ObjectRetentionInfo info = await _objects.GetRetentionAsync(reference.StorageKey, ct)
                        .ConfigureAwait(false);
                    if (info.Exists && !info.LegalHold)
                    {
                        await ReportAsync(runId, Check.LegalHoldDrift, "CRITICAL", hold.Id.ToString("D"),
                            string.Create(CultureInfo.InvariantCulture,
                                $"Customer hold {hold.CaseReference} active but object '{reference.StorageKey}' carries no store-side hold."),
                            ct).ConfigureAwait(false);
                        critical++;
                        break;
                    }
                }
            }
        }

        return critical;
    }

    /// <summary>Check 5: every chain, re-walked and re-hashed. Any failure is P1.</summary>
    private async Task<int> CheckAuditChainsAsync(Guid runId, CancellationToken ct)
    {
        int critical = 0;
        for (short chain = 0; chain < _auditOptions.ChainCount; chain++)
        {
            ct.ThrowIfCancellationRequested();
            ChainVerification result = await _auditVerifier.VerifyChainAsync(chain, 1, long.MaxValue, ct)
                .ConfigureAwait(false);

            if (!result.Verified)
            {
                await ReportAsync(runId, Check.AuditChain, "CRITICAL",
                    chain.ToString(CultureInfo.InvariantCulture),
                    string.Create(CultureInfo.InvariantCulture,
                        $"Audit chain {chain} failed verification at seq {result.FirstBrokenSeq}. The trail is the evidence; treat as P1."),
                    ct).ConfigureAwait(false);
                critical++;
            }
        }

        return critical;
    }

    /// <summary>Check 6: erasures the system believes completed but did not.</summary>
    private async Task<int> CheckIncompleteErasureAsync(Guid runId, CancellationToken ct)
    {
        (long keys, long statements) = await _repository.CountIncompleteErasuresAsync(ct).ConfigureAwait(false);

        int critical = 0;
        if (keys > 0)
        {
            await ReportAsync(runId, Check.IncompleteErasure, "CRITICAL", "customer_key",
                string.Create(CultureInfo.InvariantCulture,
                    $"{keys} customer keys are DESTROYED yet still hold wrapped_cek bytes. Erasure did not complete; the customer believes it did."),
                ct).ConfigureAwait(false);
            critical++;
        }

        if (statements > 0)
        {
            await ReportAsync(runId, Check.IncompleteErasure, "CRITICAL", "statement",
                string.Create(CultureInfo.InvariantCulture,
                    $"{statements} statements of DESTROYED-key customers still carry wrapped DEKs. The bookkeeping pass did not finish."),
                ct).ConfigureAwait(false);
            critical++;
        }

        return critical;
    }

    private async Task ReportAsync(
        Guid runId, string check, string severity, string subject, string detail, CancellationToken ct)
    {
        await _repository.AddFindingAsync(
            runId, new ReconciliationFinding(check, severity, subject, detail), ct).ConfigureAwait(false);
        _metrics.ReconciliationFinding(check, severity);
    }

    [LoggerMessage(
        EventId = 4050,
        Level = LogLevel.Information,
        Message = "Reconciliation run {RunId} completed with {CriticalFindings} critical findings.")]
    private static partial void LogCompleted(ILogger logger, Guid runId, int criticalFindings);

    [LoggerMessage(
        EventId = 4051,
        Level = LogLevel.Error,
        Message = "Reconciliation run {RunId} failed; recorded FAILED and will not block the sweep loop.")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid runId);
}
