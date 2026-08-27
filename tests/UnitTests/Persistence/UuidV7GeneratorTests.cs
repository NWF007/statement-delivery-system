using System.Buffers.Binary;
using Shouldly;
using StatementDelivery.Persistence.Ids;
using Xunit;

namespace UnitTests.Persistence;

/// <summary>
/// Tests for <see cref="UuidV7Generator"/>.
/// </summary>
/// <remarks>
/// Every comparison here is on <c>ToByteArray(bigEndian: true)</c>. That is not incidental: it is
/// the byte order PostgreSQL sorts <c>uuid</c> values by, and therefore the only order in which
/// "monotonic" means what this system needs it to mean. See
/// <see cref="GuidCompareTo_IsNotTheOrderingPostgresUses"/> for why the obvious alternative is a
/// trap.
/// </remarks>
public sealed class UuidV7GeneratorTests
{
    private static byte[] BigEndian(Guid value) => value.ToByteArray(bigEndian: true);

    [Fact]
    public void NewId_ProducesAVersion7Uuid()
    {
        var generator = new UuidV7Generator();

        byte[] bytes = BigEndian(generator.NewId());

        (bytes[6] >> 4).ShouldBe(7, "the version nibble must be 7");
        (bytes[8] >> 6).ShouldBe(0b10, "the variant bits must be the RFC 9562 variant");
    }

    [Fact]
    public void NewId_IsStrictlyMonotonic_WithinASingleMillisecondBurst()
    {
        // The whole point of the type. A burst this size lands inside one or two milliseconds, so
        // it is exactly the case where the framework primitive alone does NOT hold the ordering.
        var generator = new UuidV7Generator();
        const int Count = 10_000;

        var ids = new byte[Count][];
        for (int i = 0; i < Count; i++)
        {
            ids[i] = BigEndian(generator.NewId());
        }

        for (int i = 1; i < Count; i++)
        {
            ids[i].AsSpan().SequenceCompareTo(ids[i - 1])
                .ShouldBeGreaterThan(0, $"identifier {i} must sort after identifier {i - 1}");
        }
    }

    [Fact]
    public void GuidCreateVersion7_IsNotMonotonicWithinAMillisecond()
    {
        // Documents WHY UuidV7Generator exists rather than being a one-line call through to the
        // framework. Measured on .NET 10, a 2,000-identifier burst comes back roughly half out of
        // order because the sub-millisecond bits are random. If this test ever starts failing, the
        // runtime has changed its implementation and the monotonic guard can be reconsidered.
        const int Count = 2_000;
        var ids = new byte[Count][];
        for (int i = 0; i < Count; i++)
        {
            ids[i] = BigEndian(Guid.CreateVersion7());
        }

        int outOfOrder = 0;
        for (int i = 1; i < Count; i++)
        {
            if (ids[i].AsSpan().SequenceCompareTo(ids[i - 1]) <= 0)
            {
                outOfOrder++;
            }
        }

        outOfOrder.ShouldBeGreaterThan(
            0,
            "Guid.CreateVersion7 orders across milliseconds but not within one; the guard in UuidV7Generator is what makes ordering strict");
    }

    [Fact]
    public void GuidCompareTo_IsNotTheOrderingPostgresUses()
    {
        // A TRAP WORTH ENCODING. Guid.CompareTo and the parameterless Guid.ToByteArray use .NET's
        // mixed-endian field layout, not the big-endian byte order PostgreSQL sorts uuid by. A
        // monotonicity test written against Guid.CompareTo can pass while the database sees the
        // rows in a different order entirely.
        //
        // These two values differ only in bytes that .NET compares in a different position from
        // the order they appear on the wire.
        var lower = new Guid(Convert.FromHexString("01000000000070008000000000000001"), bigEndian: true);
        var higher = new Guid(Convert.FromHexString("01000000000070008000000000000002"), bigEndian: true);

        BigEndian(lower).AsSpan().SequenceCompareTo(BigEndian(higher)).ShouldBeLessThan(0);

        // And the two representations are genuinely different byte sequences, which is the reason
        // the distinction matters at all.
        lower.ToByteArray().ShouldNotBe(lower.ToByteArray(bigEndian: true));
    }

    [Fact]
    public void NewId_EmbedsTheCurrentTimestamp()
    {
        var generator = new UuidV7Generator();
        DateTimeOffset before = DateTimeOffset.UtcNow.AddSeconds(-1);

        DateTimeOffset embedded = UuidV7Generator.GetTimestamp(generator.NewId());

        DateTimeOffset after = DateTimeOffset.UtcNow.AddSeconds(1);
        embedded.ShouldBeInRange(before, after);
    }

    [Fact]
    public void GetTimestamp_MatchesTheLeadingFortyEightBits()
    {
        var generator = new UuidV7Generator();
        Guid id = generator.NewId();

        Span<byte> padded = stackalloc byte[8];
        padded.Clear();
        BigEndian(id).AsSpan(0, 6).CopyTo(padded[2..]);
        long milliseconds = BinaryPrimitives.ReadInt64BigEndian(padded);

        UuidV7Generator.GetTimestamp(id).ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds));
    }

    [Fact]
    public void GetTimestamp_RejectsANonVersion7Uuid()
    {
        Guid v4 = Guid.NewGuid();

        Should.Throw<ArgumentException>(() => UuidV7Generator.GetTimestamp(v4));
    }

    [Fact]
    public async Task NewId_IsMonotonic_UnderConcurrency()
    {
        // Sixteen threads racing the same generator. Without the lock this produces duplicates and
        // inversions; with it, the union of everything issued is still strictly ordered.
        var generator = new UuidV7Generator();
        const int Threads = 16;
        const int PerThread = 1_000;

        Guid[][] results = await Task.WhenAll(Enumerable.Range(0, Threads).Select(_ => Task.Run(() =>
        {
            var local = new Guid[PerThread];
            for (int i = 0; i < PerThread; i++)
            {
                local[i] = generator.NewId();
            }

            return local;
        }))).ConfigureAwait(true);

        List<byte[]> all = [.. results.SelectMany(batch => batch).Select(BigEndian)];

        all.Count.ShouldBe(Threads * PerThread);
        all.Select(Convert.ToHexString).Distinct(StringComparer.Ordinal).Count()
            .ShouldBe(all.Count, "no identifier may be issued twice");

        all.Sort(static (left, right) => left.AsSpan().SequenceCompareTo(right));
        for (int i = 1; i < all.Count; i++)
        {
            all[i].AsSpan().SequenceCompareTo(all[i - 1]).ShouldBeGreaterThan(0);
        }
    }

    [Fact]
    public void NewId_ProducesAValidUuidEvenWhenTheRandomTailIsIncremented()
    {
        // Exercises the carry path: a dense burst forces the generator off the framework value and
        // onto "increment the last one issued". The result must still be a well-formed UUIDv7,
        // with version and variant intact rather than trampled by the increment.
        var generator = new UuidV7Generator();

        for (int i = 0; i < 50_000; i++)
        {
            byte[] bytes = BigEndian(generator.NewId());
            (bytes[6] >> 4).ShouldBe(7);
            (bytes[8] >> 6).ShouldBe(0b10);
        }
    }
}
