using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace MockLedger.Api;

/// <summary>Latency shape: a two-point sketch of a distribution.</summary>
public sealed class LatencyOptions
{
    /// <summary>Gets or sets the median injected latency, in milliseconds.</summary>
    [Range(0, 60_000)]
    public int P50 { get; set; } = 20;

    /// <summary>Gets or sets the 99th-percentile injected latency, in milliseconds.</summary>
    [Range(0, 120_000)]
    public int P99 { get; set; } = 200;
}

/// <summary>
/// Configurable misbehaviour. The entire reason this service exists.
/// </summary>
/// <remarks>
/// A resilience policy tested only against an in-process stub is not tested: a stub cannot time
/// out on the wire, cannot return 429 with Retry-After from a real socket, and cannot produce the
/// half-open flapping a circuit breaker actually sees. Every knob here is settable through
/// configuration - including at runtime through the test host - so the failure tests in Part J
/// dial in exactly the outage they need.
/// </remarks>
public sealed class FaultInjectionOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "FaultInjection";

    /// <summary>Gets or sets the injected latency distribution.</summary>
    public LatencyOptions LatencyMs { get; set; } = new();

    /// <summary>Gets or sets the fraction of requests that fail with 503. 0.0 to 1.0.</summary>
    [Range(0.0, 1.0)]
    public double ErrorRate { get; set; }

    /// <summary>Gets or sets the per-second request ceiling. Exceeding it earns 429 + Retry-After.</summary>
    [Range(1, 1_000_000)]
    public int RateLimitPerSecond { get; set; } = 500;

    /// <summary>
    /// Gets or sets the accounts that return MALFORMED payloads - a 200 whose body does not
    /// deserialise. This is the poison-item path: the worker's ledger client throws on parse, the
    /// attempt burns, and after the ceiling the item quarantines. See Part G.
    /// </summary>
    public IList<Guid> PoisonAccountIds { get; } = [];
}

/// <summary>
/// Applies the configured faults, in the order a real degraded service would.
/// </summary>
public sealed class FaultInjector
{
    private readonly IOptionsMonitor<FaultInjectionOptions> _options;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    private long _windowStartTicks;
    private int _windowCount;

    /// <summary>Initialises a new instance of the <see cref="FaultInjector"/> class.</summary>
    /// <param name="options">Monitored options, so tests can flip faults mid-run.</param>
    /// <param name="time">Time source.</param>
    public FaultInjector(IOptionsMonitor<FaultInjectionOptions> options, TimeProvider time)
    {
        _options = options;
        _time = time;
    }

    /// <summary>Gets the currently configured options.</summary>
    public FaultInjectionOptions Current => _options.CurrentValue;

    /// <summary>
    /// Token-bucket-ish rate limit over a one-second window.
    /// </summary>
    /// <returns>Null when admitted; otherwise the Retry-After to send with the 429.</returns>
    public TimeSpan? RateLimitCheck()
    {
        FaultInjectionOptions options = Current;
        long now = _time.GetTimestamp();

        lock (_gate)
        {
            double windowSeconds = _time.GetElapsedTime(_windowStartTicks, now).TotalSeconds;
            if (windowSeconds >= 1.0)
            {
                _windowStartTicks = now;
                _windowCount = 0;
            }

            if (_windowCount >= options.RateLimitPerSecond)
            {
                double remaining = Math.Max(0.0, 1.0 - windowSeconds);
                return TimeSpan.FromSeconds(Math.Max(remaining, 0.05));
            }

            _windowCount++;
            return null;
        }
    }

    /// <summary>Injected latency for one request, sampled from the configured two-point sketch.</summary>
    /// <param name="requestHash">A per-request value that spreads samples across the distribution.</param>
    public TimeSpan SampleLatency(int requestHash)
    {
        LatencyOptions latency = Current.LatencyMs;

        // 99% of requests near P50, 1% near P99 - a coarse but honest sketch. Derived from the
        // request hash rather than a shared RNG so the injector itself stays contention-free.
        uint sample = (uint)requestHash;
        int ms = sample % 100 == 0 ? latency.P99 : latency.P50;

        // +/- 25% spread so latencies are not suspiciously uniform.
        int jitter = (int)(sample % 51) - 25;
        return TimeSpan.FromMilliseconds(Math.Max(0, ms + (ms * jitter / 100)));
    }

    /// <summary>Whether this request should fail with 503, per the configured error rate.</summary>
    /// <param name="requestHash">A per-request value; the decision is derived, not random.</param>
    public bool ShouldError(int requestHash)
    {
        double rate = Current.ErrorRate;
        return rate > 0 && ((uint)requestHash % 10_000) < rate * 10_000;
    }

    /// <summary>The malformed body poison accounts receive: truncated JSON with a wrong-typed field.</summary>
    public static string PoisonPayload(Guid accountId) => string.Create(
        CultureInfo.InvariantCulture,
        $"{{\"accountId\":\"{accountId}\",\"openingBalanceMinorUnits\":\"NOT-A-NUMBER\",\"transactions\":[{{\"posted");
}
