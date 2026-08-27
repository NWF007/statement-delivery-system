namespace StatementDelivery.ServiceDefaults.Caching;

/// <summary>
/// Distributed cache configuration.
/// </summary>
/// <remarks>
/// Optional by design. A service with no <see cref="ConnectionString"/> gets an in-memory
/// distributed cache instead of failing to start, so a worker that has no use for Redis does not
/// have to carry a dependency on it, and a developer can run one service without the whole stack.
/// </remarks>
public sealed class CacheOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Cache";

    /// <summary>
    /// Gets or sets the Redis connection string. Empty means "use the in-memory implementation".
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Gets or sets the key prefix, so that several services sharing one Redis instance cannot
    /// collide on a key name.
    /// </summary>
    public string InstanceName { get; set; } = "statement-delivery:";
}
