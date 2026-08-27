using System.ComponentModel.DataAnnotations;

namespace StatementDelivery.Persistence.Leasing;

/// <summary>
/// Configuration for <see cref="PostgresLeaseManager"/>.
/// </summary>
public sealed class LeaseOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Lease";

    /// <summary>
    /// Gets or sets how long an acquired lease stays valid without a renewal, in seconds.
    /// </summary>
    /// <remarks>
    /// This is the worst-case time the system runs with no leader after a holder dies without
    /// releasing. Shorter means faster failover and more renewal traffic; longer means the
    /// opposite. Thirty seconds is a deliberate compromise for work that runs on a schedule
    /// rather than continuously.
    /// </remarks>
    [Range(5, 600)]
    public int TimeToLiveSeconds { get; set; } = 30;

    /// <summary>
    /// Gets the renewal interval: one third of the time to live.
    /// </summary>
    /// <remarks>
    /// One third means two consecutive renewals can fail - a transient network blip, a PgBouncer
    /// restart - before the lease actually expires. At one half, a single missed renewal plus
    /// ordinary scheduling jitter is enough to drop a lease that was never really lost.
    /// </remarks>
    public TimeSpan RenewalInterval => TimeSpan.FromSeconds(TimeToLiveSeconds / 3.0);

    /// <summary>Gets the time to live as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan TimeToLive => TimeSpan.FromSeconds(TimeToLiveSeconds);

    /// <summary>
    /// Gets or sets an explicit holder identity. Empty means host name plus process id.
    /// </summary>
    /// <remarks>
    /// Deployed services should leave this alone: one process per replica already yields a unique
    /// identity. It exists so that a test can run two contending managers inside one process, where
    /// the default would give both the same identity and each would RENEW the other's lease rather
    /// than contend for it - and both would believe they had won.
    /// </remarks>
    public string? HolderId { get; set; }
}
