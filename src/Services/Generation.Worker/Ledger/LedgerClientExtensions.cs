using System.ComponentModel.DataAnnotations;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.CircuitBreaker;

namespace Generation.Worker.Ledger;

/// <summary>Ledger client configuration.</summary>
public sealed class LedgerOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Ledger";

    /// <summary>Gets or sets the ledger base URL.</summary>
    [Required]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the per-attempt timeout, in seconds.</summary>
    [Range(1, 60)]
    public int AttemptTimeoutSeconds { get; set; } = 2;

    /// <summary>Gets or sets the client-side request ceiling per second, per replica.</summary>
    /// <remarks>
    /// The FLEET's politeness budget divided by replica count. The ledger enforces its own limit
    /// and answers 429; this one exists so the fleet does not have to be told. Token bucket, so
    /// short bursts ride on accumulated tokens rather than being shaped flat.
    /// </remarks>
    [Range(1, 100_000)]
    public int RateLimitPerSecond { get; set; } = 50;

    /// <summary>Gets or sets how long an opened circuit holds before probing, in seconds.</summary>
    /// <remarks>30 in production; tests shorten it so recovery is observable without waiting.</remarks>
    [Range(1, 600)]
    public int BreakDurationSeconds { get; set; } = 30;
}

/// <summary>Wires the resilient ledger client.</summary>
public static class LedgerClientExtensions
{
    /// <summary>The named HttpClient the pipeline attaches to.</summary>
    public const string ClientName = "ledger";

    /// <summary>Registers <see cref="ILedgerClient"/> with the full resilience pipeline.</summary>
    /// <param name="builder">The host builder.</param>
    /// <param name="retryJitterRandomizer">
    /// Test seam, same idiom as the injectable <see cref="TimeProvider"/> used elsewhere: pins
    /// the retry jitter's random draw so backoff SHAPE is deterministically assertable. Null in
    /// production (the default), which keeps Polly's thread-safe shared randomness.
    /// </param>
    /// <returns>The builder, for chaining.</returns>
    public static IHostApplicationBuilder AddLedgerClient(
        this IHostApplicationBuilder builder, Func<double>? retryJitterRandomizer = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        _ = builder.Services
            .AddOptions<LedgerOptions>()
            .Bind(builder.Configuration.GetSection(LedgerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // ONE shared state provider, registered as a singleton, so the render loop and the
        // orchestrator - which live in the same process - both see the breaker the HTTP handler
        // actually trips. This is what "pause the run when the circuit opens" reads.
        var circuitState = new CircuitBreakerStateProvider();
        builder.Services.AddSingleton(circuitState);

        LedgerOptions bound = builder.Configuration
            .GetSection(LedgerOptions.SectionName).Get<LedgerOptions>() ?? new LedgerOptions();

        _ = builder.Services
            .AddHttpClient<ILedgerClient, LedgerClient>(ClientName, client =>
            {
                client.BaseAddress = new Uri(bound.BaseUrl);

                // The pipeline's total-timeout below is the real bound; this is the backstop for
                // a misconfigured pipeline, not the working limit.
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddResilienceHandler("ledger", (pipeline, context) =>
            {
                LedgerOptions options = context.GetOptions<LedgerOptions>();

                // ORDER MATTERS, OUTERMOST FIRST.
                //
                // Rate limiter first: a request the budget refuses must not consume a retry, trip
                // the breaker, or wait out a timeout - it never happened.
                //
                // Total timeout above retry: bounds the whole conversation including backoff, so
                // a render item spends at most ~10s on the ledger before its attempt fails.
                //
                // Retry above breaker: each ATTEMPT lands on the breaker, so the breaker sees the
                // true failure rate. Retry below breaker would hide two failures in every three.
                //
                // Attempt timeout innermost: 2s per try - the ledger's P99 is 200ms, so 2s is
                // ten Ps of grace, and anything slower is indistinguishable from down.
                _ = pipeline
                    .AddRateLimiter(new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = Math.Max(1, options.RateLimitPerSecond),
                        TokensPerPeriod = Math.Max(1, options.RateLimitPerSecond),
                        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                        QueueLimit = Math.Max(1, options.RateLimitPerSecond),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true,
                    }))
                    .AddTimeout(TimeSpan.FromSeconds(10))
                    .AddRetry(new HttpRetryStrategyOptions
                    {
                        MaxRetryAttempts = 3,
                        BackoffType = DelayBackoffType.Exponential,
                        Delay = TimeSpan.FromMilliseconds(200),

                        // ⚠ JITTER IS NOT OPTIONAL. 360 workers that fail together retry
                        // together without it, and the synchronised herd keeps the ledger down -
                        // each wave re-arrives exactly as the previous one finishes killing it.
                        UseJitter = true,

                        // Retry-After from a 429 overrides the computed backoff: the server said
                        // when, and guessing earlier than that is just queue-jumping that fails.
                        ShouldRetryAfterHeader = true,

                        // Polly's default (Random.Shared) unless a test pinned the draw.
                        Randomizer = retryJitterRandomizer ?? Random.Shared.NextDouble,
                    })
                    .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                    {
                        // Open at 50% failure over a 20-request window; hold 30s before probing.
                        FailureRatio = 0.5,
                        MinimumThroughput = 20,
                        SamplingDuration = TimeSpan.FromSeconds(30),
                        BreakDuration = TimeSpan.FromSeconds(options.BreakDurationSeconds),
                        StateProvider = circuitState,
                    })
                    .AddTimeout(TimeSpan.FromSeconds(options.AttemptTimeoutSeconds));
            });

        return builder;
    }
}
