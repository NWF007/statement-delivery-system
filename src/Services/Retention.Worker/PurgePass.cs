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
using StatementDelivery.ServiceDefaults.Storage;

namespace Retention.Worker;

/// <summary>The daily purge: decide per statement, delete storage first, keep the metadata.</summary>
/// <remarks>
/// <para>
/// PURGE ORDER — STORAGE FIRST, DATABASE SECOND (ADR-0034), and the crash-in-the-middle is why.
/// Delete succeeds, mark fails: the retry re-deletes (a no-op — a missing object is success) and
/// marks. Benign. Mark succeeds, delete fails: a paid-for object exists that no database row
/// points at — an orphan under a Compliance lock, unreachable and undeletable until its date.
/// The first ordering's failure is recoverable by doing nothing; the second's leaks money and
/// bytes until the orphan sweep finds it. So: storage, then row.
/// </para>
/// <para>
/// METADATA SURVIVES (hard constraint 1, ADR-0036). The row stays with status PURGED, purged_at
/// and the audit trail intact; only the pointers and crypto material are nulled. You must be
/// able to demonstrate THAT deletion occurred, when, and under what authority — a vanished row
/// proves nothing, being indistinguishable from a row that never existed or one an attacker
/// removed.
/// </para>
/// </remarks>
public sealed partial class PurgePass
{
    private readonly RetentionSweepRepository _statements;
    private readonly HoldResolution _holds;
    private readonly ICustomerKeyStore _keys;
    private readonly IStatementObjectAdmin _objects;
    private readonly IUnitOfWork _unitOfWork;
    private readonly SystemAudit _audit;
    private readonly RetentionMetrics _metrics;
    private readonly RetentionWorkerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PurgePass> _logger;
    private long _lockSkipsThisPass;

    /// <summary>Initialises a new instance of the <see cref="PurgePass"/> class.</summary>
    /// <param name="statements">Statement-side sweep queries.</param>
    /// <param name="holds">The shared hold resolver - both layers, one implementation (Part B).</param>
    /// <param name="keys">Customer key rows.</param>
    /// <param name="objects">The object store's admin surface.</param>
    /// <param name="unitOfWork">Transactions.</param>
    /// <param name="audit">The worker's audit writer.</param>
    /// <param name="metrics">Metrics.</param>
    /// <param name="options">Worker options.</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Logger.</param>
    public PurgePass(
        RetentionSweepRepository statements,
        HoldResolution holds,
        ICustomerKeyStore keys,
        IStatementObjectAdmin objects,
        IUnitOfWork unitOfWork,
        SystemAudit audit,
        RetentionMetrics metrics,
        IOptions<RetentionWorkerOptions> options,
        TimeProvider time,
        ILogger<PurgePass> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _statements = statements;
        _holds = holds;
        _keys = keys;
        _objects = objects;
        _unitOfWork = unitOfWork;
        _audit = audit;
        _metrics = metrics;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>Runs one bounded purge batch.</summary>
    /// <param name="fenceToken">The lease's fence token, recorded on every audit entry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many statements were purged.</returns>
    public async Task<int> RunAsync(long fenceToken, CancellationToken cancellationToken)
    {
        DateOnly today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);

        (long unblocked, long holdBlocked) = await _statements
            .CountEligibleForPurgeAsync(today, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<PurgeCandidate> candidates = await _statements.ListPurgeCandidatesAsync(
            today, _options.PurgeBatchSize, cancellationToken).ConfigureAwait(false);

        int purged = 0;
        _lockSkipsThisPass = 0;
        foreach (PurgeCandidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ProcessAsync(candidate, today, fenceToken, cancellationToken).ConfigureAwait(false))
            {
                purged++;
            }
        }

        // Only the store knows its locks, so the lock-blocked series is what THIS pass observed
        // (and the "none" series subtracts it - those rows are accounted for, not stalled).
        _metrics.EligibleForPurge(
            Math.Max(0, unblocked - _lockSkipsThisPass), holdBlocked, _lockSkipsThisPass);

        return purged;
    }

