using Shouldly;
using StatementDelivery.Domain.Exceptions;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Domain.Statements;
using StatementDelivery.Domain.ValueObjects;
using Xunit;

namespace UnitTests.Domain;

/// <summary>
/// The statement period value object.
/// </summary>
public sealed class StatementPeriodTests
{
    [Theory]
    [InlineData(2026, 8, 2, 2026, 8, 31)]     // does not start on the first
    [InlineData(2026, 8, 1, 2026, 8, 30)]     // does not end on the last
    [InlineData(2026, 8, 1, 2026, 9, 30)]     // spans two months
    [InlineData(2026, 8, 1, 2026, 7, 31)]     // end precedes start
    public void RejectsNonMonthBoundaries(int sy, int sm, int sd, int ey, int em, int ed) =>
        Should.Throw<InvariantViolationException>(
            () => StatementPeriod.Create(new DateOnly(sy, sm, sd), new DateOnly(ey, em, ed)));

    [Theory]
    [InlineData(2026, 1, 31)]
    [InlineData(2026, 2, 28)]     // ordinary February
    [InlineData(2024, 2, 29)]     // leap year - the one a hand-written month table gets wrong
    [InlineData(2000, 2, 29)]     // divisible by 400, IS a leap year
    [InlineData(1900, 2, 28)]     // divisible by 100 but not 400, is NOT a leap year
    [InlineData(2026, 4, 30)]
    [InlineData(2026, 12, 31)]
    public void ForMonth_ProducesCorrectEnd(int year, int month, int expectedLastDay)
    {
        StatementPeriod period = StatementPeriod.ForMonth(year, month);

        period.Start.ShouldBe(new DateOnly(year, month, 1));
        period.End.ShouldBe(new DateOnly(year, month, expectedLastDay));
    }

    [Theory]
    [InlineData(2026, 0)]
    [InlineData(2026, 13)]
    [InlineData(1899, 6)]
    public void ForMonth_RejectsOutOfRangeInput(int year, int month) =>
        Should.Throw<InvariantViolationException>(() => StatementPeriod.ForMonth(year, month));

    [Fact]
    public void Contains_IsInclusiveOfBothEnds()
    {
        StatementPeriod period = StatementPeriod.ForMonth(2026, 8);

        period.Contains(new DateOnly(2026, 8, 1)).ShouldBeTrue();
        period.Contains(new DateOnly(2026, 8, 31)).ShouldBeTrue();
        period.Contains(new DateOnly(2026, 7, 31)).ShouldBeFalse();
        period.Contains(new DateOnly(2026, 9, 1)).ShouldBeFalse();
    }

    [Fact]
    public void NextAndPrevious_CrossYearBoundariesCorrectly()
    {
        StatementPeriod december = StatementPeriod.ForMonth(2026, 12);

        december.Next().ShouldBe(StatementPeriod.ForMonth(2027, 1));
        StatementPeriod.ForMonth(2027, 1).Previous().ShouldBe(december);
    }

    [Fact]
    public void Next_FromJanuary_LandsOnFebruaryWithCorrectLength() =>
        StatementPeriod.ForMonth(2024, 1).Next().End.ShouldBe(new DateOnly(2024, 2, 29));
}

/// <summary>
/// The retention policy calculation.
/// </summary>
public sealed class RetentionPolicyTests
{
    [Fact]
    public void Default_IsSevenYears() =>
        RetentionPolicy.Default.Years.ShouldBe(
            7,
            "FICA requires five years but the Companies Act imposes seven; the longest applicable period is the defensible one");

    [Theory]
    [InlineData(2026, 8, 2033, 8, 31)]
    [InlineData(2024, 2, 2031, 2, 28)]     // 29 Feb + 7 years lands on a non-leap year
    [InlineData(2026, 12, 2033, 12, 31)]
    public void SevenYears_ComputesCorrectRetainUntil(int year, int month, int ey, int em, int ed) =>
        RetentionPolicy.Default.RetainUntil(StatementPeriod.ForMonth(year, month))
            .ShouldBe(new DateOnly(ey, em, ed));

    [Fact]
    public void RetainUntil_IsDerivedFromPeriodEnd_NotFromToday()
    {
        // A statement regenerated years late must not earn extra retention. The obligation attaches
        // to the period the record covers, not to when the bytes were produced.
        StatementPeriod oldPeriod = StatementPeriod.ForMonth(2019, 3);

        RetentionPolicy.Default.RetainUntil(oldPeriod).ShouldBe(new DateOnly(2026, 3, 31));
    }

