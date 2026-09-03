using Download.Gateway.Configuration;
using Download.Gateway.Downloads;
using Scalar.AspNetCore;
using StatementDelivery.Crypto;
using StatementDelivery.Persistence;
using StatementDelivery.ServiceDefaults;
using StatementDelivery.ServiceDefaults.HealthChecks;
using StatementDelivery.ServiceDefaults.Storage;

// Chiseled images have no shell and no curl, so the container health check is the application
// probing itself: `dotnet <Service>.dll --healthcheck`. See HealthCheckProbe.
if (HealthCheckProbe.IsProbe(args))
{
    return await HealthCheckProbe.RunAsync().ConfigureAwait(false);
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPersistence(serviceName: "download-gateway");
builder.AddObjectStorage();
builder.AddCrypto();
builder.AddDownloadGateway();

// The encrypting S3 adapter replaced the filesystem store here. includeWriter is false - this
// service reads statements and must not be able to write one, and that is enforced by what it can
// inject rather than by everyone remembering not to call the wrong method.
builder.AddEncryptedContentStore(includeWriter: false);

WebApplication app = builder.Build();

// First, so the exception handler it installs sits in front of everything below it.
app.MapDefaultEndpoints();

// Before the rate limiter: the limiter partitions on the client address, so the address must be
// resolved first. Configured to ForwardedHeaders.None unless RateLimiting:TrustForwardedHeaders is
// set together with an explicit proxy list - see DownloadGatewayExtensions.
app.UseForwardedHeaders();

app.UseRateLimiter();

app.MapDownloadEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

await app.RunAsync().ConfigureAwait(false);
return 0;
