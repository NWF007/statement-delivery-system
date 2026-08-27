using System.Globalization;
using StatementDelivery.Domain.Exceptions;

namespace StatementDelivery.Domain.ValueObjects;

/// <summary>
/// A closed calendar-month range: the period a statement covers.
/// </summary>
/// <remarks>
/// <para>
/// A record CLASS rather than a record struct, and that is deliberate. A struct always has a
/// <c>default</c> instance, and <c>default(StatementPeriod)</c> would be a period from
/// 0001-01-01 to 0001-01-01 - an invalid value that no constructor ever produced and no validation
/// ever saw. Making it a class means the only invalid value is null, which the nullable reference
/// analysis already tracks.
/// </para>
/// <para>
/// <see cref="Start"/> is also the RANGE partition key of the <c>statement</c> table, which is why
/// it must be a true month boundary: a period starting mid-month would land in a partition whose
/// bounds do not correspond to the month anybody means when they say "August".
/// </para>
/// </remarks>
public sealed record StatementPeriod
{
    private StatementPeriod(DateOnly start, DateOnly end)
    {
        Start = start;
        End = end;
    }

    /// <summary>Gets the first day of the covered month. Also the partition key.</summary>
    public DateOnly Start { get; }

    /// <summary>Gets the last day of the covered month.</summary>
    public DateOnly End { get; }

    /// <summary>Gets the calendar year.</summary>
    public int Year => Start.Year;

    /// <summary>Gets the calendar month.</summary>
    public int Month => Start.Month;

    /// <summary>Creates the period covering one calendar month.</summary>
    /// <param name="year">The year.</param>
    /// <param name="month">The month, 1 to 12.</param>
    /// <returns>The period.</returns>
    /// <exception cref="InvariantViolationException">The year or month is out of range.</exception>
    public static StatementPeriod ForMonth(int year, int month)
    {
        if (year is < 1900 or > 9999)
        {
            throw new InvariantViolationException(
                $"Statement period year must be between 1900 and 9999, got {year.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (month is < 1 or > 12)
        {
            throw new InvariantViolationException(
                $"Statement period month must be between 1 and 12, got {month.ToString(CultureInfo.InvariantCulture)}.");
        }

        // DaysInMonth handles February, including leap years, so the end date is derived rather
        // than asserted. A hand-written month-length table is the classic source of a 28 February
        // bug that only appears every fourth year.
        var start = new DateOnly(year, month, 1);
        var end = new DateOnly(year, month, DateTime.DaysInMonth(year, month));

        return new StatementPeriod(start, end);
    }

    /// <summary>
    /// Creates a period from an explicit start and end, validating every invariant.
    /// </summary>
    /// <remarks>
    /// Exists for rehydration from the database, where both columns are stored. It re-validates
    /// rather than trusting the row: a period that violates its invariants in storage is a bug
    /// worth surfacing at the boundary, not one to propagate into the model.
    /// </remarks>
    /// <param name="start">The first day of the month.</param>
    /// <param name="end">The last day of the same month.</param>
    /// <returns>The period.</returns>
    /// <exception cref="InvariantViolationException">Any invariant is violated.</exception>
    public static StatementPeriod Create(DateOnly start, DateOnly end)
    {
        if (start.Day != 1)
        {
            throw new InvariantViolationException(
                $"Statement period must start on the first day of a month, got {start:yyyy-MM-dd}.");
        }

        if (end < start)
        {
            throw new InvariantViolationException(
                $"Statement period end {end:yyyy-MM-dd} precedes start {start:yyyy-MM-dd}.");
        }

        if (end.Year != start.Year || end.Month != start.Month)
        {
            throw new InvariantViolationException(
                $"Statement period must not span months: {start:yyyy-MM-dd} to {end:yyyy-MM-dd}.");
        }

        int lastDay = DateTime.DaysInMonth(start.Year, start.Month);
        if (end.Day != lastDay)
        {
            throw new InvariantViolationException(
                $"Statement period must end on the last day of its month ({lastDay}), got {end:yyyy-MM-dd}.");
        }

        return new StatementPeriod(start, end);
    }

    /// <summary>Determines whether a date falls inside this period, inclusive of both ends.</summary>
    /// <param name="date">The date to test.</param>
    /// <returns><see langword="true"/> when the date is covered.</returns>
    public bool Contains(DateOnly date) => date >= Start && date <= End;

    /// <summary>Returns the period covering the following month.</summary>
    /// <returns>The next period.</returns>
    public StatementPeriod Next()
    {
        DateOnly next = Start.AddMonths(1);
        return ForMonth(next.Year, next.Month);
    }

    /// <summary>Returns the period covering the preceding month.</summary>
    /// <returns>The previous period.</returns>
    public StatementPeriod Previous()
    {
        DateOnly previous = Start.AddMonths(-1);
        return ForMonth(previous.Year, previous.Month);
    }

    /// <inheritdoc />
    public override string ToString() => Start.ToString("yyyy-MM", CultureInfo.InvariantCulture);
}
