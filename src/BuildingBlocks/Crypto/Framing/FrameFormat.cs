using System.Buffers.Binary;
using System.Globalization;

namespace StatementDelivery.Crypto.Framing;

/// <summary>
/// The SDP1 framed-AEAD wire format: layout, nonce construction and AAD construction.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS FILE EXISTS AT ALL. .NET deliberately does not expose a streaming AEAD API.
/// <c>AesGcm</c> and <c>AesCcm</c> do not derive from <c>SymmetricAlgorithm</c>, expose no block
/// functionality and offer only one-shot <c>Encrypt</c>/<c>Decrypt</c> over spans. That was a
/// deliberate refusal, not an oversight: GCM is CTR plus GMAC, the tag covers the WHOLE message,
/// and a naive streaming decryptor emits plaintext as it goes and only learns the ciphertext was
/// forged at the very end - by which point it has already handed the caller unauthenticated bytes.
/// </para>
/// <para>
/// That leaves a direct conflict. The download path must stream in O(1) memory (a 200 MB statement
/// must not land on the large object heap). Envelope encryption must be authenticated. The only
/// AEAD primitive available is one-shot. Something has to give, and what gives is the assumption
/// that one message equals one AEAD operation.
/// </para>
/// <para>
/// THE RESOLUTION: split the plaintext into fixed-size frames, encrypt each frame independently,
/// and authenticate each frame before its bytes are released. This is not novel - it is the
/// construction behind Google Tink's <c>AesGcmHkdfStreaming</c> and the AWS Encryption SDK's framed
/// message format, whose body AAD carries the message id, a content-type string that differs for
/// regular versus final frames, the sequence number and the content length. That design is copied
/// here on purpose; the final-frame distinction in particular is what defends against truncation.
/// </para>
/// <para>
/// The format, in full:
/// </para>
/// <code>
/// HEADER (plaintext on the wire, authenticated by its own tag)
///   offset  size  field
///   0       4     magic          "SDP1" (ASCII)
///   4       1     version        0x01
///   5       4     frameSize      uint32 big-endian (default 65536)
///   9       16    messageId      CSPRNG, unique per object
///   25      7     noncePrefix    CSPRNG, unique per object
///   32      16    headerTag      GCM tag over empty plaintext, header[0..32] as AAD
///   = 48 bytes
///
/// BODY - repeated frames, for frame index i (0-based):
///   isFinal = 1 if this is the last frame, else 0
///   nonce (12) = noncePrefix(7) || frameIndex(4, BE) || isFinal(1)
///   aad        = messageId(16) || frameIndex(4, BE) || isFinal(1) || plaintextLength(4, BE)
///             || statementId(16) || customerId(16) || statementVersion(4, BE)
///   on-wire    = [plaintextLength: 4 BE][ciphertext: plaintextLength][tag: 16]
/// </code>
/// <para>
/// THREE PROPERTIES THIS BUYS, each of which is the reason a piece of the layout is shaped the way
/// it is:
/// </para>
/// <para>
/// 1. NONCE UNIQUENESS IS STRUCTURAL, NOT PROBABILISTIC. IV reuse under one key breaks GCM
/// catastrophically - not just confidentiality but authenticity, because the forgery key can be
/// recovered from two messages sharing a nonce. Here the 7-byte prefix is fresh CSPRNG output per
/// object and the 4-byte index is unique within an object, so two frames under one DEK cannot
/// collide by construction. THE PREFIX MUST NEVER BE DERIVED FROM A COUNTER: a counter that resets
/// - a redeployed pod, a restored snapshot, a reset sequence - reissues a prefix that was already
/// used, and every guarantee above evaporates silently.
/// </para>
/// <para>
/// 2. THE AAD BINDS THE CIPHERTEXT TO ITS IDENTITY. See <see cref="CryptoContext"/> for why that
/// matters against an attacker holding database write access.
/// </para>
/// <para>
/// 3. THE HEADER IS AUTHENTICATED. Encrypting an empty plaintext with <c>header[0..32]</c> as AAD
/// yields a tag over the frame size, the message id and the nonce prefix. Tampering with the frame
/// size to induce misparsing - the classic attack on a length-prefixed format - fails at the very
/// first operation, before a single body byte is touched.
/// </para>
/// </remarks>
public static class FrameFormat
{
    /// <summary>The four magic bytes that open every object: <c>SDP1</c>.</summary>
    public static ReadOnlySpan<byte> Magic => "SDP1"u8;

    /// <summary>Length of <see cref="Magic"/>.</summary>
    public const int MagicLength = 4;

    /// <summary>The only format version this code writes or accepts.</summary>
    public const byte FormatVersion = 0x01;

    /// <summary>Total header length, including the header tag.</summary>
    public const int HeaderLength = 48;

