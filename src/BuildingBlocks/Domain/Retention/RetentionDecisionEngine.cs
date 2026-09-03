namespace StatementDelivery.Domain.Retention;

/// <summary>
/// Resolves the conflicting legal obligations over one statement into a single, explained
/// decision. Pure — no I/O, no clock, no randomness.
/// </summary>
/// <remarks>
/// <para>
/// The obligations genuinely point in opposite directions, and that is the normal condition of
/// regulated data, not a bug in the law:
/// </para>
/// <para>
/// POPIA s24 — the data subject MAY request erasure. POPIA s14 — MUST NOT retain longer than
/// necessary. FICA s23 — MUST retain at least five years from the end of the relationship.
/// Companies Act — MUST retain seven years. A litigation hold — MUST retain indefinitely.
/// Object Lock — CANNOT delete before retain-until, regardless of what anyone decides.
/// </para>
/// <para>
/// The engine's job is to make the conflict VISIBLE AND DECIDABLE, not to pick a side in code:
/// every blocked outcome carries the statutory basis and the date, so the API can answer with a
/// citation and the audit trail can reconstruct the decision. Silently resolving a legal
/// conflict is hard constraint 2's forbidden move.
/// </para>
/// <para>
/// WHY THIS PRECEDENCE ORDER (ADR-0033):
/// </para>
/// <para>
/// 1. A destroyed key short-circuits everything — there is nothing left to protect or delete;
///    the ciphertext is indistinguishable from random bytes and only bookkeeping remains.
/// 2. Legal hold outranks everything else because litigation preservation is absolute: no
///    statute's expiry, no storage mechanism, no policy shortens it.
/// 3. Object Lock comes next because it is a PHYSICAL impossibility rather than a policy — until
///    the lock expires, no principal, not even root, can delete the object.
/// 4. Statutory retention is a policy: a hold can extend it, nothing can shorten it.
/// 5. Only when nothing above applies is purging permitted.
/// </para>
/// </remarks>
public static class RetentionDecisionEngine
{
    /// <summary>The statutes behind <see cref="RetentionDecision.RetainStatutory"/>, cited as the API and audit see them.</summary>
    public const string StatutoryBasis = "Companies Act s24 / FICA s23";

    /// <summary>Decides what may be done with one statement today.</summary>
    /// <param name="ctx">Every fact the decision depends on. See <see cref="RetentionContext"/> for who is authoritative for what.</param>
    /// <returns>The decision, carrying its reason.</returns>
    public static RetentionDecision Decide(RetentionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // DEFENCE IN DEPTH (ADR-0040): an erased customer with an ACTIVE hold should be
        // impossible - erasure is blocked by holds upstream - so observing it means something
        // went badly wrong, and the one response that cannot compound the damage is to keep
        // treating the statement as held. Callers count retention_erased_under_hold_total when
        // they see this combination; any non-zero value pages.
        if (ctx.CustomerKeyDestroyed && ctx.HasActiveLegalHold)
        {
            return new RetentionDecision.BlockedByLegalHold(
                ctx.LegalHoldCaseReference
                    ?? throw new ArgumentException(
                        "An active legal hold must carry its case reference.", nameof(ctx)));
        }

        if (ctx.CustomerKeyDestroyed)
        {
            return new RetentionDecision.AlreadyErased();
        }

        if (ctx.HasActiveLegalHold)
        {
            // The case reference is mandatory at placement (a hold nobody can trace to a case is
            // a hold nobody will ever dare release), so its absence here is a data fault worth
            // surfacing loudly rather than papering over with an empty string.
            return new RetentionDecision.BlockedByLegalHold(
                ctx.LegalHoldCaseReference
                    ?? throw new ArgumentException(
                        "An active legal hold must carry its case reference.", nameof(ctx)));
        }

        if (ctx.ObjectLockRetainUntil is { } lockUntil && lockUntil > ctx.Today)
        {
            return new RetentionDecision.BlockedByObjectLock(lockUntil);
        }

        if (ctx.RetainUntil > ctx.Today)
        {
            return new RetentionDecision.RetainStatutory(StatutoryBasis, ctx.RetainUntil);
        }

        return new RetentionDecision.Purge();
    }
}
