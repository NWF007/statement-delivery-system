using System.Globalization;
using StatementDelivery.Domain.Exceptions;
using StatementDelivery.Domain.Identifiers;

namespace StatementDelivery.Domain.Tokens;

/// <summary>
/// The rules governing how long a download link lives and how often it may be used.
/// </summary>
/// <remarks>
/// Pure calculation, no I/O, no clock of its own. The database enforces the same ceiling
/// independently via <c>ck_token_ttl</c> - defence in depth, because a bug here would otherwise
/// mint a long-lived credential and nothing would notice.
/// </remarks>
/// <param name="DefaultTtl">Applied when the caller does not ask for a specific lifetime.</param>
/// <param name="MaxTtl">The ceiling. Requests above it are clamped, not rejected.</param>
/// <param name="SingleUse">Whether redemption consumes the token.</param>
public sealed record TokenPolicy(TimeSpan DefaultTtl, TimeSpan MaxTtl, bool SingleUse)
{
    /// <summary>
    /// The shortest lifetime that is operationally sane.
    /// </summary>
    /// <remarks>
    /// Below this, ordinary clock skew between the issuing service and the redeeming one is enough
    /// to make a link that was valid when returned already expired when followed - a failure the
    /// customer cannot distinguish from a broken system.
    /// </remarks>
    public static readonly TimeSpan MinimumTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Ten minutes, one hour, single use.
    /// </summary>
    /// <remarks>
    /// Ten minutes is long enough to click a link in an email and short enough that a URL leaked
    /// into a browser history, a proxy log or a screenshot is worthless by the time anyone finds
    /// it. One hour is the ceiling because a bearer credential in a URL should never outlive the
    /// session that asked for it.
    /// </remarks>
    public static readonly TokenPolicy Default =
        new(TimeSpan.FromMinutes(10), TimeSpan.FromHours(1), SingleUse: true);

    /// <summary>
    /// Resolves the lifetime for a request.
    /// </summary>
    /// <remarks>
    /// CLAMPS above the maximum rather than rejecting: a client asking for a day gets an hour and a
    /// working link, which is the useful behaviour. REJECTS below the minimum rather than clamping,
    /// because a caller asking for five seconds has misunderstood something, and silently giving
    /// them thirty would hide the misunderstanding.
    /// </remarks>
    /// <param name="requested">The requested lifetime, or null for the default.</param>
    /// <returns>The lifetime to apply.</returns>
    /// <exception cref="InvariantViolationException">The request is below <see cref="MinimumTtl"/>.</exception>
    public TimeSpan Resolve(TimeSpan? requested)
    {
        if (requested is null)
        {
            return DefaultTtl;
        }

        if (requested.Value < MinimumTtl)
        {
            throw new InvariantViolationException(string.Create(
                CultureInfo.InvariantCulture,
                $"A download link must live at least {MinimumTtl.TotalSeconds} seconds; {requested.Value.TotalSeconds} was requested."));
        }

        return requested.Value > MaxTtl ? MaxTtl : requested.Value;
    }
}

/// <summary>
/// A single-use, short-lived, customer-bound capability to download one statement.
/// </summary>
/// <remarks>
/// Holds the token's HASH, never its plaintext. There is no property, constructor parameter or
/// method here through which a <see cref="TokenSecret"/> can enter or leave - an architecture test
/// asserts the persistence layer never references that type at all.
/// </remarks>
public sealed record DownloadToken
{
    private DownloadToken()
    {
    }

    /// <summary>Gets the link identifier. Safe to return to the customer; it is not the credential.</summary>
    public required DownloadTokenId Id { get; init; }

    /// <summary>Gets the statement this token grants access to, and only this one.</summary>
    public required StatementId StatementId { get; init; }

    /// <summary>
    /// Gets the RANGE partition key of that statement.
    /// </summary>
    /// <remarks>
    /// Carried on the token so redemption can fetch the statement with a PRUNED point lookup.
    /// Without it, the hottest path in the system would fan out across every monthly partition on
    /// every download. It costs nothing to capture: the issue endpoint already requires the period
    /// for exactly the same reason.
    /// </remarks>
    public required DateOnly StatementPeriod { get; init; }

    /// <summary>
    /// Gets the customer the token is bound to.
    /// </summary>
    /// <remarks>
    /// BINDING MATTERS. Without it, a token leaked from one customer would be a capability against
    /// whatever statement it names, with no way to reason about who should hold it. With it, the
    /// token is useless to anyone else and every redemption can be attributed to an owner in the
    /// audit trail.
    /// </remarks>
    public required CustomerId CustomerId { get; init; }

    /// <summary>Gets the SHA-256 of the plaintext. The only form that is ever stored.</summary>
    public required TokenHash Hash { get; init; }

