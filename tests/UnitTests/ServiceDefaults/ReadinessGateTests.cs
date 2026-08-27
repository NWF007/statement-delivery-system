using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;
using StatementDelivery.ServiceDefaults.HealthChecks;
using Xunit;

namespace UnitTests.ServiceDefaults;

/// <summary>
/// Tests for the readiness gate and the health check that reports it.
/// </summary>
public sealed class ReadinessGateTests
{
    [Fact]
    public void Gate_StartsReady() => new ReadinessGate().IsReady.ShouldBeTrue();

    [Fact]
    public void BeginDraining_IsOneWay()
    {
        // There is no way back on purpose. "Shutting down, then changed its mind" is not a state
        // any orchestrator models, and an instance that rejoined the load balancer mid-shutdown
        // would take traffic it is about to stop being able to serve.
        var gate = new ReadinessGate();

        gate.BeginDraining();
        gate.BeginDraining();

        gate.IsReady.ShouldBeFalse();
        typeof(ReadinessGate).GetMethods()
            .ShouldNotContain(method => method.Name.Contains("Ready", StringComparison.Ordinal) && method.Name.StartsWith("Set", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HealthCheck_IsHealthyWhileReady()
    {
        var check = new ReadinessGateHealthCheck(new ReadinessGate());

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task HealthCheck_IsUnhealthyOnceDraining()
    {
        var gate = new ReadinessGate();
        var check = new ReadinessGateHealthCheck(gate);
        gate.BeginDraining();

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description.ShouldNotBeNull().ShouldContain("Draining");
    }
}
