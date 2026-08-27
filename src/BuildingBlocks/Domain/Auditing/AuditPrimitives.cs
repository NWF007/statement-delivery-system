using System.Globalization;
using StatementDelivery.Domain.Identifiers;

namespace StatementDelivery.Domain.Auditing;

/// <summary>
/// Who performed the audited action.
/// </summary>
public static class ActorType
{
    /// <summary>An authenticated customer acting on their own data.</summary>
    public const string Customer = "CUSTOMER";

    /// <summary>A background process acting without a human.</summary>
    public const string System = "SYSTEM";

    /// <summary>An internal operator. Every staff action is a privileged one.</summary>
    public const string Staff = "STAFF";

    /// <summary>
    /// An unauthenticated caller. The download gateway's ordinary case: the token is the
    /// credential, so there is no identity to record beyond the token itself.
    /// </summary>
    public const string Anonymous = "ANONYMOUS";

    /// <summary>Every permitted value, for validation.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Customer, System, Staff, Anonymous };
}

/// <summary>How the audited action ended.</summary>
public static class AuditOutcome
{
    /// <summary>The action completed.</summary>
    public const string Success = "SUCCESS";

    /// <summary>
    /// The action was refused. THE INTERESTING ONE.
    /// </summary>
    /// <remarks>
    /// An audit log that records only successes cannot detect enumeration, credential stuffing or
    /// a compromised session probing what it can reach. The denials are the signal.
    /// </remarks>
    public const string Denied = "DENIED";

    /// <summary>The action failed for a reason that was not a policy decision.</summary>
    public const string Error = "ERROR";

    /// <summary>Every permitted value, for validation.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Success, Denied, Error };
}

/// <summary>
/// The catalogue of audited actions.
/// </summary>
/// <remarks>
/// CONSTANTS, NOT AN ENUM, and not a CHECK constraint on the column either. Adding an action is
/// something a feature will do routinely; if the set were an enum backed by a database constraint,
/// every new action would need a migration, and a migration in the same deploy as the code that
/// writes the new value is a rollout ordering problem nobody needs. The column is free text
/// precisely so the vocabulary can grow without the schema moving.
/// </remarks>
public static class AuditAction
{
    /// <summary>A customer listed their statements.</summary>
    public const string StatementListViewed = "STATEMENT_LIST_VIEWED";

    /// <summary>A customer read one statement's metadata.</summary>
    public const string StatementMetadataViewed = "STATEMENT_METADATA_VIEWED";

    /// <summary>A statement was rendered and stored.</summary>
    public const string StatementGenerated = "STATEMENT_GENERATED";

    /// <summary>A statement moved between lifecycle states.</summary>
    public const string StatementStatusChanged = "STATEMENT_STATUS_CHANGED";

    /// <summary>An action was refused. See <see cref="DenialReason"/> for the internal detail.</summary>
    public const string AccessDenied = "ACCESS_DENIED";

    /// <summary>A download link was issued to a customer.</summary>
    public const string LinkIssued = "LINK_ISSUED";

    /// <summary>A download link was revoked before it was used.</summary>
    public const string LinkRevoked = "LINK_REVOKED";

    /// <summary>
    /// A token was successfully consumed and streaming began.
    /// </summary>
    /// <remarks>
    /// Recorded BEFORE the bytes are sent and committed with the consume, so an aborted transfer
    /// still leaves a record that access was granted. Attempted access is what the audit must
    /// capture; whether the client finished reading is a separate, later fact.
    /// </remarks>
    public const string DownloadStarted = "DOWNLOAD_STARTED";

    /// <summary>The full statement was delivered.</summary>
    public const string DownloadCompleted = "DOWNLOAD_COMPLETED";

    /// <summary>The transfer ended before all bytes were sent - usually a client disconnect.</summary>
    public const string DownloadIncomplete = "DOWNLOAD_INCOMPLETE";
}

/// <summary>
/// Why an action was refused.
/// </summary>
/// <remarks>
/// INTERNAL ONLY. These are written to <c>audit_event.denial_reason_code</c> and MUST NOT reach a
/// caller. "Not yours" and "does not exist" are deliberately indistinguishable over the wire - see
/// docs/adr/0012-404-not-403-for-unowned-resources.md - and returning the reason would undo that
/// in one line.
/// </remarks>
public static class DenialReason
{
    /// <summary>The route's customer identifier did not match the authenticated subject.</summary>
    public const string SubjectMismatch = "SUBJECT_MISMATCH";

    /// <summary>The resource exists but belongs to another customer.</summary>
    public const string NotOwner = "NOT_OWNER";

    /// <summary>No such resource, for this owner or any other.</summary>
    public const string NotFound = "NOT_FOUND";

    /// <summary>The request carried no usable subject claim.</summary>
    public const string NoSubjectClaim = "NO_SUBJECT_CLAIM";

    // -----------------------------------------------------------------------------------------
    //  Token redemption failures.
    //
    //  All four produce an IDENTICAL response - same status, same body, same headers. The
    //  distinction lives here, in the audit trail, and nowhere else. Audit richly, respond opaquely:
    //  an investigator needs to know a consumed token was replayed rather than an unknown one
    //  guessed, and the caller must not be able to tell those apart.
    // -----------------------------------------------------------------------------------------

