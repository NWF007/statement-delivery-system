using System.Buffers.Binary;
using System.Buffers.Text;
using StatementDelivery.Domain.Exceptions;

namespace StatementDelivery.Domain.ValueObjects;

/// <summary>
/// An opaque keyset-pagination position: the last row of the previous page.
/// </summary>
/// <remarks>
/// <para>
/// KEYSET, NEVER OFFSET. <c>OFFSET</c> on a live table with concurrent inserts produces duplicates
/// and gaps - the offset is computed against a result set that has already changed by the time the
/// next page is requested. It also degrades linearly, because the server generates and discards
/// every skipped row.
/// </para>
/// <para>
/// CARRYING THE PARTITION KEY IS THE POINT. <c>statement</c> is RANGE-partitioned on
/// <c>period_start</c>. A cursor of just the identifier would force the planner to consider every
/// partition on the follow-up page; carrying the period with it keeps page two as pruneable as
/// page one.
/// </para>
/// <para>
/// OPAQUE, NOT SECRET. It is Base64Url and anyone can decode it. Never put anything in a cursor the
/// caller is not already entitled to see, and always re-authorise the decoded values - the query
/// that consumes this still carries <c>customer_id = @owner</c> in its WHERE clause.
/// </para>
/// </remarks>
/// <param name="PeriodStart">The partition key of the last row on the previous page.</param>
/// <param name="Id">The identifier of the last row, breaking ties within one period.</param>
public readonly record struct Cursor(DateOnly PeriodStart, Guid Id)
{
    /// <summary>
    /// Format version. An unknown version fails to decode rather than being reinterpreted as the
    /// current layout, so changing the payload later cannot silently misread cursors clients
    /// already hold.
    /// </summary>
    private const byte FormatVersion = 1;

    private const int PayloadLength = 1 + sizeof(int) + 16;

    /// <summary>Encodes the cursor as a URL-safe string.</summary>
    /// <returns>The encoded cursor.</returns>
    public string Encode()
    {
        Span<byte> payload = stackalloc byte[PayloadLength];
        payload[0] = FormatVersion;

        // DayNumber rather than a formatted date: fixed width, no culture, no ambiguity, and it
        // sorts identically to the date it represents.
        BinaryPrimitives.WriteInt32BigEndian(payload[1..], PeriodStart.DayNumber);

        if (!Id.TryWriteBytes(payload[5..], bigEndian: true, out _))
        {
            throw new InvariantViolationException("Failed to write the cursor identifier.");
        }

        return Base64Url.EncodeToString(payload);
    }

    /// <summary>
    /// Attempts to decode a cursor produced by <see cref="Encode"/>.
    /// </summary>
    /// <remarks>
    /// Never throws. A cursor is caller-supplied input on a public API, so a malformed one must
    /// produce a clean 400 rather than an unhandled exception and a 500. Note in particular that
    /// <c>Base64Url.TryDecodeFromChars</c> THROWS on invalid characters rather than returning
    /// false, which is why validity is checked first.
    /// </remarks>
    /// <param name="encoded">The encoded cursor.</param>
    /// <param name="cursor">The decoded cursor when the method returns true.</param>
    /// <returns><see langword="false"/> for null, empty, malformed or unknown-version input.</returns>
    public static bool TryDecode(string? encoded, out Cursor cursor)
    {
        cursor = default;

        if (string.IsNullOrWhiteSpace(encoded) || !Base64Url.IsValid(encoded))
        {
            return false;
        }

        Span<byte> payload = stackalloc byte[PayloadLength];
        if (!Base64Url.TryDecodeFromChars(encoded, payload, out int written)
            || written != PayloadLength
            || payload[0] != FormatVersion)
        {
            return false;
        }

        int dayNumber = BinaryPrimitives.ReadInt32BigEndian(payload[1..]);
        if (dayNumber < DateOnly.MinValue.DayNumber || dayNumber > DateOnly.MaxValue.DayNumber)
        {
            return false;
        }

        cursor = new Cursor(DateOnly.FromDayNumber(dayNumber), new Guid(payload[5..], bigEndian: true));
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => Encode();
}

/// <summary>
/// One page of a keyset-paginated result.
/// </summary>
/// <remarks>
/// There is deliberately no total count. Counting rows matching a predicate on a 2.5-billion-row
/// table costs a scan the page itself did not need, and the answer is stale before it reaches the
/// client. <see cref="HasMore"/> comes free from fetching one row beyond the limit.
/// </remarks>
/// <typeparam name="T">The item type.</typeparam>
/// <param name="Items">The page contents.</param>
/// <param name="NextCursor">The cursor to pass for the following page, or null at the end.</param>
/// <param name="HasMore">Whether a following page exists.</param>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, Cursor? NextCursor, bool HasMore);