    private async Task<bool> ProcessAsync(
        PurgeCandidate candidate, DateOnly today, long fenceToken, CancellationToken ct)
    {
        var statementId = new StatementId(candidate.Id);
        var customerId = new CustomerId(candidate.CustomerId);

        // Build the context from the AUTHORITATIVE sources: holds through the SHARED resolver
        // (both layers, one implementation - Part B), the lock from the object store itself
        // (hard constraint 4 — if S3 says locked, the database's opinion does not matter), the
        // key from its row.
        ObjectRetentionInfo info = candidate.StorageKey is null
            ? new ObjectRetentionInfo(Exists: false, Mode: null, RetainUntil: null, LegalHold: false)
            : await _objects.GetRetentionAsync(candidate.StorageKey, ct).ConfigureAwait(false);

        HoldState holds = await _holds.ResolveForStatementAsync(statementId, customerId, info, ct)
            .ConfigureAwait(false);

        CustomerKeyRecord? key = await _keys.FindAsync(customerId, ct).ConfigureAwait(false);

        RetentionDecision decision = RetentionDecisionEngine.Decide(RetentionContextFactory.Create(
            holds, key?.DestroyedAt is not null, candidate.RetainUntil, info, today));

        switch (decision)
        {
            case RetentionDecision.Purge:
                return await ExecutePurgeAsync(candidate, fenceToken, ct).ConfigureAwait(false);

            case RetentionDecision.BlockedByLegalHold blocked:
                if (key?.DestroyedAt is not null)
                {
                    // The engine's A4 guard fired: this statement is ERASED and HELD at once, a
                    // state erasure's hold gate exists to make impossible. Blocking is the safe
                    // response; this metric is the page that says the gate failed somewhere.
                    _metrics.ErasedUnderHold();
                }

                await AuditSkipAsync(
                    candidate, AuditAction.RetentionSkippedLegalHold, fenceToken,
                    [("case_reference", blocked.CaseReference)], ct).ConfigureAwait(false);
                _metrics.Purged(RetentionMetrics.PurgeOutcome.SkippedLegalHold);
                return false;

            case RetentionDecision.BlockedByObjectLock lockBlocked:
                await AuditSkipAsync(
                    candidate, AuditAction.RetentionSkippedObjectLock, fenceToken,
                    [("object_lock_until", lockBlocked.Until.ToString("O"))], ct).ConfigureAwait(false);
                _metrics.Purged(RetentionMetrics.PurgeOutcome.SkippedObjectLock);
                _lockSkipsThisPass++;
                return false;

            case RetentionDecision.RetainStatutory retain:
                // Should be unreachable: the candidate query selects rows whose retain_until has
                // passed, so the engine agreeing to retain means the two dates DISAGREE. That is
                // a data-integrity problem worth paging on, not a normal path — cheap to check,
                // and the kind of defensive tripwire that catches a whole class of bugs.
                LogDateDisagreement(_logger, candidate.Id, candidate.RetainUntil, retain.Until);
                await AuditSkipAsync(
                    candidate, AuditAction.RetentionDateDisagreement, fenceToken,
                    [
                        ("db_retain_until", candidate.RetainUntil.ToString("O")),
                        ("engine_retain_until", retain.Until.ToString("O")),
                    ],
                    ct).ConfigureAwait(false);
                _metrics.Purged(RetentionMetrics.PurgeOutcome.DateDisagreement);
                return false;

            case RetentionDecision.AlreadyErased:
                // The key is gone; the object is undecipherable ciphertext that a Compliance
                // lock may still pin in place. No storage call — mark the row, tombstone the
                // key as ERASED so the orphan sweep knows the remnant is lawful.
                await _unitOfWork.ExecuteAsync(
                    async (NpgsqlTransaction tx, CancellationToken token) =>
                    {
                        if (await _statements.MarkPurgedAsync(candidate, "ERASED", tx, token).ConfigureAwait(false))
                        {
                            _ = await _audit.RecordAsync(
                                AuditAction.RetentionPurged, AuditOutcome.Success, customerId, statementId, tx,
                                Detail(fenceToken, ("mode", "already_erased")), token).ConfigureAwait(false);
                        }
                    },
                    ct).ConfigureAwait(false);
                _metrics.Purged(RetentionMetrics.PurgeOutcome.AlreadyErased);
                return true;

            default:
                throw new InvalidOperationException($"Unhandled retention decision {decision}.");
        }
    }

    private async Task<bool> ExecutePurgeAsync(PurgeCandidate candidate, long fenceToken, CancellationToken ct)
    {
        // STEP 1 — storage. Idempotent: a missing object returns an empty version list, which is
        // exactly what the crash-and-retry path produces on its second visit.
        IReadOnlyList<string> deletedVersions = candidate.StorageKey is null
            ? []
            : await _objects.DeleteObjectVersionsAsync(candidate.StorageKey, ct).ConfigureAwait(false);

        // STEP 2 + 3 — the row and the audit, one transaction, with the deleted version ids in
        // the audit detail as the proof of what went.
        bool marked = false;
        await _unitOfWork.ExecuteAsync(
            async (NpgsqlTransaction tx, CancellationToken token) =>
            {
                marked = await _statements.MarkPurgedAsync(candidate, "PURGED", tx, token).ConfigureAwait(false);
                if (marked)
                {
                    _ = await _audit.RecordAsync(
                        AuditAction.RetentionPurged, AuditOutcome.Success,
                        new CustomerId(candidate.CustomerId), new StatementId(candidate.Id), tx,
                        Detail(
                            fenceToken,
                            ("storage_key", candidate.StorageKey),
                            ("deleted_version_ids", string.Join(",", deletedVersions)),
                            ("deleted_version_count", deletedVersions.Count)),
                        token).ConfigureAwait(false);
                }
            },
            ct).ConfigureAwait(false);

        if (marked)
        {
            _metrics.Purged(RetentionMetrics.PurgeOutcome.Purged);
        }

        return marked;
    }

    private Task<AuditReceipt> AuditSkipAsync(
        PurgeCandidate candidate, string action, long fenceToken,
        (string Key, object? Value)[] extra, CancellationToken ct)
    {
        return _unitOfWork.ExecuteAsync(
            (NpgsqlTransaction tx, CancellationToken token) =>
                _audit.RecordAsync(
                    action, AuditOutcome.Success,
                    new CustomerId(candidate.CustomerId), new StatementId(candidate.Id), tx,
                    Detail(fenceToken, extra), token),
            ct);
    }

    private static Dictionary<string, object?> Detail(
        long fenceToken, params (string Key, object? Value)[] extra)
    {
        var detail = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["fence_token"] = fenceToken,
        };
        foreach ((string key, object? value) in extra)
        {
            detail[key] = value;
        }

        return detail;
    }

    [LoggerMessage(
        EventId = 4010,
        Level = LogLevel.Critical,
        Message = "Statement {StatementId}: the candidate query says retain_until {DbDate:O} has passed but the decision engine says retain until {EngineDate:O}. The two dates disagree - this is a data-integrity problem, not a scheduling hiccup.")]
    private static partial void LogDateDisagreement(
        ILogger logger, Guid statementId, DateOnly dbDate, DateOnly engineDate);
}
