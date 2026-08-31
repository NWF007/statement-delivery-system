using System.Security.Cryptography;
using Shouldly;
using StatementDelivery.Crypto.Framing;
using UnitTests.Storage;
using Xunit;

namespace UnitTests.Crypto;

/// <summary>
/// ADR-0032's rule applied to <c>IStreamingCipher</c>: the awkward input shape production
/// actually produces - a NON-SEEKABLE source - exercised explicitly.
/// </summary>
/// <remarks>
/// The existing cipher suite covers empty input, exact frame multiples, short reads and 200MB -
/// every SIZE shape - but every source was a seekable MemoryStream. The storage adapter's
/// CRITICAL taught the lesson: the cipher sits directly downstream of the same pipe, and its
/// non-seekable behaviour was an assumption, not a fact.
/// </remarks>
public sealed class CipherPortShapeTests
{
    [Fact]
    public async Task Encrypt_FromNonSeekableSource_RoundTrips()
    {
        byte[] plaintext = new byte[300 * 1024 + 17]; // deliberately not a frame multiple
        RandomNumberGenerator.Fill(plaintext.AsSpan(0, 4096));
        byte[] dek = RandomNumberGenerator.GetBytes(32);
        var ctx = new CryptoContext(Guid.NewGuid(), Guid.NewGuid(), 1);
        var cipher = new FramedAeadCipher();

        using var ciphertext = new MemoryStream();
        CipherResult result;

        using (var source = new NonSeekableReadStream(new MemoryStream(plaintext, writable: false)))
        {
            result = await cipher.EncryptAsync(
                source, ciphertext, dek, ctx, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        // The accounting must be exact even though the source could not be pre-measured.
        result.PlaintextLength.ShouldBe(plaintext.Length);
        result.PlaintextSha256.ShouldBe(SHA256.HashData(plaintext));
        ciphertext.Length.ShouldBe(result.CiphertextLength);

        // And the bytes decrypt - from a non-seekable CIPHERTEXT source too, which is the shape
        // a network stream presents on the read path.
        using var decrypted = new MemoryStream();
        ciphertext.Position = 0;
        using (var cipherSource = new NonSeekableReadStream(ciphertext))
        {
            await cipher.DecryptAsync(
                cipherSource, decrypted, dek, ctx, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        decrypted.ToArray().ShouldBe(plaintext);
    }
}
