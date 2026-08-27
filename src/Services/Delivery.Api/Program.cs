using Delivery.Api.Auditing;
using Delivery.Api.Configuration;
using Delivery.Api.Downloads;
using Delivery.Api.Statements;
using Scalar.AspNetCore;
using StatementDelivery.Persistence;
using StatementDelivery.ServiceDefaults;
using StatementDelivery.ServiceDefaults.HealthChecks;

// Chiseled images have no shell and no curl, so the container health check is the application
// probing itself: `dotnet <Service>.dll --healthcheck`. See HealthCheckProbe.
if (HealthCheckProbe.IsProbe(args))
{
    return await HealthCheckProbe.RunAsync().ConfigureAwait(false);
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPersistence(serviceName: "delivery-api");
builder.AddDeliveryApi();

WebApplication app = builder.Build();

// First, so the exception handler it installs sits in front of everything below it.
app.MapDefaultEndpoints();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapStatementEndpoints();
app.MapDownloadLinkEndpoints();
app.MapAuditVerifyEndpoint();

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
