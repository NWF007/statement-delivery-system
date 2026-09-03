using Shouldly;
using StatementDelivery.Domain.Retention;
using StatementDelivery.ServiceDefaults.Retention;
using StatementDelivery.ServiceDefaults.Storage;
using Xunit;

namespace UnitTests.Retention;

/// <summary>
/// The 96-row matrix over CONTEXT CONSTRUCTION — the layer where the critical hold defect lived.
/// </summary>
/// <remarks>
/// <para>
/// The engine's own 32-row table proves the engine; it proves nothing about the code that
/// builds the engine's input, and that is exactly where three rounds of seam defects have
/// lived (ADR-0041). This matrix enumerates the cross product of everything that can reach the
/// factory — hold scope (4) × store hold (2) × key state (3) × retention (2) × object lock (2)
/// = 96 rows — and asserts on the resulting <see cref="RetentionContext"/> FIELDS, not on the
/// final decision.
/// </para>
/// <para>
/// The critical defect is one cell: every <c>HoldScope.Statement</c> row requires
/// <c>HasActiveLegalHold == true</c>. A second, high-severity one is another cell: every
/// store-hold row requires the same, with the drift marker as the case reference when no
/// database record exists.
/// </para>
/// </remarks>
public static class RetentionContextFactoryTests
{
    /// <summary>How a database hold covers the subject.</summary>
    public enum HoldScope
    {
        /// <summary>No database hold at all.</summary>
        None,

        /// <summary>A customer-scoped hold (statement_id NULL).</summary>
        Customer,

        /// <summary>A statement-scoped hold — the scope the critical defect was blind to.</summary>
        Statement,

        /// <summary>Both scopes at once.</summary>
        Both,
    }

    /// <summary>The customer key's lifecycle position.</summary>
    public enum KeyStatus
    {
        /// <summary>ACTIVE: nothing destroyed.</summary>
        Active,

        /// <summary>SCHEDULED_DESTRUCTION: the cooling-off window is open — the key still exists.</summary>
        Scheduled,

        /// <summary>DESTROYED: crypto-erasure has happened.</summary>
        Destroyed,
    }

    private static readonly DateOnly Today = new(2026, 8, 31);
    private static readonly DateOnly Past = new(2026, 1, 15);
    private static readonly DateOnly Future = new(2031, 3, 14);
    private const string CaseRef = "CASE-2026-0042";

    /// <summary>All 96 combinations.</summary>
    public static TheoryData<HoldScope, bool, KeyStatus, bool, bool> AllContextInputs()
    {
        var data = new TheoryData<HoldScope, bool, KeyStatus, bool, bool>();
        foreach (HoldScope scope in Enum.GetValues<HoldScope>())
        {
            foreach (bool storeHold in new[] { false, true })
            {
                foreach (KeyStatus key in Enum.GetValues<KeyStatus>())
                {
                    foreach (bool retentionExpired in new[] { false, true })
                    {
                        foreach (bool objectLockExpired in new[] { false, true })
                        {
                            data.Add(scope, storeHold, key, retentionExpired, objectLockExpired);
                        }
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllContextInputs))]
    public static void BuildRetentionContext_ProducesCorrectFields(
        HoldScope scope, bool storeHold, KeyStatus key, bool retentionExpired, bool objectLockExpired)
    {
        // The world, as the resolvers deliver it. Since V021, EVERY database hold — whatever
        // its scope — reaches the resolver's customer predicate, so any scope other than None
        // means HasDbHold. That sentence is the requirement the critical defect violated, and
        // this mapping is where the matrix encodes it.
        bool hasDbHold = scope != HoldScope.None;
        var holds = new HoldState(hasDbHold, storeHold, hasDbHold ? CaseRef : null);
        bool destroyed = key == KeyStatus.Destroyed;
        DateOnly retainUntil = retentionExpired ? Past : Future;
        var objectInfo = new ObjectRetentionInfo(
            Exists: true,
            Mode: "COMPLIANCE",
            RetainUntil: objectLockExpired ? Past : Future,
            LegalHold: storeHold);

        RetentionContext ctx = RetentionContextFactory.Create(holds, destroyed, retainUntil, objectInfo, Today);

        // Field by field, stated literally from the requirements — never by re-running the
        // factory's own expressions.
        ctx.HasActiveLegalHold.ShouldBe(
            scope != HoldScope.None || storeHold,
            "a hold in ANY scope or EITHER layer must reach the engine - both defects were this field");

        if (scope != HoldScope.None)
        {
            ctx.LegalHoldCaseReference.ShouldBe(CaseRef);
        }
        else if (storeHold)
        {
            ctx.LegalHoldCaseReference.ShouldBe(
                HoldState.DriftCaseReference,
                "a store-only hold must be legible as drift, not dressed as a real case");
        }
        else
        {
            ctx.LegalHoldCaseReference.ShouldBeNull();
        }

        ctx.CustomerKeyDestroyed.ShouldBe(
            key == KeyStatus.Destroyed,
            "SCHEDULED_DESTRUCTION is a live fuse, not a destroyed key - the write guard owns that state, not this flag");

        ctx.RetainUntil.ShouldBe(retentionExpired ? Past : Future);
        ctx.ObjectLockRetainUntil.ShouldBe(
            objectLockExpired ? Past : Future,
            "the lock date comes from the STORE's answer, never the database (hard constraint 4)");
        ctx.Today.ShouldBe(Today);
    }

    [Fact]
    public static void BuildRetentionContext_MissingObject_CarriesNoLock()
    {
        // A store answer for a missing object cannot carry a lock; neither can an unconsulted
        // store. Both must leave the lock branch silent so the statute branch answers.
        RetentionContext missing = RetentionContextFactory.Create(
            new HoldState(false, false, null), false, Future,
            new ObjectRetentionInfo(Exists: false, null, Future, false), Today);
        missing.ObjectLockRetainUntil.ShouldBeNull();

        RetentionContext unconsulted = RetentionContextFactory.Create(
            new HoldState(false, false, null), false, Future, null, Today);
        unconsulted.ObjectLockRetainUntil.ShouldBeNull();
    }
}
