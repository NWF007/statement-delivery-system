using Generation.Worker;
using Generation.Worker.Configuration;
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
// /health/live, /health/ready and /ping surface as the APIs. There are no other HTTP endpoints:
// an orchestrator that cannot probe a worker cannot tell a wedged replica from a busy one.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPersistence(serviceName: "generation-worker");
builder.AddObjectStorage();
builder.AddGenerationWorker();

WebApplication app = builder.Build();

app.MapDefaultEndpoints();

await app.RunAsync().ConfigureAwait(false);
return 0;
