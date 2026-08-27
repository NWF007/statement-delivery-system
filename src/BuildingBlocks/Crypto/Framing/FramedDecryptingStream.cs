using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace StatementDelivery.Crypto.Framing;

/// <summary>
/// A read-only stream that pulls SDP1 ciphertext and yields verified plaintext.
/// </summary>
/// <remarks>
/// <para>
/// EVERY BYTE THIS STREAM RETURNS HAS ALREADY BEEN AUTHENTICATED. A frame is decrypted, its tag
/// verified and its AAD checked before a single byte of it is copied into the caller's buffer -
/// <c>AesGcm.Decrypt</c> verifies before it writes, and throws without writing when it does not.
/// That is the property naive streaming GCM cannot offer.
/// </para>
/// <para>
/// TWO DECODER RULES CARRY THE TRUNCATION DEFENCE, and both are enforced here:
/// </para>
/// <para>
/// RULE 1 - IF THE STREAM ENDS WITHOUT A FRAME THAT AUTHENTICATES AS FINAL, THROW. Do not return
/// what was decrypted so far. Without this, an attacker who drops trailing frames leaves an object
/// in which every remaining frame still authenticates perfectly, and the recipient gets a
/// valid-looking, silently truncated statement. For a financial document that is a serious
/// integrity failure - a statement missing its last pages looks exactly like a statement.
/// </para>
/// <para>
/// RULE 2 - IF A FINAL FRAME IS FOLLOWED BY MORE BYTES, THROW.
/// </para>
/// <para>
/// HOW FINALITY IS DETERMINED, since it is not a field on the wire: the decoder reads one frame,
/// then attempts to read the NEXT frame's length prefix. End of stream means the frame just read
/// was the last one, so it is decrypted with <c>isFinal = 1</c>; anything else means it was not, so
/// it is decrypted with <c>isFinal = 0</c>. Because that bit is in both the nonce and the AAD, the
/// two interpretations produce different tags and only the one the encoder intended verifies.
/// </para>
/// <para>
/// Both attacks therefore reduce to the same failure. DROP THE FINAL FRAME and the preceding frame
/// becomes last on the wire, so the decoder tries it as final and the tag rejects it - rule 1.
/// APPEND BYTES AFTER THE FINAL FRAME and the real final frame is no longer last, so the decoder
/// tries it as regular and the tag rejects it - rule 2. Neither needs a length field to be trusted,
/// which matters because a length field is exactly what an attacker would edit.
/// </para>
/// <para>
/// THE HONEST LIMITATION, stated in ADR-0019 as well as here: plaintext is released one frame at a
/// time, so truncation is only detected when the stream ends. A client reading progressively will
/// already hold authentic-but-incomplete data by the time this stream throws. The mitigations are
/// that <c>Content-Length</c> is set from the recorded plaintext length, so an HTTP client sees a
/// short read and treats the response as failed, and that this decoder throws rather than returning
/// partial output. Full protection would mean buffering the entire object before releasing any of
/// it, which forfeits O(1) memory - the requirement this whole format exists to preserve.
/// </para>
/// </remarks>
public sealed class FramedDecryptingStream : Stream
{
    private readonly Stream _source;
    private readonly AesGcm _aes;
    private readonly CryptoContext _context;
    private readonly byte[] _messageId = new byte[FrameFormat.MessageIdLength];
    private readonly byte[] _noncePrefix = new byte[FrameFormat.NoncePrefixLength];
    private readonly byte[] _lengthPrefix = new byte[FrameFormat.LengthPrefixLength];
    private readonly byte[] _lookahead = new byte[FrameFormat.LengthPrefixLength];
    private readonly byte[]? _expectedSha256;
    private readonly IncrementalHash? _digest;
    private readonly bool _leaveSourceOpen;

    private byte[] _inputBuffer = [];
    private byte[] _outputBuffer = [];
    private int _frameSize;
    private int _outputStart;
    private int _outputEnd;
    private uint _frameIndex;
    private bool _headerRead;
    private bool _finalFrameConsumed;
    private bool _disposed;
    private long _delivered;

