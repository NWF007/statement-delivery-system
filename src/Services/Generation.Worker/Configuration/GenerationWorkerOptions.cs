using System.ComponentModel.DataAnnotations;

namespace Generation.Worker.Configuration;

/// <summary>
/// Configuration for the batch generation worker.
/// </summary>
public sealed class GenerationWorkerOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Generation";

    /// <summary>Gets or sets how often the worker looks for work, in seconds.</summary>
    /// <remarks>
    /// Polling rather than LISTEN/NOTIFY. Behind a transaction-mode pooler a LISTEN subscription
    /// lives on a backend the client no longer owns, so notifications are delivered to nobody.
    /// See the trap list in Persistence/Connections/NpgsqlConnectionFactory.cs.
    /// </remarks>
    [Range(1, 3600)]
    public int PollIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// Gets or sets how long an in-flight unit of work may continue after a shutdown signal,
    /// in seconds.
    /// </summary>
    /// <remarks>
    /// This is what makes the batch resumable rather than merely restartable. Abandoning a unit
    /// mid-flight at 1,400 renders per second leaves partially written work that the next run has
    /// to detect and reconcile; letting it finish means the only state that ever exists is
    /// "claimed" or "done". Must be less than the host shutdown budget, or the host kills the unit
    /// before this grace expires and the setting does nothing.
    /// </remarks>
    [Range(0, 300)]
    public int UnitOfWorkGraceSeconds { get; set; } = 20;

    /// <summary>
    /// Gets or sets the number of statements claimed per unit of work.
    /// </summary>
    /// <remarks>
    /// Sized so that one unit finishes comfortably inside
    /// <see cref="UnitOfWorkGraceSeconds"/>. A batch that cannot finish within the grace window is
    /// a batch that gets cancelled on every single deploy.
    /// </remarks>
    [Range(1, 100_000)]
    public int BatchSize { get; set; } = 500;

    /// <summary>Gets the poll interval as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);

    /// <summary>Gets the unit-of-work grace period as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan UnitOfWorkGrace => TimeSpan.FromSeconds(UnitOfWorkGraceSeconds);
}

/// <summary>
/// Service-specific wiring for the generation worker.
/// </summary>
public static class GenerationWorkerExtensions
{
    /// <summary>
    /// Binds and validates worker options and registers the batch orchestration service.
    /// </summary>
    /// <param name="builder">The web application builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static WebApplicationBuilder AddGenerationWorker(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddOptions<GenerationWorkerOptions>()
            .Bind(builder.Configuration.GetSection(GenerationWorkerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddHostedService<BatchOrchestrationService>();

        return builder;
    }
}
