using Microsoft.Extensions.Options;
using Npgsql;
using Retention.Worker.Configuration;
using StatementDelivery.Contracts.Events;
using StatementDelivery.Domain.Abstractions;
using StatementDelivery.Domain.Auditing;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Messaging;
using StatementDelivery.Persistence.Retention;
using StatementDelivery.Persistence.Uow;
using StatementDelivery.ServiceDefaults.Auditing;

namespace Retention.Worker;

/// <summary>Completes pending restores whose retrieval window has elapsed.</summary>
/// <remarks>
/// <para>
/// LOCAL SIMULATION, LABELLED AS ONE (ADR-0038). MinIO has no Glacier tier, so nothing moves:
/// "restore completes" here means the configured artificial delay elapsed. In production this
/// job instead polls <c>HeadObject</c> for the <c>x-amz-restore</c> completion marker (or
/// consumes the S3 restore-completed event) after a real <c>RestoreObject</c> call. The
/// database contract — PENDING flips to AVAILABLE with an expiry, and the event publishes
/// through the outbox — is identical in both worlds, which is what makes the simulation honest.
/// </para>
/// <para>
/// The statement row stays ARCHIVED, faithfully: a Glacier restore produces a TEMPORARY copy
/// and the object's storage class never changes. The download gateway admits an archived
/// statement only while an unexpired AVAILABLE restore exists.
/// </para>
/// </remarks>
public sealed class RestoreCompleter
{
    private readonly RestoreRequestRepository _restores;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IIntegrationEventPublisher _outbox;
    private readonly SystemAudit _audit;
    private readonly RetentionMetrics _metrics;
    private readonly RetentionWorkerOptions _options;
    private readonly IIdGenerator _ids;
    private readonly TimeProvider _time;

    /// <summary>Initialises a new instance of the <see cref="RestoreCompleter"/> class.</summary>
    /// <param name="restores">The request store.</param>
    /// <param name="unitOfWork">Transactions.</param>
    /// <param name="outbox">The transactional outbox.</param>
    /// <param name="audit">The worker's audit writer.</param>
    /// <param name="metrics">Metrics.</param>
    /// <param name="options">Worker options.</param>
    /// <param name="ids">Id generator for the event.</param>
    /// <param name="time">Clock.</param>
    public RestoreCompleter(
        RestoreRequestRepository restores,
        IUnitOfWork unitOfWork,
        IIntegrationEventPublisher outbox,
        SystemAudit audit,
        RetentionMetrics metrics,
        IOptions<RetentionWorkerOptions> options,
        IIdGenerator ids,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        _restores = restores;
        _unitOfWork = unitOfWork;
        _outbox = outbox;
        _audit = audit;
        _metrics = metrics;
        _options = options.Value;
        _ids = ids;
        _time = time;
    }

    /// <summary>Completes due restores, bounded.</summary>
    /// <param name="fenceToken">The lease's fence token, recorded on the audit entries.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many completed.</returns>
    public async Task<int> RunAsync(long fenceToken, CancellationToken cancellationToken)
    {
        IReadOnlyList<RestoreRequestRow> due = await _restores.ListDueAsync(
            _options.RestoreBatchSize, cancellationToken).ConfigureAwait(false);

        int completed = 0;
        foreach (RestoreRequestRow restore in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DateTimeOffset now = _time.GetUtcNow();
            DateTimeOffset expiresAt = now.AddHours(_options.RestoredCopyHours);

            // One transaction: the completion, the audit, and the event commit or fail together
            // — "the restore completed" and "the caller will be told" are one fact.
            bool marked = false;
            await _unitOfWork.ExecuteAsync(
                async (NpgsqlTransaction tx, CancellationToken token) =>
                {
                    marked = await _restores.MarkAvailableAsync(restore.Id, expiresAt, tx, token)
                        .ConfigureAwait(false);
                    if (!marked)
                    {
                        return;
                    }

                    _ = await _audit.RecordAsync(
                        AuditAction.StatementRestored, AuditOutcome.Success,
                        new CustomerId(restore.CustomerId), new StatementId(restore.StatementId), tx,
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["fence_token"] = fenceToken,
                            ["restore_id"] = restore.Id.ToString("D"),
                            ["expires_at"] = expiresAt.ToString("O"),
                        },
                        token).ConfigureAwait(false);

                    await _outbox.PublishAsync(
                        new StatementRestored(
                            _ids.NewId(), now, restore.Id, restore.StatementId, restore.CustomerId, expiresAt),
                        tx, token).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);

            if (marked)
            {
                _metrics.RestoreCompleted();
                completed++;
            }
        }

        return completed;
    }
}
