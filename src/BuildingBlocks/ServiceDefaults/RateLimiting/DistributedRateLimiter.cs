using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace StatementDelivery.ServiceDefaults.RateLimiting;

/// <summary>One limit: how many requests, over how long, under what name.</summary>
/// <param name="Name">Appears in the counter key and in telemetry.</param>
/// <param name="Limit">Permitted requests per window per partition.</param>
/// <param name="Window">The window length.</param>
public readonly record struct RateLimitRule(string Name, int Limit, TimeSpan Window);

/// <summary>The outcome of a limit check.</summary>
/// <param name="Allowed">Whether the request may proceed.</param>
/// <param name="RetryAfter">How long to wait before retrying, when refused.</param>
/// <param name="ViolatedRule">Which rule refused it, for telemetry. Never returned to the caller.</param>
public readonly record struct RateLimitDecision(bool Allowed, TimeSpan RetryAfter, string? ViolatedRule)
{
    /// <summary>An allowed decision.</summary>
    public static RateLimitDecision Allow { get; } = new(true, TimeSpan.Zero, null);
}

/// <summary>
/// Counts requests per partition per window, shared across replicas.
/// </summary>
/// <remarks>
/// <para>
/// SHARED STATE IS THE POINT. An in-process limiter divides the effective limit by the replica
/// count: with six replicas and a 30/minute rule, a client round-robined across them gets 180. For
/// the redeem path - where the limit IS the anti-enumeration control - that is the difference
/// between a control and a decoration.
/// </para>
/// <para>
/// FAILS CLOSED. If Redis is unavailable the limiter does NOT open up; it falls back to a
/// conservative in-process counter using the same limits. That is deliberately stricter than
/// necessary during a Redis outage - the alternative, removing the control because its backing
/// store is down, is how an outage becomes an incident.
/// </para>
/// </remarks>
public interface IDistributedRateLimiter
{
    /// <summary>Checks every rule for a partition, consuming one request from each.</summary>
    /// <param name="partitionKey">What the limit is scoped to - an address, or a customer.</param>
    /// <param name="rules">The rules to apply. All must pass.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The decision.</returns>
    Task<RateLimitDecision> CheckAsync(
        string partitionKey,
        IReadOnlyList<RateLimitRule> rules,
        CancellationToken cancellationToken);
}

/// <summary>
/// Redis-backed fixed-window limiter with an in-process fallback.
/// </summary>
public sealed partial class DistributedRateLimiter : IDistributedRateLimiter
{
    /// <summary>
    /// INCR then EXPIRE-if-new, atomically.
    /// </summary>
    /// <remarks>
    /// A Lua script rather than two commands, because INCR followed by a separate EXPIRE has a
    /// window in which the process dies between them and leaves a key with no TTL - a counter that
    /// never resets, permanently locking out whoever owns that partition. The script executes
    /// atomically on the server, so either both happen or neither does.
    /// </remarks>
    private const string IncrementScript = """
        local current = redis.call('INCR', KEYS[1])
        if current == 1 then
            redis.call('PEXPIRE', KEYS[1], ARGV[1])
        end
        return { current, redis.call('PTTL', KEYS[1]) }
        """;

    private readonly RedisConnection _redis;
    private readonly ILogger<DistributedRateLimiter> _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, LocalWindow> _fallback = new(StringComparer.Ordinal);

    /// <summary>Initialises a new instance of the <see cref="DistributedRateLimiter"/> class.</summary>
    /// <param name="redis">The shared Redis connection, which may be absent.</param>
    /// <param name="time">Time source.</param>
    /// <param name="logger">Logger.</param>
    public DistributedRateLimiter(RedisConnection redis, TimeProvider time, ILogger<DistributedRateLimiter> logger)
    {
        _redis = redis;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RateLimitDecision> CheckAsync(
        string partitionKey,
        IReadOnlyList<RateLimitRule> rules,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        ArgumentNullException.ThrowIfNull(rules);

        foreach (RateLimitRule rule in rules)
        {
            string key = string.Create(CultureInfo.InvariantCulture, $"rl:{rule.Name}:{partitionKey}");

            (long count, TimeSpan ttl) = await IncrementAsync(key, rule.Window, cancellationToken).ConfigureAwait(false);

            if (count > rule.Limit)
            {
                return new RateLimitDecision(false, ttl <= TimeSpan.Zero ? rule.Window : ttl, rule.Name);
            }
        }

        return RateLimitDecision.Allow;
    }

    private async Task<(long Count, TimeSpan Ttl)> IncrementAsync(
        string key,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        if (_redis.IsAvailable)
        {
            try
            {
                IDatabase database = _redis.Multiplexer!.GetDatabase();
                var result = (RedisValue[]?)await database
                    .ScriptEvaluateAsync(
                        IncrementScript,
                        [key],
                        [(long)window.TotalMilliseconds])
                    .ConfigureAwait(false);

                if (result is { Length: 2 })
                {
                    return ((long)result[0], TimeSpan.FromMilliseconds(Math.Max(0, (long)result[1])));
                }
            }
            catch (RedisException ex)
            {
                // FAIL CLOSED. Fall through to the in-process counter with the same limits rather
                // than allowing the request. Logged at warning because a limiter silently degrading
                // to per-replica counting is something an operator needs to know about.
                LogRedisUnavailable(_logger, ex);
            }
            catch (TimeoutException ex)
            {
                LogRedisUnavailable(_logger, ex);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return IncrementLocal(key, window);
    }

    /// <summary>
    /// The conservative fallback: a per-process fixed window.
    /// </summary>
    /// <remarks>
    /// Weaker than the Redis path - each replica counts separately, so the effective limit is
    /// multiplied by the replica count. It is still far better than no limit at all, which is what
    /// "fail open" would mean.
    /// </remarks>
    private (long Count, TimeSpan Ttl) IncrementLocal(string key, TimeSpan window)
    {
        DateTimeOffset now = _time.GetUtcNow();

        LocalWindow updated = _fallback.AddOrUpdate(
            key,
            _ => new LocalWindow(now.Add(window), 1),
            (_, existing) => existing.ExpiresAt <= now
                ? new LocalWindow(now.Add(window), 1)
                : existing with { Count = existing.Count + 1 });

        // Bounded: without this, a flood of distinct addresses during a Redis outage would grow the
        // dictionary without limit and turn a rate-limit degradation into a memory exhaustion.
        if (_fallback.Count > 100_000)
        {
            foreach (KeyValuePair<string, LocalWindow> entry in _fallback)
            {
                if (entry.Value.ExpiresAt <= now)
                {
                    _ = _fallback.TryRemove(entry.Key, out _);
                }
            }
        }

        return (updated.Count, updated.ExpiresAt - now);
    }

    private sealed record LocalWindow(DateTimeOffset ExpiresAt, long Count);

    [LoggerMessage(
        EventId = 7000,
        Level = LogLevel.Warning,
        Message = "Redis is unavailable for rate limiting. Falling back to per-process counters, which are weaker: the effective limit is multiplied by the replica count.")]
    private static partial void LogRedisUnavailable(ILogger logger, Exception exception);
}