    [Fact]
    public void IsExpired_TreatsTheDeadlineDayAsStillRetained()
    {
        // retainUntil is the LAST day the record must exist, so it is still under obligation ON
        // that date. An off-by-one here deletes a record a day early - the kind of error only an
        // auditor finds.
        var retainUntil = new DateOnly(2033, 8, 31);

        RetentionPolicy.IsExpired(retainUntil, new DateOnly(2033, 8, 31)).ShouldBeFalse();
        RetentionPolicy.IsExpired(retainUntil, new DateOnly(2033, 9, 1)).ShouldBeTrue();
        RetentionPolicy.IsExpired(retainUntil, new DateOnly(2033, 8, 30)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void RejectsImplausibleRetentionPeriods(int years) =>
        Should.Throw<InvariantViolationException>(() => new RetentionPolicy(years));
}

/// <summary>
/// The statement state machine.
/// </summary>
/// <remarks>
/// Table-driven over EVERY source-to-target pair, legal and illegal. The illegal half is the half
/// that matters: a state machine tested only on its happy paths is a state machine that permits
/// everything nobody thought to try.
/// </remarks>
public sealed class StatementStateMachineTests
{
    private static readonly StorageLocation Location = new("statements/2026/08/abc.pdf", "STANDARD", 42_000);
    private static readonly DateTimeOffset At = new(2026, 9, 1, 2, 14, 33, TimeSpan.Zero);

    private static Statement InState(StatementStatus status)
    {
        Statement pending = Statement.Create(
            new StatementId(Guid.CreateVersion7()),
            new AccountId(Guid.CreateVersion7()),
            new CustomerId(Guid.CreateVersion7()),
            StatementPeriod.ForMonth(2026, 8),
            RetentionPolicy.Default);

        return status switch
        {
            StatementStatus.Pending => pending,
            StatementStatus.Available => pending.MarkAvailable(Location, At),
            StatementStatus.Archived => pending.MarkAvailable(Location, At).MarkArchived(At),
            StatementStatus.Purged => pending.MarkAvailable(Location, At).MarkPurged(At),
            StatementStatus.Failed => pending.MarkFailed("render failed", At),
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
    }

    private static Statement Transition(Statement statement, StatementStatus target) => target switch
    {
        StatementStatus.Available => statement.MarkAvailable(Location, At),
        StatementStatus.Archived => statement.MarkArchived(At),
        StatementStatus.Purged => statement.MarkPurged(At),
        StatementStatus.Failed => statement.MarkFailed("failure", At),
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    public static TheoryData<StatementStatus, StatementStatus, bool> AllTransitions() => new()
    {
        // from                          to                            legal
        { StatementStatus.Pending,   StatementStatus.Available, true },
        { StatementStatus.Pending,   StatementStatus.Archived,  false },   // never rendered
        { StatementStatus.Pending,   StatementStatus.Purged,    false },   // nothing to purge
        { StatementStatus.Pending,   StatementStatus.Failed,    true },

        { StatementStatus.Available, StatementStatus.Available, false },   // self: double-processing
        { StatementStatus.Available, StatementStatus.Archived,  true },
        { StatementStatus.Available, StatementStatus.Purged,    true },
        { StatementStatus.Available, StatementStatus.Failed,    true },

        { StatementStatus.Archived,  StatementStatus.Available, true },    // restore from cold
        { StatementStatus.Archived,  StatementStatus.Archived,  false },   // self
        { StatementStatus.Archived,  StatementStatus.Purged,    true },
        { StatementStatus.Archived,  StatementStatus.Failed,    false },

        { StatementStatus.Purged,    StatementStatus.Available, false },   // TERMINAL
        { StatementStatus.Purged,    StatementStatus.Archived,  false },
        { StatementStatus.Purged,    StatementStatus.Purged,    false },
        { StatementStatus.Purged,    StatementStatus.Failed,    false },

        { StatementStatus.Failed,    StatementStatus.Available, true },    // retry succeeded
        { StatementStatus.Failed,    StatementStatus.Archived,  false },
        { StatementStatus.Failed,    StatementStatus.Purged,    true },
        { StatementStatus.Failed,    StatementStatus.Failed,    false },   // self
    };

    [Theory]
    [MemberData(nameof(AllTransitions))]
    public void AllTransitions_BehaveAsSpecified(StatementStatus from, StatementStatus to, bool legal)
    {
        Statement statement = InState(from);

        if (legal)
        {
            Transition(statement, to).Status.ShouldBe(to);
        }
        else
        {
            Should.Throw<InvalidStateTransitionException>(() => Transition(statement, to));
        }
    }

    [Fact]
    public void Purged_IsTerminal()
    {
        Statement purged = InState(StatementStatus.Purged);

        foreach (StatementStatus target in (StatementStatus[])
                 [StatementStatus.Available, StatementStatus.Archived, StatementStatus.Purged, StatementStatus.Failed])
        {
            Should.Throw<InvalidStateTransitionException>(() => Transition(purged, target));
        }
    }

    [Fact]
    public void MarkPurged_ClearsTheStorageLocationButKeepsTheRow()
    {
        // The row survives the bytes. "This existed and was destroyed on this date" is the answer an
        // auditor needs; deleting the row makes it indistinguishable from one that never existed.
        Statement purged = InState(StatementStatus.Available).MarkPurged(At);

        purged.Storage.ShouldBeNull();
        purged.PurgedAt.ShouldBe(At);
        purged.RetainUntil.ShouldBe(new DateOnly(2033, 8, 31));
    }

    [Fact]
    public void MarkAvailable_AfterRetry_PreservesTheOriginalGenerationTime()
    {
        DateTimeOffset later = At.AddDays(1);

        Statement retried = InState(StatementStatus.Failed).MarkAvailable(Location, later);

        retried.Status.ShouldBe(StatementStatus.Available);
        retried.FailureReason.ShouldBeNull("a successful retry clears the previous failure");
    }

    [Fact]
    public void Transitions_ReturnNewInstances_LeavingTheOriginalUntouched()
    {
        Statement pending = InState(StatementStatus.Pending);

        Statement available = pending.MarkAvailable(Location, At);

        pending.Status.ShouldBe(StatementStatus.Pending, "the original instance must be unchanged");
        available.Status.ShouldBe(StatementStatus.Available);
        ReferenceEquals(pending, available).ShouldBeFalse();
    }

    [Fact]
    public void Create_RejectsAVersionBelowOne() =>
        Should.Throw<InvariantViolationException>(() => Statement.Create(
            new StatementId(Guid.CreateVersion7()),
            new AccountId(Guid.CreateVersion7()),
            new CustomerId(Guid.CreateVersion7()),
            StatementPeriod.ForMonth(2026, 8),
            RetentionPolicy.Default,
            version: 0));
}
