using System.ComponentModel.DataAnnotations;

namespace Retention.Worker.Configuration;

/// <summary>
/// Configuration for the retention worker.
/// </summary>
public sealed class RetentionWorkerOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Retention";

    /// <summary>
    /// Gets or sets the lease name that elects the single replica allowed to run retention work.
    /// </summary>
    public string LeaseName { get; set; } = "retention-sweep";

    /// <summary>Gets or sets how often the elected leader runs a sweep, in seconds.</summary>
    [Range(10, 86_400)]
    public int SweepIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Gets or sets how often a standby replica retries acquiring the lease, in seconds.
    /// </summary>
    /// <remarks>
    /// Should be shorter than the lease time to live, or a leader that dies leaves the work
    /// unattended for the remainder of the lease PLUS most of this interval.
    /// </remarks>
    [Range(1, 3600)]
    public int LeaderElectionRetrySeconds { get; set; } = 10;

    /// <summary>Gets the sweep interval as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan SweepInterval => TimeSpan.FromSeconds(SweepIntervalSeconds);

    /// <summary>Gets the leader-election retry interval as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan LeaderElectionRetry => TimeSpan.FromSeconds(LeaderElectionRetrySeconds);
}

/// <summary>
/// Service-specific wiring for the retention worker.
/// </summary>
public static class RetentionWorkerExtensions
{
    /// <summary>
    /// Binds and validates worker options and registers the retention sweep service.
    /// </summary>
    /// <param name="builder">The web application builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static WebApplicationBuilder AddRetentionWorker(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddOptions<RetentionWorkerOptions>()
            .Bind(builder.Configuration.GetSection(RetentionWorkerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddHostedService<RetentionSweepService>();

        return builder;
    }
}
