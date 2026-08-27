using System.Globalization;
using StatementDelivery.Domain.Exceptions;

namespace StatementDelivery.Domain.ValueObjects;

/// <summary>
/// How long a statement must be kept. Pure calculation; no I/O, no clock of its own.
/// </summary>
/// <remarks>
/// <para>
/// SEVEN YEARS, NOT FIVE, AND THE REASON MATTERS.
/// </para>
/// <para>
/// FICA requires records to be kept for a minimum of five years from the end of the business
/// relationship. The Companies Act imposes seven on companies. Both apply, they do not agree, and
/// the shorter one is not a ceiling - it is a floor. The defensible engineering position is the
/// LONGEST applicable period, because retaining longer than one statute requires is a storage cost
/// while retaining shorter than another requires is a compliance failure.
/// </para>
/// <para>
/// It is configurable rather than hard-coded because the applicable period is a legal question
/// that changes without the code changing, and because a jurisdiction with different rules should
/// be a configuration change rather than a release.
/// </para>
/// <para>
/// <see cref="RetainUntil"/> is computed from the period END, not from the generation date. A
/// statement regenerated three years late must not thereby earn three extra years of retention -
/// the obligation attaches to the period the record covers, not to when the bytes were produced.
/// </para>
/// </remarks>
/// <param name="Years">The retention period in whole years.</param>
public sealed record RetentionPolicy(int Years)
{
    /// <summary>The default policy: seven years. See the type remarks for why.</summary>
    public static readonly RetentionPolicy Default = new(7);

    /// <summary>Gets the retention period in whole years.</summary>
    public int Years { get; } = Years is < 1 or > 100
        ? throw new InvariantViolationException(
            $"Retention period must be between 1 and 100 years, got {Years.ToString(CultureInfo.InvariantCulture)}.")
        : Years;

    /// <summary>
    /// Computes the date up to and including which a statement for <paramref name="period"/> must
    /// be retained.
    /// </summary>
    /// <param name="period">The period the statement covers.</param>
    /// <returns>The last date on which the statement must still exist.</returns>
    public DateOnly RetainUntil(StatementPeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);
        return period.End.AddYears(Years);
    }

    /// <summary>
    /// Determines whether a retention obligation has lapsed.
    /// </summary>
    /// <remarks>
    /// Strictly greater than, not greater-or-equal: <c>retainUntil</c> is the last date the record
    /// must exist, so it is still under obligation ON that date. An off-by-one here deletes a
    /// record a day early, which is exactly the kind of error an auditor finds and nobody else
    /// does.
    /// </remarks>
    /// <param name="retainUntil">The retention deadline stored on the statement.</param>
    /// <param name="asOf">The date to evaluate against.</param>
    /// <returns><see langword="true"/> when the record may be purged.</returns>
    public static bool IsExpired(DateOnly retainUntil, DateOnly asOf) => asOf > retainUntil;
}