    /// <summary>The part of the header covered by the header tag as AAD: everything before the tag.</summary>
    public const int HeaderAuthenticatedLength = 32;

    /// <summary>GCM tag length. 16 bytes - the full tag. Truncated tags are not used.</summary>
    public const int TagLength = 16;

    /// <summary>GCM nonce length. 12 bytes, the only length that avoids GHASH-based nonce derivation.</summary>
    public const int NonceLength = 12;

    /// <summary>Bytes of per-object random that open every nonce.</summary>
    public const int NoncePrefixLength = 7;

    /// <summary>Bytes of per-object random identifying the message inside the AAD.</summary>
    public const int MessageIdLength = 16;

    /// <summary>Bytes of big-endian length that precede each frame's ciphertext on the wire.</summary>
    public const int LengthPrefixLength = 4;

    /// <summary>Required DEK length. AES-256 only; there is no 128-bit option to negotiate down to.</summary>
    public const int KeyLength = 32;

    /// <summary>Default frame size: 64 KiB.</summary>
    /// <remarks>
    /// Chosen as the balance between per-frame overhead (20 bytes of length prefix and tag, so
    /// 0.03% at this size) and the working-set cost of one frame buffer per concurrent transfer.
    /// </remarks>
    public const int DefaultFrameSize = 65536;

    /// <summary>Smallest accepted frame size.</summary>
    /// <remarks>
    /// Tests use small frames to reach multi-frame paths without multi-megabyte fixtures. A floor
    /// exists because a 1-byte frame size would make the 20 bytes of per-frame overhead exceed the
    /// payload twentyfold, and an attacker who could set it would have a memory-amplification
    /// primitive.
    /// </remarks>
    public const int MinFrameSize = 16;

    /// <summary>Largest accepted frame size: 4 MiB.</summary>
    /// <remarks>
    /// THIS IS AN ANTI-AMPLIFICATION BOUND, not a tuning knob. The decoder sizes a buffer from the
    /// header, and the header is read before its tag is verified - so an unbounded value here would
    /// let a corrupt or hostile object dictate an arbitrary allocation. The bound is checked BEFORE
    /// the buffer is rented; the tag check follows immediately after.
    /// </remarks>
    public const int MaxFrameSize = 4 * 1024 * 1024;

    /// <summary>Length of the additional authenticated data attached to every frame.</summary>
    public const int AadLength = MessageIdLength + 4 + 1 + 4 + 16 + 16 + 4;

    /// <summary>
    /// The largest usable frame index.
    /// </summary>
    /// <remarks>
    /// <c>0xFFFFFFFF</c> is reserved for the header's own nonce, so the body stops one short of it.
    /// At the default frame size this still permits a 256 TiB object; the ceiling exists to make the
    /// reservation airtight, not because anything is expected to approach it.
    /// </remarks>
    public const uint MaxFrameIndex = 0xFFFF_FFFE;

    /// <summary>Offset of the header tag within the header.</summary>
    public const int HeaderTagOffset = 32;

    private const int VersionOffset = 4;
    private const int FrameSizeOffset = 5;
    private const int MessageIdOffset = 9;
    private const int NoncePrefixOffset = 25;

    /// <summary>
    /// Writes the 32 authenticated header bytes. The caller appends the tag at
    /// <see cref="HeaderTagOffset"/>.
    /// </summary>
    /// <param name="header">A span of at least <see cref="HeaderLength"/> bytes.</param>
    /// <param name="frameSize">The frame size this object uses.</param>
    /// <param name="messageId">16 bytes of CSPRNG output, unique to this object.</param>
    /// <param name="noncePrefix">7 bytes of CSPRNG output, unique to this object.</param>
    public static void WriteHeader(
        Span<byte> header,
        int frameSize,
        ReadOnlySpan<byte> messageId,
        ReadOnlySpan<byte> noncePrefix)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(header.Length, HeaderLength);
        ArgumentOutOfRangeException.ThrowIfNotEqual(messageId.Length, MessageIdLength);
        ArgumentOutOfRangeException.ThrowIfNotEqual(noncePrefix.Length, NoncePrefixLength);
        ValidateFrameSize(frameSize);

