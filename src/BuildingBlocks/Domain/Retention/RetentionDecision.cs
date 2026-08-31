namespace StatementDelivery.Domain.Retention;

/// <summary>
/// Everything the retention decision needs to know about one statement, gathered by the caller.
/// </summary>
/// <remarks>
/// <para>
/// The engine is pure: it never performs I/O, so every fact arrives here. Two of these facts have
/// authoritative sources the caller must respect when building the context:
/// </para>
/// <para>
/// <see cref="ObjectLockRetainUntil"/> comes from the OBJECT STORE (<c>GetObjectRetention</c>),
/// never from the database. Hard constraint 4: if S3 says the object is locked, the database's
/// opinion does not matter — the lock is a physical impossibility, not a policy. Null means the
/// caller found no object-level lock (or no object at all, for an already-erased statement).
/// </para>
/// <para>
/// <see cref="Today"/> is passed in rather than read from a clock so the decision is reproducible:
/// the same context always yields the same decision, in tests, in an audit reconstruction, and in
/// production.
/// </para>
/// </remarks>
/// <param name="RetainUntil">The statutory retention date from the statement row.</param>
/// <param name="ObjectLockRetainUntil">The object store's retain-until date, or null when the store reports no lock.</param>
/// <param name="HasActiveLegalHold">Whether any unreleased legal hold covers this statement (statement- or customer-scoped).</param>
/// <param name="LegalHoldCaseReference">The case reference of the active hold; null when there is none.</param>
/// <param name="CustomerKeyDestroyed">Whether the customer's CEK has been destroyed (crypto-erasure already happened).</param>
/// <param name="Today">The date the decision is being made, in the retention calendar's time zone.</param>
public sealed record RetentionContext(
    DateOnly RetainUntil,
    DateOnly? ObjectLockRetainUntil,
    bool HasActiveLegalHold,
    string? LegalHoldCaseReference,
    bool CustomerKeyDestroyed,
    DateOnly Today);

/// <summary>
/// The outcome of a retention decision — a verdict plus the reason, because the reason is what
/// flows into the API response and the audit record.
/// </summary>
/// <remarks>
/// Compare <c>409 Conflict</c> with <c>409 Conflict — cannot erase: FICA s23 requires retention
/// until 2031-03-14</c>. The second is a defensible regulatory response; the first is a bug
/// report. Every blocked or retained outcome therefore carries its basis and its date.
/// </remarks>
public abstract record RetentionDecision
{
    private RetentionDecision()
    {
    }

    /// <summary>Nothing prevents removal: delete the object versions, then mark the row purged.</summary>
    public sealed record Purge : RetentionDecision;

    /// <summary>A statutory retention period has not yet expired.</summary>
    /// <param name="Basis">The statutes requiring retention, cited.</param>
    /// <param name="Until">When the obligation ends.</param>
    public sealed record RetainStatutory(string Basis, DateOnly Until) : RetentionDecision;

    /// <summary>An active legal hold forbids any destruction, indefinitely.</summary>
    /// <param name="CaseReference">The matter the hold belongs to — the only key that can ever release it.</param>
    public sealed record BlockedByLegalHold(string CaseReference) : RetentionDecision;

    /// <summary>The object store's Compliance-mode lock makes deletion physically impossible.</summary>
    /// <param name="Until">When the lock expires and the object becomes merely eligible for deletion.</param>
    public sealed record BlockedByObjectLock(DateOnly Until) : RetentionDecision;

    /// <summary>The customer's key is already destroyed: the ciphertext is unreadable, only bookkeeping remains.</summary>
    public sealed record AlreadyErased : RetentionDecision;
}