    /// <summary>Gets when the link was issued.</summary>
    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>Gets when the link expires. Also the RANGE partition key.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Gets when the link was redeemed, or null.</summary>
    public DateTimeOffset? ConsumedAt { get; init; }

    /// <summary>Gets when the link was revoked, or null.</summary>
    public DateTimeOffset? RevokedAt { get; init; }

    /// <summary>Gets why the link was revoked. Internal; never returned to a caller.</summary>
    public string? RevokedReason { get; init; }

    /// <summary>Gets a value indicating whether redemption consumes the link.</summary>
    public required bool SingleUse { get; init; }

    /// <summary>Creates a token from a generated secret's hash.</summary>
    /// <param name="id">The link identifier.</param>
    /// <param name="statementId">The statement being granted.</param>
    /// <param name="statementPeriod">The statement RANGE partition key.</param>
    /// <param name="customerId">The owner the token is bound to.</param>
    /// <param name="hash">The hash of the plaintext.</param>
    /// <param name="issuedAt">Issue instant.</param>
    /// <param name="ttl">Resolved lifetime.</param>
    /// <param name="singleUse">Whether redemption consumes the link.</param>
    /// <returns>The token.</returns>
    public static DownloadToken Issue(
        DownloadTokenId id,
        StatementId statementId,
        DateOnly statementPeriod,
        CustomerId customerId,
        TokenHash hash,
        DateTimeOffset issuedAt,
        TimeSpan ttl,
        bool singleUse)
    {
        if (ttl < TokenPolicy.MinimumTtl)
        {
            throw new InvariantViolationException("A download link must live at least 30 seconds.");
        }

        if (ttl > TokenPolicy.Default.MaxTtl)
        {
            throw new InvariantViolationException("A download link must not live longer than one hour.");
        }

        return new DownloadToken
        {
            Id = id,
            StatementId = statementId,
            StatementPeriod = statementPeriod,
            CustomerId = customerId,
            Hash = hash,
            IssuedAt = issuedAt,
            ExpiresAt = issuedAt.Add(ttl),
            SingleUse = singleUse,
        };
    }

    /// <summary>Rehydrates a token from storage.</summary>
    /// <param name="id">The link identifier.</param>
    /// <param name="statementId">The statement.</param>
    /// <param name="statementPeriod">The statement RANGE partition key.</param>
    /// <param name="customerId">The owner.</param>
    /// <param name="hash">The stored hash.</param>
    /// <param name="issuedAt">Issue instant.</param>
    /// <param name="expiresAt">Expiry instant.</param>
    /// <param name="consumedAt">Consumption instant, if any.</param>
    /// <param name="revokedAt">Revocation instant, if any.</param>
    /// <param name="revokedReason">Revocation reason, if any.</param>
    /// <param name="singleUse">Whether redemption consumes the link.</param>
    /// <returns>The token.</returns>
    public static DownloadToken Rehydrate(
        DownloadTokenId id,
        StatementId statementId,
        DateOnly statementPeriod,
        CustomerId customerId,
        TokenHash hash,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        DateTimeOffset? consumedAt,
        DateTimeOffset? revokedAt,
        string? revokedReason,
        bool singleUse) =>
        new()
        {
            Id = id,
            StatementId = statementId,
            StatementPeriod = statementPeriod,
            CustomerId = customerId,
            Hash = hash,
            IssuedAt = issuedAt,
            ExpiresAt = expiresAt,
            ConsumedAt = consumedAt,
            RevokedAt = revokedAt,
            RevokedReason = revokedReason,
            SingleUse = singleUse,
        };

    // =========================================================================================
    //  ⚠  THIS METHOD MUST NEVER BE USED TO GATE REDEMPTION.
    //
    //  Redemption is decided by the DATABASE, in a single atomic UPDATE ... RETURNING. Reading a
    //  token, calling this, and then updating if it returns true reintroduces exactly the
    //  time-of-check-to-time-of-use race this design exists to eliminate: two concurrent requests
    //  both read ConsumedAt == null, both see true here, and both proceed - and the statement is
    //  delivered twice from a link that promised once.
    //
    //  It exists for TESTING and DIAGNOSTICS only: to express the rule in a unit test without a
    //  database, and to explain in an operator tool why a given link is not working.
    //
    //  If you find yourself writing `if (token.IsRedeemable(now))` outside a test, stop.
    // =========================================================================================

    /// <summary>
    /// Pure predicate describing redeemability. FOR TESTS AND DIAGNOSTICS ONLY - see the comment
    /// above; never use this to make an access decision.
    /// </summary>
    /// <param name="now">The instant to evaluate at.</param>
    /// <returns><see langword="true"/> when the token would currently be redeemable.</returns>
    public bool IsRedeemable(DateTimeOffset now) =>
        RevokedAt is null
        && (ConsumedAt is null || !SingleUse)
        && now < ExpiresAt;
}
