using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace StatementDelivery.ServiceDefaults.HealthChecks;

/// <summary>
/// A process-wide switch that lets this instance declare itself unready without dying.
/// </summary>
/// <remarks>
/// Flipped by <see cref="Hosting.GracefulShutdownService"/> the moment SIGTERM arrives, so the
/// load balancer stops sending new work while the process is still perfectly capable of finishing
/// the work it already has.
/// </remarks>
public sealed class ReadinessGate
{
    private volatile bool _isReady = true;

    /// <summary>Gets a value indicating whether this instance is accepting new traffic.</summary>
    public bool IsReady => _isReady;

    /// <summary>
    /// Declares this instance unready. One-way: an instance that has begun draining never
    /// re-enters the load balancer, because "shutting down, then changed its mind" is not a state
    /// any orchestrator models.
    /// </summary>
    public void BeginDraining() => _isReady = false;
}

/// <summary>
/// Readiness check backed by <see cref="ReadinessGate"/>.
/// </summary>
/// <remarks>
/// Tagged <c>ready</c> only. It must never be tagged <c>live</c>: reporting the drain window as a
/// liveness failure would make the orchestrator kill the pod at exactly the moment it is trying to
/// finish in-flight downloads cleanly.
/// </remarks>
public sealed class ReadinessGateHealthCheck : IHealthCheck
{
    /// <summary>The registered name of this check.</summary>
    public const string Name = "self";

    private readonly ReadinessGate _gate;

    /// <summary>Initialises a new instance of the <see cref="ReadinessGateHealthCheck"/> class.</summary>
    /// <param name="gate">The readiness gate.</param>
    public ReadinessGateHealthCheck(ReadinessGate gate) => _gate = gate;

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_gate.IsReady
            ? HealthCheckResult.Healthy("Accepting traffic.")
            : HealthCheckResult.Unhealthy("Draining: this instance has received a shutdown signal and is finishing in-flight work."));
}
