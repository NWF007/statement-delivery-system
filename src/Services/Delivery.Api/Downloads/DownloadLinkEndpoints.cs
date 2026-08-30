using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Npgsql;
using StatementDelivery.Domain.Abstractions;
using StatementDelivery.Domain.Auditing;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Domain.Statements;
using StatementDelivery.Domain.Tokens;
using StatementDelivery.Persistence.Repositories;
using StatementDelivery.Persistence.Tokens;
using StatementDelivery.Persistence.Uow;
using StatementDelivery.ServiceDefaults.Auditing;
using StatementDelivery.ServiceDefaults.RateLimiting;

namespace Delivery.Api.Downloads;

/// <summary>
/// Marks an endpoint as exempt from idempotency replay.
/// </summary>
/// <remarks>
/// <para>
/// SECURITY: idempotency replay is deliberately disabled on link issue.
/// </para>
/// <para>
/// An idempotency store keeps the request hash AND THE RESPONSE BODY, so that a retry with the same
/// key returns the same answer without re-executing. Applying that here would persist the PLAINTEXT
/// TOKEN in the database - breaking the single most important invariant in this design, that the
/// plaintext exists in exactly one place, exactly once, and never at rest.
/// </para>
/// <para>
/// The trade is not close. A duplicate link is harmless: each one is independently single-use,
/// short-lived, bound to the same customer and the same statement, and separately audited. Issuing
/// two costs a row. Storing one plaintext token costs a breach.
/// </para>
/// <para>
/// This is a real conflict between two good practices - idempotent writes and secrets-never-at-rest
/// - and it is resolved by deciding which property matters more rather than mechanically applying
/// both. See docs/adr/0014-no-idempotency-replay-on-link-issue.md.
/// </para>
/// <para>
/// NOTE: no idempotency middleware or store exists in this repository yet. This attribute is the
/// marker the future implementation MUST honour, placed now so the decision is recorded before
/// anything is built that would otherwise apply replay to every endpoint uniformly.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class SkipIdempotencyAttribute : Attribute
{
    /// <summary>Initialises a new instance of the <see cref="SkipIdempotencyAttribute"/> class.</summary>
    /// <param name="reason">Why this endpoint must not be replayed.</param>
    public SkipIdempotencyAttribute(string reason) => Reason = reason;

    /// <summary>Gets the reason this endpoint is exempt.</summary>
    public string Reason { get; }
}

/// <summary>Configuration for issuing download links.</summary>
public sealed class DownloadLinkOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "DownloadLinks";

    /// <summary>Gets or sets the public base URL of the download gateway.</summary>
    /// <remarks>
    /// The link points at a DIFFERENT service from the one that issues it: the gateway is
    /// unauthenticated and separately scaled, so a flood against it cannot exhaust this API.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    public string GatewayBaseUrl { get; set; } = "http://localhost:8082";

    /// <summary>Gets or sets the default link lifetime, in seconds.</summary>
    [Range(30, 3600)]
    public int DefaultTtlSeconds { get; set; } = 600;

    /// <summary>Gets or sets the maximum link lifetime, in seconds. The database caps this too.</summary>
    [Range(30, 3600)]
    public int MaxTtlSeconds { get; set; } = 3600;
}

/// <summary>The body of a link-issue request.</summary>
/// <param name="TtlSeconds">Requested lifetime. Clamped to the maximum; null uses the default.</param>
public sealed record IssueLinkRequest(int? TtlSeconds);

/// <summary>
/// The response to a link-issue request. THE ONLY PLACE THE PLAINTEXT TOKEN EVER APPEARS.
/// </summary>
/// <param name="LinkId">The link identifier. Not the credential; use it to revoke.</param>
/// <param name="Url">The download URL, containing the plaintext token.</param>
/// <param name="ExpiresAt">When the link stops working.</param>
/// <param name="SingleUse">Whether one redemption consumes it.</param>
public sealed record IssueLinkResponse(string LinkId, string Url, DateTimeOffset ExpiresAt, bool SingleUse);

/// <summary>
/// Issuing and revoking download links.
/// </summary>
public static class DownloadLinkEndpoints
{
    /// <summary>Maps the link endpoints.</summary>
    /// <param name="app">The web application.</param>
    /// <returns>The application, for chaining.</returns>
    public static WebApplication MapDownloadLinkEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder group = app.MapGroup("/v1")
            .RequireAuthorization()
            // Two layers, and they are not the same control. The named policy is in-process and
            // protects THIS REPLICA from a flood. The distributed rules are counted in Redis across
            // every replica and enforce the CUSTOMER's budget - which an in-process limiter cannot
            // do, because a caller round-robined across six replicas would get six times the budget.
            .RequireRateLimiting(Configuration.DeliveryApiExtensions.PerCallerPolicy)
            .RequireDistributedRateLimitPerSubject(Configuration.DeliveryApiExtensions.IssueLinkRules);

