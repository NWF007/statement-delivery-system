using StatementDelivery.Domain.Retention;
using StatementDelivery.ServiceDefaults.Storage;

namespace StatementDelivery.ServiceDefaults.Retention;

/// <summary>
/// THE one place a <see cref="RetentionContext"/> is built from resolved facts. Pure.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0041's rule made concrete. The decision engine is exhaustively tested — 32 rows, every
/// input combination — and the Prompt 6 CRITICAL lived one layer up anyway, in three hand-rolled
/// context constructions that each mapped the world onto the engine's inputs slightly
/// differently (one dropped statement-scoped holds; one dropped the store layer entirely). The
/// enumeration stopped exactly where the real-world variation started.
/// </para>
/// <para>
/// So the mapping is now one pure function with its own exhaustive matrix
/// (BuildRetentionContext_ProducesCorrectFields, 96 rows: hold scope × store hold × key state ×
/// retention × lock). Callers resolve facts — through <see cref="HoldResolution"/>, the key
/// store, the object store — and hand them here; nobody maps facts to engine inputs by hand.
/// </para>
/// </remarks>
public static class RetentionContextFactory
{
    /// <summary>Builds the engine's input from resolved facts.</summary>
    /// <param name="holds">Both layers' hold state, from <see cref="HoldResolution"/>.</param>
    /// <param name="customerKeyDestroyed">Whether the customer's CEK is destroyed.</param>
    /// <param name="retainUntil">The statutory retention date (the statement's, or for a customer-wide decision the latest across their statements).</param>
    /// <param name="objectInfo">
    /// The object store's answer, when it was consulted: the lock date comes from HERE and never
    /// from the database (hard constraint 4). Null when the store was not asked (the erasure
    /// API's advisory evaluation, or a statement with no object) — the context then carries no
    /// lock, and the statute branch answers.
    /// </param>
    /// <param name="today">The decision date. A parameter, so decisions are reproducible.</param>
    /// <returns>The context, ready for <see cref="RetentionDecisionEngine.Decide"/>.</returns>
    public static RetentionContext Create(
        HoldState holds,
        bool customerKeyDestroyed,
        DateOnly retainUntil,
        ObjectRetentionInfo? objectInfo,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(holds);

        return new RetentionContext(
            RetainUntil: retainUntil,

            // Only an EXISTING object can be locked; a store answer for a missing object
            // carries no lock, and an unconsulted store carries no opinion at all.
            ObjectLockRetainUntil: objectInfo is { Exists: true } ? objectInfo.RetainUntil : null,

            // EITHER layer blocks - that single expression is what the audit found duplicated
            // and diverging across the purge and erasure paths.
            HasActiveLegalHold: holds.IsHeld,
            LegalHoldCaseReference: holds.EffectiveCaseReference,
            CustomerKeyDestroyed: customerKeyDestroyed,
            Today: today);
    }
}
