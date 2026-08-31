using Generation.Worker;
using Generation.Worker.Configuration;
using Generation.Worker.Ledger;
using StatementDelivery.Crypto;
using StatementDelivery.Persistence;
using StatementDelivery.Rendering;
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
builder.AddCrypto();
builder.AddStatementRendering();
builder.AddLedgerClient();

// The outbox WRITE half: statement.available lands in the outbox inside the render's finalize
// transaction; the relay hosted below is the read half.
builder.Services.AddSingleton<
    StatementDelivery.Messaging.IIntegrationEventPublisher,
    StatementDelivery.Messaging.Outbox.OutboxEventPublisher>();

builder.AddGenerationWorker();

// includeWriter: true. This is the only service that may WRITE statement content - it is the one
// that renders it. The download gateway gets the reader alone, so "the internet-facing service
// cannot create or overwrite a statement" is enforced by what it can inject rather than by
// convention.
builder.AddEncryptedContentStore(includeWriter: true);

WebApplication app = builder.Build();

app.MapDefaultEndpoints();

await app.RunAsync().ConfigureAwait(false);
return 0;
