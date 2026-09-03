using Microsoft.Extensions.Options;
using Npgsql;
using Retention.Worker.Configuration;
using StatementDelivery.Domain.Auditing;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Persistence.Retention;
using StatementDelivery.Persistence.Uow;
using StatementDelivery.ServiceDefaults.Auditing;

namespace Retention.Worker;

/// <summary>Transitions old AVAILABLE statements to the cold tier.</summary>
/// <remarks>
/// WHAT IS REAL AND WHAT IS SIMULATED (ADR-0038): in production the OBJECT transition is an S3
/// lifecycle rule (to Glacier Instant or Flexible Retrieval) — this code never copies bytes,
/// because a lifecycle transition changes the storage class in place, key unchanged, Object
/// Lock preserved. What this pass owns in BOTH worlds is the database's view: status ARCHIVED,
/// tier GLACIER, audited. Locally, MinIO has no cold tier, so the tier flag plus the simulated
/// restore latency IS the archive behaviour — labelled as such, never presented as the real
/// thing. (The schema's tier vocabulary is GLACIER, from V006; 'COLD' names the same tier.)
/// </remarks>
public sealed class ArchivePass
{
    private readonly RetentionSweepRepository _statements;
    private readonly IUnitOfWork _unitOfWork;
    private readonly SystemAudit _audit;
    private readonly RetentionMetrics _metrics;
    private readonly RetentionWorkerOptions _options;
    private readonly TimeProvider _time;

    /// <summary>Initialises a new instance of the <see cref="ArchivePass"/> class.</summary>
    /// <param name="statements">Statement-side sweep queries.</param>
    /// <param name="unitOfWork">Transactions.</param>
    /// <param name="audit">The worker's audit writer.</param>
    /// <param name="metrics">Metrics.</param>
    /// <param name="options">Worker options.</param>
    /// <param name="time">Clock.</param>
    public ArchivePass(
        RetentionSweepRepository statements,
        IUnitOfWork unitOfWork,
        SystemAudit audit,
        RetentionMetrics metrics,
        IOptions<RetentionWorkerOptions> options,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        _statements = statements;
        _unitOfWork = unitOfWork;
        _audit = audit;
        _metrics = metrics;
        _options = options.Value;
        _time = time;
    }

    /// <summary>Runs one bounded archive batch.</summary>
    /// <param name="fenceToken">The lease's fence token, recorded on the audit entries.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many statements were archived.</returns>
    public async Task<int> RunAsync(long fenceToken, CancellationToken cancellationToken)
    {
        DateOnly cutoff = DateOnly.FromDateTime(
            _time.GetUtcNow().UtcDateTime.AddMonths(-_options.ArchiveAfterMonths));

        IReadOnlyList<PurgeCandidate> candidates = await _statements.ListArchiveCandidatesAsync(
            cutoff, _options.ArchiveBatchSize, cancellationToken).ConfigureAwait(false);

        int archived = 0;
        foreach (PurgeCandidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool marked = false;
            await _unitOfWork.ExecuteAsync(
                async (NpgsqlTransaction tx, CancellationToken token) =>
                {
                    marked = await _statements.MarkArchivedAsync(
                        candidate.Id, candidate.PeriodStart, tx, token).ConfigureAwait(false);
                    if (marked)
                    {
                        _ = await _audit.RecordAsync(
                            AuditAction.StatementArchived, AuditOutcome.Success,
                            new CustomerId(candidate.CustomerId), new StatementId(candidate.Id), tx,
                            new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["fence_token"] = fenceToken,
                                ["tier"] = "GLACIER",
                            },
                            token).ConfigureAwait(false);
                    }
                },
                cancellationToken).ConfigureAwait(false);

            if (marked)
            {
                archived++;
            }
        }

        if (archived > 0)
        {
            _metrics.Archived(archived);
        }

        return archived;
    }
}
