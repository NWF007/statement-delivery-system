using Microsoft.Extensions.Options;
using Npgsql;
using Retention.Worker.Configuration;
using StatementDelivery.Crypto.Keys;
using StatementDelivery.Domain.Auditing;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Domain.Retention;
using StatementDelivery.Persistence.Retention;
using StatementDelivery.Persistence.Uow;
using StatementDelivery.ServiceDefaults.Auditing;
using StatementDelivery.ServiceDefaults.Retention;

namespace Retention.Worker;

/// <summary>Executes scheduled erasures whose cooling-off window has closed.</summary>
/// <remarks>
/// <para>
/// RE-EVALUATE, THEN DESTROY. The decision made seven days ago is not trusted: a legal hold may
/// have been placed during the cooling-off window, and destroying held evidence because the
/// check ran a week early is precisely the failure the re-evaluation exists to prevent. Blocked
/// requests stay SCHEDULED and are re-tried (and re-audited) on later passes until the hold
/// clears or the request is cancelled.
/// </para>
/// <para>
/// EXECUTION ORDER: destroy the key FIRST, then mark the statements and complete the request.
/// A crash in between leaves a destroyed key with statements still marked AVAILABLE — their
/// downloads already fail closed (CryptoErasedException → uniform 404/410), and the next pass
/// finishes the bookkeeping idempotently. The reverse order could mark a customer erased while
/// their key still exists, which is a lie with a seven-year audit trail.
/// </para>
/// <para>
/// THE OBJECTS REMAIN, deliberately. They sit under Compliance-mode locks nothing can lift, and
/// nothing needs to: ciphertext whose key no longer exists anywhere is indistinguishable from
/// random bytes. That is what makes crypto-erasure work where deletion cannot (the insight the
/// whole of Prompt 4 was building towards). Tombstones mark each remnant as lawful so the
/// orphan sweep does not report the system's own design as a finding.
/// </para>
/// </remarks>
public sealed partial class ErasureExecutor
{
    private readonly ErasureRepository _erasures;
    private readonly RetentionSweepRepository _statements;
    private readonly HoldResolution _holds;
    private readonly ICustomerKeyService _keyService;
    private readonly IDataKeyBroker _keyCache;
    private readonly IUnitOfWork _unitOfWork;
    private readonly SystemAudit _audit;
    private readonly RetentionMetrics _metrics;
    private readonly RetentionWorkerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ErasureExecutor> _logger;

    /// <summary>Initialises a new instance of the <see cref="ErasureExecutor"/> class.</summary>
    /// <param name="erasures">The request queue.</param>
    /// <param name="statements">Statement-side operations.</param>
    /// <param name="holds">The shared hold resolver - both layers, one implementation (Part B).</param>
    /// <param name="keyService">The key hierarchy — owns destruction.</param>
    /// <param name="keyCache">This process's DEK cache, evicted after destruction (Part G).</param>
    /// <param name="unitOfWork">Transactions.</param>
    /// <param name="audit">The worker's audit writer.</param>
    /// <param name="metrics">Metrics.</param>
    /// <param name="options">Worker options.</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Logger.</param>
    public ErasureExecutor(
        ErasureRepository erasures,
        RetentionSweepRepository statements,
        HoldResolution holds,
        ICustomerKeyService keyService,
        IDataKeyBroker keyCache,
        IUnitOfWork unitOfWork,
        SystemAudit audit,
        RetentionMetrics metrics,
        IOptions<RetentionWorkerOptions> options,
        TimeProvider time,
        ILogger<ErasureExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _erasures = erasures;
        _statements = statements;
        _holds = holds;
        _keyService = keyService;
        _keyCache = keyCache;
        _unitOfWork = unitOfWork;
        _audit = audit;
        _metrics = metrics;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>Runs one bounded executor batch.</summary>
    /// <param name="fenceToken">The lease's fence token, recorded on every audit entry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many erasures completed.</returns>
    public async Task<int> RunAsync(long fenceToken, CancellationToken cancellationToken)
    {
        IReadOnlyList<ErasureRequestRow> due = await _erasures.ListDueAsync(
            _options.ErasureBatchSize, cancellationToken).ConfigureAwait(false);

        int completed = 0;
        foreach (ErasureRequestRow request in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ExecuteAsync(request, fenceToken, cancellationToken).ConfigureAwait(false))
            {
                completed++;
            }
        }

        return completed;
    }

    private async Task<bool> ExecuteAsync(ErasureRequestRow request, long fenceToken, CancellationToken ct)
    {
        var customer = new CustomerId(request.CustomerId);
        DateOnly today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);

        // THE RE-EVALUATION. Fresh reads, seven days after the request was judged lawful -
        // through the SHARED resolver, so a hold in EITHER layer blocks. The store side matters
        // most here: hold placement is storage-first (ADR-0037), so its designed crash residue
        // is a store hold with no database row, and destruction is the one operation that
        // residue must still stop.
        HoldState holds = await _holds.ResolveForCustomerAsync(customer, ct).ConfigureAwait(false);

