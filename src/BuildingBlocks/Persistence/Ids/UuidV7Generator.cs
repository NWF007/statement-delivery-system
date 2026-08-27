using System.Buffers.Binary;
using StatementDelivery.Domain.Abstractions;

namespace StatementDelivery.Persistence.Ids;

/// <summary>
/// Generates RFC 9562 UUIDv7 identifiers that are strictly increasing in big-endian byte order.
/// </summary>
/// <remarks>
/// <para>
/// WHY UUIDv7 AT ALL. UUIDv4 is random, so every insert lands at a random point in the B-tree.
/// At 30 million inserts a month that means constant page splits, index fragmentation, write
/// amplification and cache misses. UUIDv7 puts a 48-bit millisecond timestamp in the leading
/// bits, so inserts append near the right edge of the index like a sequential key while staying
/// globally unique and non-enumerable. See docs/adr/0006-uuidv7-primary-keys.md.
/// </para>
/// <para>
/// WHY THIS IS NOT JUST A CALL TO Guid.CreateVersion7. The framework method is monotonic ACROSS
/// milliseconds but not WITHIN one: the sub-millisecond bits are random, so a burst generated
/// inside a single millisecond comes back roughly half out of order. Measured on .NET 10, a
/// 2,000-identifier burst produced 1,019 descending adjacent pairs. Index locality only needs
/// approximate ordering, so that would have been acceptable - but strict ordering is testable,
/// costs one comparison, and makes keyset pagination on the identifier safe. This type therefore
/// layers a per-process monotonic guard on top: when the framework hands back a value that does
/// not sort after the last one issued, the last one is incremented instead.
/// </para>
/// <para>
/// WHY BIG-ENDIAN COMPARISON. Guid.CompareTo and the parameterless Guid.ToByteArray use .NET's
/// mixed-endian field layout, which is NOT the byte order PostgreSQL sorts uuid values by. An
/// ordering test written against Guid.CompareTo can pass for the wrong reason. Everything here
/// compares ToByteArray(bigEndian: true), which is the on-the-wire order PostgreSQL sees.
/// </para>
/// <para>
/// Thread-safe. Contention is a single lock around sixteen bytes of comparison; at the design
/// peak of 1,400 inserts per second it is not measurable.
/// </para>
/// </remarks>
public sealed class UuidV7Generator : IIdGenerator
{
    private const int VersionByteIndex = 6;
    private const ushort VersionMask = 0xF000;
    private const ushort RandAMask = 0x0FFF;
    private const ulong VariantMask = 0xC000_0000_0000_0000UL;
    private const ulong RandBMask = 0x3FFF_FFFF_FFFF_FFFFUL;
    private const ulong TimestampMask = 0x0000_FFFF_FFFF_FFFFUL;

    private readonly Lock _gate = new();
    private readonly byte[] _last = new byte[16];
    private bool _hasLast;

    /// <inheritdoc />
    public Guid NewId()
    {
        Span<byte> candidate = stackalloc byte[16];

        lock (_gate)
        {
            if (!Guid.CreateVersion7().TryWriteBytes(candidate, bigEndian: true, out _))
            {
                throw new InvalidOperationException("Failed to write a UUIDv7 into a 16-byte buffer.");
            }

            if (_hasLast && candidate.SequenceCompareTo(_last) <= 0)
            {
                // Same millisecond (or a clock that went backwards). Take the last value issued
                // and increment it. The result is still a well-formed UUIDv7: version and variant
                // nibbles are preserved and only the random payload moves.
                _last.CopyTo(candidate);
                Increment(candidate);
            }

            candidate.CopyTo(_last);
            _hasLast = true;
        }

        return new Guid(candidate, bigEndian: true);
    }

    /// <summary>
    /// Extracts the creation instant embedded in a UUIDv7.
    /// </summary>
    /// <remarks>
    /// Operational convenience, and a reminder of the trade-off: this method working at all is
    /// exactly why a UUIDv7 must never be used as a download token. On PostgreSQL 18+ the same
    /// information is available server-side as uuid_extract_timestamp().
    /// </remarks>
    /// <param name="id">A version 7 UUID.</param>
    /// <returns>The embedded millisecond timestamp, in UTC.</returns>
    /// <exception cref="ArgumentException">The value is not a version 7 UUID.</exception>
    public static DateTimeOffset GetTimestamp(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!id.TryWriteBytes(bytes, bigEndian: true, out _))
        {
            throw new ArgumentException("Failed to read the UUID bytes.", nameof(id));
        }

        if ((bytes[VersionByteIndex] >> 4) != 7)
        {
            throw new ArgumentException("The value is not a version 7 UUID.", nameof(id));
        }

        Span<byte> padded = stackalloc byte[8];
        padded.Clear();
        bytes[..6].CopyTo(padded[2..]);
        return DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64BigEndian(padded));
    }

    /// <summary>
    /// Adds one to the random payload of a big-endian UUIDv7 in place, carrying into the
    /// timestamp if - and only if - both random fields have saturated.
    /// </summary>
    /// <remarks>
    /// Carrying is written out in full for correctness, but reaching it requires 2^74 identifiers
    /// inside one millisecond. In practice the first step always terminates.
    /// </remarks>
    private static void Increment(Span<byte> bytes)
    {
        // rand_b: the low 62 bits of bytes 8..15. The top two bits are the RFC 9562 variant.
        ulong low = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
        ulong variant = low & VariantMask;
        ulong randB = (low & RandBMask) + 1;
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], variant | (randB & RandBMask));
        if ((randB & RandBMask) != 0)
        {
            return;
        }

        // rand_a: the low 12 bits of bytes 6..7. The top four bits are the version.
        ushort high = BinaryPrimitives.ReadUInt16BigEndian(bytes[6..8]);
        ushort version = (ushort)(high & VersionMask);
        int randA = (high & RandAMask) + 1;
        BinaryPrimitives.WriteUInt16BigEndian(bytes[6..8], (ushort)(version | (randA & RandAMask)));
        if ((randA & RandAMask) != 0)
        {
            return;
        }

        // Both random fields wrapped. Borrow a millisecond from the future; the value stays a
        // valid, still-increasing UUIDv7.
        Span<byte> padded = stackalloc byte[8];
        padded.Clear();
        bytes[..6].CopyTo(padded[2..]);
        ulong timestamp = (BinaryPrimitives.ReadUInt64BigEndian(padded) + 1) & TimestampMask;
        BinaryPrimitives.WriteUInt64BigEndian(padded, timestamp);
        padded[2..].CopyTo(bytes[..6]);
    }
}
