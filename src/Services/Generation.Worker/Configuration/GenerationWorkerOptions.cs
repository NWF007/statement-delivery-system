using System.ComponentModel.DataAnnotations;
using Generation.Worker.Ledger;
using StatementDelivery.ServiceDefaults.Auditing;

namespace Generation.Worker.Configuration;

/// <summary>
/// Configuration for the batch generation worker.
/// </summary>
public sealed class GenerationWorkerOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Generation";

    /// <summary>Gets or sets how often an idle worker looks for a run to serve, in seconds.</summary>
    /// <remarks>
    /// Polling rather than LISTEN/NOTIFY. Behind a transaction-mode pooler a LISTEN subscription
    /// lives on a backend the client no longer owns, so notifications are delivered to nobody.
    /// See the trap list in Persistence/Connections/NpgsqlConnectionFactory.cs.
    /// </remarks>
    [Range(1, 3600)]
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Gets or sets how long in-flight renders may continue after a shutdown signal, in seconds.
    /// </summary>
    /// <remarks>
    /// Must be inside the host's 30-second shutdown budget, or the host kills the render before
    /// this grace expires and the setting does nothing. Anything still RENDERING when the pod
    /// dies anyway is the reaper's job - belt and braces, because SIGKILL runs no code at all.
    /// </remarks>
    [Range(0, 300)]
    public int ShutdownGraceSeconds { get; set; } = 20;

    /// <summary>Gets or sets how many items one claim round trip takes.</summary>
    /// <remarks>
    /// FIFTY, NOT ONE, and the arithmetic is the reason: at ~1,400 items/second fleet-wide,
    /// claiming singly is 1,400 UPDATE round trips per second against one hot table; claiming
    /// fifty is ~28. Going much higher trades that away again - a batch is invisible to other
    /// workers while claimed, so oversized batches strand work behind a slow replica, and a
    /// crashed worker's whole batch waits for the reaper. 50 puts a batch at ~20 seconds of work
    /// for one worker, which bounds both losses.
    /// </remarks>
    [Range(1, 1000)]
    public int ClaimBatchSize { get; set; } = 50;

    /// <summary>Gets or sets the attempts ceiling. At or past it, an item is quarantined.</summary>
    [Range(1, 10)]
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Gets or sets how many renders run concurrently per replica.</summary>
    [Range(1, 64)]
    public int RenderParallelism { get; set; } = 8;

    /// <summary>Gets or sets the claimed-work channel capacity.</summary>
    /// <remarks>
    /// BOUNDED, NOT UNBOUNDED. An unbounded channel lets a fast claimer pull the entire queue
    /// into this replica's memory while slow renderers fall behind - reintroducing exactly the
    /// problem the database queue solves. At capacity the claimer WAITS, which is the
    /// backpressure doing its job.
    /// </remarks>
    [Range(1, 10_000)]
    public int ChannelCapacity { get; set; } = 100;

    /// <summary>Gets or sets how long a RENDERING claim may sit before the reaper presumes its worker dead.</summary>
    [Range(1, 1440)]
    public int StaleClaimMinutes { get; set; } = 15;

    /// <summary>Gets or sets the orchestrator's monitoring interval, in seconds.</summary>
    [Range(5, 3600)]
    public int MonitorIntervalSeconds { get; set; } = 30;

    /// <summary>Gets or sets the run deadline, hours from run creation, used by the projection alert.</summary>
    [Range(1, 168)]
    public int RunDeadlineHours { get; set; } = 6;

    /// <summary>Gets or sets the outbox relay's polling interval, in milliseconds.</summary>
    /// <remarks>
    /// 500ms polling is adequate below roughly 10,000 events/second - the relay claims 500 rows
    /// per tick, so the loop's ceiling is ~1M events/sec of drain and latency stays sub-second.
    /// Above that, log-based CDC (Debezium) becomes justified, at the price of running connector
    /// state and schema evolution as infrastructure. See ADR-0026's Revisit-when.
    /// </remarks>
    [Range(50, 60_000)]
    public int RelayIntervalMilliseconds { get; set; } = 500;

    /// <summary>Gets or sets how many outbox rows the relay publishes per tick.</summary>
    [Range(1, 10_000)]
    public int RelayBatchSize { get; set; } = 500;

    /// <summary>Gets the poll interval as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);

    /// <summary>Gets the shutdown grace as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan ShutdownGrace => TimeSpan.FromSeconds(ShutdownGraceSeconds);

    /// <summary>Gets the stale-claim threshold as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan StaleClaimAfter => TimeSpan.FromMinutes(StaleClaimMinutes);

    /// <summary>Gets the monitor interval as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan MonitorInterval => TimeSpan.FromSeconds(MonitorIntervalSeconds);

    /// <summary>Gets the relay interval as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan RelayInterval => TimeSpan.FromMilliseconds(RelayIntervalMilliseconds);
}

/// <summary>
/// Service-specific wiring for the generation worker.
/// </summary>
public static class GenerationWorkerExtensions
{
    /// <summary>
    /// Binds options and registers the four hosted loops: renderer, orchestrator, reaper, relay.
    /// </summary>
    /// <remarks>
    /// ONE PROCESS, TWO ROLES - the reason the shared lease abstraction exists at all. The
    /// RENDER loop runs on every replica, always. The ORCHESTRATOR, REAPER and RELAY loops run
    /// on every replica too, but each gates itself behind an <c>ILeaseManager</c> lease, so
    /// exactly one replica at a time actually plans, monitors, reaps or relays. Scale to 400
    /// replicas and you get 400 renderers and one of each singleton role, with no separate
    /// deployment, no special "coordinator" image, and failover measured in one lease TTL.
    /// </remarks>
    /// <param name="builder">The web application builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static WebApplicationBuilder AddGenerationWorker(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        _ = builder.Services.AddOptions<GenerationWorkerOptions>()
            .Bind(builder.Configuration.GetSection(GenerationWorkerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddSingleton<GenerationMetrics>();
        builder.Services.AddSingleton<SystemAudit>();
        builder.Services.AddSingleton<RenderPipeline>();

        builder.Services.AddHostedService<RenderWorkerService>();
        builder.Services.AddHostedService<RunOrchestratorService>();
        builder.Services.AddHostedService<StaleClaimReaperService>();
        builder.Services.AddHostedService<OutboxRelayService>();

        return builder;
    }
}
