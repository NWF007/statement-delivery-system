using System.Security.Cryptography;
using Shouldly;
using StatementDelivery.Domain.Abstractions;
using StatementDelivery.Domain.Exceptions;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Domain.Tokens;
using StatementDelivery.Persistence.Ids;
using Xunit;

namespace UnitTests.Domain;

/// <summary>
/// The token primitives, tested without a database, a clock or a container.
/// </summary>
/// <remarks>
/// These are the rules the atomic consume relies on being true. They are cheap to run and they fail
/// loudly, which is the right shape for the layer everything else is built on.
/// </remarks>
public sealed class DownloadTokenTests
{
    /// <summary>A deterministic byte source, so a hash test can assert an exact value.</summary>
    private sealed class FixedRandomBytes : IRandomBytes
    {
        private readonly byte _fill;

        public FixedRandomBytes(byte fill) => _fill = fill;

        public void Fill(Span<byte> destination) => destination.Fill(_fill);
    }

    private sealed class CryptoBytes : IRandomBytes
    {
        public void Fill(Span<byte> destination) => RandomNumberGenerator.Fill(destination);
    }

    private static readonly IIdGenerator Ids = new UuidV7Generator();

    [Fact]
    public void TokenSecret_Generate_Produces32Bytes()
    {
        TokenSecret secret = TokenSecret.Generate(new CryptoBytes());

        secret.Bytes.Length.ShouldBe(32);
        TokenSecret.SizeInBytes.ShouldBe(32);
    }

