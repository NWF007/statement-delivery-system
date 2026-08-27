using StackExchange.Redis;

namespace StatementDelivery.ServiceDefaults.RateLimiting;

/// <summary>
/// Holds the shared Redis connection, which may be absent.
/// </summary>
/// <remarks>
/// A wrapper rather than registering a nullable <see cref="IConnectionMultiplexer"/> directly,
/// because dependency injection cannot express a nullable service type. It also makes "Redis may
/// not be here" an explicit part of every consumer's signature rather than a surprise at runtime -
/// which matters, because the correct behaviour when it is absent is to degrade conservatively
/// rather than to fail or to skip the control.
/// </remarks>
/// <param name="Multiplexer">The connection, or null when no Redis is configured.</param>
public sealed record RedisConnection(IConnectionMultiplexer? Multiplexer)
{
    /// <summary>Gets a value indicating whether a usable connection is available right now.</summary>
    public bool IsAvailable => Multiplexer is { IsConnected: true };
}
