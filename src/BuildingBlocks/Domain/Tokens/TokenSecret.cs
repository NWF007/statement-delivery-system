using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using StatementDelivery.Domain.Exceptions;

namespace StatementDelivery.Domain.Tokens;

/// <summary>
/// Supplies cryptographically secure random bytes.
/// </summary>
/// <remarks>
/// A domain port so a test can inject a deterministic sequence and assert on an exact token. The
/// production implementation is <c>RandomNumberGenerator.Fill</c>; nothing else is acceptable.
/// <see cref="Random"/> is seeded from a predictable source and produces predictable output - a
/// token generated from it has nothing like 256 bits of unguessable entropy, however long it looks.
/// </remarks>
public interface IRandomBytes
{
    /// <summary>Fills the buffer with cryptographically secure random bytes.</summary>
    /// <param name="destination">The buffer to fill.</param>
    void Fill(Span<byte> destination);
}

/// <summary>The 32 raw bytes of a token, held inline.</summary>
[InlineArray(TokenSecret.SizeInBytes)]
internal struct TokenBuffer
{
    private byte _element0;
}

/// <summary>
/// The plaintext download token. 32 bytes of CSPRNG output.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS IS A <c>ref struct</c>, AND IT IS THE WHOLE POINT OF THE TYPE.
/// </para>
/// <para>
/// A <c>ref struct</c> cannot be boxed, cannot be stored in a field, cannot be captured by a
/// lambda, cannot be an <c>async</c> local across an <c>await</c>, cannot go into a collection, and
/// cannot be put on the heap. Every one of those is a way the plaintext could end up somewhere it
/// outlives the request - a captured closure, a cached object, a heap a memory dump would find it
/// in. The language makes each of them a COMPILE ERROR rather than a code-review question.
/// </para>
/// <para>
/// The bytes live in an <c>InlineArray</c> field rather than behind a <c>byte[]</c>, so they sit in
/// the struct itself - on the stack - instead of on the heap where a <c>byte[]</c> would linger
/// until collected, and could be recovered from a dump long after the request finished.
/// </para>
/// <para>
/// THE PLAINTEXT IS WRITTEN TO EXACTLY ONE PLACE: the HTTP response body of the issue request.
/// Never a database column, never a log line, never a span attribute, never an exception message,
/// never a metric label. <c>ToUrlSafeString</c> is the only way to get it out, and it exists solely
/// to build that one response.
/// </para>
/// </remarks>
public readonly ref struct TokenSecret
{
    /// <summary>
    /// 32 bytes - 256 bits.
    /// </summary>
    /// <remarks>
    /// The search space is 2^256. An attacker enumerating tokens at a billion attempts a second
    /// since the beginning of the universe would have covered an unmeasurable fraction of it. This
    /// is why the token can be a bearer credential in a URL, and why per-IP rate limiting rather
    /// than token complexity is the control that actually matters.
    /// </remarks>
    public const int SizeInBytes = 32;

    /// <summary>Length of the base64url encoding: 32 bytes with no padding.</summary>
    public const int EncodedLength = 43;

    private readonly TokenBuffer _bytes;

    private TokenSecret(TokenBuffer bytes) => _bytes = bytes;

    /// <summary>Gets the raw bytes.</summary>
    [UnscopedRef]
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>Generates a new token.</summary>
    /// <param name="randomBytes">The entropy source.</param>
    /// <returns>A new token.</returns>
    public static TokenSecret Generate(IRandomBytes randomBytes)
    {
        ArgumentNullException.ThrowIfNull(randomBytes);

        TokenBuffer buffer = default;
        randomBytes.Fill(buffer);
        return new TokenSecret(buffer);
    }

    /// <summary>
    /// Reconstructs a token from its URL form, for redemption.
    /// </summary>
    /// <remarks>
    /// Never throws on bad input. A token arrives from the open internet on an unauthenticated
    /// endpoint, so malformed input is the ordinary case and must produce the same generic denial
    /// as every other failure - not an exception, and not a 400 that would distinguish "malformed"
    /// from "wrong".
    /// </remarks>
    /// <param name="encoded">The base64url form from the URL.</param>
    /// <param name="buffer">Caller-provided storage; must be <see cref="SizeInBytes"/> long.</param>
    /// <param name="secret">The decoded token when the method returns true.</param>
    /// <returns><see langword="true"/> when the input decoded to exactly 32 bytes.</returns>
    public static bool TryParse(string? encoded, Span<byte> buffer, out TokenSecret secret)
    {
        secret = default;

        if (encoded is null
            || encoded.Length != EncodedLength
            || buffer.Length != SizeInBytes
            || !Base64Url.IsValid(encoded))
        {
            return false;
        }

        if (!Base64Url.TryDecodeFromChars(encoded, buffer, out int written) || written != SizeInBytes)
        {
            return false;
        }

        TokenBuffer bytes = default;
        buffer.CopyTo(bytes);
        secret = new TokenSecret(bytes);
        return true;
    }

    /// <summary>
    /// Encodes the token for the URL. THE ONLY WAY THE PLAINTEXT LEAVES THIS TYPE.
    /// </summary>
    /// <remarks>
    /// Base64url with no padding: 43 characters, safe in a path segment without escaping. An
    /// <c>=</c> would need percent-encoding, and a token that survives one round of encoding but
    /// not two is a support ticket waiting to happen.
    /// </remarks>
    /// <returns>The 43-character URL-safe encoding.</returns>
    public string ToUrlSafeString() => Base64Url.EncodeToString(Bytes);

    /// <summary>
    /// Computes the hash that is persisted.
    /// </summary>
    /// <remarks>
    /// SHA-256 OVER THE RAW BYTES, NOT OVER THE STRING. Hashing the encoded form would work, but it
    /// makes the stored value depend on an encoding choice - change the encoding and every existing
    /// token silently stops matching.
    /// </remarks>
    /// <returns>The hash.</returns>
    public TokenHash ComputeHash()
    {
        Span<byte> hash = stackalloc byte[SizeInBytes];
        _ = SHA256.HashData(Bytes, hash);
        return TokenHash.FromBytes(hash);
    }

    /// <summary>
    /// Refuses to render the plaintext.
    /// </summary>
    /// <remarks>
    /// The single most likely accident is an interpolated string in a log message. A ref struct
    /// cannot be boxed, so most such attempts do not compile - but string interpolation would call
    /// this. It returns a marker instead, so even the path the compiler allows leaks nothing.
    /// </remarks>
    /// <returns>A redaction marker, never the token.</returns>
    public override string ToString() => "TokenSecret([REDACTED])";

    // A ref struct cannot be boxed and cannot be a generic type argument, so Equals(object) is
    // unreachable and the type can never be a dictionary key. There is deliberately nothing to
    // override for those: the language has already made both mistakes impossible. ToString IS
    // reachable - string interpolation calls it - which is why it is overridden above to redact.
}
