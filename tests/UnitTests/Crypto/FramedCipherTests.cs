using System.Buffers.Binary;
using System.Security.Cryptography;
using Shouldly;
using StatementDelivery.Crypto.Framing;
using Xunit;

namespace UnitTests.Crypto;

/// <summary>
/// The SDP1 framed AEAD, exercised as a format rather than as an implementation.
/// </summary>
/// <remarks>
/// <para>
/// EVERY TAMPER TEST IN HERE ASSERTS THE SAME THING: the decoder throws and returns nothing. That
/// uniformity is the point - a format where some corruptions produce an error and others produce
/// slightly-wrong plaintext is a format that will eventually hand a customer somebody else's
/// statement, or their own statement with pages missing.
/// </para>
/// <para>
/// A small frame size is used throughout so that multi-frame paths are reachable without
/// multi-megabyte fixtures. The format records the frame size per object, so exercising 64-byte
/// frames tests exactly the same code that runs at 64 KiB.
/// </para>
/// </remarks>
/// <summary>
/// Runs alone: the two 200 MB tests measure the MANAGED HEAP DELTA across the operation, and any
/// collection running in parallel donates its allocations (QuestPDF's static font and layout
/// caches alone are tens of MB) to the "after" reading. The first full CI execution showed the
/// delta at ~68 MB with parallel neighbours and ~200 KB alone - same code, same bound.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MemoryMeasurementCollection
{
    /// <summary>The collection name.</summary>
    public const string Name = "memory-measurement";
}

[Collection(MemoryMeasurementCollection.Name)]
public sealed class FramedCipherTests
{
    private const int SmallFrame = 64;

    private static readonly CryptoContext Context = new(
        Guid.Parse("0199a1f0-1111-7000-8000-000000000001"),
        Guid.Parse("0199a1f0-2222-7000-8000-000000000002"),
        Version: 3);

    private static readonly byte[] Key = Convert.FromHexString(
        "6f9d3c1b8a27e45d0f13b6c9a8e75d24310fbe9c7a6d5e4f3b2a190807060504");

    // ─── Round trips ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RoundTrip_EmptyInput()
    {
        // Zero bytes is not a degenerate case to be special-cased away: it is the same case as
        // "length divides evenly", because 0 is a multiple of every frame size. One empty final
        // frame, and the object is still a well-formed, fully authenticated message.
        await AssertRoundTripAsync([], SmallFrame);
    }

    [Fact]
    public async Task RoundTrip_SingleByte()
    {
        await AssertRoundTripAsync([0x5A], SmallFrame);
    }

    [Fact]
    public async Task RoundTrip_ExactlyOneFrame()
    {
        await AssertRoundTripAsync(Pattern(SmallFrame - 1), SmallFrame);
    }

    [Fact]
    public async Task RoundTrip_ExactlyTwoFrames()
    {
        await AssertRoundTripAsync(Pattern((SmallFrame * 2) - 1), SmallFrame);
    }

    [Fact]
    public async Task RoundTrip_LengthIsExactMultipleOfFrameSize()
    {
        // THE CLASSIC OFF-BY-ONE IN FRAMED FORMATS. Without the deliberate empty final frame, a
        // message whose length divides evenly ends on a frame carrying isFinal = 0, and the
        // decoder's first rule correctly rejects a perfectly legitimate object. The bug looks like
        // "encryption works except for files that happen to be a round number of bytes".
        foreach (int multiple in new[] { 1, 2, 5 })
        {
            byte[] plaintext = Pattern(SmallFrame * multiple);
            byte[] ciphertext = await AssertRoundTripAsync(plaintext, SmallFrame);

            // Proven structurally as well as behaviourally: the frame count is one MORE than the
            // number of full frames, and the extra one carries no payload.
            (byte[] Header, List<byte[]> Frames) parsed = Split(ciphertext);
            parsed.Frames.Count.ShouldBe(multiple + 1);
            parsed.Frames[^1].Length.ShouldBe(FrameFormat.LengthPrefixLength + FrameFormat.TagLength);
        }
    }