        group.MapPost("/statements/{statementId}/download-links", IssueAsync)
            .WithMetadata(new SkipIdempotencyAttribute("Response contains a secret"))
            .WithName("IssueDownloadLink")
            .WithSummary("Issues a single-use download link for one of the caller's own statements.")
            .WithDescription(
                "The `period` query parameter is REQUIRED - it is the statement's partition key. "
                + "The response body is the ONLY place the plaintext token ever appears: it is never "
                + "stored, logged or traced. Idempotency replay is deliberately disabled on this "
                + "endpoint because an idempotency store persists response bodies (ADR-0014). "
                + "A statement that does not exist and one owned by another customer both return 404.")
            .Produces<IssueLinkResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapDelete("/download-links/{linkId}", RevokeAsync)
            .WithName("RevokeDownloadLink")
            .WithSummary("Revokes an unused download link immediately.")
            .WithDescription(
                "Revocation takes effect on the next redemption attempt, because every redemption "
                + "validates against this database. With a raw S3 presigned URL this is impossible - "
                + "the only way to revoke one link is to rotate the signing credential, invalidating "
                + "every link. That limitation is why this system proxies rather than redirects. "
                + "Returns 404 for not found, not owned, or already consumed - indistinguishably.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return app;
    }

    private static async Task<IResult> IssueAsync(
        string statementId,
        HttpContext http,
        IssueLinkRequest? request,
        IStatementReadRepository statements,
        IDownloadTokenRepository tokens,
        IUnitOfWork unitOfWork,
        IRandomBytes randomBytes,
        IIdGenerator ids,
        RequestAudit audit,
        IOptions<DownloadLinkOptions> options,
        TimeProvider time,
        CancellationToken cancellationToken,
        string? period = null)
    {
        DownloadLinkOptions settings = options.Value;

        // The JWT subject, never anything from the request. Step 1 of the flow.
        if (!TryGetSubject(http, out CustomerId subject))
        {
            await audit.RecordAsync(
                http, AuditAction.AccessDenied, AuditOutcome.Denied, null, null,
                DenialReason.NoSubjectClaim, cancellationToken: cancellationToken).ConfigureAwait(false);
            return Results.NotFound();
        }

        if (!StatementId.TryParse(statementId, out StatementId id))
        {
            return Results.Problem(title: "Invalid statement id", detail: "statementId must be a UUID.", statusCode: 400);
        }

        if (!DateOnly.TryParseExact(period, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly periodStart)
            || periodStart.Day != 1)
        {
            return Results.Problem(
                title: "Missing or invalid period",
                detail: "period is required and must be the first day of the statement's month (yyyy-MM-dd). "
                      + "It is the partition key: without it the lookup would scan every partition.",
                statusCode: 400);
        }

        // Step 2. OWNERSHIP AS A PREDICATE - the owner is in the WHERE clause, not compared after.
        Statement? statement = await statements
            .FindAsync(id, periodStart, subject, cancellationToken).ConfigureAwait(false);

        if (statement is null)
        {
            // Not found and not owned are indistinguishable. See ADR-0012.
            await audit.RecordAsync(
                http, AuditAction.AccessDenied, AuditOutcome.Denied, subject, id,
                DenialReason.NotFound, cancellationToken: cancellationToken).ConfigureAwait(false);
            return Results.NotFound();
        }

        // Step 3. Specific codes are safe HERE because ownership has already been proven: the
        // caller is entitled to know the state of their own statement.
        switch (statement.Status)
        {
            case StatementStatus.Purged:
                return Results.Problem(
                    title: "Statement no longer available",
                    detail: "This statement has passed its retention period and has been destroyed.",
                    statusCode: StatusCodes.Status410Gone);

            case StatementStatus.Archived:
                return Results.Problem(
                    title: "Statement is archived",
                    detail: "This statement is in cold storage. Request a restore before downloading it.",
                    statusCode: StatusCodes.Status409Conflict);

            case StatementStatus.Pending:
            case StatementStatus.Failed:
                return Results.Problem(
                    title: "Statement is not ready",
                    detail: "This statement has not finished generating.",
                    statusCode: StatusCodes.Status409Conflict);

            case StatementStatus.Available:
            default:
                break;
        }

        var policy = new TokenPolicy(
            TimeSpan.FromSeconds(settings.DefaultTtlSeconds),
            TimeSpan.FromSeconds(settings.MaxTtlSeconds),
            SingleUse: true);

        TimeSpan ttl;
        try
        {
            ttl = policy.Resolve(request?.TtlSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null);
        }
        catch (StatementDelivery.Domain.Exceptions.InvariantViolationException ex)
        {
            return Results.Problem(title: "Invalid ttlSeconds", detail: ex.Message, statusCode: 400);
        }

        // -----------------------------------------------------------------------------------------
        // Steps 4-5. THE PLAINTEXT LIVES ONLY INSIDE THIS SYNCHRONOUS LOCAL FUNCTION.
        //
        // TokenSecret is a ref struct, so it CANNOT cross an await - the compiler enforces that.
        // Minting it here and returning only the hash and the URL string means the secret itself
        // never reaches the async state machine, never lands on the heap in a captured local, and
        // cannot be reached by anything that outlives this call.
        //
        // The URL string is the one permitted copy. It goes into the response body and nowhere else.
        // -----------------------------------------------------------------------------------------
        static (TokenHash Hash, string Plaintext) Mint(IRandomBytes rng)
        {
            TokenSecret secret = TokenSecret.Generate(rng);
            return (secret.ComputeHash(), secret.ToUrlSafeString());
        }

        (TokenHash hash, string plaintext) = Mint(randomBytes);

        DateTimeOffset issuedAt = time.GetUtcNow();
        var linkId = DownloadTokenId.New(ids);

        DownloadToken token = DownloadToken.Issue(
            linkId, id, periodStart, subject, hash, issuedAt, ttl, policy.SingleUse);

        // Step 6. The insert and the audit share one transaction: a link that exists without a
        // record of who asked for it must be impossible.
        //
        // THEY NOW ACTUALLY DO. This comment was here before the audit append was inside the
        // transaction it describes - the insert committed, and LINK_ISSUED was written afterwards
        // in a second transaction, so a failure in between produced exactly the live link with no
        // record that the comment says is impossible. See ADR-0025.
        await unitOfWork.ExecuteAsync(
            async (NpgsqlTransaction transaction, CancellationToken token2) =>
            {
                // Only the HASH crosses this boundary. There is no overload that could take the
                // plaintext, and the repository never references TokenSecret at all.
                await tokens.InsertAsync(token, http.Connection.RemoteIpAddress, transaction, token2)
                    .ConfigureAwait(false);

                // LAST STATEMENT IN THE TRANSACTION: the append locks this chain's head, and
                // everything else on that chain waits behind it until commit.
                _ = await audit.RecordAsync(
                    http, AuditAction.LinkIssued, AuditOutcome.Success, subject, id, transaction,
                    detail: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["link_id"] = linkId.ToString(),

                        // The HASH is recorded, never the plaintext. This is what correlates a later
                        // redemption with the issue that produced it.
                        ["token_hash"] = hash.ToString(),
                        ["ttl_seconds"] = (long)ttl.TotalSeconds,
                        ["expires_at"] = token.ExpiresAt.ToString("O", CultureInfo.InvariantCulture),
                    },
                    cancellationToken: token2).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        // Step 7. The plaintext leaves the process exactly once, here.
        return Results.Created(
            $"/v1/download-links/{linkId}",
            new IssueLinkResponse(
                linkId.ToString(),
                $"{settings.GatewayBaseUrl.TrimEnd('/')}/v1/d/{plaintext}",
                token.ExpiresAt,
                token.SingleUse));
    }

    private static async Task<IResult> RevokeAsync(
        string linkId,
        HttpContext http,
        IDownloadTokenRepository tokens,
        IUnitOfWork unitOfWork,
        RequestAudit audit,
        CancellationToken cancellationToken)
    {
        if (!TryGetSubject(http, out CustomerId subject))
        {
            return Results.NotFound();
        }

        if (!DownloadTokenId.TryParse(linkId, out DownloadTokenId id))
        {
            return Results.NotFound();
        }

        bool revoked = await unitOfWork.ExecuteAsync(
            async (NpgsqlTransaction transaction, CancellationToken token) =>
            {
                bool wasRevoked = await tokens
                    .RevokeAsync(id, subject, "CUSTOMER_REQUESTED", transaction, token)
                    .ConfigureAwait(false);

                if (wasRevoked)
                {
                    // A REVOCATION IS A STATE CHANGE, so its record binds to the transaction that
                    // made it. A revoked link with no record of the revocation is the same class of
                    // hole as a consumed token with no record of the download.
                    //
                    // Last statement in the transaction, for the chain-head lock.
                    _ = await audit.RecordAsync(
                        http, AuditAction.LinkRevoked, AuditOutcome.Success, subject, null, transaction,
                        detail: new Dictionary<string, object?>(StringComparer.Ordinal) { ["link_id"] = linkId },
                        cancellationToken: token).ConfigureAwait(false);
                }

                return wasRevoked;
            },
            cancellationToken).ConfigureAwait(false);

        if (!revoked)
        {
            // Not found, not owned, already consumed and already revoked are one response.
            //
            // The UPDATE matched no row, so nothing was written and there is nothing to bind to -
            // the transactionless overload is the correct one here.
            await audit.RecordAsync(
                http, AuditAction.AccessDenied, AuditOutcome.Denied, subject, null,
                DenialReason.NotFound,
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["link_id"] = linkId },
                cancellationToken).ConfigureAwait(false);

            return Results.NotFound();
        }

        return Results.NoContent();
    }

    private static bool TryGetSubject(HttpContext http, out CustomerId subject)
    {
        string? claim = http.User.FindFirstValue("sub") ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return CustomerId.TryParse(claim, out subject);
    }
}