    /// <summary>The token was valid but has already been redeemed. A replay.</summary>
    public const string Consumed = "CONSUMED";

    /// <summary>The token was revoked before use.</summary>
    public const string Revoked = "REVOKED";

    /// <summary>The token existed but its lifetime had elapsed.</summary>
    public const string Expired = "EXPIRED";

    /// <summary>
    /// No such token has ever existed. In volume, this is what a guessing attack looks like.
    /// </summary>
    public const string UnknownToken = "UNKNOWN_TOKEN";

    /// <summary>The presented value was not a well-formed token at all.</summary>
    public const string MalformedToken = "MALFORMED_TOKEN";
}

/// <summary>
/// An audit record to be appended. Carries no chain position: the writer assigns that under lock.
/// </summary>
/// <param name="Id">Identifier of this occurrence. UUIDv7.</param>
/// <param name="StatementId">The statement acted on, if any. Drives chain assignment.</param>
/// <param name="CustomerId">The customer involved, if any. Drives chain assignment when there is no statement.</param>
/// <param name="ActorType">One of <see cref="Auditing.ActorType"/>.</param>
/// <param name="ActorId">The acting identity, or null when anonymous.</param>
/// <param name="Action">One of <see cref="AuditAction"/>.</param>
/// <param name="Outcome">One of <see cref="AuditOutcome"/>.</param>
/// <param name="DenialReasonCode">Internal denial detail. Never returned to a caller.</param>
/// <param name="SourceIp">The caller address, or null when unavailable.</param>
/// <param name="UserAgentHash">
/// A HASH of the user agent, never the value. The raw string is a fingerprinting vector and has no
/// diagnostic value the hash lacks: comparing hashes still answers "same client as last time".
/// </param>
/// <param name="Context">Free-form diagnostic detail, serialised canonically into the hash.</param>
/// <param name="OccurredAt">When the action happened, in UTC.</param>
public sealed record AuditEntry(
    AuditEventId Id,
    StatementId? StatementId,
    CustomerId? CustomerId,
    string ActorType,
    string? ActorId,
    string Action,
    string Outcome,
    string? DenialReasonCode,
    string? SourceIp,
    string? UserAgentHash,
    IReadOnlyDictionary<string, object?> Context,
    DateTimeOffset OccurredAt);

/// <summary>
/// A persisted audit record, including its position in the hash chain.
/// </summary>
/// <remarks>
/// IMMUTABLE, with no mutating methods at all - not even internal ones. The database enforces the
/// same rule with a trigger and a revoked grant; this type makes it true in the model as well, so
/// that code which never touches the database still cannot express an amendment.
/// </remarks>
/// <param name="ChainId">Which chain this record belongs to.</param>
/// <param name="ChainSeq">Position within that chain. Strictly increasing, no gaps.</param>
/// <param name="Entry">The recorded action.</param>
/// <param name="PreviousHash">The hash of the preceding record, or the chain genesis for the first.</param>
/// <param name="Hash">This record's hash.</param>
public sealed record AuditEvent(
    short ChainId,
    long ChainSeq,
    AuditEntry Entry,
    byte[] PreviousHash,
    byte[] Hash);

/// <summary>Proof that a record was appended, returned to the caller of an append.</summary>
/// <param name="ChainId">The chain the record landed in.</param>
/// <param name="Seq">Its position in that chain.</param>
/// <param name="Hash">Its hash. Quote this to prove the record existed at this position.</param>
public readonly record struct AuditReceipt(short ChainId, long Seq, byte[] Hash)
{
    /// <summary>Gets the hash in lower-case hexadecimal, for logging and support tickets.</summary>
    public string HashHex => Convert.ToHexStringLower(Hash);
}

/// <summary>
/// The result of walking one chain and recomputing every hash.
/// </summary>
/// <param name="ChainId">The chain examined.</param>
/// <param name="Verified">Whether every record's hash matched its recomputation.</param>
/// <param name="EventsChecked">How many records were walked.</param>
/// <param name="FirstBrokenSeq">The first position that failed, or null when the chain verified.</param>
/// <param name="ExpectedHash">What the hash should have been at that position.</param>
/// <param name="ActualHash">What was stored there.</param>
public sealed record ChainVerification(
    short ChainId,
    bool Verified,
    long EventsChecked,
    long? FirstBrokenSeq,
    byte[]? ExpectedHash,
    byte[]? ActualHash)
{
    /// <summary>A clean result for a chain that verified end to end.</summary>
    /// <param name="chainId">The chain examined.</param>
    /// <param name="eventsChecked">How many records were walked.</param>
    /// <returns>A verified result.</returns>
    public static ChainVerification Ok(short chainId, long eventsChecked) =>
        new(chainId, true, eventsChecked, null, null, null);

    /// <summary>A human-readable summary, for logs and health output.</summary>
    /// <returns>The summary.</returns>
    public override string ToString() => Verified
        ? string.Create(CultureInfo.InvariantCulture, $"chain {ChainId}: verified, {EventsChecked} event(s)")
        : string.Create(CultureInfo.InvariantCulture, $"chain {ChainId}: BROKEN at seq {FirstBrokenSeq}");
}
