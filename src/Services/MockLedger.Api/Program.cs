using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using MockLedger.Api;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<FaultInjectionOptions>()
    .Bind(builder.Configuration.GetSection(FaultInjectionOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<FaultInjector>();

WebApplication app = builder.Build();

// Minimal liveness surface, mirroring the real services' shape without their dependencies.
app.MapGet("/health/live", static () => Results.Ok(new { status = "Healthy" }));
app.MapGet("/health/ready", static () => Results.Ok(new { status = "Healthy" }));

// =============================================================================================
//  GET /ledger/v1/accounts/{accountId}/transactions?from=&to=
//
//  Fault order mirrors a real degraded service: the rate limiter answers before any work is
//  done (a saturated service sheds load first), latency applies to everything that gets past
//  it (a slow service is slow even when it errors), and the error rate fires after the wait
//  (a timeout-then-503 is the sequence a circuit breaker actually opens on).
// =============================================================================================
app.MapGet(
    "/ledger/v1/accounts/{accountId:guid}/transactions",
    async (
        Guid accountId,
        [FromQuery] DateOnly from,
        [FromQuery] DateOnly to,
        FaultInjector faults,
        HttpContext http,
        CancellationToken cancellationToken) =>
    {
        if (to < from)
        {
            return Results.Problem(
                title: "Invalid period", detail: "'to' precedes 'from'.", statusCode: 400);
        }

        // 1. Rate limit - before any work, with Retry-After so a well-behaved client backs off
        //    by instruction rather than by guess.
        if (faults.RateLimitCheck() is { } retryAfter)
        {
            http.Response.Headers.RetryAfter =
                Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        // A per-request hash driving latency and error decisions: varies per call (the tick
        // count) so repeated calls sample the distribution, unlike the DATA, which must not vary.
        int requestHash = HashCode.Combine(accountId, Environment.TickCount64);

        // 2. Latency.
        TimeSpan delay = faults.SampleLatency(requestHash);
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        // 3. Injected failure.
        if (faults.ShouldError(requestHash))
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        // 4. Poison: a 200 whose body will not deserialise. The worker's parse failure is the
        //    quarantine path under test in Part G.
        if (faults.Current.PoisonAccountIds.Contains(accountId))
        {
            return Results.Text(FaultInjector.PoisonPayload(accountId), "application/json");
        }

        if (!LedgerGenerator.IsKnown(accountId))
        {
            return Results.NotFound();
        }

        // 5. The deterministic payload. Same (account, period) in, same bytes out - always.
        return Results.Ok(LedgerGenerator.Generate(accountId, from, to));
    });

// Tests host this service with WebApplicationFactory<FaultInjectionOptions> - any public type
// from this assembly locates the entry point, same trick the other service factories use.
await app.RunAsync().ConfigureAwait(false);
