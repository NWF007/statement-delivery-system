using System.ComponentModel.DataAnnotations;

namespace StatementDelivery.Persistence.Partitioning;

/// <summary>
/// Configuration for range-partition maintenance.
/// </summary>
/// <remarks>
/// An INSERT into a range-partitioned table with no partition covering the row FAILS. A missing
/// partition is therefore an outage, not a warning, and pre-creation is a first-class scheduled
/// job rather than a cron script somebody remembers to write.
/// See docs/adr/0007-partitioning-strategy.md.
/// </remarks>
public sealed class PartitionOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Partitioning";

    /// <summary>
    /// Gets or sets a value indicating whether the maintenance background service runs.
    /// </summary>
    /// <remarks>
    /// The readiness health check runs regardless. Turning maintenance off - to hand the job to
    /// pg_partman, say - must not turn off the check that verifies the outcome.
    /// </remarks>
    public bool MaintenanceEnabled { get; set; } = true;

    /// <summary>Gets or sets how often maintenance runs, in minutes.</summary>
    [Range(1, 1440)]
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Gets or sets the lease name that serialises maintenance across replicas.
    /// </summary>
    /// <remarks>
    /// Creating a partition is idempotent, so concurrent runs are safe rather than harmful - but
    /// they take an ACCESS EXCLUSIVE lock on the parent table, and N replicas queueing for that
    /// lock every interval is a self-inflicted stall.
    /// </remarks>
    public string LeaseName { get; set; } = "partition-maintenance";

    /// <summary>Gets the tables under maintenance.</summary>
    public IList<PartitionedTableOptions> Tables { get; } = [];

    /// <summary>Gets the maintenance interval as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan Interval => TimeSpan.FromMinutes(IntervalMinutes);
}

/// <summary>
/// One range-partitioned table and how far ahead its partitions must exist.
/// </summary>
public sealed class PartitionedTableOptions
{
    /// <summary>Gets or sets the unqualified parent table name.</summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^[a-z_][a-z0-9_]*$", ErrorMessage = "Table must be a lower-case unquoted identifier.")]
    public string Table { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the partition granularity: <c>day</c> or <c>month</c>.
    /// </summary>
    [Required]
    [RegularExpression("^(day|month)$", ErrorMessage = "Granularity must be 'day' or 'month'.")]
    public string Granularity { get; set; } = "month";

    /// <summary>
    /// Gets or sets how many whole periods beyond the current one must always exist.
    /// </summary>
    /// <remarks>
    /// Three months for monthly tables and seven days for daily ones. The number is a deadline,
    /// not a preference: it is how long every human involved can be unreachable before an insert
    /// starts failing.
    /// </remarks>
    [Range(1, 120)]
    public int PeriodsAhead { get; set; } = 3;
}
