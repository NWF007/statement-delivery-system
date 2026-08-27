using System.Buffers.Binary;
using StatementDelivery.Domain.Exceptions;

namespace StatementDelivery.Domain.Tokens;

/// <summary>
/// The SHA-256 of a token. This is what is persisted; the plaintext never is.
/// </summary>
/// <remarks>
/// <para>
/// WHY SHA-256 AND NOT BCRYPT, SCRYPT OR ARGON2 - because a reviewer will ask, and a future
/// contributor will otherwise "fix" it.
/// </para>
/// <para>
/// Password hashing algorithms are deliberately SLOW. That slowness exists to resist brute force
/// against LOW-ENTROPY secrets: a human-chosen password has perhaps 30 bits of real entropy, so an
/// attacker who steals the hashes can guess their way in unless each guess is made expensive.
/// </para>
/// <para>
/// A download token has 256 bits of CSPRNG entropy. There is no brute-force exposure to resist -
/// the search space is 2^256, and no amount of hardware makes a dent in it. Slow hashing would buy
/// nothing and would cost latency on EVERY redemption, on the customer-facing hot path. Fast
/// hashing is the correct choice for a high-entropy secret, and salting is pointless for the same
/// reason: there are no dictionaries of random 32-byte values to precompute.
/// </para>
/// <para>
/// Stored as four 64-bit words rather than a <c>byte[]</c>, so the type has free structural
/// equality, is comparable without allocating, and cannot be mutated after construction by a caller
/// holding the same array.
/// </para>
/// <para>
/// No constant-time comparison here, deliberately. The comparison that matters happens in
/// PostgreSQL's unique index, and learning a stored hash byte by byte would not help anyway: the
/// hash is not the credential, and inverting SHA-256 to recover the token is the problem this
/// scheme rests on being hard.
/// </para>
/// </remarks>
public readonly record struct TokenHash
{
    /// <summary>Length of a SHA-256 digest.</summary>
    public const int SizeInBytes = 32;

    private readonly ulong _word0;
    private readonly ulong _word1;
    private readonly ulong _word2;
    private readonly ulong _word3;

    private TokenHash(ulong word0, ulong word1, ulong word2, ulong word3)
    {
        _word0 = word0;
        _word1 = word1;
        _word2 = word2;
        _word3 = word3;
    }

    /// <summary>Wraps a 32-byte digest.</summary>
    /// <param name="bytes">The digest.</param>
    /// <returns>The hash.</returns>
    /// <exception cref="InvariantViolationException">The input is not 32 bytes.</exception>
    public static TokenHash FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != SizeInBytes)
        {
            throw new InvariantViolationException($"A token hash is {SizeInBytes} bytes, got {bytes.Length}.");
        }

        // Big-endian throughout, so the words do not depend on the machine's byte order and a hash
        // written on one architecture reads back identically on another.
        return new TokenHash(
            BinaryPrimitives.ReadUInt64BigEndian(bytes),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[16..]),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[24..]));
    }

    /// <summary>Writes the digest into a caller-provided buffer.</summary>
    /// <param name="destination">A 32-byte buffer.</param>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length != SizeInBytes)
        {
            throw new InvariantViolationException($"Destination must be {SizeInBytes} bytes, got {destination.Length}.");
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination, _word0);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], _word1);
        BinaryPrimitives.WriteUInt64BigEndian(destination[16..], _word2);
        BinaryPrimitives.WriteUInt64BigEndian(destination[24..], _word3);
    }

    /// <summary>
    /// Materialises the digest as an array, for a <c>bytea</c> parameter.
    /// </summary>
    /// <remarks>
    /// Allocates, which is fine: this is a HASH, not the secret. Putting the hash on the heap costs
    /// nothing, because possession of it grants nothing.
    /// </remarks>
    /// <returns>The 32-byte digest.</returns>
    public byte[] ToArray()
    {
        byte[] bytes = new byte[SizeInBytes];
        WriteTo(bytes);
        return bytes;
    }

    /// <summary>
    /// Renders the digest as lower-case hexadecimal.
    /// </summary>
    /// <remarks>
    /// SAFE TO LOG, unlike the plaintext. Recording which hash was presented is how a redemption is
    /// correlated with the token that was issued, and the hash reveals nothing usable.
    /// </remarks>
    /// <returns>64 hexadecimal characters.</returns>
    public override string ToString() => Convert.ToHexStringLower(ToArray());
}