    /// <summary>
    /// Initialises a new instance of the <see cref="FramedDecryptingStream"/> class.
    /// </summary>
    /// <param name="source">The ciphertext source. Read forward-only, never seeked.</param>
    /// <param name="dek">The 32-byte data encryption key.</param>
    /// <param name="context">The identity that must match what was bound at encryption time.</param>
    /// <param name="expectedSha256">
    /// The plaintext digest recorded when the object was written, or null to skip the check. Supply
    /// it whenever it is known: it catches storage-level corruption that is authentic per frame -
    /// an object silently replaced by an older version of itself, for instance.
    /// </param>
    /// <param name="leaveSourceOpen">Whether to leave <paramref name="source"/> open on dispose.</param>
    public FramedDecryptingStream(
        Stream source,
        ReadOnlySpan<byte> dek,
        CryptoContext context,
        ReadOnlySpan<byte> expectedSha256 = default,
        bool leaveSourceOpen = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNotEqual(dek.Length, FrameFormat.KeyLength);

        _source = source;
        _context = context;
        _leaveSourceOpen = leaveSourceOpen;
        _aes = new AesGcm(dek, FrameFormat.TagLength);

        if (!expectedSha256.IsEmpty)
        {
            _expectedSha256 = expectedSha256.ToArray();
            _digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        }
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length =>
        throw new NotSupportedException("The plaintext length is recorded in the database, not in the ciphertext.");

    /// <inheritdoc />
    public override long Position
    {
        get => _delivered;
        set => throw new NotSupportedException("A framed plaintext stream is forward-only.");
    }

    /// <summary>
    /// Reads and verifies the header now, rather than on the first read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY THIS EXISTS: WHERE A FAILURE SURFACES DECIDES WHAT THE CLIENT SEES. Left to the first
    /// read, a bad header tag - wrong key, tampered frame size, an object that is not this format at
    /// all - is discovered INSIDE the response body, after the status line and headers have already
    /// gone out. The client then receives a 200 with a truncated body, which is precisely the
    /// "authentic-looking but wrong" outcome this whole format exists to prevent.
    /// </para>
    /// <para>
    /// Priming first moves that failure to before a single header is written, so the caller can
    /// answer with its ordinary denial instead. It costs one 48-byte read and one GCM verification.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the header has been authenticated.</returns>
    /// <exception cref="CiphertextIntegrityException">The header is not authentic.</exception>
    public async Task PrimeAsync(CancellationToken cancellationToken)
    {
        if (!_headerRead)
        {
            await ReadAndVerifyHeaderAsync(synchronous: false, cancellationToken).ConfigureAwait(false);
        }
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
        throw new NotSupportedException("A framed plaintext stream is forward-only.");

    /// <inheritdoc />
    public override void SetLength(long value) =>
        throw new NotSupportedException("A framed plaintext stream is read-only.");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("A framed plaintext stream is read-only.");

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;

            _aes.Dispose();
            _digest?.Dispose();

            if (_inputBuffer.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(_inputBuffer, clearArray: true);
                _inputBuffer = [];
            }

            if (_outputBuffer.Length > 0)
            {
                // clearArray, always. This buffer held decrypted statement content.
                ArrayPool<byte>.Shared.Return(_outputBuffer, clearArray: true);
                _outputBuffer = [];
            }

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

    private async Task<bool> ProduceNext(bool synchronous, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_headerRead)
        {
            await ReadAndVerifyHeaderAsync(synchronous, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (_finalFrameConsumed)
        {
            return false;
        }

        int declared = (int)BinaryPrimitives.ReadUInt32BigEndian(_lengthPrefix);

        // The AAD binds this length, so a tampered prefix fails authentication anyway - but only
        // after it has been used to size a read. Bounding it first keeps a corrupt object from
        // dictating a 4 GB read before the tag gets a chance to reject it.
        if (declared < 0 || declared > _frameSize)
        {
            throw new CiphertextIntegrityException(IntegrityFailure.InvalidFrameLength);
        }

        int onWire = declared + FrameFormat.TagLength;

        if (!await TryReadExactAsync(_inputBuffer.AsMemory(0, onWire), synchronous, cancellationToken)
            .ConfigureAwait(false))
        {
            throw new CiphertextIntegrityException(IntegrityFailure.TruncatedFrame);
        }

        // THE LOOKAHEAD. This single read is what decides finality, and therefore what makes rules 1
        // and 2 above enforceable without trusting any field an attacker could edit.
        bool hasNextFrame = await TryReadExactAsync(_lookahead.AsMemory(), synchronous, cancellationToken)
            .ConfigureAwait(false);

        bool isFinal = !hasNextFrame;

        DecryptFrame(declared, isFinal);

        if (isFinal)
        {
            _finalFrameConsumed = true;
            VerifyDigest();
        }
        else
        {
            if (_frameIndex >= FrameFormat.MaxFrameIndex)
            {
                throw new CiphertextIntegrityException(IntegrityFailure.InvalidFrameLength);
            }

            _frameIndex++;
            _lookahead.CopyTo(_lengthPrefix.AsSpan());
        }

        return true;
    }

    private void DecryptFrame(int plaintextLength, bool isFinal)
    {
        Span<byte> nonce = stackalloc byte[FrameFormat.NonceLength];
        FrameFormat.BuildFrameNonce(nonce, _noncePrefix, _frameIndex, isFinal);

        Span<byte> aad = stackalloc byte[FrameFormat.AadLength];
        FrameFormat.BuildAad(aad, _messageId, _frameIndex, isFinal, plaintextLength, _context);

        Span<byte> plaintext = _outputBuffer.AsSpan(0, plaintextLength);

        try
        {
            // VERIFY-THEN-WRITE. AesGcm.Decrypt checks the tag before it writes the destination and
            // clears the destination if the check fails, so `plaintext` never holds unauthenticated
            // bytes even transiently. This is the line that makes per-frame release safe.
            _aes.Decrypt(
                nonce,
                _inputBuffer.AsSpan(0, plaintextLength),
                _inputBuffer.AsSpan(plaintextLength, FrameFormat.TagLength),
                plaintext,
                aad);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            // Every tamper, reorder, duplicate and truncation converges here. The caller learns
            // only that the ciphertext is untrustworthy - never which check caught it.
            throw new CiphertextIntegrityException(IntegrityFailure.AuthenticationFailed, ex);
        }

        // CANONICAL FRAMING, CHECKED AFTER AUTHENTICATION AND BEFORE RELEASE. Every frame except the
        // terminating one is exactly frameSize, so a given plaintext has one and only one encoding.
        //
        // THE ORDER IS DELIBERATE AND IS NOT INTERCHANGEABLE WITH THE BOUNDS CHECK ABOVE. That one
        // is a RESOURCE check - it decides how many bytes to read - so it must run before the read.
        // This one is a FORMAT check, and running it before the tag verification would let a
        // structural rule pre-empt the cryptographic one: appending bytes after the final frame
        // demotes that frame to non-final, and its length is necessarily short, so a pre-emptive
        // length check would reject it as malformed and the isFinal-in-the-nonce defence would never
        // be exercised. Both reject, but only one of them proves the format works.
        if (!isFinal && plaintextLength != _frameSize)
        {
            throw new CiphertextIntegrityException(IntegrityFailure.InvalidFrameLength);
        }

        _digest?.AppendData(plaintext);

        _outputStart = 0;
        _outputEnd = plaintextLength;
    }

    private void VerifyDigest()
    {
        if (_digest is null || _expectedSha256 is null)
        {
            return;
        }

        Span<byte> actual = stackalloc byte[32];
        _ = _digest.GetHashAndReset(actual);

        // Fixed-time, because this compares a value an attacker may be able to influence against one
        // they want to learn. The timing leak is small and the cost of closing it is nil.
        if (!CryptographicOperations.FixedTimeEquals(actual, _expectedSha256))
        {
            throw new CiphertextIntegrityException(IntegrityFailure.ContentDigestMismatch);
        }
    }

    private async ValueTask ReadAndVerifyHeaderAsync(bool synchronous, CancellationToken cancellationToken)
    {
        byte[] header = new byte[FrameFormat.HeaderLength];

        if (!await TryReadExactAsync(header.AsMemory(), synchronous, cancellationToken).ConfigureAwait(false))
        {
            throw new CiphertextIntegrityException(IntegrityFailure.NotThisFormat);
        }

        FrameFormat.ReadHeader(header, out _frameSize, _messageId, _noncePrefix);

        Span<byte> nonce = stackalloc byte[FrameFormat.NonceLength];
        FrameFormat.BuildHeaderNonce(nonce, _noncePrefix);

        try
        {
            _aes.Decrypt(
                nonce,
                ciphertext: [],
                header.AsSpan(FrameFormat.HeaderTagOffset, FrameFormat.TagLength),
                plaintext: [],
                header.AsSpan(0, FrameFormat.HeaderAuthenticatedLength));
        }
        catch (AuthenticationTagMismatchException ex)
        {
            // A separate reason from a body failure because the operational meaning differs: a bad
            // header tag means the object metadata was altered or the wrong key was supplied, which
            // is a different page than a corrupt payload. The caller still sees one exception type.
            throw new CiphertextIntegrityException(IntegrityFailure.HeaderAuthenticationFailed, ex);
        }

        // Rented only now, from the frame size the header just declared - which has been range-
        // checked by ReadHeader AND authenticated by the tag above, in that order.
        _inputBuffer = ArrayPool<byte>.Shared.Rent(_frameSize + FrameFormat.TagLength);
        _outputBuffer = ArrayPool<byte>.Shared.Rent(_frameSize);

        // Prime the first length prefix. A header with no body at all cannot contain a final frame,
        // so rule 1 rejects it here rather than after a confusing partial read.
        if (!await TryReadExactAsync(_lengthPrefix.AsMemory(), synchronous, cancellationToken).ConfigureAwait(false))
        {
            throw new CiphertextIntegrityException(IntegrityFailure.MissingFinalFrame);
        }

        _headerRead = true;
    }

    /// <summary>
    /// Reads exactly the requested bytes. Returns false at a clean end of stream, and throws when
    /// the stream ends part way through - which is a truncated object, not an empty one.
    /// </summary>
    private async ValueTask<bool> TryReadExactAsync(
        Memory<byte> destination,
        bool synchronous,
        CancellationToken cancellationToken)
    {
        int read = synchronous
            ? _source.ReadAtLeast(destination.Span, destination.Length, throwOnEndOfStream: false)
            : await _source.ReadAtLeastAsync(destination, destination.Length, throwOnEndOfStream: false, cancellationToken)
                .ConfigureAwait(false);

        if (read == destination.Length)
        {
            return true;
        }

        if (read == 0)
        {
            return false;
        }

        throw new CiphertextIntegrityException(IntegrityFailure.TruncatedFrame);
    }
}
