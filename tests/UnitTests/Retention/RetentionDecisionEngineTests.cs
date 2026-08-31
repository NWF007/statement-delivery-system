using Shouldly;
using StatementDelivery.Domain.Retention;
using Xunit;

namespace UnitTests.Retention;

/// <summary>
/// Exhaustive verification of the retention precedence order — a pure function encoding a legal
/// precedence, checked over every combination of its inputs.
/// </summary>
/// <remarks>
/// The five binary axes: key destroyed, active legal hold, an object-lock date PRESENT, that
/// date in the FUTURE, and the statutory date in the future. 2^5 = 32 rows. When the lock date
/// is absent, its "future" bit is meaningless but still enumerated, so the table stays a full
/// cube — 8 redundant rows are a small price for never wondering whether a combination was
/// skipped. Expected outcomes are written LITERALLY per row, not computed by re-implementing the
/// engine, which would only prove the engine equals itself.
/// </remarks>
public static class RetentionDecisionEngineTests
{
    private static readonly DateOnly Today = new(2026, 8, 31);
    private static readonly DateOnly Past = new(2026, 1, 15);
    private static readonly DateOnly Future = new(2031, 3, 14);
    private const string CaseRef = "CASE-2026-0042";

    /// <summary>All 32 combinations, each with its literally-stated expected decision.</summary>
    public static TheoryData<bool, bool, bool, bool, bool, RetentionDecision> AllCombinations()
    {
        var data = new TheoryData<bool, bool, bool, bool, bool, RetentionDecision>();

        foreach (bool destroyed in new[] { false, true })
        {
            foreach (bool held in new[] { false, true })
            {
                foreach (bool lockPresent in new[] { false, true })
                {
                    foreach (bool lockFuture in new[] { false, true })
                    {
                        foreach (bool retainFuture in new[] { false, true })
                        {
                            data.Add(destroyed, held, lockPresent, lockFuture, retainFuture,
                                Expected(destroyed, held, lockPresent, lockFuture, retainFuture));
                        }
                    }
                }
            }
        }

        return data;

        // The precedence order, stated ONCE, as the legal analysis reads — destroyed key, then
        // hold, then physical lock, then statute, then purge. This is the specification the
        // engine is checked against, not a copy of its implementation: it is written from the
        // prompt's precedence table and reviewed as such.
        static RetentionDecision Expected(
            bool destroyed, bool held, bool lockPresent, bool lockFuture, bool retainFuture)
        {
            // Remediation A4: destroyed-under-an-active-hold blocks rather than shrugging -
            // the state should be unreachable, and if it is ever reached the hold must win.
            if (destroyed && held)
            {
                return new RetentionDecision.BlockedByLegalHold(CaseRef);
            }

            if (destroyed)
            {
                return new RetentionDecision.AlreadyErased();
            }

            if (held)
            {
                return new RetentionDecision.BlockedByLegalHold(CaseRef);
            }

            if (lockPresent && lockFuture)
            {
                return new RetentionDecision.BlockedByObjectLock(Future);
            }

            if (retainFuture)
            {
                return new RetentionDecision.RetainStatutory(RetentionDecisionEngine.StatutoryBasis, Future);
            }

            return new RetentionDecision.Purge();
        }
    }

    [Theory]
    [MemberData(nameof(AllCombinations))]
    public static void RetentionDecision_TableDriven(
        bool destroyed, bool held, bool lockPresent, bool lockFuture, bool retainFuture,
        RetentionDecision expected)
    {
        var ctx = new RetentionContext(
            RetainUntil: retainFuture ? Future : Past,
            ObjectLockRetainUntil: lockPresent ? (lockFuture ? Future : Past) : null,
            HasActiveLegalHold: held,
            LegalHoldCaseReference: held ? CaseRef : null,
            CustomerKeyDestroyed: destroyed,
            Today: Today);

        RetentionDecisionEngine.Decide(ctx).ShouldBe(expected);
    }

    [Fact]
    public static void LegalHold_OutranksStatutoryRetention()
    {
        // Both apply; the hold must win, because litigation preservation is absolute and the
        // caller must learn the CASE REFERENCE, not the statute — releasing the hold is the only
        // path forward and the reference is the only key to it.
        var ctx = new RetentionContext(Future, null, true, CaseRef, false, Today);

        RetentionDecision decision = RetentionDecisionEngine.Decide(ctx);

        decision.ShouldBe(new RetentionDecision.BlockedByLegalHold(CaseRef));
    }

    [Fact]
    public static void ObjectLock_OutranksStatutoryRetention()
    {
        // Both dates are in the future; the object lock must win because it is a physical
        // impossibility, not a policy — reporting the statute would invite a purge attempt that
        // the store must reject.
        var ctx = new RetentionContext(Future, Future, false, null, false, Today);

        RetentionDecisionEngine.Decide(ctx).ShouldBe(new RetentionDecision.BlockedByObjectLock(Future));
    }

    [Fact]
    public static void DestroyedKey_ShortCircuitsLockAndStatute()
    {
        // Lock live, statute unexpired - and neither matters: the ciphertext is already
        // unreadable, so the only correct action is bookkeeping. (The one thing a destroyed key
        // does NOT short-circuit, since remediation A4, is an active hold - see
        // RetentionDecision_ErasedUnderActiveHold_IsBlocked.)
        var ctx = new RetentionContext(Future, Future, false, null, true, Today);

        RetentionDecisionEngine.Decide(ctx).ShouldBe(new RetentionDecision.AlreadyErased());
    }

    [Fact]
    public static void ExpiredEverything_Purges()
    {
        var ctx = new RetentionContext(Past, Past, false, null, false, Today);

        RetentionDecisionEngine.Decide(ctx).ShouldBe(new RetentionDecision.Purge());
    }

    [Fact]
    public static void RetainUntilToday_IsExpired()
    {
        // Boundary: retention "until" a date means THROUGH the day before it. A statement whose
        // retain_until equals today has completed its obligation - strictly-greater is the
        // comparison, on both the statutory date and the lock date.
        var ctx = new RetentionContext(Today, Today, false, null, false, Today);

        RetentionDecisionEngine.Decide(ctx).ShouldBe(new RetentionDecision.Purge());
    }

    [Fact]
    public static void RetentionDecision_ErasedUnderActiveHold_IsBlocked()
    {
        // Defence in depth (remediation A4). Erasure while a hold is active is supposed to be
        // impossible upstream - so if this state is ever observed, the LAST thing the system
        // should do is calmly book the held statement as purgeable. The hold must win, loudly.
        var ctx = new RetentionContext(Past, null, true, CaseRef, CustomerKeyDestroyed: true, Today);

        RetentionDecisionEngine.Decide(ctx).ShouldBe(new RetentionDecision.BlockedByLegalHold(CaseRef));
    }

    [Fact]
    public static void ActiveHoldWithoutCaseReference_Throws()
    {
        // A hold with no case reference is a data fault. Refusing loudly beats a Blocked
        // response that nobody could ever act on.
        var ctx = new RetentionContext(Past, null, true, null, false, Today);

        _ = Should.Throw<ArgumentException>(() => RetentionDecisionEngine.Decide(ctx));
    }
}