    [Fact]
    public async Task RoundTrip_PreservesPlaintextSha256()
    {
        byte[] plaintext = Pattern(1000);
        byte[] expected = SHA256.HashData(plaintext);

        var cipher = new FramedAeadCipher(SmallFrame);
        using var source = new MemoryStream(plaintext);
        using var sink = new MemoryStream();

        CipherResult result = await cipher
            .EncryptAsync(source, sink, Key, Context, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        // Computed DURING encryption, in the same pass, never by re-reading the source.
        result.PlaintextSha256.ShouldBe(expected);
        result.PlaintextLength.ShouldBe(plaintext.Length);
        result.CiphertextLength.ShouldBe(sink.Length);
        result.CiphertextLength.ShouldBe(FrameFormat.CiphertextLengthFor(plaintext.Length, SmallFrame));
    }

    [Fact]
    public async Task RoundTrip_SourceReturningShortReads_IsFramedCorrectly()
    {
        // A source is entitled to return fewer bytes than asked for without being at its end, and a
        // network stream nearly always does. Treating a short read as end-of-input would emit a
        // final frame in the middle of the statement - producing an object that decrypts to a
        // truncated statement WITHOUT tripping any integrity check, because the encoder itself
        // signed the lie. This is why the encoder uses ReadAtLeast rather than Read.
        byte[] plaintext = Pattern(500);

        var cipher = new FramedAeadCipher(SmallFrame);
        using var source = new DribbleStream(plaintext, bytesPerRead: 7);
        using var sink = new MemoryStream();

        CipherResult result = await cipher
            .EncryptAsync(source, sink, Key, Context, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        result.PlaintextLength.ShouldBe(plaintext.Length);
        (await DecryptAsync(sink.ToArray()).ConfigureAwait(true)).ShouldBe(plaintext);
    }

    [Fact]
    public async Task RoundTrip_LargeInput_200MB()
    {
        // 200 MB, and at no point does 200 MB exist anywhere. The encrypting stream pulls from a
        // generator, the decrypting stream pulls from the encrypting stream, and the sink digests
        // what falls out - a three-stage pipeline whose entire working set is a handful of frame
        // buffers. Holding the object in a MemoryStream would prove the format round-trips while
        // quietly abandoning the constraint the format exists to satisfy.
        const long Size = 200L * 1024 * 1024;

        byte[] expectedDigest = await DigestOfAsync(new PatternStream(Size)).ConfigureAwait(true);

        using var plaintext = new PatternStream(Size);
        await using var encrypting = new FramedEncryptingStream(plaintext, Key, Context, leaveSourceOpen: true);
        await using var decrypting = new FramedDecryptingStream(encrypting, Key, Context, leaveSourceOpen: true);
        using var sink = new HashingSinkStream();

        await decrypting.CopyToAsync(sink, TestContext.Current.CancellationToken).ConfigureAwait(true);

        sink.Length.ShouldBe(Size);
        sink.Digest().ShouldBe(expectedDigest);

        encrypting.PlaintextLength.ShouldBe(Size);
        encrypting.CiphertextLength.ShouldBe(FrameFormat.CiphertextLengthFor(Size, FrameFormat.DefaultFrameSize));
        encrypting.PlaintextSha256.ShouldBe(expectedDigest);
    }

    // ─── Memory. The constraint the whole format exists to preserve. ─────────────────────────────

    [Fact]
    public async Task Encrypt_200MB_UsesConstantMemory()
    {
        const long Size = 200L * 1024 * 1024;

        long delta = await MeasureManagedHeapDeltaAsync(async () =>
        {
            var cipher = new FramedAeadCipher();
            using var plaintext = new PatternStream(Size);
            using var sink = new HashingSinkStream();

            _ = await cipher.EncryptAsync(plaintext, sink, Key, Context, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
        }).ConfigureAwait(true);

        // Printed, not merely asserted: the bound is what fails the build, but the actual number is
        // what tells you whether a change moved it from 200 KB to 9 MB while still passing.
        TestContext.Current.TestOutputHelper?.WriteLine($"encrypt 200 MB: managed heap delta {delta} bytes ({delta / 1024.0:F1} KB)");

        delta.ShouldBeLessThan(
            10L * 1024 * 1024,
            $"encrypting 200 MB grew the managed heap by {delta / 1024} KB; it must be independent of object size");
    }

    [Fact]
    public async Task Decrypt_200MB_UsesConstantMemory()
    {
        const long Size = 200L * 1024 * 1024;

        long delta = await MeasureManagedHeapDeltaAsync(async () =>
        {
            using var plaintext = new PatternStream(Size);
            await using var encrypting = new FramedEncryptingStream(plaintext, Key, Context, leaveSourceOpen: true);
            await using var decrypting = new FramedDecryptingStream(encrypting, Key, Context, leaveSourceOpen: true);
            using var sink = new HashingSinkStream();

            await decrypting.CopyToAsync(sink, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }).ConfigureAwait(true);

        TestContext.Current.TestOutputHelper?.WriteLine($"decrypt 200 MB: managed heap delta {delta} bytes ({delta / 1024.0:F1} KB)");

        delta.ShouldBeLessThan(
            10L * 1024 * 1024,
            $"decrypting 200 MB grew the managed heap by {delta / 1024} KB; it must be independent of object size");
    }

    // ─── Tamper detection. Every one must throw. ─────────────────────────────────────────────────

    [Fact]
    public async Task Tamper_SingleCiphertextByte_IsDetected()
    {
        byte[] ciphertext = await EncryptAsync(Pattern(200)).ConfigureAwait(true);

        // One bit, in the middle of the first frame's payload.
        ciphertext[FrameFormat.HeaderLength + FrameFormat.LengthPrefixLength + 10] ^= 0x01;

        await ShouldRejectAsync(ciphertext, IntegrityFailure.AuthenticationFailed).ConfigureAwait(true);
    }

    [Fact]
    public async Task Tamper_SingleTagByte_IsDetected()
    {
        byte[] ciphertext = await EncryptAsync(Pattern(200)).ConfigureAwait(true);

        int firstTagOffset = FrameFormat.HeaderLength + FrameFormat.LengthPrefixLength + SmallFrame;
        ciphertext[firstTagOffset] ^= 0x80;

        await ShouldRejectAsync(ciphertext, IntegrityFailure.AuthenticationFailed).ConfigureAwait(true);
    }

    [Fact]
    public async Task Tamper_HeaderFrameSize_IsDetected()
    {
        byte[] ciphertext = await EncryptAsync(Pattern(200)).ConfigureAwait(true);

        // A VALID value, not a wild one - the interesting attack is the one that survives the range
        // check and goes on to make the decoder misparse the body. The header tag is what stops it,
        // and it stops it before a single body byte is read.
        BinaryPrimitives.WriteUInt32BigEndian(ciphertext.AsSpan(5, 4), 128);

        await ShouldRejectAsync(ciphertext, IntegrityFailure.HeaderAuthenticationFailed).ConfigureAwait(true);
    }

    [Fact]
    public async Task Tamper_NoncePrefix_IsDetected()
    {
        byte[] ciphertext = await EncryptAsync(Pattern(200)).ConfigureAwait(true);

        ciphertext[25] ^= 0xFF;

        await ShouldRejectAsync(ciphertext, IntegrityFailure.HeaderAuthenticationFailed).ConfigureAwait(true);
    }

    [Fact]
    public async Task Truncate_DropFinalFrame_IsDetected()
    {
        // ★ THE TEST THAT PROVES THE FORMAT.
        //
        // Every frame that remains after this edit authenticates PERFECTLY. Its tag is genuine, its
        // AAD is genuine, its index is genuine. A framed format without a final-frame marker accepts
        // this object without complaint and hands back a statement that is silently missing its
        // tail - authentic, well-formed, and wrong. For a financial document that is the worst
        // possible failure mode, because nothing about it looks like a failure.
        //
        // What catches it: dropping the last frame promotes the preceding frame to last-on-the-wire,
        // so the decoder decrypts it with isFinal = 1 - and isFinal is in both the nonce and the
        // AAD, so the tag does not verify. If this test is missing, the format is broken in a way
        // every other test in this file still passes.
        byte[] ciphertext = await EncryptAsync(Pattern(200)).ConfigureAwait(true);

        (byte[] header, List<byte[]> frames) = Split(ciphertext);
        frames.Count.ShouldBeGreaterThan(1, "the fixture must have a droppable final frame");

        byte[] truncated = Concat(header, frames.Take(frames.Count - 1));

        await ShouldRejectAsync(truncated, IntegrityFailure.AuthenticationFailed).ConfigureAwait(true);
    }

    [Fact]
    public async Task Truncate_MidFrame_IsDetected()
    {
        byte[] ciphertext = await EncryptAsync(Pattern(200)).ConfigureAwait(true);

        // Cut inside the last frame's tag, so the stream ends part way through a frame rather than
        // on a boundary. That is a different code path from dropping a whole frame.
        byte[] truncated = ciphertext[..(ciphertext.Length - 5)];

        await ShouldRejectAsync(truncated, IntegrityFailure.TruncatedFrame).ConfigureAwait(true);
    }

    [Fact]
    public async Task Reorder_SwapTwoFrames_IsDetected()
    {
        byte[] ciphertext = await EncryptAsync(Pattern(200)).ConfigureAwait(true);

        (byte[] header, List<byte[]> frames) = Split(ciphertext);
        frames.Count.ShouldBeGreaterThan(2);

        (frames[0], frames[1]) = (frames[1], frames[0]);

        // The frame index is in the nonce and in the AAD, so a frame moved to another position is
        // decrypted against a nonce and an AAD it was never encrypted under.
        await ShouldRejectAsync(Concat(header, frames), IntegrityFailure.AuthenticationFailed).ConfigureAwait(true);
    }

    [Fact]
    public async Task Duplicate_RepeatAFrame_IsDetected()
    {
        byte[] ciphertext = await EncryptAsync(Pattern(200)).ConfigureAwait(true);

        (byte[] header, List<byte[]> frames) = Split(ciphertext);
        List<byte[]> withDuplicate = [frames[0], .. frames];

        await ShouldRejectAsync(Concat(header, withDuplicate), IntegrityFailure.AuthenticationFailed)
            .ConfigureAwait(true);
    }

    [Fact]
    public async Task AppendBytes_AfterFinalFrame_IsDetected()
    {
        // DECODER RULE 2. Appending bytes demotes the real final frame from last-on-the-wire, so it
        // is decrypted with isFinal = 0 and its tag fails - without the decoder having to trust any
        // length field, which is precisely the field an attacker would edit.
        byte[] ciphertext = await EncryptAsync(Pattern(200)).ConfigureAwait(true);
        byte[] extended = [.. ciphertext, .. new byte[] { 0, 0, 0, 8, 1, 2, 3, 4 }];

        await ShouldRejectAsync(extended, IntegrityFailure.AuthenticationFailed).ConfigureAwait(true);
    }

    // ─── AAD binding: the defence against an attacker with database write access ─────────────────

    [Fact]
    public async Task Decrypt_WithDifferentStatementId_Fails()
    {
        byte[] ciphertext = await EncryptAsync(Pattern(200)).ConfigureAwait(true);

        // The concrete attack: rewrite customer A's storage_key to point at customer B's object.
        // The fetch succeeds - the row says so - and the DECRYPTION fails.
        await ShouldRejectAsync(
            ciphertext,
            IntegrityFailure.AuthenticationFailed,
            Context with { StatementId = Guid.NewGuid() }).ConfigureAwait(true);
    }

    [Fact]
    public async Task Decrypt_WithDifferentCustomerId_Fails()
    {
        byte[] ciphertext = await EncryptAsync(Pattern(200)).ConfigureAwait(true);

        await ShouldRejectAsync(
            ciphertext,
            IntegrityFailure.AuthenticationFailed,
            Context with { CustomerId = Guid.NewGuid() }).ConfigureAwait(true);
    }

    [Fact]
    public async Task Decrypt_WithDifferentVersion_Fails()
    {
        byte[] ciphertext = await EncryptAsync(Pattern(200)).ConfigureAwait(true);

        // Version matters because a regenerated statement is a NEW object with the same statement id
        // and the same owner. Without it in the AAD, v1 and v2 would be interchangeable - and v1 is
        // exactly the document a customer disputes.
        await ShouldRejectAsync(
            ciphertext,
            IntegrityFailure.AuthenticationFailed,
            Context with { Version = 4 }).ConfigureAwait(true);
    }

    // ─── Nonce discipline ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Encrypt_TwoObjects_ProduceDifferentNoncePrefixes()
    {
        byte[] first = await EncryptAsync(Pattern(200)).ConfigureAwait(true);
        byte[] second = await EncryptAsync(Pattern(200)).ConfigureAwait(true);

        // Nonce reuse under one key is a total break of GCM, not a degradation: it leaks the XOR of
        // the plaintexts AND permits tag forgery. Uniqueness here comes from 7 fresh CSPRNG bytes per
        // object, never from a counter that some restore or redeploy could rewind.
        byte[] firstPrefix = first[25..32];
        byte[] secondPrefix = second[25..32];

        firstPrefix.ShouldNotBe(secondPrefix);
        first[9..25].ShouldNotBe(second[9..25]);
    }

    [Fact]
    public async Task Encrypt_SameInputTwice_ProducesDifferentCiphertext()
    {
        byte[] plaintext = Pattern(200);

        byte[] first = await EncryptAsync(plaintext).ConfigureAwait(true);
        byte[] second = await EncryptAsync(plaintext).ConfigureAwait(true);

        first.ShouldNotBe(second);

        // And both still decrypt to the same thing, which is what makes the difference randomness
        // rather than corruption.
        (await DecryptAsync(first).ConfigureAwait(true)).ShouldBe(plaintext);
        (await DecryptAsync(second).ConfigureAwait(true)).ShouldBe(plaintext);
    }

    [Fact]
    public async Task Decrypt_WithWrongKey_Fails()
    {
        byte[] ciphertext = await EncryptAsync(Pattern(200)).ConfigureAwait(true);
        byte[] wrongKey = [.. Key];
        wrongKey[0] ^= 0xFF;

        var cipher = new FramedAeadCipher(SmallFrame);
        using var source = new MemoryStream(ciphertext);
        using var sink = new MemoryStream();

        CiphertextIntegrityException error = await Should
            .ThrowAsync<CiphertextIntegrityException>(() =>
                cipher.DecryptAsync(source, sink, wrongKey, Context, TestContext.Current.CancellationToken))
            .ConfigureAwait(true);

        // The header tag catches it first, so the wrong key never reaches the body.
        error.Reason.ShouldBe(IntegrityFailure.HeaderAuthenticationFailed);
        sink.Length.ShouldBe(0);
    }

    [Fact]
    public async Task Decrypt_VerifiesTheRecordedPlaintextDigest()
    {
        byte[] plaintext = Pattern(200);
        byte[] ciphertext = await EncryptAsync(plaintext).ConfigureAwait(true);
        byte[] wrongDigest = SHA256.HashData([.. plaintext, 0x00]);

        using var source = new MemoryStream(ciphertext);
        await using var decrypting = new FramedDecryptingStream(source, Key, Context, wrongDigest);
        using var sink = new MemoryStream();

        CiphertextIntegrityException error = await Should
            .ThrowAsync<CiphertextIntegrityException>(() =>
                decrypting.CopyToAsync(sink, TestContext.Current.CancellationToken))
            .ConfigureAwait(true);

        error.Reason.ShouldBe(IntegrityFailure.ContentDigestMismatch);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static byte[] Pattern(int length)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 31) + 7);
        }

        return bytes;
    }

    private static async Task<byte[]> EncryptAsync(byte[] plaintext, int frameSize = SmallFrame)
    {
        var cipher = new FramedAeadCipher(frameSize);
        using var source = new MemoryStream(plaintext);
        using var sink = new MemoryStream();

        _ = await cipher.EncryptAsync(source, sink, Key, Context, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        return sink.ToArray();
    }

    private static async Task<byte[]> DecryptAsync(byte[] ciphertext, CryptoContext? context = null)
    {
        var cipher = new FramedAeadCipher(SmallFrame);
        using var source = new MemoryStream(ciphertext);
        using var sink = new MemoryStream();

        await cipher.DecryptAsync(source, sink, Key, context ?? Context, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        return sink.ToArray();
    }

    private static async Task<byte[]> AssertRoundTripAsync(byte[] plaintext, int frameSize)
    {
        byte[] ciphertext = await EncryptAsync(plaintext, frameSize).ConfigureAwait(true);

        ciphertext.Length.ShouldBe((int)FrameFormat.CiphertextLengthFor(plaintext.Length, frameSize));
        ciphertext[..4].ShouldBe("SDP1"u8.ToArray());
        ciphertext[4].ShouldBe(FrameFormat.FormatVersion);

        (await DecryptAsync(ciphertext).ConfigureAwait(true)).ShouldBe(plaintext);
        return ciphertext;
    }

    private static async Task ShouldRejectAsync(
        byte[] ciphertext,
        IntegrityFailure expected,
        CryptoContext? context = null)
    {
        var cipher = new FramedAeadCipher(SmallFrame);
        using var source = new MemoryStream(ciphertext);
        using var sink = new MemoryStream();

        CiphertextIntegrityException error = await Should
            .ThrowAsync<CiphertextIntegrityException>(() =>
                cipher.DecryptAsync(source, sink, Key, context ?? Context, TestContext.Current.CancellationToken))
            .ConfigureAwait(true);

        error.Reason.ShouldBe(expected);
    }

    /// <summary>Splits a ciphertext into its header and its frames, for surgical tampering.</summary>
    private static (byte[] Header, List<byte[]> Frames) Split(byte[] ciphertext)
    {
        byte[] header = ciphertext[..FrameFormat.HeaderLength];
        List<byte[]> frames = [];

        int offset = FrameFormat.HeaderLength;
        while (offset < ciphertext.Length)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(ciphertext.AsSpan(offset, 4));
            int total = FrameFormat.LengthPrefixLength + length + FrameFormat.TagLength;

            frames.Add(ciphertext[offset..(offset + total)]);
            offset += total;
        }

        return (header, frames);
    }

    private static byte[] Concat(byte[] header, IEnumerable<byte[]> frames)
    {
        using var buffer = new MemoryStream();
        buffer.Write(header);

        foreach (byte[] frame in frames)
        {
            buffer.Write(frame);
        }

        return buffer.ToArray();
    }

    private static async Task<byte[]> DigestOfAsync(Stream source)
    {
        await using (source.ConfigureAwait(false))
        {
            using var sha = SHA256.Create();
            return await sha.ComputeHashAsync(source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>A source that never returns a full buffer, so short reads are exercised.</summary>
    private sealed class DribbleStream(byte[] data, int bytesPerRead) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            int take = Math.Min(Math.Min(bytesPerRead, buffer.Length), data.Length - _position);
            data.AsSpan(_position, take).CopyTo(buffer);
            _position += take;
            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Generates deterministic bytes without ever holding them, so 200 MB costs nothing.</summary>
    internal sealed class PatternStream(long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            int take = (int)Math.Min(buffer.Length, length - _position);

            for (int i = 0; i < take; i++)
            {
                buffer[i] = (byte)(((_position + i) * 31) + 7);
            }

            _position += take;
            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Counts and digests what is written to it, holding nothing.</summary>
    internal sealed class HashingSinkStream : Stream
    {
        private readonly IncrementalHash _digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _written;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _written;

        public override long Position
        {
            get => _written;
            set => throw new NotSupportedException();
        }

        public byte[] Digest() => _digest.GetCurrentHash();

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            Write(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _digest.AppendData(buffer);
            _written += buffer.Length;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _digest.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Runs an operation and reports how much the managed heap grew across it.
    /// </summary>
    /// <remarks>
    /// A full blocking collection on both sides, so what is measured is retained memory rather than
    /// garbage awaiting collection. The array pool retains its rented buffers between the two
    /// measurements, which is exactly right: those buffers are the constant cost being asserted, and
    /// a few frame-sized arrays sit far below the ten-megabyte bound.
    /// </remarks>
    private static async Task<long> MeasureManagedHeapDeltaAsync(Func<Task> operation)
    {
        long before = GC.GetTotalMemory(forceFullCollection: true);

        await operation().ConfigureAwait(true);

        long after = GC.GetTotalMemory(forceFullCollection: true);
        return after - before;
    }
}
