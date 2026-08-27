using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StatementDelivery.ServiceDefaults.HealthChecks;

namespace StatementDelivery.ServiceDefaults.Hosting;

/// <summary>
/// Options for the shutdown drain window.
/// </summary>
public sealed class GracefulShutdownOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "GracefulShutdown";

    /// <summary>
    /// Gets or sets how long to keep serving after readiness flips to false, in seconds.
    /// </summary>
    /// <remarks>
    /// This is not idle waiting. It is the window in which the load balancer notices the failing
    /// readiness probe and stops routing new requests here. Removing it is the single most common
    /// cause of 502s during a rolling deploy: the pod stops accepting connections the instant it
    /// gets SIGTERM, while the load balancer is still a probe interval away from finding out.
    /// Ten seconds covers a typical 2-second probe period with a 3-failure threshold, plus slack.
    /// </remarks>
    [Range(0, 120)]
    public int DrainSeconds { get; set; } = 10;

    /// <summary>
    /// Gets or sets the total shutdown budget, in seconds.
    /// </summary>
    /// <remarks>
    /// Must exceed <see cref="DrainSeconds"/>: what is left over is the time in-flight requests get
    /// to finish. Downloads STREAM, so a request here can legitimately still be mid-transfer of a
    /// multi-megabyte statement when the signal arrives, and cutting it off means a customer gets a
    /// truncated PDF rather than an error they can retry.
    /// </remarks>
    [Range(1, 600)]
    public int ShutdownTimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Implements the drain half of graceful shutdown.
/// </summary>
/// <remarks>
/// <para>
/// The sequence on SIGTERM is:
/// </para>
/// <list type="number">
/// <item><description>Readiness flips to false, so <c>/health/ready</c> starts failing.</description></item>
/// <item><description>
/// This service holds shutdown open for the drain window, giving the load balancer time to observe
/// that failure and stop routing new requests here. Liveness keeps passing throughout, so the
/// orchestrator does not mistake a draining pod for a hung one and kill it.
/// </description></item>
/// <item><description>
/// The host then stops accepting connections and waits out the remaining shutdown budget for
/// in-flight requests - including streaming downloads - to complete.
/// </description></item>
/// </list>
/// <para>
/// Implemented as <see cref="IHostedLifecycleService"/> rather than as a callback on
/// ApplicationStopping, because StoppingAsync is awaited: the host genuinely waits for it, which
/// is the whole point. A fire-and-forget callback would flip the flag and then let shutdown
/// proceed immediately, which looks correct in code review and does nothing at all.
/// </para>
/// </remarks>
public sealed partial class GracefulShutdownService : IHostedLifecycleService
{
    private readonly ReadinessGate _gate;
    private readonly GracefulShutdownOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GracefulShutdownService> _logger;

    /// <summary>Initialises a new instance of the <see cref="GracefulShutdownService"/> class.</summary>
    /// <param name="gate">The readiness gate to flip.</param>
    /// <param name="options">Shutdown options.</param>
    /// <param name="timeProvider">Time source, substitutable in tests.</param>
    /// <param name="logger">Logger.</param>
    public GracefulShutdownService(
        ReadinessGate gate,
        IOptions<GracefulShutdownOptions> options,
        TimeProvider timeProvider,
        ILogger<GracefulShutdownService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _gate = gate;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        _gate.BeginDraining();

        TimeSpan drain = TimeSpan.FromSeconds(_options.DrainSeconds);
        if (drain <= TimeSpan.Zero)
        {
            return;
        }

        LogDrainStarted(_logger, _options.DrainSeconds);

        try
        {
            await Task.Delay(drain, _timeProvider, cancellationToken).ConfigureAwait(false);
            LogDrainCompleted(_logger);
        }
        catch (OperationCanceledException)
        {
            // The shutdown budget ran out first. Nothing to do but let the host proceed - but say
            // so, because it means the budget is too small for the configured drain window.
            LogDrainInterrupted(_logger);
        }
    }

    /// <inheritdoc />
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Information,
        Message = "Shutdown signal received. Readiness is now failing; draining for {DrainSeconds}s so the load balancer can stop routing new requests here.")]
    private static partial void LogDrainStarted(ILogger logger, int drainSeconds);

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Information,
        Message = "Drain window elapsed. Waiting for in-flight requests to complete.")]
    private static partial void LogDrainCompleted(ILogger logger);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Warning,
        Message = "Shutdown budget expired during the drain window. In-flight requests may be cut off; raise GracefulShutdown:ShutdownTimeoutSeconds.")]
    private static partial void LogDrainInterrupted(ILogger logger);
}
