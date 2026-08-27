using System.Security.Cryptography;
using StatementDelivery.Domain.Tokens;

namespace StatementDelivery.ServiceDefaults.Security;

/// <summary>
/// The production <see cref="IRandomBytes"/>: the operating system CSPRNG.
/// </summary>
/// <remarks>
/// <para>
/// The ONLY acceptable implementation outside a test. <see cref="RandomNumberGenerator"/> draws
/// from the platform's cryptographic entropy source; <see cref="Random"/> does not, and a token
/// built from it is predictable to anyone who can observe or guess the seed - however long the
/// token looks.
/// </para>
/// <para>
/// This class is the entire basis of the scheme. Every other control - short TTL, single use,
/// customer binding, hashing at rest, per-IP rate limiting - assumes the token is unguessable. If
/// this is ever "optimised" to something faster, none of them matter.
/// </para>
/// </remarks>
public sealed class CryptoRandomBytes : IRandomBytes
{
    /// <inheritdoc />
    public void Fill(Span<byte> destination) => RandomNumberGenerator.Fill(destination);
}
