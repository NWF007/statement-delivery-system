using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Polly;
using StatementDelivery.Domain.Tokens;
using StatementDelivery.Persistence.Connections;
using StatementDelivery.Persistence.Partitioning;
using StatementDelivery.ServiceDefaults.Caching;
using StatementDelivery.ServiceDefaults.Diagnostics;
using StatementDelivery.ServiceDefaults.HealthChecks;
using StatementDelivery.ServiceDefaults.Hosting;
using StatementDelivery.ServiceDefaults.Logging;
using StatementDelivery.ServiceDefaults.RateLimiting;
using StatementDelivery.ServiceDefaults.Security;
using StatementDelivery.ServiceDefaults.Serialization;

namespace StatementDelivery.ServiceDefaults;

/// <summary>
/// The cross-cutting wiring every service in this platform shares.
/// </summary>
/// <remarks>
/// The goal is a service <c>Program.cs</c> of about fifteen lines. Anything a service has to
/// remember to configure for itself is something one of the four services will eventually forget,
/// and the one that forgets will be the one that matters.
/// </remarks>
public static class ServiceDefaultsExtensions
{
    /// <summary>
    /// The OTLP endpoint environment variable. When it is unset, no exporter is registered and
    /// the service runs with telemetry collected but not shipped.
    /// </summary>
    private const string OtlpEndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>
    /// Overrides the logical service name. Honoured so that <c>service.name</c> in the telemetry
    /// backend matches what the deployment calls the service, rather than whatever the assembly
    /// happens to be named. The OpenTelemetry SDK reads the same variable, so setting it once in
    /// compose or a Kubernetes manifest is enough.
    /// </summary>
    private const string ServiceNameVariable = "OTEL_SERVICE_NAME";

    /// <summary>
    /// Wires observability, health checks, resilience, service discovery, structured logging,
    /// problem details and graceful shutdown.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        ServiceIdentity identity = ResolveIdentity(builder);
        builder.Services.AddSingleton(identity);

        builder.AddStructuredLogging();
        builder.AddObservability();
        builder.AddDefaultHealthChecks();
        builder.AddProblemDetailsHandling();

