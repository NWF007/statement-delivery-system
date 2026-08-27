using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Npgsql;
using StatementDelivery.Domain.Auditing;
using StatementDelivery.Domain.Statements;
using StatementDelivery.Domain.Tokens;
using StatementDelivery.Persistence.Repositories;
using StatementDelivery.Persistence.Tokens;
using StatementDelivery.Persistence.Uow;
using StatementDelivery.ServiceDefaults.Auditing;
using StatementDelivery.ServiceDefaults.RateLimiting;
using StatementDelivery.ServiceDefaults.Storage;

namespace Download.Gateway.Downloads;

/// <summary>
/// The public redemption endpoint.
/// </summary>
/// <remarks>
/// <para>
/// THIS ENDPOINT IS UNAUTHENTICATED, AND THAT IS A DECISION RATHER THAN AN OMISSION.
/// </para>
/// <para>
/// The token IS the credential. That is deliberate: it lets the customer hand the URL to their
/// operating system's download manager, their browser, a mail client that pre-fetches links, or a
/// third-party app - none of which can or should be holding a bank JWT. Requiring the JWT here
/// would be strictly more secure and would break the exact use case the link exists to serve.
/// </para>
/// <para>
/// The security rests entirely on the token's properties, and each one is load-bearing:
/// 256 bits of CSPRNG entropy, a ten-minute lifetime, single use, bound to one customer AND one
/// statement, stored only as a SHA-256, never written to a log or a span, and revocable instantly.
/// Weaken any of them and this endpoint stops being defensible. See ADR-0015.
/// </para>
/// </remarks>
public static class DownloadEndpoints
{
    /// <summary>The redemption route. Short by design: it is pasted, typed and printed.</summary>
    public const string RedeemRoute = "/v1/d/{token}";

