using Shouldly;
using StatementDelivery.Persistence.Leasing;
using Xunit;

namespace UnitTests.Persistence;

/// <summary>
/// Tests for <see cref="LeaseOptions"/>.
/// </summary>
public sealed class LeaseOptionsTests
{
    [Theory]
    [InlineData(30, 10)]
    [InlineData(60, 20)]
    [InlineData(15, 5)]
    public void RenewalInterval_IsOneThirdOfTheTimeToLive(int timeToLiveSeconds, int expectedRenewalSeconds)
    {
        // One third, not one half. At one half a single missed renewal plus ordinary scheduling
        // jitter drops a lease that was never really lost, and a dropped lease means leadership
        // moves for no reason. One third tolerates two consecutive failures.
        var options = new LeaseOptions { TimeToLiveSeconds = timeToLiveSeconds };

        options.RenewalInterval.ShouldBe(TimeSpan.FromSeconds(expectedRenewalSeconds));
        options.TimeToLive.ShouldBe(TimeSpan.FromSeconds(timeToLiveSeconds));
    }

    [Fact]
    public void RenewalInterval_IsAlwaysShorterThanTheTimeToLive()
    {
        foreach (int seconds in Enumerable.Range(5, 120))
        {
            var options = new LeaseOptions { TimeToLiveSeconds = seconds };
            options.RenewalInterval.ShouldBeLessThan(options.TimeToLive);
        }
    }
}
