using Retention.Worker.Configuration;
using StatementDelivery.Crypto;
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

// A WebApplication host rather than a bare generic host, purely so the worker exposes the same
// /health/live, /health/ready and /ping surface as the APIs. There are no other HTTP endpoints.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPersistence(serviceName: "retention-worker");

// Object storage was wired in here in the same commit that gave the worker the ability to delete
// an object, exactly as the earlier note in this file promised. The full admin surface (delete,
// lock reads, legal holds, listing) belongs to THIS service alone; AddCrypto brings the key
// hierarchy the erasure executor destroys keys through; the encrypted content store registration
// brings the Object Lock readiness verification.
builder.AddObjectStorage();
builder.AddCrypto();
builder.AddEncryptedContentStore(includeWriter: false);
builder.AddObjectAdminStore();
builder.AddHoldResolution();
builder.AddRetentionWorker();

WebApplication app = builder.Build();

app.MapDefaultEndpoints();

await app.RunAsync().ConfigureAwait(false);
return 0;
