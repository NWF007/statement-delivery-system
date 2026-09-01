using Delivery.Api.Auditing;
using Delivery.Api.Configuration;
using Delivery.Api.Downloads;
using Delivery.Api.Retention;
using Delivery.Api.Runs;
using Delivery.Api.Statements;
using Scalar.AspNetCore;
using StatementDelivery.Persistence;
using StatementDelivery.ServiceDefaults;
using StatementDelivery.ServiceDefaults.HealthChecks;
using StatementDelivery.ServiceDefaults.Retention;
using StatementDelivery.ServiceDefaults.Storage;

// Chiseled images have no shell and no curl, so the container health check is the application
// probing itself: `dotnet <Service>.dll --healthcheck`. See HealthCheckProbe.
if (HealthCheckProbe.IsProbe(args))
{
    return await HealthCheckProbe.RunAsync().ConfigureAwait(false);
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPersistence(serviceName: "delivery-api");

// Object storage arrives in Prompt 6, and ONLY the admin surface: the legal-hold endpoints set
// and release object-store holds (dual-layer enforcement, ADR-0037). This service still cannot
// read or write statement CONTENT - it registers no content store, and in production its IAM
// principal is scoped to Get/PutObjectLegalHold and nothing else.
builder.AddObjectStorage();
builder.AddObjectAdminStore();
builder.AddHoldResolution();
builder.AddDeliveryApi();

WebApplication app = builder.Build();

// First, so the exception handler it installs sits in front of everything below it.
app.MapDefaultEndpoints();

// ORDER IS LOAD-BEARING. UseRateLimiter MUST come after UseAuthentication: the PerCallerPolicy
// partition key reads httpContext.User.FindFirst("sub"), and before authentication runs that
// principal is empty - so every request fell through to the RemoteIpAddress branch and the whole
// fleet shared ONE 120/minute partition. Measured on 2026-09-01: 150 requests from 150 DISTINCT
// customers returned exactly 120x201 then 30x429. That is precisely the "one corporate NAT
// punishes every customer behind it" failure the partition key was written to avoid.
// Unauthenticated requests still partition by IP, via the same fallback.
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapStatementEndpoints();
app.MapDownloadLinkEndpoints();
app.MapAuditVerifyEndpoint();
app.MapStatementRunEndpoints();
app.MapLegalHoldEndpoints();
app.MapErasureEndpoints();
app.MapRestoreEndpoints();
app.MapReconciliationEndpoints();

if (app.Environment.IsDevelopment())
{
    // Development only. The OpenAPI document enumerates the entire attack surface, and a public
    // API has no reason to publish that to unauthenticated callers.
    app.MapOpenApi();
    app.MapScalarApiReference();

    // Development only, and guarded again inside: the endpoint cannot sign anything unless a
    // development signing key is configured, which JwtOptionsValidator forbids outside
    // Development. Without it the authenticated read path cannot be exercised at all.
    app.MapDevTokenEndpoint();
}

await app.RunAsync().ConfigureAwait(false);
return 0;

/// <summary>
/// Entry point marker.
/// </summary>
/// <remarks>
/// Top-level statements generate an INTERNAL Program class, which WebApplicationFactory cannot
/// reach from a test assembly. Declaring it public here is the standard way to make the real
/// application - its real authentication, its real routing, its real DI graph - testable end to
/// end. Testing a hand-assembled copy of the pipeline instead would prove only that the copy works.
/// </remarks>
public partial class Program;
