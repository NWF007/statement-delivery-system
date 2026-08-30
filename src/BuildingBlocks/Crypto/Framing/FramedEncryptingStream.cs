using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace StatementDelivery.Crypto.Framing;

/// <summary>
/// A read-only stream that pulls plaintext from a source and yields SDP1 ciphertext.
/// </summary>
/// <remarks>
/// <para>
/// PULL, NOT PUSH, AND THAT IS THE WHOLE REASON IT EXISTS. <c>PutObject</c> wants a stream it can
/// read from; a push-shaped API would force the caller to buffer the whole ciphertext first, either
/// in memory (which forfeits the O(1) requirement and puts a 200 MB statement on the large object
/// heap) or on disk (which makes every write path depend on scratch space). Handing S3 a stream
/// that encrypts as it is read costs one frame buffer, whatever the object size.
/// </para>
/// <para>
/// Exactly two buffers are live for the lifetime of one transfer, both rented from the shared array
/// pool: one frame of plaintext and one frame of ciphertext plus its length prefix and tag. Nothing
/// else scales with object size.
/// </para>
/// <para>
/// The plaintext digest is accumulated as the bytes go past. See
/// <see cref="CipherResult.PlaintextSha256"/> for why a second pass was not an option.
/// </para>
/// </remarks>
public sealed class FramedEncryptingStream : Stream
{
    private readonly Stream _source;
    private readonly AesGcm _aes;
    private readonly int _frameSize;
    private readonly CryptoContext _context;
    private readonly byte[] _messageId = new byte[FrameFormat.MessageIdLength];
    private readonly byte[] _noncePrefix = new byte[FrameFormat.NoncePrefixLength];
    private readonly IncrementalHash _digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly bool _leaveSourceOpen;

    // Captured ONCE, at construction. Computing it on demand from the source would return a
    // different answer on every call as the source is consumed, and Stream.Length is a contract
    // that callers - the AWS SDK among them - are entitled to read more than once.
    private readonly long _declaredCiphertextLength = -1;

    private byte[] _plaintextBuffer;
    private byte[] _outputBuffer;
    private int _outputStart;
    private int _outputEnd;
    private uint _frameIndex;
    private bool _headerWritten;
    private bool _finalFrameWritten;
    private bool _disposed;
    private long _plaintextLength;
    private long _ciphertextLength;
    private long _delivered;
    private byte[]? _plaintextSha256;

    /// <summary>
    /// Initialises a new instance of the <see cref="FramedEncryptingStream"/> class.
    /// </summary>
    /// <param name="source">The plaintext source. Read forward-only, never seeked.</param>
    /// <param name="dek">The 32-byte data encryption key. Not retained beyond this stream.</param>
    /// <param name="context">The identity bound into every frame.</param>
    /// <param name="frameSize">The frame size. Defaults to 64 KiB.</param>
    /// <param name="leaveSourceOpen">Whether to leave <paramref name="source"/> open on dispose.</param>
    public FramedEncryptingStream(
        Stream source,
        ReadOnlySpan<byte> dek,
        CryptoContext context,
        int frameSize = FrameFormat.DefaultFrameSize,
        bool leaveSourceOpen = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNotEqual(dek.Length, FrameFormat.KeyLength);
        FrameFormat.ValidateFrameSize(frameSize);

        _source = source;
        _frameSize = frameSize;
        _context = context;
        _leaveSourceOpen = leaveSourceOpen;
        _aes = new AesGcm(dek, FrameFormat.TagLength);

        // FRESH CSPRNG OUTPUT PER OBJECT, NEVER A COUNTER. See FrameFormat: a nonce prefix derived
        // from anything that can reset - a restored snapshot, a redeployed pod, a rewound sequence -
        // reissues a prefix that has already been used under some key, and GCM nonce reuse is a
        // total break rather than a degradation.
        RandomNumberGenerator.Fill(_messageId);
        RandomNumberGenerator.Fill(_noncePrefix);

        _plaintextBuffer = ArrayPool<byte>.Shared.Rent(frameSize);
        _outputBuffer = ArrayPool<byte>.Shared.Rent(
            Math.Max(FrameFormat.HeaderLength, FrameFormat.LengthPrefixLength + frameSize + FrameFormat.TagLength));

        if (source.CanSeek)
        {
            _declaredCiphertextLength =
                FrameFormat.CiphertextLengthFor(source.Length - source.Position, frameSize);
        }
    }