    /// <summary>Maps the redemption endpoint.</summary>
    /// <param name="app">The web application.</param>
    /// <returns>The application, for chaining.</returns>
    public static WebApplication MapDownloadEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(RedeemRoute, RedeemAsync)
            .AllowAnonymous()
            // THREE DISTINCT CONTROLS, EACH ADDRESSING A DIFFERENT THREAT.
            //
            // PerAddress (in-process, per replica) sheds load before this node falls over.
            // ConcurrentDownloads bounds how many transfers stream at once - a request budget alone
            // does not bound a STREAMING endpoint, because twenty requests each holding a connection
            // open for a multi-megabyte transfer cost far more than twenty that finish in a
            // millisecond.
            // The distributed per-address budget is the ANTI-ENUMERATION control, and it is the one
            // that has to be counted fleet-wide: a guessing script spread across replicas would walk
            // straight past a per-process counter.
            .RequireRateLimiting(Configuration.DownloadGatewayExtensions.PerAddressPolicy)
            .RequireRateLimiting(Configuration.DownloadGatewayExtensions.ConcurrentDownloadsPolicy)
            .RequireDistributedRateLimitPerAddress(
                Configuration.DownloadGatewayExtensions.RedeemRules(
                    app.Services.GetRequiredService<IOptions<Configuration.GatewayRateLimitOptions>>().Value))
            .WithName("RedeemDownloadToken")
            .WithSummary("Redeems a single-use download link and streams the statement.")
            .WithDescription(
                "Unauthenticated by design: the token is the credential. Every failure - expired, "
                + "already consumed, revoked, never existed, or malformed - returns a byte-identical "
                + "404. Range requests are not supported (Accept-Ranges: none): a resumed transfer "
                + "would be a second request against a single-use token. See ADR-0015 and ADR-0016.")
            .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return app;
    }

    private static async Task<IResult> RedeemAsync(
        string token,
        HttpContext http,
        IUnitOfWork unitOfWork,
        IDownloadTokenRepository tokens,
        IStatementReadRepository statements,
        IStatementContentStore content,
        RequestAudit audit,
        DownloadMetrics metrics,
        IOptions<DownloadOptions> options,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        DownloadOptions settings = options.Value;
        long startedAt = time.GetTimestamp();

        // -----------------------------------------------------------------------------------------
        // STEP 2. Decode. A malformed value is the ordinary case on a public endpoint, not an
        // exception - and it must produce the SAME denial as a wrong-but-well-formed token. A 400
        // here would tell an attacker their encoding was right, which is free information.
        //
        // The buffer is stackalloc'd: the plaintext never reaches the heap.
        // -----------------------------------------------------------------------------------------
        Span<byte> raw = stackalloc byte[TokenSecret.SizeInBytes];
        TokenHash hash;

        if (!TokenSecret.TryParse(token, raw, out TokenSecret secret))
        {
            raw.Clear();
            return await DenyAsync(
                http, audit, metrics, DenialReason.MalformedToken, settings, time, startedAt, cancellationToken)
                .ConfigureAwait(false);
        }

        // STEP 3. Hash, then immediately forget the plaintext. Everything downstream works from the
        // hash, which is safe to log, safe to put in a span, and useless to anyone who steals it.
        hash = secret.ComputeHash();
        raw.Clear();

        string? userAgentHash = HashUserAgent(http.Request.Headers.UserAgent.ToString());

        // -----------------------------------------------------------------------------------------
        // STEPS 4-7. Consume atomically, resolve the statement, audit, and COMMIT - all before a
        // single byte is streamed.
        // -----------------------------------------------------------------------------------------
        RedemptionOutcome outcome = await unitOfWork.ExecuteAsync(
            async (NpgsqlTransaction transaction, CancellationToken token2) =>
            {
                TokenConsumption? consumed = await tokens
                    .ConsumeAsync(hash, http.Connection.RemoteIpAddress, userAgentHash, transaction, token2)
                    .ConfigureAwait(false);

                if (consumed is null)
                {
                    return RedemptionOutcome.Denied(null);
                }

                // The token carries BOTH the statement and the owner, so this fetch re-enforces the
                // binding: a token whose customer no longer owns the statement resolves to nothing.
                // It is also a PRUNED point lookup, because the token carried the partition key.
                Statement? statement = await statements
                    .FindAsync(consumed.Value.StatementId, consumed.Value.StatementPeriod, consumed.Value.CustomerId, token2)
                    .ConfigureAwait(false);

                return statement is null
                    ? RedemptionOutcome.Denied(consumed)
                    : RedemptionOutcome.Consumed(consumed.Value, statement);
            },
            cancellationToken).ConfigureAwait(false);

        // -----------------------------------------------------------------------------------------
        // STEP 6a. Zero rows. Diagnose OUTSIDE the transaction, audit the reason, deny generically.
        // -----------------------------------------------------------------------------------------
        if (outcome.Statement is null)
        {
            string reason = outcome.Consumption is null
                ? await tokens.DiagnoseFailureAsync(hash, cancellationToken).ConfigureAwait(false)

                // The token was valid but the statement it names is gone. Treated as a denial rather
                // than an error: from the caller's side it is simply unavailable.
                : DenialReason.NotFound;

            return await DenyAsync(
                http, audit, metrics, reason, settings, time, startedAt, cancellationToken,
                outcome.Consumption?.CustomerId, outcome.Consumption?.StatementId).ConfigureAwait(false);
        }

        TokenConsumption consumption = outcome.Consumption!.Value;
        Statement resolved = outcome.Statement;

        // -----------------------------------------------------------------------------------------
        // STEP 6b. The token is PROVEN VALID at this point, so a specific status is safe here in a
        // way it never is above. The caller has demonstrated possession of a legitimate credential
        // for this exact statement; telling them it is archived reveals nothing they did not already
        // know, and telling them nothing would send them to support instead of to the restore flow.
        //
        // The uniform-denial rule governs TOKEN VALIDATION failures. It does not govern outcomes
        // reached after validation succeeded. That distinction is the whole reason these two codes
        // can coexist with the rule above.
        // -----------------------------------------------------------------------------------------
        if (resolved.Status == StatementStatus.Purged)
        {
            await audit.RecordAsync(
                http, AuditAction.AccessDenied, AuditOutcome.Denied,
                consumption.CustomerId, consumption.StatementId, DenialReason.NotFound,
                Detail(("token_hash", hash.ToString()), ("status", "PURGED")),
                cancellationToken).ConfigureAwait(false);

            return Results.Problem(
                title: "Statement no longer available",
                detail: "This statement has passed its retention period and has been destroyed.",
                statusCode: StatusCodes.Status410Gone);
        }

        if (resolved.Status == StatementStatus.Archived)
        {
            await audit.RecordAsync(
                http, AuditAction.AccessDenied, AuditOutcome.Denied,
                consumption.CustomerId, consumption.StatementId, DenialReason.NotFound,
                Detail(("token_hash", hash.ToString()), ("status", "ARCHIVED")),
                cancellationToken).ConfigureAwait(false);

            return Results.Problem(
                title: "Statement is archived",
                detail: "This statement is in cold storage and must be restored before it can be downloaded.",
                statusCode: StatusCodes.Status409Conflict);
        }

        // -----------------------------------------------------------------------------------------
        // STEP 7. DOWNLOAD_STARTED is audited and the transaction is COMMITTED before streaming.
        //
        // ⚠ IF THE CLIENT DISCONNECTS AT 40%, THE TOKEN STAYS CONSUMED. That is correct, and the
        // alternative is a hole. Consumption records ATTEMPTED ACCESS, and attempted access is what
        // an audit trail must capture. If the token were only consumed on SUCCESSFUL completion, an
        // attacker could replay it indefinitely simply by aborting the connection every time - and
        // each abort would leave no evidence that access had been granted at all. See ADR-0017.
        // -----------------------------------------------------------------------------------------
        await audit.RecordAsync(
            http, AuditAction.DownloadStarted, AuditOutcome.Success,
            consumption.CustomerId, consumption.StatementId,
            detail: Detail(
                ("token_hash", hash.ToString()),
                ("link_id", consumption.Id.ToString()),
                ("size_bytes", resolved.Storage?.SizeBytes)),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (resolved.Storage is null)
        {
            metrics.Denied(DenialReason.NotFound);
            return Deny();
        }

        // STEP 8. Open the stream. Prompt 4 swapped this adapter for an encrypting one and NOT ONE
        // LINE BELOW CHANGED - which was the test of whether this port was the right shape.
        //
        // What did change is the catch. A decryption failure is not a caller error and has no
        // equivalent in the filesystem adapter, so there was nothing here to handle it: it would
        // have propagated to the global exception handler as a 500, after the response had already
        // begun. That is the one behaviour the design cannot allow, so this block exists.
        StatementContent? statementContent;

        try
        {
            statementContent = await content
                .OpenReadAsync(resolved.Storage, cancellationToken).ConfigureAwait(false);
        }
        catch (StatementDelivery.Crypto.Framing.CiphertextIntegrityException ex)
        {
            // A DECRYPTION FAILURE IS AN INCIDENT, NOT A DENIAL - but it answers like one.
            //
            // The token was valid. Ownership was proven. The row was found. And the bytes it points
            // at did not authenticate, which means the object was corrupted, truncated, substituted
            // or tampered with - or the row was rewritten to point at somebody else's object, which
            // is the attack the frame AAD exists to defeat.
            //
            // The caller gets the same 404 as every other failure, because telling them anything
            // else would confirm which of those it was. The metric pages an operator.
            metrics.DecryptionFailed();

            await audit.RecordAsync(
                http, AuditAction.AccessDenied, AuditOutcome.Error,
                consumption.CustomerId, consumption.StatementId, DenialReason.DecryptionFailed,
                Detail(
                    ("token_hash", hash.ToString()),
                    ("link_id", consumption.Id.ToString()),
                    ("integrity_failure", ex.Reason.ToString())),
                cancellationToken).ConfigureAwait(false);

            return Deny();
        }

        if (statementContent is null)
        {
            metrics.Denied(DenialReason.NotFound);
            return Deny();
        }

        return new StreamedStatementResult(statementContent, resolved, consumption, hash, audit, metrics, settings);
    }

    /// <summary>
    /// The uniform denial. EVERY failure path returns exactly this.
    /// </summary>
    /// <remarks>
    /// Same status, same body, same headers - no branch may vary any of them. Expired, consumed,
    /// revoked, never-existed and malformed are indistinguishable to the caller; the difference
    /// lives in the audit trail and nowhere else.
    /// </remarks>
    private static IResult Deny() => Results.Json(
        new ProblemDetails
        {
            Type = "https://statement-delivery.example/problems/download-unavailable",
            Title = "Download unavailable",
            Status = StatusCodes.Status404NotFound,
            Detail = "This download link is not available.",
        },
        statusCode: StatusCodes.Status404NotFound,
        contentType: "application/problem+json");

    private static async Task<IResult> DenyAsync(
        HttpContext http,
        RequestAudit audit,
        DownloadMetrics metrics,
        string reason,
        DownloadOptions settings,
        TimeProvider time,
        long startedAt,
        CancellationToken cancellationToken,
        StatementDelivery.Domain.Identifiers.CustomerId? customerId = null,
        StatementDelivery.Domain.Identifiers.StatementId? statementId = null)
    {
        metrics.Denied(reason);

        // AUDIT RICHLY, RESPOND OPAQUELY. The reason is recorded here and never returned.
        await audit.RecordAsync(
            http, AuditAction.AccessDenied, AuditOutcome.Denied,
            customerId, statementId, reason,
            Detail(("outcome", "denied")),
            cancellationToken).ConfigureAwait(false);

        // Pad to the configured floor so the cheap failures do not finish visibly faster than the
        // expensive ones. See DownloadOptions for an honest account of what this does and does not
        // achieve.
        TimeSpan elapsed = time.GetElapsedTime(startedAt);
        TimeSpan floor = TimeSpan.FromMilliseconds(settings.DenialFloorMilliseconds);

        if (elapsed < floor)
        {
            await Task.Delay(floor - elapsed, time, cancellationToken).ConfigureAwait(false);
        }

        return Deny();
    }

    private static Dictionary<string, object?> Detail(params (string Key, object? Value)[] entries)
    {
        var detail = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach ((string key, object? value) in entries)
        {
            detail[key] = value;
        }

        return detail;
    }

    private static string? HashUserAgent(string? userAgent) =>
        string.IsNullOrEmpty(userAgent)
            ? null
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(userAgent)));

    private readonly record struct RedemptionOutcome(TokenConsumption? Consumption, Statement? Statement)
    {
        public static RedemptionOutcome Denied(TokenConsumption? consumption) => new(consumption, null);

        public static RedemptionOutcome Consumed(TokenConsumption consumption, Statement statement) =>
            new(consumption, statement);
    }

    /// <summary>
    /// Streams the statement with a bounded, pooled buffer and constant memory use.
    /// </summary>
    /// <remarks>
    /// A custom <see cref="IResult"/> rather than <c>Results.Stream</c> so that the byte count, the
    /// completion audit and the incomplete-transfer metric are all handled on the same code path
    /// that does the copying.
    /// </remarks>
    private sealed class StreamedStatementResult : IResult
    {
        private readonly StatementContent _content;
        private readonly Statement _statement;
        private readonly TokenConsumption _consumption;
        private readonly TokenHash _hash;
        private readonly RequestAudit _audit;
        private readonly DownloadMetrics _metrics;
        private readonly DownloadOptions _options;

        public StreamedStatementResult(
            StatementContent content,
            Statement statement,
            TokenConsumption consumption,
            TokenHash hash,
            RequestAudit audit,
            DownloadMetrics metrics,
            DownloadOptions options)
        {
            _content = content;
            _statement = statement;
            _consumption = consumption;
            _hash = hash;
            _audit = audit;
            _metrics = metrics;
            _options = options;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            await using StatementContent content = _content;
            CancellationToken cancellationToken = httpContext.RequestAborted;

            ApplySecurityHeaders(httpContext.Response, _statement, content);

            long written = 0;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(_options.StreamBufferBytes);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                // BOUNDED, POOLED, COUNTED. Never ReadToEndAsync, never ToArray, never MemoryStream -
                // any of those would tie memory use to statement size times concurrency, and a
                // 200 MB statement would land on the large object heap.
                int read;
                while ((read = await content.Stream
                    .ReadAsync(buffer.AsMemory(0, _options.StreamBufferBytes), cancellationToken)
                    .ConfigureAwait(false)) > 0)
                {
                    await httpContext.Response.Body
                        .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);

                    written += read;
                }

                await httpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (StatementDelivery.Crypto.Framing.CiphertextIntegrityException ex)
            {
                // ⚠ A FRAME FAILED TO AUTHENTICATE PART WAY THROUGH THE TRANSFER.
                //
                // This clause has to exist and has to come first. The header is verified before the
                // response starts (see PrimeAsync), but everything else the format defends against -
                // a flipped ciphertext byte, a reordered frame, a DROPPED FINAL FRAME, a
                // content_sha256 mismatch - is only detectable while streaming. Without this catch
                // those escape: CiphertextIntegrityException is deliberately neither an IOException
                // nor a CryptographicException, so the clause below does not see it, and it reaches
                // the global handler. On the first frame that produces a 500 with a traceId instead
                // of the uniform 404; later it kills the response mid-body with no audit record and
                // no metric - which is precisely the case an operator most needs to hear about.
                _metrics.DecryptionFailed();

                await _audit.RecordAsync(
                    httpContext,
                    AuditAction.AccessDenied,
                    AuditOutcome.Error,
                    _consumption.CustomerId,
                    _consumption.StatementId,
                    DenialReason.DecryptionFailed,
                    Detail(
                        ("token_hash", _hash.ToString()),
                        ("link_id", _consumption.Id.ToString()),
                        ("integrity_failure", ex.Reason.ToString()),
                        ("bytes_sent", written)),

                    // CancellationToken.None deliberately, as on the incomplete path: the request may
                    // already be tearing down and this record must survive it.
                    CancellationToken.None).ConfigureAwait(false);

                if (httpContext.Response.HasStarted)
                {
                    // ABORT, DO NOT LET IT END CLEANLY. Bytes are already on the wire - that is the
                    // limitation ADR-0019 §A5 names and accepts - so the only thing still under our
                    // control is whether the client can tell. A clean end to a 200 with a
                    // Content-Length it never reached is a silently truncated statement; a reset
                    // connection is an unambiguous failure that every HTTP client surfaces.
                    httpContext.Abort();
                    return;
                }

                // Nothing has left yet, so the uniform denial is still available and is what the
                // caller gets - byte-identical to an expired token, a revoked one and one that never
                // existed.
                //
                // CLEAR FIRST, AND THIS IS NOT OPTIONAL. ApplySecurityHeaders has already staged a
                // 200, a Content-Length of the whole statement, and a PDF Content-Disposition. Those
                // are still only staged - nothing has flushed - but writing the denial over the top
                // of them would emit a 404 announcing several megabytes it will never send, which
                // leaves the client waiting on a body that never arrives and is trivially
                // distinguishable from every other denial. Clear() resets status, headers and the
                // buffered body, which is exactly the state Deny() expects.
                httpContext.Response.Clear();

                await Deny().ExecuteAsync(httpContext).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // The client hung up. The token STAYS CONSUMED - see ADR-0017. Recorded so the
                // range-request decision can be revisited with evidence rather than intuition.
                _metrics.Incomplete();
                await RecordOutcomeAsync(httpContext, AuditAction.DownloadIncomplete, written, stopwatch.Elapsed)
                    .ConfigureAwait(false);
                return;
            }
            finally
            {
                // clearArray, since Prompt 4. This buffer held DECRYPTED statement content - it did
                // not before, when the same bytes came off a plain file and were no more sensitive
                // in the pool than on the disk they came from. Returning it unwiped now hands the
                // next renter a window onto somebody's bank statement, and the crypto streams
                // already wipe theirs for exactly this reason.
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }

            _metrics.Completed(written);
            await RecordOutcomeAsync(httpContext, AuditAction.DownloadCompleted, written, stopwatch.Elapsed)
                .ConfigureAwait(false);
        }

        private Task<AuditReceipt> RecordOutcomeAsync(HttpContext httpContext, string action, long written, TimeSpan elapsed) =>

            // OUTSIDE the transaction that consumed the token, which has long since committed.
            // Holding it open across a multi-megabyte transfer would pin a pooled connection - and
            // behind PgBouncer that is one of a few dozen the whole fleet shares.
            //
            // CancellationToken.None deliberately: the client has already disconnected on the
            // incomplete path, so the request token is cancelled, and the whole point is to record
            // that fact.
            _audit.RecordAsync(
                httpContext,
                action,
                action == AuditAction.DownloadCompleted ? AuditOutcome.Success : AuditOutcome.Error,
                _consumption.CustomerId,
                _consumption.StatementId,
                detail: Detail(
                    ("token_hash", _hash.ToString()),
                    ("link_id", _consumption.Id.ToString()),
                    ("bytes_sent", written),
                    ("duration_ms", (long)elapsed.TotalMilliseconds)),
                cancellationToken: CancellationToken.None);

        /// <summary>
        /// Applies every security header, on every successful response.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>Referrer-Policy: no-referrer</c> is the one that matters MOST here and is the easiest
        /// to overlook. If a statement URL is ever rendered in a page context, then without it the
        /// token - which is the whole credential - is sent in the <c>Referer</c> header to every
        /// third-party resource that page loads: analytics, fonts, advertising. One omitted header
        /// would hand the credential to an arbitrary number of unrelated origins.
        /// </para>
        /// <para>
        /// <c>Accept-Ranges: none</c> is a decision, not an oversight. Range requests enable
        /// resumable downloads and are fundamentally incompatible with a single-use token: resuming
        /// means a SECOND request, and the token is already consumed. See ADR-0016.
        /// </para>
        /// </remarks>
        private static void ApplySecurityHeaders(HttpResponse response, Statement statement, StatementContent content)
        {
            IHeaderDictionary headers = response.Headers;

            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = content.ContentType;
            response.ContentLength = content.Length;

            // A statement must never sit in a shared cache, a CDN, or a browser's disk cache where
            // the next user of the machine can retrieve it.
            headers.CacheControl = "no-store, no-cache, must-revalidate, private";
            headers.Pragma = "no-cache";
            headers.Expires = "0";

            headers.ContentDisposition = string.Create(
                CultureInfo.InvariantCulture,
                $"attachment; filename=\"statement-{statement.Period.Start:yyyy-MM}.pdf\"");

            // Stops a browser from second-guessing the declared type and rendering the bytes as
            // something executable in this origin.
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";

            // THE IMPORTANT ONE. See the remarks above.
            headers["Referrer-Policy"] = "no-referrer";

            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers.StrictTransportSecurity = "max-age=63072000; includeSubDomains; preload";

            headers.AcceptRanges = "none";
        }
    }
}
