using Shouldly;
using StatementDelivery.ServiceDefaults.Retention;
using Xunit;

namespace UnitTests.Retention;

/// <summary>
/// The combined hold state's semantics — the expression the purge and erasure paths each
/// hand-rolled, until the two copies drifted apart.
/// </summary>
public static class HoldStateTests
{
    [Fact]
    public static void HoldResolution_ReportsDrift_WhenLayersDisagree()
    {
        // Physically held, no legal record: the storage-first placement's designed crash
        // residue. It must BLOCK (IsHeld), it must be LEGIBLE as drift, and its case reference
        // must be the unmistakable synthetic marker rather than a real-looking case number.
        var drift = new HoldState(HasDbHold: false, HasStoreHold: true, DbCaseReference: null);

        drift.IsHeld.ShouldBeTrue("a store-side hold alone must still block destruction");
        drift.IsDrift.ShouldBeTrue();
        drift.EffectiveCaseReference.ShouldBe(HoldState.DriftCaseReference);
    }

    [Fact]
    public static void HoldState_BothLayers_IsHeldAndNotDrift()
    {
        var both = new HoldState(true, true, "CASE-2026-0042");

        both.IsHeld.ShouldBeTrue();
        both.IsDrift.ShouldBeFalse("agreement between the layers is the healthy state");
        both.EffectiveCaseReference.ShouldBe("CASE-2026-0042");
    }

    [Fact]
    public static void HoldState_DbOnly_IsHeldAndNotDrift()
    {
        // DB hold with no store hold is reconciliation check 3's finding, not this record's:
        // the DB hold alone already blocks, so nothing is under-protected.
        var dbOnly = new HoldState(true, false, "CASE-2026-0042");

        dbOnly.IsHeld.ShouldBeTrue();
        dbOnly.IsDrift.ShouldBeFalse();
        dbOnly.EffectiveCaseReference.ShouldBe("CASE-2026-0042");
    }

    [Fact]
    public static void HoldState_NoHolds_DoesNotBlock()
    {
        var none = new HoldState(false, false, null);

        none.IsHeld.ShouldBeFalse();
        none.IsDrift.ShouldBeFalse();
        none.EffectiveCaseReference.ShouldBeNull();
    }
}