        // RETENTION IS NEUTRAL HERE, DELIBERATELY. Feeding the customer's max retain_until into
        // the engine blocked every erasure as RetainUntilFuture for seven years - but
        // crypto-erasure is the mechanism that satisfies both statutes at once: the ciphertext
        // object REMAINS retained for FICA (the executor never deletes it), while its
        // readability dies for POPIA. The engine sees retention as already-elapsed so that the
        // decision reduces to the true erasure blockers: a legal hold in either layer, or a key
        // already destroyed. Erasure_Executes_ObjectSurvives_ReadPathDies_AuditPreserved pins
        // exactly this.
        RetentionDecision decision = RetentionDecisionEngine.Decide(RetentionContextFactory.Create(
            holds,
            customerKeyDestroyed: false,
            retainUntil: today,
            objectInfo: null,
            today));

        if (decision is not RetentionDecision.Purge)
        {
            // Blocked at execution time. The request stays SCHEDULED and the next pass
            // re-evaluates. AUDIT ON TRANSITION, NOT ON EVALUATION (V022): a blocked erasure is
            // one fact until something about it changes, and re-appending it every pass was
            // ~288 chain entries per day per blocked request, each crossing the chain head's
            // FOR UPDATE serialisation point. Append when the blocking reason CHANGES, or once
            // per 24 h as a heartbeat; the bookkeeping columns update either way.
            string reason = decision.GetType().Name;
            bool auditDue = !string.Equals(reason, request.LastBlockedReason, StringComparison.Ordinal)
                || request.LastBlockedAt is null
                || _time.GetUtcNow() - request.LastBlockedAt >= TimeSpan.FromHours(24);

            LogExecutionBlocked(_logger, request.CustomerId, reason);
            await _unitOfWork.ExecuteAsync(
                async (NpgsqlTransaction tx, CancellationToken token) =>
                {
                    await _erasures.RecordBlockedAsync(request.Id, reason, tx, token).ConfigureAwait(false);

                    if (auditDue)
                    {
                        _ = await _audit.RecordAsync(
                            AuditAction.ErasureBlocked, AuditOutcome.Denied, customer, null, tx,
                            new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["fence_token"] = fenceToken,
                                ["erasure_id"] = request.Id.ToString("D"),
                                ["stage"] = "execution_reevaluation",
                                ["decision"] = reason,
                                ["case_reference"] = holds.EffectiveCaseReference,
                                ["hold_drift"] = holds.IsDrift,
                            },
                            token).ConfigureAwait(false);
                    }
                },
                ct).ConfigureAwait(false);
            return false;
        }

        // Gather the storage refs BEFORE the statements are marked (marking nulls the keys),
        // bounded page by page; the tombstones must exist for every remnant object.
        var refs = new List<StatementStorageRef>();
        var afterId = Guid.Empty;
        DateOnly afterPeriod = DateOnly.MinValue;
        while (true)
        {
            IReadOnlyList<StatementStorageRef> page = await _statements.ListStorageRefsForCustomerAsync(
                customer, afterId, afterPeriod, 500, ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            refs.AddRange(page);
            afterId = page[^1].Id;
            afterPeriod = page[^1].PeriodStart;
        }

        // STEP 1 — the irreversible act. Idempotent: a crashed pass re-runs this as a no-op.
        await _keyService.DestroyCekAsync(
            customer,
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{request.Reason} ({request.RequestReference})"),
            ct).ConfigureAwait(false);

        // THIS process's cache must not outlive the key it wrapped. Other processes' caches
        // expire on MaxAge; the database-side write guard covers that window (Part G), and the
        // residual read-side gap is documented in docs/LIMITATIONS.md.
        _keyCache.Evict(customer);

        // STEP 2 — the bookkeeping, one transaction: statements PURGED, tombstones for the
        // lawful remnants, the request completed, ERASURE_COMPLETED appended last (ADR-0025).
        await _unitOfWork.ExecuteAsync(
            async (NpgsqlTransaction tx, CancellationToken token) =>
            {
                foreach (StatementStorageRef reference in refs)
                {
                    await _statements.WriteTombstoneAsync(
                        reference.StorageKey, reference.Id, reference.PeriodStart, request.CustomerId,
                        "ERASED", tx, token).ConfigureAwait(false);
                }

                int marked = await _statements.MarkCustomerStatementsErasedAsync(customer, tx, token)
                    .ConfigureAwait(false);

                await _erasures.CompleteAsync(request.Id, tx, token).ConfigureAwait(false);

                _ = await _audit.RecordAsync(
                    AuditAction.ErasureCompleted, AuditOutcome.Success, customer, null, tx,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["fence_token"] = fenceToken,
                        ["erasure_id"] = request.Id.ToString("D"),
                        ["request_reference"] = request.RequestReference,
                        ["statements_marked"] = marked,
                        ["remnant_objects"] = refs.Count,
                    },
                    token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        _metrics.ErasureCompleted();
        LogCompleted(_logger, request.CustomerId, refs.Count);
        return true;
    }

    [LoggerMessage(
        EventId = 4030,
        Level = LogLevel.Warning,
        Message = "Erasure for customer {CustomerId} blocked at execution time by {Decision}. The request stays scheduled; the block was audited. If this is a legal hold placed during cooling-off, the system worked as designed.")]
    private static partial void LogExecutionBlocked(ILogger logger, Guid customerId, string decision);

    [LoggerMessage(
        EventId = 4031,
        Level = LogLevel.Information,
        Message = "Crypto-erasure completed for customer {CustomerId}. {RemnantObjects} objects remain in storage as undecryptable ciphertext under their Compliance locks.")]
    private static partial void LogCompleted(ILogger logger, Guid customerId, int remnantObjects);
}
