using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using StatementDelivery.Domain.Auditing;
using StatementDelivery.Persistence.Auditing;

namespace Delivery.Api.Auditing;

/// <summary>One chain's verification outcome, as returned to a caller.</summary>
/// <param name="ChainId">The chain examined.</param>
/// <param name="Verified">Whether every hash in the range recomputed correctly.</param>
/// <param name="EventsChecked">How many records were walked.</param>
/// <param name="FirstBrokenSeq">The first sequence number that failed, when broken.</param>
public sealed record ChainVerificationResponse(
    short ChainId,
    bool Verified,
    long EventsChecked,
    long? FirstBrokenSeq);

/// <summary>The result of verifying every chain.</summary>
/// <param name="Verified">True only when every chain verified.</param>
/// <param name="ChainsChecked">How many chains were walked.</param>
/// <param name="EventsChecked">Total records walked across all chains.</param>
/// <param name="Chains">Per-chain detail.</param>
public sealed record AuditVerificationResponse(
    bool Verified,
    int ChainsChecked,
    long EventsChecked,
    IReadOnlyList<ChainVerificationResponse> Chains);

/// <summary>
/// The operator-facing endpoint that re-walks the audit chains and recomputes every hash.
/// </summary>
/// <remarks>
/// <para>
/// STAFF ONLY. A customer has no business enumerating the audit trail, and the verification result
/// is an operational signal rather than customer-visible information. It is behind an explicit
/// scope rather than merely behind authentication, because "any valid bearer token" on this
/// platform means "any customer".
/// </para>
/// <para>
/// WHAT A CLEAN RESULT PROVES: within the verified range, no record was altered and none was
/// removed from the middle. Either would break every hash after it.
/// </para>
/// <para>
/// WHAT IT DOES NOT PROVE: that the chain has not been TRUNCATED, or wholesale rewritten. Both
/// produce a shorter or different chain that is internally perfectly consistent. Closing that gap
/// needs a terminal hash held somewhere the database administrator cannot reach - which is what
/// IChainAnchor exists for, and which is still a no-op. Reporting "verified" here is therefore an
/// honest statement about integrity and NOT a statement about completeness. See ADR-0010.
/// </para>
/// </remarks>
public static class AuditVerifyEndpoint
{
    /// <summary>Maps the audit verification endpoint.</summary>
    /// <param name="app">The web application.</param>
    /// <returns>The application, for chaining.</returns>
    public static WebApplication MapAuditVerifyEndpoint(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/v1/audit/verify", VerifyAsync)
            .WithTags(Configuration.DeliveryApiOpenApi.Tags.Audit)
            .RequireAuthorization(Configuration.DeliveryApiExtensions.StaffPolicy)
            .WithName("VerifyAuditChains")
            .WithSummary("Re-walks the audit hash chains and recomputes every hash.")
            .WithDescription(
                "Staff only. Verifies that no record has been altered or removed from the middle of "
                + "a chain. It CANNOT detect truncation or wholesale rewriting - that needs an "
                + "externally held terminal hash (ADR-0010). Omit chainId to verify every chain.")
            .Produces<AuditVerificationResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> VerifyAsync(
        IAuditVerifier verifier,
        IOptions<AuditOptions> auditOptions,
        CancellationToken cancellationToken,
        short? chainId = null,
        long fromSeq = 1,
        long? toSeq = null)
    {
        int chainCount = auditOptions.Value.ChainCount;

        if (chainId is not null && (chainId < 0 || chainId >= chainCount))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Unknown chain",
                detail: string.Create(
                    CultureInfo.InvariantCulture,
                    $"chainId must be between 0 and {chainCount - 1}."));
        }

        if (fromSeq < 1)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid range",
                detail: "fromSeq must be at least 1.");
        }

        // long.MaxValue rather than a count query. The verifier streams and stops at the end of the
        // chain on its own, so an open-ended upper bound costs nothing and avoids a second query
        // whose answer would already be stale by the time the walk reached it.
        long upper = toSeq ?? long.MaxValue;

        if (upper < fromSeq)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid range",
                detail: "toSeq must not be less than fromSeq.");
        }

        IEnumerable<short> chains = chainId is not null
            ? [chainId.Value]
            : Enumerable.Range(0, chainCount).Select(static id => (short)id);

        var results = new List<ChainVerificationResponse>(chainCount);
        long totalEvents = 0;
        bool allVerified = true;

        // SEQUENTIAL, not parallel. Each chain walk is a streaming read holding a connection; running
        // sixteen at once would take sixteen connections from a pool sized for request traffic, and
        // this is an operator tool that runs occasionally, not a hot path.
        foreach (short chain in chains)
        {
            ChainVerification result = await verifier
                .VerifyChainAsync(chain, fromSeq, upper, cancellationToken)
                .ConfigureAwait(false);

            // The expected and actual hashes are DELIBERATELY not returned. They are the recomputed
            // internals of the trail; an operator investigating a break reads them from the logs,
            // and returning them over HTTP hands anyone who reaches this endpoint the material to
            // work out how the chain is constructed.
            results.Add(new ChainVerificationResponse(
                result.ChainId,
                result.Verified,
                result.EventsChecked,
                result.FirstBrokenSeq));

            totalEvents += result.EventsChecked;
            allVerified &= result.Verified;
        }

        var response = new AuditVerificationResponse(allVerified, results.Count, totalEvents, results);

        // 409 on a broken chain, not 200-with-a-flag. A monitoring probe that only watches status
        // codes must not read a tampered audit trail as a healthy response - and something will
        // eventually watch this endpoint with exactly that little care.
        return allVerified
            ? Results.Ok(response)
            : Results.Json(response, statusCode: StatusCodes.Status409Conflict);
    }
}
