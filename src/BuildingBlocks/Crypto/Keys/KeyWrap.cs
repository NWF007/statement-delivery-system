using System.Security.Cryptography;
using System.Text;

namespace StatementDelivery.Crypto.Keys;

/// <summary>
/// AES-256-GCM key wrapping: the envelope every stored key sits inside.
/// </summary>
/// <remarks>
/// <para>
/// USED AT TWO TIERS, AND THAT SYMMETRY IS INTENTIONAL. The cohort KEK wraps a customer CEK; the
/// customer CEK wraps a per-object DEK. Same envelope, same authentication, same failure mode when
/// the wrong key is offered - so there is one wrapping format to review rather than two.
/// </para>
/// <para>
/// LAYOUT: <c>version(1) || nonce(12) || ciphertext(n) || tag(16)</c>. For a 32-byte key that is 61
/// bytes, which is the arithmetic behind the DEK size check: a raw, unwrapped DEK would be exactly
/// 32 bytes, so anything shorter than about 40 in a <c>wrapped_dek</c> column is a plaintext key
/// that somebody has persisted, and a single SQL query finds it.
/// </para>
/// <para>
/// THE CONTEXT STRING IS THE AAD, and it is what turns a silent failure into a loud one. Unwrapping
/// a cohort-7 blob with the cohort-8 key fails HERE, at the tag check, rather than returning 32
/// bytes of nonsense that go on to produce a statement that will not open, months later, with
/// nothing in the logs to say why.
/// </para>
/// <para>
/// A fresh nonce per wrap, from the platform CSPRNG. Wrapping is rare - once per customer for a CEK,
/// once per object or cached batch for a DEK - so there is no counter and no reason for one.
/// </para>
/// </remarks>
public static class KeyWrap
{
    /// <summary>The wrapped-blob format version.</summary>
    public const byte Version = 0x01;

    /// <summary>The algorithm name recorded alongside a wrapped key.</summary>
    public const string AlgorithmName = "AES-256-GCM";

    private const int NonceLength = 12;
    private const int TagLength = 16;

    /// <summary>The overhead a wrap adds to the key length.</summary>
    public const int Overhead = 1 + NonceLength + TagLength;

    /// <summary>Wraps key material.</summary>
    /// <param name="wrappingKey">The key doing the wrapping. 32 bytes.</param>
    /// <param name="plaintext">The key being wrapped.</param>
    /// <param name="context">The AAD. Identifies what this blob may be unwrapped as.</param>
    /// <returns>The wrapped blob, safe to persist.</returns>
    public static byte[] Wrap(ReadOnlySpan<byte> wrappingKey, ReadOnlySpan<byte> plaintext, string context)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(wrappingKey.Length, 32);
        ArgumentOutOfRangeException.ThrowIfZero(plaintext.Length);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        byte[] blob = new byte[Overhead + plaintext.Length];
        blob[0] = Version;

        Span<byte> nonce = blob.AsSpan(1, NonceLength);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(wrappingKey, TagLength);
        aes.Encrypt(
            nonce,
            plaintext,
            blob.AsSpan(1 + NonceLength, plaintext.Length),
            blob.AsSpan(1 + NonceLength + plaintext.Length, TagLength),
            Encoding.UTF8.GetBytes(context));

        return blob;
    }

    /// <summary>Unwraps key material.</summary>
    /// <param name="wrappingKey">The key that wrapped it. 32 bytes.</param>
    /// <param name="blob">The wrapped bytes as stored.</param>
    /// <param name="context">The AAD used when wrapping. A mismatch throws.</param>
    /// <returns>The plaintext key. The caller owns it and must dispose it.</returns>
    /// <exception cref="CryptographicException">The blob is malformed, or the key or context is wrong.</exception>
    public static DataKey Unwrap(ReadOnlySpan<byte> wrappingKey, ReadOnlySpan<byte> blob, string context)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(wrappingKey.Length, 32);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        // BOTH BOUNDS, AND THE UPPER ONE IS NOT DECORATIVE.
        //
        // Without it, a blob longer than 93 bytes made `material[..keyLength]` slice past the
        // 64-byte stack buffer and throw ArgumentOutOfRangeException - which is NOT a
        // CryptographicException, so the storage adapter's translation did not catch it, and the
        // download answered with a 500 carrying a traceId instead of the uniform 404. That is an
        // oracle: it tells an attacker their edit reached the KEY layer rather than the data layer.
        //
        // Reachable by exactly the adversary this design names. The schema's only bound on
        // wrapped_dek is a 40-byte FLOOR (V013), so a database-write attacker can set a 200-byte
        // blob and the row is perfectly legal. This method also documents itself as throwing
        // CryptographicException for a malformed blob, so the old behaviour broke its own contract.
        const int MaxKeyLength = 64;

        if (blob.Length <= Overhead || blob.Length - Overhead > MaxKeyLength || blob[0] != Version)
        {
            throw new CryptographicException("Wrapped key blob is not in the expected format.");
        }

        int keyLength = blob.Length - Overhead;
        Span<byte> material = stackalloc byte[MaxKeyLength];
        material = material[..keyLength];

        try
        {
            using var aes = new AesGcm(wrappingKey, TagLength);
            aes.Decrypt(
                blob.Slice(1, NonceLength),
                blob.Slice(1 + NonceLength, keyLength),
                blob.Slice(1 + NonceLength + keyLength, TagLength),
                material,
                Encoding.UTF8.GetBytes(context));

            return DataKey.CopyFrom(material);
        }
        finally
        {
            // The stack slot held a live key for the length of this method. Wiping it means a frame
            // reused by the next call does not still carry it.
            CryptographicOperations.ZeroMemory(material);
        }
    }
}