        Magic.CopyTo(header[..MagicLength]);
        header[VersionOffset] = FormatVersion;
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(FrameSizeOffset, 4), (uint)frameSize);
        messageId.CopyTo(header.Slice(MessageIdOffset, MessageIdLength));
        noncePrefix.CopyTo(header.Slice(NoncePrefixOffset, NoncePrefixLength));
    }

    /// <summary>
    /// Reads and structurally validates the header. Does NOT verify the header tag - the caller does
    /// that, because only the caller holds the key.
    /// </summary>
    /// <param name="header">At least <see cref="HeaderLength"/> bytes as read from the wire.</param>
    /// <param name="frameSize">The declared frame size.</param>
    /// <param name="messageId">Where the message id is copied.</param>
    /// <param name="noncePrefix">Where the nonce prefix is copied.</param>
    /// <exception cref="CiphertextIntegrityException">The bytes are not an SDP1 header.</exception>
    public static void ReadHeader(
        ReadOnlySpan<byte> header,
        out int frameSize,
        Span<byte> messageId,
        Span<byte> noncePrefix)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(header.Length, HeaderLength);

        if (!header[..MagicLength].SequenceEqual(Magic) || header[VersionOffset] != FormatVersion)
        {
            throw new CiphertextIntegrityException(IntegrityFailure.NotThisFormat);
        }

        uint declared = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(FrameSizeOffset, 4));

        // BOUNDS BEFORE ALLOCATION, AND BEFORE THE TAG CHECK. The tag verification the caller runs
        // next will reject a tampered frame size - but only after this value has already been used
        // to size a buffer, so the bound has to come first. The ordering is not decorative.
        if (declared < MinFrameSize || declared > MaxFrameSize)
        {
            throw new CiphertextIntegrityException(IntegrityFailure.NotThisFormat);
        }

        frameSize = (int)declared;
        header.Slice(MessageIdOffset, MessageIdLength).CopyTo(messageId);
        header.Slice(NoncePrefixOffset, NoncePrefixLength).CopyTo(noncePrefix);
    }

    /// <summary>
    /// Builds the nonce for one body frame: <c>noncePrefix(7) || frameIndex(4 BE) || isFinal(1)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="isFinal"/> APPEARS IN THE NONCE AS WELL AS THE AAD, and the duplication is
    /// the whole defence. Because the last byte of the nonce differs, identical plaintext at an
    /// identical index encrypted as final and as non-final produces different keystream and a
    /// different tag. A regular frame therefore can never be reinterpreted as a final frame, and a
    /// final frame can never be reinterpreted as a regular one, even by an attacker who can rewrite
    /// the object freely. See <see cref="FramedDecryptingStream"/> for the two decoder rules this
    /// enables.
    /// </para>
    /// </remarks>
    /// <param name="nonce">A span of exactly <see cref="NonceLength"/> bytes.</param>
    /// <param name="noncePrefix">The object's 7-byte prefix.</param>
    /// <param name="frameIndex">The 0-based frame index.</param>
    /// <param name="isFinal">Whether this is the terminating frame.</param>
    public static void BuildFrameNonce(
        Span<byte> nonce,
        ReadOnlySpan<byte> noncePrefix,
        uint frameIndex,
        bool isFinal)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(nonce.Length, NonceLength);
        ArgumentOutOfRangeException.ThrowIfNotEqual(noncePrefix.Length, NoncePrefixLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(frameIndex, MaxFrameIndex);

        noncePrefix.CopyTo(nonce[..NoncePrefixLength]);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.Slice(NoncePrefixLength, 4), frameIndex);
        nonce[NonceLength - 1] = isFinal ? (byte)1 : (byte)0;
    }

    /// <summary>
    /// Builds the nonce used for the header's own tag.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>noncePrefix(7) || 0xFFFFFFFF || 0xFF</c>. It shares the object's prefix - it must, being
    /// under the same key - and is separated from every body nonce by its LAST BYTE: body frames end
    /// in 0x00 or 0x01 and nothing else, so a nonce ending in 0xFF cannot be produced by
    /// <see cref="BuildFrameNonce"/> for any index at all. The reserved index is belt and braces on
    /// top of that, and is why <see cref="MaxFrameIndex"/> stops one short of <c>uint.MaxValue</c>.
    /// </para>
    /// </remarks>
    /// <param name="nonce">A span of exactly <see cref="NonceLength"/> bytes.</param>
    /// <param name="noncePrefix">The object's 7-byte prefix.</param>
    public static void BuildHeaderNonce(Span<byte> nonce, ReadOnlySpan<byte> noncePrefix)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(nonce.Length, NonceLength);
        ArgumentOutOfRangeException.ThrowIfNotEqual(noncePrefix.Length, NoncePrefixLength);

        noncePrefix.CopyTo(nonce[..NoncePrefixLength]);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.Slice(NoncePrefixLength, 4), uint.MaxValue);
        nonce[NonceLength - 1] = 0xFF;
    }

    /// <summary>
    /// Builds one frame's additional authenticated data.
    /// </summary>
    /// <remarks>
    /// <c>messageId(16) || frameIndex(4 BE) || isFinal(1) || plaintextLength(4 BE) ||
    /// statementId(16) || customerId(16) || statementVersion(4 BE)</c>.
    /// The first four fields defend the STRUCTURE of the message - which object, which position,
    /// terminating or not, how long. The last three defend its IDENTITY. Neither set is sufficient
    /// alone: without the structure fields frames can be reordered or dropped, and without the
    /// identity fields a whole object can be substituted for another.
    /// </remarks>
    /// <param name="aad">A span of exactly <see cref="AadLength"/> bytes.</param>
    /// <param name="messageId">The object's 16-byte message id.</param>
    /// <param name="frameIndex">The 0-based frame index.</param>
    /// <param name="isFinal">Whether this is the terminating frame.</param>
    /// <param name="plaintextLength">This frame's plaintext length.</param>
    /// <param name="context">The statement identity being bound in.</param>
    public static void BuildAad(
        Span<byte> aad,
        ReadOnlySpan<byte> messageId,
        uint frameIndex,
        bool isFinal,
        int plaintextLength,
        in CryptoContext context)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(aad.Length, AadLength);
        ArgumentOutOfRangeException.ThrowIfNotEqual(messageId.Length, MessageIdLength);
        ArgumentOutOfRangeException.ThrowIfNegative(plaintextLength);

        messageId.CopyTo(aad[..MessageIdLength]);
        int offset = MessageIdLength;

        BinaryPrimitives.WriteUInt32BigEndian(aad.Slice(offset, 4), frameIndex);
        offset += 4;

        aad[offset] = isFinal ? (byte)1 : (byte)0;
        offset += 1;

        BinaryPrimitives.WriteUInt32BigEndian(aad.Slice(offset, 4), (uint)plaintextLength);
        offset += 4;

        // BIG-ENDIAN GUID BYTES, NOT Guid.ToByteArray(). The default layout is little-endian for the
        // first three fields on every platform, which is fine for round-tripping inside one process
        // and wrong the moment the value has to mean the same thing to another implementation - or
        // to a byte-for-byte comparison in a test written by hand. RFC 4122 order is the
        // interoperable one, and this AAD is a wire format.
        _ = context.StatementId.TryWriteBytes(aad.Slice(offset, 16), bigEndian: true, out _);
        offset += 16;

        _ = context.CustomerId.TryWriteBytes(aad.Slice(offset, 16), bigEndian: true, out _);
        offset += 16;

        BinaryPrimitives.WriteInt32BigEndian(aad.Slice(offset, 4), context.Version);
    }

    /// <summary>
    /// How many frames a plaintext of this length produces.
    /// </summary>
    /// <remarks>
    /// ALWAYS AT LEAST ONE, AND ALWAYS ONE MORE THAN THE FULL FRAMES WHEN THE LENGTH DIVIDES
    /// EVENLY. A length that is an exact multiple of the frame size has no partial tail, so without
    /// a deliberate empty final frame the object would end on a frame carrying <c>isFinal = 0</c>
    /// and the decoder's first rule would reject a perfectly legitimate object. Zero-length input is
    /// the same case (0 is a multiple of everything) and produces exactly one empty final frame.
    /// This is the classic off-by-one in framed formats and it has its own test.
    /// </remarks>
    /// <param name="plaintextLength">The plaintext length.</param>
    /// <param name="frameSize">The frame size.</param>
    /// <returns>The frame count, including the terminating frame.</returns>
    public static long FrameCountFor(long plaintextLength, int frameSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(plaintextLength);
        ValidateFrameSize(frameSize);

        return (plaintextLength / frameSize) + 1;
    }

    /// <summary>
    /// The exact on-the-wire length for a plaintext of this length.
    /// </summary>
    /// <remarks>
    /// Deterministic, which is what lets the S3 write path declare <c>Content-Length</c> before a
    /// byte has been encrypted, and lets the read path set <c>Content-Length</c> from the recorded
    /// plaintext size. Both matter: an HTTP client that knows the expected length sees a truncated
    /// transfer as a short read rather than as a complete response.
    /// </remarks>
    /// <param name="plaintextLength">The plaintext length.</param>
    /// <param name="frameSize">The frame size.</param>
    /// <returns>Header plus every frame's length prefix, ciphertext and tag.</returns>
    public static long CiphertextLengthFor(long plaintextLength, int frameSize) =>
        HeaderLength
        + (FrameCountFor(plaintextLength, frameSize) * (LengthPrefixLength + TagLength))
        + plaintextLength;

    /// <summary>Throws when a frame size is outside the accepted range.</summary>
    /// <param name="frameSize">The candidate frame size.</param>
    public static void ValidateFrameSize(int frameSize)
    {
        if (frameSize is < MinFrameSize or > MaxFrameSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameSize),
                frameSize,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Frame size must be between {MinFrameSize} and {MaxFrameSize} bytes."));
        }
    }
}
