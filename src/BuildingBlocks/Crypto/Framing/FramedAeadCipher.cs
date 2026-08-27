using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace StatementDelivery.Crypto.Framing;

/// <summary>
/// Authenticated encryption over streams.
/// </summary>
/// <remarks>
/// <para>
/// The port exists so that callers state WHAT they want - these bytes, encrypted, bound to this
/// identity - without any of them holding an opinion about frames, nonces or tags. Nothing outside
/// this namespace constructs a nonce.
/// </para>
/// <para>
/// <see cref="CryptoContext"/> is a required parameter on both methods rather than an optional
/// property or an ambient value. A caller cannot forget to bind identity into the ciphertext,
/// because there is no overload that lets them.
/// </para>
/// </remarks>
public interface IStreamingCipher
{
    /// <summary>Encrypts a stream, returning what the pass measured.</summary>
    /// <param name="plaintext">The source. Read forward-only.</param>
    /// <param name="ciphertext">The sink.</param>
    /// <param name="dek">The 32-byte data encryption key.</param>
    /// <param name="ctx">The identity to bind into every frame.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Lengths and the plaintext digest, computed in the same pass.</returns>
    Task<CipherResult> EncryptAsync(
        Stream plaintext,
        Stream ciphertext,
        ReadOnlyMemory<byte> dek,
        CryptoContext ctx,
        CancellationToken ct);

    /// <summary>Decrypts a stream, verifying every frame before releasing it.</summary>
    /// <param name="ciphertext">The source. Read forward-only.</param>
    /// <param name="plaintext">The sink.</param>
    /// <param name="dek">The 32-byte data encryption key.</param>
    /// <param name="ctx">The identity that must match what was bound at encryption time.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the final frame has been verified.</returns>
    /// <exception cref="CiphertextIntegrityException">
    /// The ciphertext was tampered with, truncated, reordered, or is not this statement.
    /// </exception>
    Task DecryptAsync(
        Stream ciphertext,
        Stream plaintext,
        ReadOnlyMemory<byte> dek,
        CryptoContext ctx,
        CancellationToken ct);
}

/// <summary>Configuration for the framed cipher.</summary>
public sealed class CipherOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Crypto";

    /// <summary>
    /// Gets or sets the frame size in bytes.
    /// </summary>
    /// <remarks>
    /// CHANGING THIS IS SAFE FOR NEW OBJECTS AND IRRELEVANT FOR OLD ONES: every object records its
    /// own frame size in its header and is decrypted with that value, not with this one. This
    /// setting only decides what the next write uses.
    /// </remarks>
    [Range(FrameFormat.MinFrameSize, FrameFormat.MaxFrameSize)]
    public int FrameSizeBytes { get; set; } = FrameFormat.DefaultFrameSize;
}

/// <summary>
/// The SDP1 framed AEAD, composed from <c>System.Security.Cryptography.AesGcm</c>.
/// </summary>
/// <remarks>
/// <para>
/// NO CIPHER IS IMPLEMENTED HERE. Every block of AES and every GHASH comes from the platform
/// primitive; what this type contributes is a FRAMING PROTOCOL around it - how the plaintext is
/// divided, how each frame gets a unique nonce, what identity is authenticated alongside it, and
/// how the end of a message is proven. That distinction matters: composing a framing layer over a
/// vetted primitive is ordinary engineering, and writing the primitive would not be.
/// </para>
/// <para>
/// Both methods are thin adapters over <see cref="FramedEncryptingStream"/> and
/// <see cref="FramedDecryptingStream"/>, so the format has exactly one implementation. The pull-
/// shaped streams are the real ones because object storage reads rather than accepts writes; these
/// push-shaped methods exist for callers that already hold both ends.
/// </para>
/// </remarks>
public sealed class FramedAeadCipher : IStreamingCipher
{
    private readonly int _frameSize;

    /// <summary>Initialises a new instance of the <see cref="FramedAeadCipher"/> class.</summary>
    /// <param name="options">Cipher options.</param>
    public FramedAeadCipher(IOptions<CipherOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        FrameFormat.ValidateFrameSize(options.Value.FrameSizeBytes);
        _frameSize = options.Value.FrameSizeBytes;
    }

    /// <summary>Initialises a new instance of the <see cref="FramedAeadCipher"/> class.</summary>
    /// <param name="frameSize">The frame size to use for new objects.</param>
    public FramedAeadCipher(int frameSize = FrameFormat.DefaultFrameSize)
    {
        FrameFormat.ValidateFrameSize(frameSize);
        _frameSize = frameSize;
    }

    /// <summary>Gets the frame size used for new objects.</summary>
    public int FrameSize => _frameSize;

    /// <inheritdoc />
    public async Task<CipherResult> EncryptAsync(
        Stream plaintext,
        Stream ciphertext,
        ReadOnlyMemory<byte> dek,
        CryptoContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(ciphertext);

        await using var encrypting = new FramedEncryptingStream(
            plaintext, dek.Span, ctx, _frameSize, leaveSourceOpen: true);

        await encrypting.CopyToAsync(ciphertext, _frameSize, ct).ConfigureAwait(false);

        // Non-null by construction: CopyToAsync ran to end of stream, which is exactly the point at
        // which the digest is finalised.
        byte[] digest = encrypting.PlaintextSha256
            ?? throw new InvalidOperationException("The encrypting stream completed without producing a digest.");

        return new CipherResult(encrypting.PlaintextLength, encrypting.CiphertextLength, digest);
    }

    /// <inheritdoc />
    public async Task DecryptAsync(
        Stream ciphertext,
        Stream plaintext,
        ReadOnlyMemory<byte> dek,
        CryptoContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(plaintext);

        await using var decrypting = new FramedDecryptingStream(
            ciphertext, dek.Span, ctx, expectedSha256: default, leaveSourceOpen: true);

        await decrypting.CopyToAsync(plaintext, FrameFormat.DefaultFrameSize, ct).ConfigureAwait(false);
    }
}