    [Fact]
    public void TokenSecret_UrlSafeString_Is43CharsNoPadding()
    {
        string encoded = TokenSecret.Generate(new CryptoBytes()).ToUrlSafeString();

        // 32 bytes is 256 bits; base64url without padding is ceil(256/6) = 43 characters.
        encoded.Length.ShouldBe(43);
        encoded.Length.ShouldBe(TokenSecret.EncodedLength);

        // URL-safe alphabet only. A '+' or '/' would be re-encoded by an intermediary and the link
        // would stop working somewhere between the email client and here.
        encoded.ShouldNotContain("=");
        encoded.ShouldNotContain("+");
        encoded.ShouldNotContain("/");
        encoded.ShouldAllBe(static c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');
    }

    [Fact]
    public void TokenSecret_TwoGenerations_AreDifferent()
    {
        var rng = new CryptoBytes();

        // Not two. Two identical values could be a one-in-2^256 coincidence; a hundred distinct
        // values would not be, and a stuck or seeded generator shows up immediately.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 100; i++)
        {
            seen.Add(TokenSecret.Generate(rng).ToUrlSafeString()).ShouldBeTrue();
        }

        seen.Count.ShouldBe(100);
    }

    [Fact]
    public void TokenHash_IsDeterministic()
    {
        TokenHash first = TokenSecret.Generate(new FixedRandomBytes(0x2A)).ComputeHash();
        TokenHash second = TokenSecret.Generate(new FixedRandomBytes(0x2A)).ComputeHash();

        second.ShouldBe(first);

        // Pinned against an independent computation rather than against itself, so a change to the
        // hashing scheme cannot pass by agreeing with its own new output.
        byte[] expected = SHA256.HashData(Enumerable.Repeat((byte)0x2A, 32).ToArray());
        first.ToArray().ShouldBe(expected);
    }

    [Fact]
    public void TokenHash_DiffersForDifferentSecrets()
    {
        TokenHash a = TokenSecret.Generate(new FixedRandomBytes(0x01)).ComputeHash();
        TokenHash b = TokenSecret.Generate(new FixedRandomBytes(0x02)).ComputeHash();

        b.ShouldNotBe(a);
    }

    [Fact]
    public void TokenSecret_TryParse_RoundTrips()
    {
        string encoded = TokenSecret.Generate(new FixedRandomBytes(0x7F)).ToUrlSafeString();

        Span<byte> buffer = stackalloc byte[TokenSecret.SizeInBytes];
        TokenSecret.TryParse(encoded, buffer, out TokenSecret parsed).ShouldBeTrue();
        parsed.ToUrlSafeString().ShouldBe(encoded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void TokenSecret_TryParse_NeverThrowsOnGarbage(string? input)
    {
        // A parse that throws on malformed input is a 400 that distinguishes "wrong shape" from
        // "wrong value" - which is exactly the oracle the uniform-failure rule exists to remove.
        Span<byte> buffer = stackalloc byte[TokenSecret.SizeInBytes];
        TokenSecret.TryParse(input, buffer, out _).ShouldBeFalse();
    }

    [Fact]
    public void TokenSecret_ToString_IsRedacted() =>
        TokenSecret.Generate(new CryptoBytes()).ToString().ShouldBe("TokenSecret([REDACTED])");

    [Fact]
    public void TokenPolicy_ClampsToMaxTtl()
    {
        TokenPolicy policy = TokenPolicy.Default;

        // Clamped, not rejected. A client asking for a day gets an hour and a working link, which
        // is the useful behaviour.
        policy.Resolve(TimeSpan.FromDays(1)).ShouldBe(policy.MaxTtl);
        policy.Resolve(TimeSpan.FromHours(1)).ShouldBe(TimeSpan.FromHours(1));
        policy.Resolve(null).ShouldBe(policy.DefaultTtl);
    }

    [Fact]
    public void TokenPolicy_RejectsSubThirtySecondTtl()
    {
        TokenPolicy policy = TokenPolicy.Default;

        // Rejected, not clamped: a caller asking for five seconds has misunderstood something, and
        // silently giving them thirty would hide the misunderstanding.
        Should.Throw<InvariantViolationException>(() => policy.Resolve(TimeSpan.FromSeconds(5)));
        Should.Throw<InvariantViolationException>(() => policy.Resolve(TimeSpan.Zero));
        policy.Resolve(TokenPolicy.MinimumTtl).ShouldBe(TokenPolicy.MinimumTtl);
    }

    [Fact]
    public void DownloadToken_Issue_RejectsTtlBeyondOneHour() =>
        Should.Throw<InvariantViolationException>(() => Issue(TimeSpan.FromHours(2)));

    [Fact]
    public void DownloadToken_Issue_SetsExpiryFromIssuedAtPlusTtl()
    {
        DownloadToken token = Issue(TimeSpan.FromMinutes(10));

        (token.ExpiresAt - token.IssuedAt).ShouldBe(TimeSpan.FromMinutes(10));
        token.ConsumedAt.ShouldBeNull();
        token.RevokedAt.ShouldBeNull();
        token.SingleUse.ShouldBeTrue();
    }

    public static TheoryData<string, bool, bool, bool, int, bool> RedeemableCases() => new()
    {
        // description, singleUse, consumed, revoked, minutesFromIssue, expected
        { "fresh, unused, within TTL", true, false, false, 1, true },
        { "at the moment of issue", true, false, false, 0, true },
        { "one second before expiry", true, false, false, 9, true },
        { "exactly at expiry", true, false, false, 10, false },
        { "past expiry", true, false, false, 11, false },
        { "already consumed, single use", true, true, false, 1, false },
        { "already consumed, multi use", false, true, false, 1, true },
        { "revoked", true, false, true, 1, false },
        { "revoked and consumed", true, true, true, 1, false },
        { "revoked after expiry", true, false, true, 99, false },
    };

    [Theory]
    [MemberData(nameof(RedeemableCases))]
    public void DownloadToken_IsRedeemable_TableDriven(
        string description,
        bool singleUse,
        bool consumed,
        bool revoked,
        int minutesFromIssue,
        bool expected)
    {
        DateTimeOffset issuedAt = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

        DownloadToken token = DownloadToken.Rehydrate(
            DownloadTokenId.New(Ids),
            StatementId.New(Ids),
            new DateOnly(2026, 8, 1),
            CustomerId.New(Ids),
            TokenSecret.Generate(new CryptoBytes()).ComputeHash(),
            issuedAt,
            issuedAt.AddMinutes(10),
            consumed ? issuedAt.AddMinutes(1) : null,
            revoked ? issuedAt.AddMinutes(1) : null,
            revoked ? "customer-request" : null,
            singleUse);

        token.IsRedeemable(issuedAt.AddMinutes(minutesFromIssue))
            .ShouldBe(expected, description);
    }

    private static DownloadToken Issue(TimeSpan ttl) =>
        DownloadToken.Issue(
            DownloadTokenId.New(Ids),
            StatementId.New(Ids),
            new DateOnly(2026, 8, 1),
            CustomerId.New(Ids),
            TokenSecret.Generate(new CryptoBytes()).ComputeHash(),
            new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero),
            ttl,
            singleUse: true);
}
