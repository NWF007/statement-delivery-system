using System.ComponentModel.DataAnnotations;
using StatementDelivery.ServiceDefaults.Auditing;

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

    /// <summary>Gets or sets the purge batch bound. Bounded batches, always.</summary>
    [Range(1, 100_000)]
    public int PurgeBatchSize { get; set; } = 10_000;

    /// <summary>Gets or sets how many hours between purge/archive passes (they are daily jobs).</summary>
    [Range(1, 168)]
    public int DailyJobIntervalHours { get; set; } = 24;

    /// <summary>Gets or sets the age, in months, at which AVAILABLE statements move to the cold tier.</summary>
    [Range(1, 120)]
    public int ArchiveAfterMonths { get; set; } = 12;

    /// <summary>Gets or sets the archive batch bound.</summary>
    [Range(1, 100_000)]
    public int ArchiveBatchSize { get; set; } = 10_000;

    /// <summary>Gets or sets how many hours between erasure-executor passes.</summary>
    /// <remarks>Daily by default, matching the brief ("a daily job") and the audit-on-transition cadence. A due erasure therefore executes up to a day after its window closes; the window is seven days, so the slack is noise.</remarks>
    [Range(1, 168)]
    public int ErasureIntervalHours { get; set; } = 24;

    /// <summary>Gets or sets the erasure executor's batch bound.</summary>
    [Range(1, 1_000)]
    public int ErasureBatchSize { get; set; } = 100;

    /// <summary>Gets or sets the restore completer's batch bound.</summary>
    [Range(1, 10_000)]
    public int RestoreBatchSize { get; set; } = 500;

    /// <summary>Gets or sets how long a restored copy stays downloadable, in hours (real Glacier restores are temporary).</summary>
    [Range(1, 720)]
    public int RestoredCopyHours { get; set; } = 48;

    /// <summary>Gets or sets the orphan sweep's listing page size.</summary>
    [Range(10, 1000)]
    public int OrphanPageSize { get; set; } = 500;

    /// <summary>Gets or sets how many listing pages the orphan sweep walks per tick.</summary>
    /// <remarks>Cadence control: pages/tick x ticks/week must cover all 256 shards weekly at the deployment's object count.</remarks>
    [Range(1, 1000)]
    public int OrphanPagesPerTick { get; set; } = 20;

    /// <summary>Gets or sets how many days between orphan-sweep activity windows.</summary>
    [Range(1, 30)]
    public int OrphanSweepIntervalDays { get; set; } = 7;

    /// <summary>Gets or sets the reconciliation sample size per check.</summary>
    [Range(10, 10_000)]
    public int ReconciliationSampleSize { get; set; } = 200;

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

        builder.Services.AddSingleton<RetentionMetrics>();
        builder.Services.AddSingleton<SystemAudit>();
        builder.Services.AddSingleton<PurgePass>();
        builder.Services.AddSingleton<ErasureExecutor>();
        builder.Services.AddSingleton<RestoreCompleter>();
        builder.Services.AddSingleton<ArchivePass>();
        builder.Services.AddSingleton<OrphanSweep>();
        builder.Services.AddSingleton<ReconciliationPass>();
        builder.Services.AddSingleton<StatementDelivery.Messaging.IIntegrationEventPublisher,
            StatementDelivery.Messaging.Outbox.OutboxEventPublisher>();
        builder.Services.AddHostedService<RetentionSweepService>();

        return builder;
    }
}