    /// <summary>Gets the plaintext bytes consumed so far. Final once the stream has been read to the end.</summary>
    public long PlaintextLength => _plaintextLength;

    /// <summary>Gets the ciphertext bytes produced so far. Final once the stream has been read to the end.</summary>
    public long CiphertextLength => _ciphertextLength;

    /// <summary>
    /// Gets the SHA-256 of the plaintext, or null until the source has been read to the end.
    /// </summary>
    /// <remarks>
    /// Null before completion rather than a partial digest. A caller that reads this early is asking
    /// the wrong question, and returning the hash of a prefix would answer it plausibly and wrongly.
    /// </remarks>
    public byte[]? PlaintextSha256 => _plaintextSha256 is null ? null : [.. _plaintextSha256];

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <summary>
    /// Gets the exact ciphertext length, when the source declares its own length.
    /// </summary>
    /// <remarks>
    /// Deliberately overridden on a non-seekable stream. The value is computed, not measured -
    /// <see cref="FrameFormat.CiphertextLengthFor"/> is exact - and having it lets the S3 write path
    /// send a <c>Content-Length</c> instead of falling back to chunked transfer encoding, which
    /// several S3-compatible implementations handle differently from real S3.
    /// </remarks>
    public override long Length => _declaredCiphertextLength >= 0
        ? _declaredCiphertextLength
        : throw new NotSupportedException("The plaintext source does not declare a length.");

