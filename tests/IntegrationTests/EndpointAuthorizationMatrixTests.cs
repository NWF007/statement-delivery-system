using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// Every mapped endpoint either requires authorisation or sits on the explicit allow-list.
/// </summary>
/// <remarks>
/// <para>
/// THE TEST THAT CATCHES THE ENDPOINT SOMEONE ADDS IN SIX MONTHS without thinking about auth.
/// It enumerates <see cref="EndpointDataSource"/> on the REAL hosts — every route that actually
/// exists, not the ones a hand-written table remembers — and fails the build if an unlisted
/// anonymous endpoint appears.
/// </para>
/// <para>
/// DELIBERATELY NOT Docker-gated: host construction resolves routing without touching the
/// database (Npgsql connects lazily), so this runs on every local build. The connection string
/// is a syntactically valid dead end.
/// </para>
/// </remarks>
public sealed class EndpointAuthorizationMatrixTests
{
    private const string DeadConnectionString =
        "Host=localhost;Port=1;Database=never;Username=never;Password=never";

    /// <summary>
    /// Routes allowed to answer without a bearer token, each with its written justification.
    /// Adding a route here is a reviewed security decision, not a default.
    /// </summary>
    private static readonly Dictionary<string, string> AllowList = new(StringComparer.Ordinal)
    {
        // Infrastructure probes: orchestrators cannot fetch tokens, and the responses carry
        // liveness booleans and build identity only.
        ["/health/live"] = "liveness probe - no data beyond up/down",
        ["/health/ready"] = "readiness probe - dependency booleans only",
        ["/ping"] = "build identity for operators - name/version/instance",

        // THE one justified anonymous business route: the token IS the credential. A bearer
        // requirement here would break the emailed-link flow the endpoint exists for, and the
        // token is single-use, hashed at rest, and consumed atomically (ADR-0013/0017).
        ["/v1/d/{token}"] = "the capability URL - the token is the credential",

        // Development-only surfaces, mapped inside IsDevelopment guards. Their absence in
        // Production is separately pinned by DevelopmentTokenEndpoint_IsMappedOnlyInsideThe-
        // DevelopmentGuard and by the OpenAPI mapping being dev-guarded in Program.cs.
        ["/v1/dev/tokens"] = "dev-only token mint, inside app.Environment.IsDevelopment()",
        ["/openapi/{documentName}.json"] = "dev-only OpenAPI document, inside the same guard",
    };

    /// <summary>
    /// Dev-only route PREFIXES: Scalar maps its UI and static assets under /scalar with
    /// version-dependent names, all inside the same IsDevelopment guard. Prefix-matched so a
    /// Scalar upgrade does not fail the build over a renamed favicon - while anything OUTSIDE
    /// /scalar still must be listed one route at a time.
    /// </summary>
    private static readonly string[] DevOnlyPrefixes = ["/scalar"];

    [Fact]
    public void DeliveryApi_EveryEndpoint_RequiresAuthOrIsAllowListed()
    {
        using var factory = new DeliveryApiFactory(DeadConnectionString);
        AssertAllRoutesCovered(factory.Services, expectAnonymousBusinessRoutes: false);
    }

    [Fact]
    public void DownloadGateway_EveryEndpoint_RequiresAuthOrIsAllowListed()
    {
        using var factory = new DownloadGatewayFactory(
            DeadConnectionString, "http://localhost:1", 30, 120, 0);
        AssertAllRoutesCovered(factory.Services, expectAnonymousBusinessRoutes: true);
    }

    private static void AssertAllRoutesCovered(IServiceProvider services, bool expectAnonymousBusinessRoutes)
    {
        var endpoints = services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .ToList();

        endpoints.ShouldNotBeEmpty("an empty enumeration would vacuously pass - the host did not map its routes");

        var unaccounted = new List<string>();
        bool sawJustifiedAnonymous = false;

        foreach (RouteEndpoint endpoint in endpoints)
        {
            string pattern = "/" + (endpoint.RoutePattern.RawText ?? "").TrimStart('/');

            bool requiresAuth = endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null
                && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null;

            if (requiresAuth)
            {
                continue;
            }

            if (DevOnlyPrefixes.Any(prefix =>
                    pattern.Equals(prefix, StringComparison.Ordinal)
                    || pattern.StartsWith(prefix + "/", StringComparison.Ordinal)))
            {
                continue;
            }

            if (AllowList.ContainsKey(pattern))
            {
                if (pattern == "/v1/d/{token}")
                {
                    sawJustifiedAnonymous = true;
                }

                continue;
            }

            unaccounted.Add(pattern);
        }

        unaccounted.ShouldBeEmpty(
            "every route must either carry an authorisation requirement or appear on the "
            + "allow-list WITH a written justification. An unlisted anonymous endpoint is "
            + "exactly the defect this test exists to catch.");

        if (expectAnonymousBusinessRoutes)
        {
            sawJustifiedAnonymous.ShouldBeTrue(
                "the gateway must still map /v1/d/{token} - its disappearance means the "
                + "enumeration is looking at the wrong host");
        }
    }
}