        // Service discovery resolves logical names such as "https://delivery-api" from
        // configuration, so the compose topology and a Kubernetes topology differ by configuration
        // rather than by code. No Aspire AppHost is involved - see ADR-0004.
        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilience();
            http.AddServiceDiscovery();
        });

        builder.Services.AddOptions<GracefulShutdownOptions>()
            .Bind(builder.Configuration.GetSection(GracefulShutdownOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Strongly-typed identifiers serialise as bare strings, not as {"value":"..."}. Without
        // this the wrapper that stops identifiers being confused in C# would leak its shape onto
        // the wire, and a client sending the string every other API uses would get a 400.
        builder.Services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.Converters.Add(new StronglyTypedIdJsonConverterFactory()));

        // The OS CSPRNG. Every other token control assumes the token is unguessable; this is the
        // registration that makes that true.
        builder.Services.AddSingleton<IRandomBytes, CryptoRandomBytes>();

        // Rate limits shared across replicas. An in-process limiter divides the effective limit by
        // the replica count, which on the redeem path would turn the anti-enumeration control into
        // a decoration. Registered even without Redis: the limiter then falls back to conservative
        // per-process counters rather than removing the limit.
        builder.Services.AddSingleton<IDistributedRateLimiter, DistributedRateLimiter>();

        builder.AddDistributedCache();

        builder.Services.AddSingleton<ReadinessGate>();
        builder.Services.AddSingleton<IHostedService, GracefulShutdownService>();

        builder.Services.Configure<HostOptions>(options =>
        {
            // The total shutdown budget. The drain window (default 10s) is spent letting the load
            // balancer notice that readiness has failed; the remainder is what in-flight requests
            // get to finish in. Downloads STREAM, so a request may legitimately still be
            // mid-transfer of a multi-megabyte statement when SIGTERM arrives, and cutting that off
            // hands the customer a truncated PDF instead of an error they can retry.
            options.ShutdownTimeout = TimeSpan.FromSeconds(30);

            // A crashing BackgroundService must take the process down rather than leaving a host
            // that passes its liveness probe while doing no work at all.
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
        });

        return builder;
    }

    /// <summary>
    /// Configures OpenTelemetry traces, metrics and logs, with the OTLP exporter driven by
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static IHostApplicationBuilder AddObservability(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        ServiceIdentity identity = ResolveIdentity(builder);

        builder.Services.AddMetrics();

        IOpenTelemetryBuilder otel = builder.Services.AddOpenTelemetry();

        otel.ConfigureResource(resource => resource
            .AddService(
                serviceName: identity.Name,
                serviceVersion: identity.Version,
                serviceInstanceId: identity.InstanceId)
            .AddAttributes(
            [
                // The OTel semantic convention key. Every dashboard and alert filters on it, so it
                // is set centrally rather than per service.
                new KeyValuePair<string, object>("deployment.environment", identity.Environment),
                new KeyValuePair<string, object>("deployment.environment.name", identity.Environment),
            ]));

        otel.WithMetrics(metrics => metrics
            .AddRuntimeInstrumentation()
            .AddHttpClientInstrumentation()
            .AddNpgsqlInstrumentation(_ => { })
            .AddMeter(
                "Microsoft.AspNetCore.Hosting",
                "Microsoft.AspNetCore.Server.Kestrel",
                "Microsoft.AspNetCore.RateLimiting",
                "Microsoft.Extensions.Diagnostics.HealthChecks",
                "System.Net.Http",
                PartitionMetrics.MeterName,

                // Delivery-path counters, including download_denied_total whose UNKNOWN_TOKEN rate is
                // what a guessing attack looks like from the outside.
                "StatementDelivery.Download"));

        otel.WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation(options =>
            {
                // Health and readiness probes fire every couple of seconds per replica, forever.
                // Sampling them would bury real traffic and dominate the telemetry bill without
                // ever answering a question anybody asked.
                options.Filter = context => !IsProbeRequest(context.Request.Path);
                options.RecordException = true;
            })
            .AddHttpClientInstrumentation()
            .AddNpgsql()

            // Spans this platform starts itself. The workers have no inbound HTTP, so without a
            // source of their own they would appear in the dashboard as services that emit logs
            // and metrics but never a single trace.
            .AddSource("StatementDelivery.*")

            // Redaction runs LAST in the processor chain so it sees whatever the instrumentation
            // libraries actually recorded, including attributes added by code nobody here wrote.
            .AddProcessor<RedactingActivityProcessor>());

        otel.WithLogging(
            logging => logging.AddProcessor<RedactingLogProcessor>(),
            options =>
            {
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;
            });

        // Only export when an endpoint is configured. Without this guard the exporter defaults to
        // localhost:4317 and every service spends its life retrying a connection to nothing, which
        // shows up as a puzzling error every few seconds in an otherwise healthy log.
        string? endpoint = builder.Configuration[OtlpEndpointVariable];
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            otel.UseOtlpExporter();
        }

        return builder;
    }

    /// <summary>
    /// Registers the default liveness and readiness checks, under DISTINCT tags.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THIS DISTINCTION IS THE MOST COMMONLY BOTCHED DETAIL IN A .NET KUBERNETES DEPLOYMENT, and
    /// getting it wrong converts a dependency blip into a full outage.
    /// </para>
    /// <para>
    /// <c>live</c> means "this process is not wedged". It MUST NOT touch any dependency. Failing it
    /// causes the container to be RESTARTED. If a liveness check queried the database, then a
    /// thirty-second database blip would fail liveness on every replica at once, the orchestrator
    /// would restart the entire fleet simultaneously, and the fleet would come back cold into a
    /// database that is already struggling. A dependency outage must never become a restart storm.
    /// </para>
    /// <para>
    /// <c>ready</c> means "this instance can serve traffic right now". It DOES check dependencies -
    /// the database pool, object storage, the cache. Failing it REMOVES the instance from the load
    /// balancer without restarting it, so it stops taking work it cannot do and rejoins on its own
    /// once the dependency recovers.
    /// </para>
    /// </remarks>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static IHostApplicationBuilder AddDefaultHealthChecks(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services
            .AddHealthChecks()

            // Liveness: deliberately checks nothing. Reaching this code proves the process is up,
            // the thread pool is servicing work and the pipeline is intact. That is the entire
            // question a liveness probe is allowed to ask.
            .AddCheck(
                "self-live",
                () => HealthCheckResult.Healthy("Process is responsive."),
                tags: ["live"])

            // Readiness: starts healthy, fails as soon as SIGTERM begins the drain window.
            .AddCheck<ReadinessGateHealthCheck>(
                ReadinessGateHealthCheck.Name,
                HealthStatus.Unhealthy,
                tags: ["ready"]);

        // Dependency checks are added from configuration rather than by each service remembering
        // to ask for them. A service configured to use PostgreSQL gets a PostgreSQL readiness
        // check; one that is not, does not. That removes the failure mode where a service ships
        // with a dependency it never probes.
        string? postgres = builder.Configuration[$"{PostgresOptions.SectionName}:{nameof(PostgresOptions.PrimaryConnectionString)}"];
        if (!string.IsNullOrWhiteSpace(postgres))
        {
            _ = builder.Services.AddHealthChecks().AddNpgSql(
                connectionString: postgres,

                // Cheapest possible round trip. The question is "is the pool able to hand me a
                // working connection", not "can the database run a query", and anything heavier
                // would make the probe itself part of the load during an incident.
                healthQuery: "SELECT 1;",
                name: "postgres",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready", "db"]);
        }

        string? redis = builder.Configuration[$"{CacheOptions.SectionName}:{nameof(CacheOptions.ConnectionString)}"];
        if (!string.IsNullOrWhiteSpace(redis))
        {
            _ = builder.Services.AddHealthChecks().AddRedis(
                redisConnectionString: redis,
                name: "redis",
                failureStatus: HealthStatus.Degraded,
                tags: ["ready", "cache"]);
        }

        return builder;
    }

    /// <summary>
    /// Registers a distributed cache: Redis when configured, in-memory otherwise.
    /// </summary>
    private static IHostApplicationBuilder AddDistributedCache(this IHostApplicationBuilder builder)
    {
        builder.Services.AddOptions<CacheOptions>()
            .Bind(builder.Configuration.GetSection(CacheOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        string? connectionString = builder.Configuration[$"{CacheOptions.SectionName}:{nameof(CacheOptions.ConnectionString)}"];
        string instanceName = builder.Configuration[$"{CacheOptions.SectionName}:{nameof(CacheOptions.InstanceName)}"]
            ?? "statement-delivery:";

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // No Redis: the rate limiter gets an empty connection and uses its in-process
            // fallback rather than dropping the control.
            builder.Services.AddSingleton(new RedisConnection(null));

            // Not a silent downgrade to a broken cache: an in-memory distributed cache is correct
            // for a single-replica worker and for local development. Anything relying on the cache
            // being SHARED must state that in its own readiness check.
            builder.Services.AddDistributedMemoryCache();
            return builder;
        }

        builder.Services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = connectionString;
            options.InstanceName = instanceName;
        });

        // A single shared multiplexer. StackExchange.Redis is built to be used as one long-lived
        // connection per process; creating one per operation exhausts sockets under load.
        builder.Services.AddSingleton(_ =>
            new RedisConnection(StackExchange.Redis.ConnectionMultiplexer.Connect(connectionString)));

        return builder;
    }

    /// <summary>
    /// Applies the platform's standard outbound HTTP policy: attempt timeout, retry with
    /// exponential backoff AND jitter, and a circuit breaker.
    /// </summary>
    /// <remarks>
    /// JITTER IS NOT OPTIONAL. Without it, every replica that failed on the same downstream blip
    /// retries at the same instant, and the retry wave is itself the outage - the classic
    /// thundering herd that turns a recoverable one-second hiccup into a sustained one.
    /// </remarks>
    /// <param name="builder">The named HTTP client builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static IHttpClientBuilder AddStandardResilience(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        _ = builder.AddStandardResilienceHandler(options =>
        {
            // Per attempt. Must be shorter than TotalRequestTimeout or a single slow attempt eats
            // the whole budget and the retries never happen.
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);

            options.Retry.MaxRetryAttempts = 3;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.Retry.Delay = TimeSpan.FromMilliseconds(500);

            // Opens once half the sampled calls in the window fail. Failing fast while a downstream
            // is down protects this service's own thread pool and connection budget: without it,
            // requests pile up waiting on something that is not coming back.
            options.CircuitBreaker.FailureRatio = 0.5;
            options.CircuitBreaker.MinimumThroughput = 10;
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
            options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(15);

            // Total budget across every attempt. At least AttemptTimeout x (1 + MaxRetryAttempts)
            // plus backoff, or the retry policy is decorative.
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(45);
        });

        return builder;
    }

    /// <summary>
    /// Registers RFC 9457 problem details and the global exception handler.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static IHostApplicationBuilder AddProblemDetailsHandling(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddProblemDetails(options =>
            options.CustomizeProblemDetails = context =>
            {
                // Every problem document carries a traceId, whatever produced it - the exception
                // handler, a 404, or a model-validation failure. One opaque string in a support
                // ticket resolves to one trace with its logs and database spans attached.
                context.ProblemDetails.Extensions["traceId"] =
                    Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;

                // A DENIAL NAMES NOTHING. On the deny statuses the request path is exactly what
                // the response must not confirm - a statement id on a 404 is an existence oracle,
                // and denial bodies must be indistinguishable across causes
                // (DownloadLifecycleTests pins them byte-identical). Errors keep the redacted
                // path: the caller already knows what they called, and the operator needs it.
                int status = context.ProblemDetails.Status ?? context.HttpContext.Response.StatusCode;
                if (status is StatusCodes.Status401Unauthorized
                    or StatusCodes.Status403Forbidden
                    or StatusCodes.Status404NotFound
                    or StatusCodes.Status429TooManyRequests)
                {
                    context.ProblemDetails.Instance = null;
                }
                else
                {
                    context.ProblemDetails.Instance ??=
                        SensitiveDataRedactor.RedactDownloadPath(context.HttpContext.Request.Path.Value);
                }
            });

        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

        return builder;
    }

    /// <summary>
    /// Maps <c>/health/live</c>, <c>/health/ready</c> and <c>/ping</c>, and installs the exception
    /// handler middleware.
    /// </summary>
    /// <remarks>
    /// Call this FIRST, immediately after <c>builder.Build()</c>. It installs
    /// <c>UseExceptionHandler</c>, which only catches what is downstream of it in the pipeline.
    /// </remarks>
    /// <param name="app">The web application.</param>
    /// <returns>The application, for chaining.</returns>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseExceptionHandler();

        // Turns a bare 404 or 415 with an empty body into a problem document with a traceId.
        app.UseStatusCodePages();

        _ = app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("live"),
            AllowCachingResponses = false,
        }).ExcludeFromDescription();

        _ = app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            AllowCachingResponses = false,
            ResponseWriter = WriteHealthReportAsync,
        }).ExcludeFromDescription();

        _ = app.MapGet("/ping", (ServiceIdentity identity, TimeProvider time) => Results.Ok(new
        {
            service = identity.Name,
            version = identity.Version,
            environment = identity.Environment,
            instance = identity.InstanceId,
            utcNow = time.GetUtcNow().UtcDateTime,
        }))
        .WithName("Ping")
        .WithSummary("Liveness diagnostic returning the build and environment this instance is running.")
        .AllowAnonymous();

        return app;
    }

    private static ServiceIdentity ResolveIdentity(IHostApplicationBuilder builder)
    {
        string? configured = builder.Configuration[ServiceNameVariable];

        return ServiceIdentity.Create(
            string.IsNullOrWhiteSpace(configured) ? builder.Environment.ApplicationName : configured,
            builder.Environment.EnvironmentName);
    }

    private static bool IsProbeRequest(PathString path) =>
        path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/ping", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writes the readiness report as JSON so that a failing probe says WHICH dependency failed.
    /// A bare "Unhealthy" forces an operator to go and reproduce it by hand.
    /// </summary>
    private static Task WriteHealthReportAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        return context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                durationMs = entry.Value.Duration.TotalMilliseconds,
                tags = entry.Value.Tags,

                // The description is authored by us and says which dependency failed. The exception
                // is not included: it can carry connection strings and server internals, and a
                // readiness endpoint is reachable from inside the cluster by anything at all.
                description = entry.Value.Description,
            }),
        });
    }

    /// <summary>
    /// JSON console logging plus the redaction and enrichment pipeline.
    /// </summary>
    private static IHostApplicationBuilder AddStructuredLogging(this IHostApplicationBuilder builder)
    {
        // One logging stack, not two. Serilog is deliberately absent: the OpenTelemetry log
        // exporter is already in the pipeline, and running both means two configuration surfaces
        // and two places for a redaction rule to be missed. See ADR-0003.
        builder.Logging.ClearProviders();

        // NOT AddJsonConsole. The stock JSON formatter writes the rendered message and every state
        // value verbatim, and the console provider is not part of the OpenTelemetry pipeline that
        // RedactingLogProcessor guards - so a download URL in a log message would reach stdout, and
        // from there a shipper and an index, entirely unredacted. See RedactingConsoleFormatter.
        builder.Logging.AddConsole(options => options.FormatterName = RedactingConsoleFormatter.FormatterName);
        builder.Logging.AddConsoleFormatter<RedactingConsoleFormatter, ConsoleFormatterOptions>(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;

            // ISO 8601 with an explicit Z. Container logs get merged across regions and hosts;
            // a local-time stamp with no offset is unorderable the moment that happens.
            options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
        });

        // Puts TraceId, SpanId and ParentId into the logging scope, so every JSON line carries the
        // correlation identifiers. This is what makes a log line in stdout and a span in the
        // dashboard the same event.
        builder.Logging.Configure(options =>
            options.ActivityTrackingOptions =
                ActivityTrackingOptions.TraceId
                | ActivityTrackingOptions.SpanId
                | ActivityTrackingOptions.ParentId);

        // Classification-driven redaction, as a THIRD layer rather than the load-bearing one. It
        // reaches every provider but only covers parameters somebody remembered to tag, which is the
        // failure mode this design rejects elsewhere. The two layers that do not depend on anyone
        // remembering are RedactingConsoleFormatter (console) and RedactingLogProcessor (OTLP).
        builder.Services.AddRedaction();
        builder.Logging.EnableRedaction();
        builder.Logging.EnableEnrichment();

        return builder;
    }
}
