using Shouldly;
using StatementDelivery.ServiceDefaults;
using Xunit;

namespace UnitTests.ServiceDefaults;

/// <summary>
/// Tests for the OpenTelemetry service resource values.
/// </summary>
public sealed class ServiceIdentityTests
{
    [Fact]
    public void Create_PopulatesEveryResourceAttribute()
    {
        ServiceIdentity identity = ServiceIdentity.Create("delivery-api", "Production");

        identity.Name.ShouldBe("delivery-api");
        identity.Environment.ShouldBe("Production");
        identity.InstanceId.ShouldNotBeNullOrWhiteSpace();
        identity.Version.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Create_StripsTheCommitShaFromTheInformationalVersion()
    {
        // The SDK appends "+<sha>" to the informational version. Keeping it would make
        // service.version a different string on every commit, so every dashboard grouped by
        // version would show one bar per build.
        ServiceIdentity identity = ServiceIdentity.Create("delivery-api", "Development");

        identity.Version.ShouldNotContain("+");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Create_RejectsAMissingServiceName(string? serviceName) =>
        Should.Throw<ArgumentException>(() => ServiceIdentity.Create(serviceName!, "Production"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Create_RejectsAMissingEnvironment(string? environment) =>
        Should.Throw<ArgumentException>(() => ServiceIdentity.Create("delivery-api", environment!));
}
