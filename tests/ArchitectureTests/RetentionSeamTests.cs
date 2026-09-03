using System.Reflection;
using Retention.Worker;
using Shouldly;
using StatementDelivery.Persistence.Retention;
using StatementDelivery.ServiceDefaults.Retention;
using Xunit;

namespace ArchitectureTests;

/// <summary>
/// The seam rule: every destructive path resolves holds through ONE component.
/// </summary>
/// <remarks>
/// The purge aggregated <c>dbHold || storeHold</c> while erasure read
/// the database alone — two hand-rolled aggregations, drifted apart, and the irreversible path
/// got the weaker one. These tests make the drift a compile-adjacent failure: neither
/// destructive pass may depend on <see cref="LegalHoldRepository"/> directly, and both must
/// depend on <see cref="HoldResolution"/>.
/// </remarks>
public static class RetentionSeamTests
{
    [Theory]
    [InlineData(typeof(PurgePass))]
    [InlineData(typeof(ErasureExecutor))]
    public static void DestructivePasses_ResolveHolds_ThroughTheSharedComponent(Type pass)
    {
        ParameterInfo[] parameters = pass.GetConstructors().Single().GetParameters();

        parameters.ShouldContain(
            p => p.ParameterType == typeof(HoldResolution),
            $"{pass.Name} must consume the shared resolver");

        parameters.ShouldNotContain(
            p => p.ParameterType == typeof(LegalHoldRepository),
            $"{pass.Name} querying legal_hold directly is how the purge and erasure aggregations drifted apart");
    }

    [Theory]
    [InlineData(typeof(PurgePass))]
    [InlineData(typeof(ErasureExecutor))]
    public static void DestructivePasses_HoldNoPrivateHoldRepository(Type pass)
    {
        // Belt and braces for the ctor check: no field smuggling either.
        pass.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .ShouldNotContain(
                f => f.FieldType == typeof(LegalHoldRepository),
                $"{pass.Name} must not hold a LegalHoldRepository");
    }
}
