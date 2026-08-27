using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Npgsql;
using StatementDelivery.Domain.Abstractions;
using StatementDelivery.Domain.Auditing;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Persistence.Auditing;
using StatementDelivery.Persistence.Uow;

namespace StatementDelivery.ServiceDefaults.Auditing;

/// <summary>
/// Builds and records audit entries for HTTP requests. Shared by both HTTP services.
/// </summary>
/// <remarks>
/// <para>
/// EVERY READ IS AUDITED, INCLUDING EVERY DENIAL. An audit log that records only successes cannot
/// detect enumeration: the attacker walking identifiers gets nothing but 404s, and 404s are exactly
/// what the log would not contain. THE DENIALS ARE THE INTERESTING DATA.
/// </para>
/// <para>
/// The denial reason is recorded INTERNALLY and never returned. Returning it would tell the caller
/// whether a statement exists but belongs to someone else, which is precisely the distinction the
/// 404 exists to hide.
/// </para>
/// </remarks>
public sealed class RequestAudit
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditWriter _auditWriter;
    private readonly IIdGenerator _ids;
    private readonly TimeProvider _time;

    /// <summary>Initialises a new instance of the <see cref="RequestAudit"/> class.</summary>
    /// <param name="unitOfWork">Transaction scope for the audit write.</param>
    /// <param name="auditWriter">The chain writer.</param>
    /// <param name="ids">Identifier generator.</param>
    /// <param name="time">Time source.</param>
    public RequestAudit(IUnitOfWork unitOfWork, IAuditWriter auditWriter, IIdGenerator ids, TimeProvider time)
    {
        _unitOfWork = unitOfWork;
        _auditWriter = auditWriter;
        _ids = ids;
        _time = time;
    }

    /// <summary>
    /// Records one audit entry in its own short transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NOT best-effort. If this throws, the exception propagates and the caller returns 500 without
    /// a body. An attacker who could make audit writes fail while still receiving data would have
    /// defeated the subsystem entirely - so a read that cannot be recorded is a read that does not
    /// happen.
    /// </para>
    /// <para>
    /// Its own transaction, held only for the append, because the append takes <c>FOR UPDATE</c> on
    /// a chain head and that row serialises every other writer on the same chain.
    /// </para>
    /// </remarks>
    /// <param name="context">The HTTP request being audited.</param>
    /// <param name="action">One of <see cref="AuditAction"/>.</param>
    /// <param name="outcome">One of <see cref="AuditOutcome"/>.</param>
    /// <param name="customerId">The customer involved, if known.</param>
    /// <param name="statementId">The statement involved, if any.</param>
    /// <param name="denialReasonCode">Internal denial detail. Never returned to the caller.</param>
    /// <param name="detail">Extra diagnostic context, hashed into the chain.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The chain receipt.</returns>
    public Task<AuditReceipt> RecordAsync(
        HttpContext context,
        string action,
        string outcome,
        CustomerId? customerId,
        StatementId? statementId,
        string? denialReasonCode = null,
        IReadOnlyDictionary<string, object?>? detail = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var entry = new AuditEntry(
            new AuditEventId(_ids.NewId()),
            statementId,
            customerId,
            context.User.Identity?.IsAuthenticated == true ? ActorType.Customer : ActorType.Anonymous,
            context.User.FindFirstValue("sub"),
            action,
            outcome,
            denialReasonCode,
            context.Connection.RemoteIpAddress?.ToString(),
            HashUserAgent(context.Request.Headers.UserAgent.ToString()),
            detail ?? EmptyContext,
            _time.GetUtcNow());

        return _unitOfWork.ExecuteAsync(
            (NpgsqlTransaction transaction, CancellationToken token) =>
                _auditWriter.AppendAsync(entry, transaction, token),
            cancellationToken);
    }

    private static readonly Dictionary<string, object?> EmptyContext = new(StringComparer.Ordinal);

    /// <summary>
    /// Hashes the user agent rather than storing it.
    /// </summary>
    /// <remarks>
    /// The raw string is a fingerprinting vector with no diagnostic power the hash lacks: comparing
    /// hashes still answers "same client as the previous attempt?", which is the only question an
    /// investigation asks of it.
    /// </remarks>
    private static string? HashUserAgent(string? userAgent) =>
        string.IsNullOrEmpty(userAgent)
            ? null
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(userAgent)));
}
