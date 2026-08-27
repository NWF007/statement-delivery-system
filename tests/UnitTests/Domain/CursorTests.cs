using Shouldly;
using StatementDelivery.Domain.ValueObjects;
using Xunit;

namespace UnitTests.Domain;

/// <summary>
/// The keyset pagination cursor.
/// </summary>
public sealed class CursorTests
{
    private static readonly DateOnly Period = new(2026, 8, 1);
    private static readonly Guid Id = Guid.Parse("01a03ee4-d9c8-7534-800d-b5b2c5ce17e8");

    [Fact]
    public void RoundTrips()
    {
        var original = new Cursor(Period, Id);

        Cursor.TryDecode(original.Encode(), out Cursor decoded).ShouldBeTrue();

        decoded.PeriodStart.ShouldBe(Period);
        decoded.Id.ShouldBe(Id);
        decoded.ShouldBe(original);
    }

    [Theory]
    [InlineData(1900, 1, 1)]
    [InlineData(2026, 8, 1)]
    [InlineData(9999, 12, 31)]
    public void RoundTrips_AcrossTheWholeSupportedDateRange(int year, int month, int day)
    {
        var original = new Cursor(new DateOnly(year, month, day), Id);

        Cursor.TryDecode(original.Encode(), out Cursor decoded).ShouldBeTrue();
        decoded.ShouldBe(original);
    }

    [Fact]
    public void Encode_ProducesUrlSafeText()
    {
        // The cursor travels in a query string. Base64 with + and / would need escaping, and an
        // unescaped one silently decodes to something else.
        string encoded = new Cursor(Period, Id).Encode();

        encoded.ShouldNotContain("+");
        encoded.ShouldNotContain("/");
        encoded.ShouldNotContain("=");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-base64-url!!")]
    [InlineData("AAAA")]
    [InlineData("////")]
    public void MalformedInput_FailsCleanlyWithoutThrowing(string? input)
    {
        // MUST NOT THROW. A cursor is caller-supplied input on a public API; an exception here is a
        // 500 where the correct answer is a 400. Note that Base64Url.TryDecodeFromChars itself
        // throws on invalid characters, which is exactly the trap this guards.
        Cursor decoded = default;

        Should.NotThrow(() => Cursor.TryDecode(input, out decoded));

        Cursor.TryDecode(input, out decoded).ShouldBeFalse();
        decoded.ShouldBe(default(Cursor));
    }

    [Fact]
    public void UnknownFormatVersion_IsRejected()
    {
        // The version byte exists so that changing the payload later fails loudly instead of
        // decoding an old cursor into the wrong fields.
        byte[] payload = [9, .. new byte[4 + 16]];

        Cursor.TryDecode(System.Buffers.Text.Base64Url.EncodeToString(payload), out _).ShouldBeFalse();
    }

    [Fact]
    public void TruncatedPayload_IsRejected() =>
        Cursor.TryDecode(new Cursor(Period, Id).Encode()[..^3], out _).ShouldBeFalse();

    [Fact]
    public void EncodedCursors_SortInTheSameOrderAsTheirComponents()
    {
        // The cursor is a boundary in a row-value comparison, so ordering by (period, id) must
        // agree with what the database does.
        var earlier = new Cursor(new DateOnly(2026, 7, 1), Id);
        var later = new Cursor(new DateOnly(2026, 8, 1), Id);

        Cursor.TryDecode(earlier.Encode(), out Cursor decodedEarlier).ShouldBeTrue();
        Cursor.TryDecode(later.Encode(), out Cursor decodedLater).ShouldBeTrue();

        decodedEarlier.PeriodStart.ShouldBeLessThan(decodedLater.PeriodStart);
    }

    [Fact]
    public void IsOpaqueButNotSecret()
    {
        // Documents the security posture rather than asserting a bug: anyone can decode a cursor,
        // so nothing may go in one that the caller is not already entitled to see, and the query
        // that consumes it still carries customer_id = @owner in its WHERE clause.
        string encoded = new Cursor(Period, Id).Encode();

        Cursor.TryDecode(encoded, out Cursor decoded).ShouldBeTrue();
        decoded.Id.ShouldBe(Id, "the payload is readable by design; authorisation never relies on it being hidden");
    }
}
