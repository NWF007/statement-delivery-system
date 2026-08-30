using System.Globalization;

namespace StatementDelivery.Crypto.Framing;

/// <summary>
/// The identity a ciphertext is cryptographically bound to.
/// </summary>
/// <remarks>
/// <para>
/// THIS IS A REQUIRED PARAMETER ON EVERY ENCRYPT AND DECRYPT CALL, AND THAT IS THE POINT. These
/// three values go into the additional authenticated data of every frame, which means the
/// ciphertext for statement A belonging to customer X cannot be decrypted as anything else - not
/// as a different statement, not for a different customer, not as a different version.
/// </para>
/// <para>
/// The threat this defends against is unusual and worth naming precisely: an attacker with WRITE
/// ACCESS TO THE DATABASE. Such an attacker can already point customer A's <c>storage_key</c> at
/// the object holding customer B's statement, and no amount of application-level authorisation
/// stops that, because the application faithfully reads the row it was given. With the identity in
/// the AAD, the fetch succeeds and the DECRYPTION FAILS - GCM rejects the mismatch. That is a
/// cryptographic defence against a database-level attack, which very little else in this system
/// can offer.
/// </para>
/// <para>
/// Making it a required constructor parameter rather than an optional property is deliberate: a
/// caller cannot forget to bind identity into the ciphertext, because there is no overload that
/// lets them.
/// </para>
/// </remarks>
/// <param name="StatementId">The statement these bytes are.</param>
/// <param name="CustomerId">The customer these bytes belong to.</param>
/// <param name="Version">The generation version. A regenerated statement is a different object.</param>
public readonly record struct CryptoContext(Guid StatementId, Guid CustomerId, int Version);

/// <summary>
/// What one encryption pass produced.
/// </summary>
/// <param name="PlaintextLength">Bytes read from the source.</param>
/// <param name="CiphertextLength">Bytes written to the sink, including the header and every tag.</param>
/// <param name="PlaintextSha256">
/// The digest of the plaintext, computed DURING encryption in the same pass.
/// </param>
/// <remarks>
/// <para>
/// <see cref="PlaintextSha256"/> is computed as the bytes stream past, not by a second read. A
/// second read would double the I/O on a 200 MB object and - worse - would be a read of a stream
/// that may not be seekable and may not return the same bytes twice.
/// </para>
/// <para>
/// It populates <c>statement.content_sha256</c> and is checked again on the way out, which is what
/// turns "the object storage returned some bytes" into "the object storage returned the bytes we
/// wrote".
/// </para>
/// </remarks>
public sealed record CipherResult(long PlaintextLength, long CiphertextLength, byte[] PlaintextSha256);

/// <summary>
/// Raised when a ciphertext fails to authenticate, is truncated, or is otherwise not what was
/// written.
/// </summary>
/// <remarks>
/// <para>
/// ONE EXCEPTION TYPE FOR EVERY FAILURE MODE, DELIBERATELY. A forged tag, a dropped final frame, a
/// reordered frame and a tampered header are all "this ciphertext is not trustworthy", and callers
/// must not be able to branch on which. The <see cref="Reason"/> is for the audit trail and the
/// operator; it never reaches a response body. See the gateway's uniform denial.
/// </para>
/// <para>
/// Deliberately NOT derived from <see cref="System.Security.Cryptography.CryptographicException"/>:
/// that type is thrown by <c>AesGcm</c> itself for argument problems as well as authentication
/// failures, and a catch block that cannot tell "your key is the wrong length" from "these bytes
/// were tampered with" will eventually treat one as the other.
/// </para>
/// </remarks>
public sealed class CiphertextIntegrityException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="CiphertextIntegrityException"/> class.</summary>
    public CiphertextIntegrityException()
        : this(IntegrityFailure.AuthenticationFailed)
    {
    }

    /// <summary>Initialises a new instance of the <see cref="CiphertextIntegrityException"/> class.</summary>
    /// <param name="message">The message.</param>
    public CiphertextIntegrityException(string message)
        : base(message) => Reason = IntegrityFailure.AuthenticationFailed;

    /// <summary>Initialises a new instance of the <see cref="CiphertextIntegrityException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public CiphertextIntegrityException(string message, Exception innerException)
        : base(message, innerException) => Reason = IntegrityFailure.AuthenticationFailed;

    /// <summary>Initialises a new instance of the <see cref="CiphertextIntegrityException"/> class.</summary>
    /// <param name="reason">Which check failed. Diagnostic only.</param>
    public CiphertextIntegrityException(IntegrityFailure reason)
        : base(string.Create(CultureInfo.InvariantCulture, $"Ciphertext failed integrity verification: {reason}."))
        => Reason = reason;

    /// <summary>Initialises a new instance of the <see cref="CiphertextIntegrityException"/> class.</summary>
    /// <param name="reason">Which check failed. Diagnostic only.</param>
    /// <param name="innerException">The cause.</param>
    public CiphertextIntegrityException(IntegrityFailure reason, Exception innerException)
        : base(
            string.Create(CultureInfo.InvariantCulture, $"Ciphertext failed integrity verification: {reason}."),
            innerException)
        => Reason = reason;

    /// <summary>Gets which check failed. For the audit trail and the operator, never for a caller.</summary>
    public IntegrityFailure Reason { get; }
}

/// <summary>Which integrity check rejected a ciphertext. Diagnostic detail for the audit trail.</summary>
public enum IntegrityFailure
{
    /// <summary>A GCM tag did not verify: the bytes, the tag, the nonce or the AAD were altered.</summary>
    AuthenticationFailed = 0,

    /// <summary>The magic bytes or version are not this format.</summary>
    NotThisFormat = 1,

    /// <summary>The header tag did not verify: frame size, message id or nonce prefix were altered.</summary>
    HeaderAuthenticationFailed = 2,

    /// <summary>The stream ended inside a frame.</summary>
    TruncatedFrame = 3,

    /// <summary>
    /// The stream ended without a frame that authenticated as final. THE TRUNCATION DEFENCE.
    /// </summary>
    MissingFinalFrame = 4,

    /// <summary>Bytes followed the final frame.</summary>
    TrailingBytesAfterFinalFrame = 5,

    /// <summary>A frame declared a plaintext length that the header's frame size forbids.</summary>
    InvalidFrameLength = 6,

    /// <summary>The plaintext digest did not match the digest recorded when it was written.</summary>
    ContentDigestMismatch = 7,
}