    /// <inheritdoc />
    public override long Position
    {
        get => _delivered;
        set => throw new NotSupportedException("A framed ciphertext stream is forward-only.");
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        while (_outputStart == _outputEnd)
        {
            // Blocking is safe here and only here: with synchronous: true every read inside is a
            // synchronous Stream.ReadAtLeast, so the task is already complete before it is observed.
            // The async overload above is the one production uses; this exists for Stream contract
            // completeness and for tests that read synchronously.
            if (!ProduceNext(synchronous: true, default).GetAwaiter().GetResult())
            {
                return 0;
            }
        }

        return Drain(buffer);
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_outputStart == _outputEnd)
        {
            if (!await ProduceNext(synchronous: false, cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }
        }

        return Drain(buffer.Span);
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override void Flush()
    {
        // Nothing is buffered on the write side; this stream is read-only.
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("A framed ciphertext stream is forward-only.");

    /// <inheritdoc />
    public override void SetLength(long value) =>
        throw new NotSupportedException("A framed ciphertext stream is read-only.");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("A framed ciphertext stream is read-only.");

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;

            _aes.Dispose();
            _digest.Dispose();

            // clearArray: the plaintext buffer held statement content and the output buffer held
            // ciphertext. Returning either to the pool without wiping it hands the next renter a
            // window onto somebody's bank statement.
            ArrayPool<byte>.Shared.Return(_plaintextBuffer, clearArray: true);
            ArrayPool<byte>.Shared.Return(_outputBuffer, clearArray: true);
            _plaintextBuffer = [];
            _outputBuffer = [];

            if (!_leaveSourceOpen)
            {
                _source.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private int Drain(Span<byte> destination)
    {
        int available = _outputEnd - _outputStart;
        int take = Math.Min(available, destination.Length);

        _outputBuffer.AsSpan(_outputStart, take).CopyTo(destination);
        _outputStart += take;
        _delivered += take;

        return take;
    }

    /// <summary>
    /// Fills the output buffer with the next unit of wire output: first the header, then one frame
    /// at a time. Returns false once the final frame has been emitted.
    /// </summary>
    private async Task<bool> ProduceNext(bool synchronous, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_headerWritten)
        {
            WriteHeaderFrame();
            return true;
        }

        if (_finalFrameWritten)
        {
            return false;
        }

        // ReadAtLeastAsync, NOT ReadAsync. A stream is free to return fewer bytes than asked for
        // without being at its end - a network stream almost always does - and treating a short read
        // as end-of-input would emit a final frame in the middle of the statement. This overload
        // returns less than requested ONLY at end of stream, which is exactly the signal needed.
        int read = synchronous
            ? _source.ReadAtLeast(_plaintextBuffer.AsSpan(0, _frameSize), _frameSize, throwOnEndOfStream: false)
            : await _source.ReadAtLeastAsync(
                _plaintextBuffer.AsMemory(0, _frameSize), _frameSize, throwOnEndOfStream: false, cancellationToken)
                .ConfigureAwait(false);

        // THE FINAL-EMPTY-FRAME RULE, and it falls out of the loop rather than being special-cased.
        // A short read means the source is exhausted, so this frame terminates the message. When the
        // plaintext length is an exact multiple of the frame size the preceding iteration consumed
        // the last full frame and this read returns 0 - producing an EMPTY final frame, which is
        // precisely what the format requires. Zero-length input takes the same path on the first
        // iteration. See FrameFormat.FrameCountFor.
        bool isFinal = read < _frameSize;

        WriteBodyFrame(read, isFinal);
        return true;
    }

    private void WriteHeaderFrame()
    {
        Span<byte> header = _outputBuffer.AsSpan(0, FrameFormat.HeaderLength);
        FrameFormat.WriteHeader(header, _frameSize, _messageId, _noncePrefix);

        Span<byte> nonce = stackalloc byte[FrameFormat.NonceLength];
        FrameFormat.BuildHeaderNonce(nonce, _noncePrefix);

        // A TAG OVER NOTHING, WITH THE HEADER AS AAD. There is no plaintext to protect here - the
        // header is public - so the encryption is empty-in, empty-out and the only output that
        // matters is the tag. That tag covers the frame size, the message id and the nonce prefix,
        // which is what makes a tampered frame size fail before any body byte is parsed.
        _aes.Encrypt(
            nonce,
            plaintext: [],
            ciphertext: [],
            header.Slice(FrameFormat.HeaderTagOffset, FrameFormat.TagLength),
            associatedData: header[..FrameFormat.HeaderAuthenticatedLength]);

        _headerWritten = true;
        _outputStart = 0;
        _outputEnd = FrameFormat.HeaderLength;
        _ciphertextLength += FrameFormat.HeaderLength;
    }

    private void WriteBodyFrame(int plaintextLength, bool isFinal)
    {
        if (_frameIndex > FrameFormat.MaxFrameIndex)
        {
            throw new InvalidOperationException(
                "Object exceeds the maximum frame count for the SDP1 format.");
        }

        ReadOnlySpan<byte> plaintext = _plaintextBuffer.AsSpan(0, plaintextLength);

        Span<byte> nonce = stackalloc byte[FrameFormat.NonceLength];
        FrameFormat.BuildFrameNonce(nonce, _noncePrefix, _frameIndex, isFinal);

        Span<byte> aad = stackalloc byte[FrameFormat.AadLength];
        FrameFormat.BuildAad(aad, _messageId, _frameIndex, isFinal, plaintextLength, _context);

        Span<byte> frame = _outputBuffer.AsSpan(
            0, FrameFormat.LengthPrefixLength + plaintextLength + FrameFormat.TagLength);

        BinaryPrimitives.WriteUInt32BigEndian(frame[..FrameFormat.LengthPrefixLength], (uint)plaintextLength);

        _aes.Encrypt(
            nonce,
            plaintext,
            frame.Slice(FrameFormat.LengthPrefixLength, plaintextLength),
            frame.Slice(FrameFormat.LengthPrefixLength + plaintextLength, FrameFormat.TagLength),
            aad);

        _digest.AppendData(plaintext);
        _plaintextLength += plaintextLength;
        _ciphertextLength += frame.Length;

        _outputStart = 0;
        _outputEnd = frame.Length;

        if (isFinal)
        {
            _finalFrameWritten = true;
            _plaintextSha256 = _digest.GetHashAndReset();
        }
        else
        {
            _frameIndex++;
        }
    }
}
