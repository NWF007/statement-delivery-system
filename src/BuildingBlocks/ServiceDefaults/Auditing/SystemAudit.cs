using Npgsql;
using StatementDelivery.Domain.Abstractions;
using StatementDelivery.Domain.Auditing;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Persistence.Auditing;

namespace StatementDelivery.ServiceDefaults.Auditing;

/// <summary>
/// Builds and records audit entries for BACKGROUND work: no HTTP request, no caller identity.
/// </summary>
/// <remarks>
/// <para>
/// THE COUNTERPART TO <see cref="RequestAudit"/>, WHICH IS HTTP-ONLY BY CONSTRUCTION - both of
/// its overloads require an <see cref="Microsoft.AspNetCore.Http.HttpContext"/>. Before this
/// class existed, a background worker appending audit records had to hand-construct
/// <see cref="AuditEntry"/> at every call site, re-deciding the conventions RequestAudit
/// encodes (actor derivation, empty-context default, UTC stamping) - and since the context
/// dictionary feeds the canonical hash, two services that drift in those conventions produce
/// chains an investigator has to read with two rulebooks. This was flagged in the Prompts 1-4
/// audit verification; Prompt 5's generation worker is its first consumer.
/// </para>
/// <para>
/// Everything here follows ADR-0025: an event describing the outcome of a state change takes the
/// caller's transaction and must be the LAST statement in it (the append locks the chain head).
/// </para>
/// </remarks>
public sealed class SystemAudit
{
    private readonly IAuditWriter _auditWriter;
    private readonly IIdGenerator _ids;
    private readonly TimeProvider _time;
    private readonly ServiceIdentity _identity;

    /// <summary>Initialises a new instance of the <see cref="SystemAudit"/> class.</summary>
    /// <param name="auditWriter">The chain writer.</param>
    /// <param name="ids">Identifier generator.</param>
    /// <param name="time">Time source.</param>
    /// <param name="identity">This service's identity, recorded as the actor.</param>
    public SystemAudit(IAuditWriter auditWriter, IIdGenerator ids, TimeProvider time, ServiceIdentity identity)
    {
        _auditWriter = auditWriter;
        _ids = ids;
        _time = time;
        _identity = identity;
    }

    /// <summary>
    /// Records one system-actor audit entry INSIDE THE CALLER'S TRANSACTION. Call it LAST.
    /// </summary>
    /// <param name="action">One of <see cref="AuditAction"/>.</param>
    /// <param name="outcome">One of <see cref="AuditOutcome"/>.</param>
    /// <param name="customerId">The customer involved, if known.</param>
    /// <param name="statementId">The statement involved, if any.</param>
    /// <param name="transaction">The caller's transaction. The append joins it.</param>
    /// <param name="detail">Extra diagnostic context, hashed into the chain.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The chain receipt.</returns>
    public Task<AuditReceipt> RecordAsync(
        string action,
        string outcome,
        CustomerId? customerId,
        StatementId? statementId,
        NpgsqlTransaction transaction,
        IReadOnlyDictionary<string, object?>? detail = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var entry = new AuditEntry(
            new AuditEventId(_ids.NewId()),
            statementId,
            customerId,
            ActorType.System,

            // The service instance, not a user: "which replica did this" is the question an
            // investigator asks of batch work.
            _identity.InstanceId,
            action,
            outcome,
            DenialReasonCode: null,
            SourceIp: null,
            UserAgentHash: null,
            detail ?? EmptyContext,
            _time.GetUtcNow());

        return _auditWriter.AppendAsync(entry, transaction, cancellationToken);
    }

    private static readonly Dictionary<string, object?> EmptyContext = new(StringComparer.Ordinal);
}
