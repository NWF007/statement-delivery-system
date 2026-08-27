using Retention.Worker.Configuration;
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

// A WebApplication host rather than a bare generic host, purely so the worker exposes the same
// /health/live, /health/ready and /ping surface as the APIs. There are no other HTTP endpoints.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPersistence(serviceName: "retention-worker");
// NO OBJECT STORAGE HERE UNTIL PROMPT 6. This service resolves nothing that touches a bucket, so
// AddObjectStorage() only registered a client nothing injected and a readiness check on a bucket it
// never reads - while requiring credentials it had no use for. Prompt 6's purge job adds it back
// alongside AddCrypto() and AddEncryptedContentStore(includeWriter: false), so the Object Lock
// verification arrives in the same commit as the ability to delete an object.
builder.AddRetentionWorker();

WebApplication app = builder.Build();

app.MapDefaultEndpoints();

await app.RunAsync().ConfigureAwait(false);
return 0;
