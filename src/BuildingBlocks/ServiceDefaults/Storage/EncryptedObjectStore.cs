using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StatementDelivery.Crypto.Framing;
using StatementDelivery.Crypto.Keys;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Domain.Statements;
using StatementDelivery.Domain.ValueObjects;

namespace StatementDelivery.ServiceDefaults.Storage;

/// <summary>What one write produced, for the statement row.</summary>
/// <param name="Key">The object key.</param>
/// <param name="Tier">The storage tier.</param>
/// <param name="PlaintextLength">The customer-visible size. This is what <c>size_bytes</c> records.</param>
/// <param name="CiphertextLength">The stored size, including the header and every frame tag.</param>
/// <param name="Envelope">The crypto envelope for the row.</param>
/// <param name="RetainUntil">When the Object Lock retention expires.</param>
/// <remarks>
/// BOTH LENGTHS, BECAUSE THEY ANSWER DIFFERENT QUESTIONS. The plaintext length is what a client
/// receives and therefore what <c>Content-Length</c> must say. The ciphertext length is what the
/// storage bill is calculated from. Recording only one of them means guessing the other, and the
/// difference is not a constant - it is 48 bytes plus 20 per frame.
/// </remarks>
public sealed record StoredObject(
    string Key,
    string Tier,
    long PlaintextLength,
    long CiphertextLength,
    CryptoEnvelope Envelope,
    DateTimeOffset RetainUntil);

/// <summary>Writes statement content. The counterpart to <see cref="IStatementContentStore"/>.</summary>
/// <remarks>
/// A SEPARATE PORT FROM THE READER, deliberately. The download gateway takes a dependency on the
/// reader and must not be able to write; the generation worker takes both. Splitting them means that
/// is enforced by what each service can inject rather than by everyone remembering not to call the
/// wrong method.
/// </remarks>
public interface IStatementContentWriter
{
    /// <summary>Encrypts and stores statement content.</summary>
    /// <remarks>
    /// ⚠ THIS SIGNATURE CARRIES TWO MORE PARAMETERS THAN THE BRIEF SKETCHED, AND IT HAS TO.
    /// <see cref="CryptoContext"/> holds exactly what the AAD binds - statement, customer, version -
    /// and the key scheme the same brief specifies is
    /// <c>statements/{shard}/{accountId}/{period}-v{version}.enc</c>, which needs the ACCOUNT and the
    /// PERIOD as well. Neither belongs in <see cref="CryptoContext"/>: putting them there would widen
    /// the authenticated data to values that have nothing to do with identity, and every existing
    /// ciphertext would stop verifying. Deriving them from what is here would mean inventing them.
    /// So they are passed alongside.
    /// </remarks>
    /// <param name="plaintext">The rendered bytes. Read forward-only, never buffered whole.</param>
    /// <param name="ctx">The identity to bind into the ciphertext.</param>
    /// <param name="accountId">The owning account. Part of the object key, not of the AAD.</param>
    /// <param name="period">The statement period. Part of the object key and of the retention date.</param>
    /// <param name="kekId">The cohort KEK expected for this customer.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Everything the statement row needs.</returns>
    Task<StoredObject> WriteAsync(
        Stream plaintext,
        CryptoContext ctx,
        AccountId accountId,
        StatementPeriod period,
        string kekId,
        CancellationToken ct);
}

/// <summary>Where unknown-length uploads spool their CIPHERTEXT while the length is measured.</summary>
/// <remarks>
/// <para>
/// THE FILE ON DISK IS CIPHERTEXT, NEVER PLAINTEXT - that is the entire reason the spool lives
/// INSIDE this adapter, after encryption, rather than in the render pipeline before it. A
/// plaintext spool would put a customer's full statement in cleartext on the container
/// filesystem: outside the threat model, unrecoverable after a SIGKILL, visible to host
/// snapshots. The ciphertext file is unreadable without the DEK, which exists only in this
/// process's memory. See docs/adr/0031-spool-ciphertext-for-unknown-length-uploads.md.
/// </para>
/// <para>
/// MUST BE REAL DISK, NOT tmpfs. A memory-backed mount silently reintroduces the full-object
/// buffering this design exists to avoid - the compose file says so where the volume would go.
/// Peak usage per replica is RenderParallelism x the largest ciphertext in flight (ciphertext is
/// plaintext + ~0.1%): at 8-way parallelism and multi-hundred-transaction statements, plan for
/// tens of megabytes, not gigabytes.
/// </para>
/// </remarks>
public sealed class SpoolOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "ObjectStorage:Spool";

    /// <summary>Gets or sets the spool directory. Defaults to the system temp directory.</summary>
    /// <remarks>
    /// A startup readiness check writes and deletes a probe file here and FAILS READINESS if it
    /// cannot: a worker that cannot spool cannot render, and refusing traffic beats burning three
    /// attempts per item and quarantining the queue.
    /// </remarks>
    public string? Directory { get; set; }

    /// <summary>Resolves the effective directory.</summary>
    public string EffectiveDirectory =>
        string.IsNullOrWhiteSpace(Directory) ? Path.GetTempPath() : Directory;
}

/// <summary>How long written objects are retained.</summary>
/// <remarks>
/// Separate from <see cref="ObjectLockOptions"/> because the two answer different questions: that
/// one is HOW immutable, this one is FOR HOW LONG. Both feed the same irreversible
/// <c>PutObject</c>, and both belong in configuration rather than in a field initialiser.
/// </remarks>
public sealed class RetentionOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "ObjectStorage:Retention";

    /// <summary>Gets or sets the retention period in years. Seven by regulation.</summary>
    /// <remarks>
    /// SHORTENING THIS DOES NOT SHORTEN ANYTHING ALREADY WRITTEN. Each object carries the
    /// retain-until date in force when it was stored, and under COMPLIANCE that date cannot be
    /// moved. Changing this affects the next write and nothing else - which is the correct
    /// behaviour, and worth knowing before someone changes it expecting a bill to fall.
    /// </remarks>
    [Range(1, 100)]
    public int Years { get; set; } = 7;
}

/// <summary>Object Lock configuration.</summary>
public sealed class ObjectLockOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "ObjectStorage:Lock";

    /// <summary>
    /// Gets or sets the retention mode: <c>COMPLIANCE</c> or <c>GOVERNANCE</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ COMPLIANCE MODE IS GENUINELY IRREVERSIBLE. Not "hard to undo" - not undoable. No user, no
    /// role, not the account root, and not AWS support can shorten or remove a compliance retention
    /// before it expires. A bug that writes objects with a seven-year compliance lock has created
    /// seven years of storage bills that nothing can cancel.
    /// </para>
    /// <para>
    /// THE MITIGATION IS THE PROFILE, AND IT IS SET EXPLICITLY RATHER THAN DEFAULTED. Development
    /// uses GOVERNANCE, which a privileged caller can bypass, so a developer who writes ten thousand
    /// test objects can delete them. Staging and Production use COMPLIANCE, because a retention a
    /// privileged user can bypass is not a regulatory record - it is a convention with a strongly
    /// worded comment.
    /// </para>
    /// <para>
    /// Defaulting this either way would be wrong. Defaulting to COMPLIANCE puts the irreversible
    /// setting one forgotten config file away from a developer machine; defaulting to GOVERNANCE puts
    /// the unenforceable one one forgotten config file away from production. So it is required, and
    /// startup fails without it.
    /// </para>
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^(COMPLIANCE|GOVERNANCE)$")]
    public string Mode { get; set; } = string.Empty;

    /// <summary>Gets or sets whether readiness fails when the bucket has no Object Lock.</summary>
    /// <remarks>
    /// True everywhere it matters. A silently unlocked bucket is worse than a crash, because the
    /// system looks compliant and is not - and the discovery happens at an audit rather than at a
    /// deploy.
    /// </remarks>
    public bool RequireBucketLock { get; set; } = true;

    /// <summary>Gets the parsed retention mode for a PutObject request.</summary>
    public ObjectLockMode PutMode =>
        string.Equals(Mode, "COMPLIANCE", StringComparison.OrdinalIgnoreCase)
            ? ObjectLockMode.Compliance
            : ObjectLockMode.Governance;

    /// <summary>Gets the parsed retention mode for a retention record.</summary>
    public ObjectLockRetentionMode RetentionMode =>
        string.Equals(Mode, "COMPLIANCE", StringComparison.OrdinalIgnoreCase)
            ? ObjectLockRetentionMode.Compliance
            : ObjectLockRetentionMode.Governance;
}

/// <summary>
/// Statement content in S3-compatible object storage, encrypted with the SDP1 framed AEAD.
/// </summary>
/// <remarks>
/// <para>
/// THE ADAPTER THE PORT WAS DEFINED FOR. <see cref="IStatementContentStore"/> was written in Prompt
/// 3 with no key identifier, no IV, no tag and no decryption callback in its signature - and it did
/// not have to change to accommodate any of this. The download gateway asks for bytes at a location
/// and receives a stream; that the stream now decrypts on the way past is entirely this adapter's
/// business.
/// </para>
/// <para>
/// READ PATH: fetch the wrapped DEK from the row (it arrives in <see cref="CryptoEnvelope"/>),
/// unwrap it through the customer CEK, <c>GetObject</c>, wrap the response in a decrypting stream,
/// and hand that back. Nothing is buffered; the first frame is verified and released while the rest
/// is still on the wire.
/// </para>
/// <para>
/// WRITE PATH: take a data key from the broker, hand <c>PutObject</c> a stream that encrypts as it
/// is read, and compute the plaintext digest in the same pass. Then set the Object Lock retention.
/// </para>
/// </remarks>
public sealed class S3StatementContentStore : IStatementContentStore, IStatementContentWriter
{
    private readonly IAmazonS3 _s3;
    private readonly SpoolOptions _spool;
    private readonly IDataKeyBroker _keys;
    private readonly ObjectStorageOptions _storage;
    private readonly ObjectLockOptions _lock;
    private readonly CipherOptions _cipher;
    private readonly RetentionPolicy _retention;
    private readonly ILogger<S3StatementContentStore> _logger;

    /// <summary>Initialises a new instance of the <see cref="S3StatementContentStore"/> class.</summary>
    /// <param name="s3">The S3 client.</param>
    /// <param name="keys">The data key broker.</param>
    /// <param name="storage">Storage options.</param>
    /// <param name="objectLock">Object Lock options.</param>
    /// <param name="cipher">Cipher options.</param>
    /// <param name="retention">Retention options.</param>
    /// <param name="logger">Logger. A decryption failure is silent to the caller and must not be silent here.</param>
    /// <param name="spool">Spool configuration for unknown-length uploads.</param>
    public S3StatementContentStore(
        IAmazonS3 s3,
        IDataKeyBroker keys,
        IOptions<ObjectStorageOptions> storage,
        IOptions<ObjectLockOptions> objectLock,
        IOptions<CipherOptions> cipher,
        IOptions<RetentionOptions>? retention = null,
        ILogger<S3StatementContentStore>? logger = null,
        IOptions<SpoolOptions>? spool = null)
    {
        _spool = spool?.Value ?? new SpoolOptions();
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(objectLock);
        ArgumentNullException.ThrowIfNull(cipher);

        _s3 = s3;
        _keys = keys;
        _storage = storage.Value;
        _lock = objectLock.Value;
        _cipher = cipher.Value;

        // CONFIGURED, NOT HARD-WIRED. RetentionPolicy documents itself as configurable "because the
        // applicable period is a legal question that changes without the code changing" - and a
        // field initialiser pinned to RetentionPolicy.Default quietly contradicted that. The object
        // lock this feeds is irreversible, so the period has to be a deployment decision rather than
        // a recompile.
        _retention = retention is null
            ? RetentionPolicy.Default
            : new RetentionPolicy(retention.Value.Years);

        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<S3StatementContentStore>.Instance;
    }

    /// <inheritdoc />
    public async Task<StatementContent?> OpenReadAsync(StorageLocation location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);

        CryptoEnvelope envelope = location.Envelope
            ?? throw new InvalidOperationException(
                "This statement has no crypto envelope. The encrypting store cannot serve unencrypted content, "
                + "and guessing that it is plaintext would be exactly the wrong guess to make.");

        // THE DIGEST IS REQUIRED, NOT OPTIONAL, AND THAT CLOSES A FAIL-OPEN.
        //
        // FramedDecryptingStream only verifies a digest it is given; passing null quietly skips the
        // check. The threat model here names an attacker with database WRITE access (see
        // CryptoContext), and the column is nullable - so under the previous "?? default" the same
        // attacker who cannot forge a frame tag could simply NULL content_sha256 and disable the one
        // check that catches an object replaced by an older, perfectly authentic version of itself.
        // A control an attacker can turn off from the same place they mount the attack is not a
        // control. V015 backs this with a CHECK so the row cannot exist in the first place.
        byte[] expectedDigest = envelope.ContentSha256 is { Length: 32 } digest
            ? digest
            : throw new CiphertextIntegrityException(IntegrityFailure.ContentDigestMismatch);

        GetObjectResponse response;

        try
        {
            response = await _s3.GetObjectAsync(
                new GetObjectRequest { BucketName = _storage.BucketName, Key = location.Key },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Null rather than an exception, per the port. At this point in the flow the download
            // token has already been consumed, so a missing object is an operational fault to audit
            // and answer generically - not an exceptional condition to unwind through.
            return null;
        }

        // OWNERSHIP OF THE S3 RESPONSE IS THE THING TO GET RIGHT FROM HERE ON.
        //
        // It is disposed exactly once on every path: by the finally below if ANYTHING between the
        // fetch and the hand-off throws, and by the caller afterwards - FramedDecryptingStream
        // disposes the response stream, StatementContent disposes the decrypting stream, so one
        // `await using` in the gateway closes the whole chain.
        //
        // A flag rather than catching specific exceptions: a cancellation, an
        // ObjectDisposedException, anything at all between here and the return would otherwise leak
        // a pooled HTTP connection to S3 - once per attempt, and an attacker can attempt as often
        // as they like.
        bool ownershipTransferred = false;

        try
        {
            // The DEK is unwrapped through the customer CEK, which is itself unwrapped through the
            // cohort KEK in KMS. Three tiers, one call site.
            //
            // TRANSLATED HERE, INSIDE THE ADAPTER, AND THAT PLACEMENT IS THE POINT. A wrapped DEK
            // that fails to unwrap is a decryption failure exactly like a bad frame tag - same cause
            // (a rewritten row, a corrupt blob, the wrong customer key), same required response.
            // But it throws CryptographicException or ArgumentException from KeyWrap, which the
            // gateway single catch does not know about; left untranslated they reach the global
            // handler and become a 500 with a traceId - visibly different from the uniform 404, and
            // therefore an oracle telling an attacker their edit reached the KEY layer rather than
            // the data layer.
            //
            // Translating in the adapter also keeps the gateway free of Crypto.Keys types, which is
            // a rule CryptoBoundaryTests enforces.
            DataKey dek;

            try
            {
                dek = await _keys
                    .UnwrapDekAsync(new CustomerId(envelope.Binding.CustomerId), envelope.WrappedDek, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is CryptographicException or CryptoErasedException or ArgumentException)
            {
                // THREE FAMILIES, ONE RESPONSE, AND THE INNER EXCEPTION IS PRESERVED.
                //
                //   CryptographicException  - a bad tag or a malformed blob (covers
                //                             AuthenticationTagMismatchException, which derives from it)
                //   CryptoErasedException   - the customer key was destroyed. The system working,
                //                             not corruption - but indistinguishable from the
                //                             caller side and required to answer identically
                //   ArgumentException       - a blob whose length no longer fits the format
                //
                // All three collapse to ONE uniform denial on the wire, because telling them apart
                // is exactly the oracle this exists to deny. They must NOT collapse for the
                // OPERATOR: "this customer was erased", "these bytes were tampered with" and "this
                // deployment holds the wrong master secret" need three different responses at 3am,
                // and the logged inner exception is the only thing that separates them.
                // Materialised into a local: the log arguments must be cheap identifiers, or the
                // analyzer rightly points out they are evaluated whether or not the level is
                // enabled. Critical is always enabled, but the rule is the rule.
                string cause = ex.GetType().Name;
                EncryptedObjectStoreLog.DecryptionFailed(_logger, envelope.Binding.StatementId, cause, ex);

                throw new CiphertextIntegrityException(IntegrityFailure.AuthenticationFailed, ex);
            }

            // try/finally rather than `using`, and it is the same rule: the key is wiped on every
            // path out. `using` cannot express it here because the return inside the try is what
            // hands the stream to the caller.
            try
            {
                var context = new CryptoContext(
                    envelope.Binding.StatementId, envelope.Binding.CustomerId, envelope.Binding.Version);

                var plaintext = new FramedDecryptingStream(
                    response.ResponseStream,
                    dek.Span,
                    context,
                    expectedDigest);

                // From this line on the decrypting stream owns the response stream, so disposing it
                // is what closes the chain - the outer finally must not also dispose the response.
                ownershipTransferred = true;

                try
                {
                    // VERIFY THE HEADER BEFORE RETURNING, so a wrong key, a tampered header or an
                    // object that is not this format at all fails HERE - where the caller can still
                    // answer with its ordinary denial - rather than half way through a 200 response.
                    // See PrimeAsync.
                    await plaintext.PrimeAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (CiphertextIntegrityException ex)
                {
                    string reason = ex.Reason.ToString();
                    EncryptedObjectStoreLog.DecryptionFailed(_logger, envelope.Binding.StatementId, reason, ex);

                    await plaintext.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
                catch
                {
                    await plaintext.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                // Content-Length is the PLAINTEXT length from the row, never the object size. They
                // differ by the framing overhead, and a client told the ciphertext length would wait
                // forever for bytes that do not exist.
                return new StatementContent(plaintext, location.SizeBytes, "application/pdf");
            }
            finally
            {
                // The stream copied the material it needs into its own AesGcm instance, so this key
                // can be wiped immediately rather than held for a multi-megabyte transfer.
                dek.Dispose();
            }
        }
        finally
        {
            if (!ownershipTransferred)
            {
                response.Dispose();
            }
        }
    }

    /// <inheritdoc />
    public async Task<StoredObject> WriteAsync(
        Stream plaintext,
        CryptoContext ctx,
        AccountId accountId,
        StatementPeriod period,
        string kekId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(period);
        ArgumentException.ThrowIfNullOrWhiteSpace(kekId);

        var customer = new CustomerId(ctx.CustomerId);
        string expectedKek = CohortAssignment.KekIdFor(CohortAssignment.ForCustomer(customer));

        // A caller that computed the cohort differently is a caller about to write an object nothing
        // can decrypt. Cheap to check, and the alternative is finding out at the first download.
        if (!string.Equals(kekId, expectedKek, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"Customer {customer} belongs to cohort KEK {expectedKek}, not {kekId}."),
                nameof(kekId));
        }

        // No byte estimate: the budget settles with the REAL count after encryption, via
        // lease.RecordBytes below. The estimate-based predecessor is the reason this comment block
        // used to carry a "KNOWN AND ACCEPTED" warning about non-seekable sources zeroing the byte
        // budget - the Prompt 5 streaming renderer walked straight into it (audit HIGH 2), and the
        // fix was to remove the estimate rather than to improve it.
        using DataKeyLease lease = await _keys.AcquireAsync(customer, ct).ConfigureAwait(false);

        string key = StorageKeyScheme.KeyFor(
            new StatementId(ctx.StatementId), accountId, period, ctx.Version);

        // From the period END, not from today. A statement regenerated years late must not thereby
        // earn extra retention - the same rule the statement row already applies, applied to the
        // object so the two cannot drift apart.
        DateOnly retainUntilDate = _retention.RetainUntil(period);
        var retainUntil = new DateTimeOffset(retainUntilDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        // =========================================================================================
        //  ENCRYPT TO A CIPHERTEXT SPOOL, THEN PUT THE SPOOL. One path for every input shape.
        //
        //  WHY A SPOOL AT ALL: the AWS SDK refuses a body it cannot measure - a non-seekable
        //  stream with no Content-Length throws "Could not determine content length" client-side.
        //  The render pipeline's pipe is exactly that shape, and it broke here (the Prompt 5
        //  audit's CRITICAL, reproduced by ContentWriterSeamTests before this fix).
        //
        //  WHY THE SPOOL IS CIPHERTEXT, NOT PLAINTEXT: spooling upstream in the pipeline would
        //  put a customer's full statement in cleartext on the container filesystem - outside the
        //  threat model, surviving SIGKILL, visible to host snapshots. This file is framed AEAD
        //  output, unreadable without a DEK that exists only in this process's memory. On Linux
        //  the file is additionally unlinked the moment it is open (see CreateSpool), so even a
        //  SIGKILL leaves no named file behind. See ADR-0031.
        //
        //  WHY ONE PATH FOR SEEKABLE INPUTS TOO: a seekable-input fast path would keep the
        //  spooled branch exercised only by the callers that need it - which is precisely how the
        //  original defect survived 364 green tests. One branch means the tested path IS the
        //  production path, for every caller.
        //
        //  The pull-stream (Span-based) rather than the cipher's Memory-based push API, so the
        //  DEK stays in the lease's pinned, wiped copy and never lands on the ordinary heap.
        // =========================================================================================
        long plaintextLength;
        long ciphertextLength;
        byte[] digest;
        FileStream spool = CreateSpool(_spool.EffectiveDirectory);

        await using (spool.ConfigureAwait(false))
        {
            // The Span-taking pull stream, so the DEK never leaves the lease's pinned copy - the
            // cipher's Memory-taking push API would force an unwiped heap copy of key material.
            await using (var encrypting = new FramedEncryptingStream(
                plaintext, lease.Key.Span, ctx, _cipher.FrameSizeBytes, leaveSourceOpen: true))
            {
                await encrypting.CopyToAsync(spool, _cipher.FrameSizeBytes, ct).ConfigureAwait(false);

                digest = encrypting.PlaintextSha256
                    ?? throw new InvalidOperationException("Encryption completed without producing a digest.");
                plaintextLength = encrypting.PlaintextLength;
                ciphertextLength = encrypting.CiphertextLength;
            }

            // Settled HERE, not after the PUT: the key protected these bytes the moment they were
            // encrypted, whether or not the upload lands. PLAINTEXT bytes - the budget bounds
            // material protected, and framing overhead is not material.
            lease.RecordBytes(plaintextLength);

            spool.Position = 0;

            var request = new PutObjectRequest
            {
                BucketName = _storage.BucketName,
                Key = key,
                InputStream = spool,
                AutoCloseStream = false,
                ContentType = "application/octet-stream",

                // ⚠ LOCK EXPIRY IS NOT DELETION. When this date passes the object merely becomes
                // ELIGIBLE for deletion - nothing removes it, and the storage bill continues for as
                // long as it exists. An explicit purge job is still required (Prompt 6). A system
                // that set a retention and assumed expiry meant cleanup would pay to store
                // 2.5 billion objects forever and would believe it had a retention policy.
                ObjectLockMode = _lock.PutMode,
                ObjectLockRetainUntilDate = retainUntil.UtcDateTime,
            };

            // Always declared, from the spool's REAL length - never inferred, never left to
            // chunked transfer encoding, which S3-compatible implementations handle inconsistently.
            request.Headers.ContentLength = ciphertextLength;

            _ = await _s3.PutObjectAsync(request, ct).ConfigureAwait(false);
        }

        return new StoredObject(
            key,
            "STANDARD",
            plaintextLength,
            ciphertextLength,
            new CryptoEnvelope(
                lease.WrappedDek,
                kekId,
                lease.Algorithm,
                digest,
                new ContentBinding(ctx.StatementId, ctx.CustomerId, ctx.Version)),
            retainUntil);
    }

    /// <summary>
    /// Opens the ciphertext spool file: delete-on-close, async, and on Linux already unlinked.
    /// </summary>
    /// <remarks>
    /// <c>DeleteOnClose</c> covers every path that disposes the stream - success, exception,
    /// cancellation. What it does not cover is a process that never disposes anything: SIGKILL, an
    /// OOM kill, a vanished node - and the generation fleet is explicitly designed to tolerate
    /// worker death. On Linux the file is therefore DELETED IMMEDIATELY AFTER OPENING: the handle
    /// stays valid, the directory entry is gone, and the kernel reclaims the blocks the instant
    /// the process dies, however it dies. Windows cannot unlink an open file without
    /// <c>FileShare.Delete</c> semantics the rest of the flags fight with, so the dev host keeps
    /// the named file and relies on DeleteOnClose - acceptable, because production is Linux.
    /// </remarks>
    private static FileStream CreateSpool(string directory)
    {
        // A configured-but-absent directory (fresh node, wiped temp volume) is created on
        // demand; a directory that CANNOT be created still throws, and the caller fails the
        // item rather than spooling somewhere unconfigured.
        _ = Directory.CreateDirectory(directory);

        string path = Path.Combine(
            directory,
            string.Create(CultureInfo.InvariantCulture, $"sdp-spool-{Guid.NewGuid():N}.enc"));

        var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Delete,
            bufferSize: 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);

        if (OperatingSystem.IsLinux())
        {
            File.Delete(path);
        }

        return stream;
    }


}

/// <summary>
/// Readiness check proving the bucket is actually configured for immutable retention.
/// </summary>
/// <remarks>
/// <para>
/// THREE CALLS, AND EACH CATCHES A DIFFERENT MIS-PROVISIONING at deploy time rather than at an
/// audit: the bucket is reachable, Object Lock is enabled, and versioning is enabled.
/// </para>
/// <para>
/// OBJECT LOCK CANNOT BE TURNED ON AFTERWARDS. Both S3 and the MinIO community build require it at
/// bucket-creation time - <c>mc mb --with-lock</c> - so a bucket created without it must be
/// DESTROYED AND RECREATED. Locally that is free. In production it is a migration of every object
/// the bucket holds, which is why this check exists at all: the cost of finding out late is
/// enormous, and the cost of finding out at startup is one failed deploy.
/// </para>
/// <para>
/// FAILING READINESS IS THE POINT. A service that starts against an unlocked bucket writes objects
/// that look retained and are not, and every one of them is a compliance finding waiting to happen.
/// </para>
/// </remarks>
public sealed class ObjectLockHealthCheck : IHealthCheck
{
    /// <summary>The registered name of this check.</summary>
    public const string Name = "object-lock";

    private readonly IAmazonS3 _s3;
    private readonly ObjectStorageOptions _storage;
    private readonly ObjectLockOptions _lock;

    /// <summary>Initialises a new instance of the <see cref="ObjectLockHealthCheck"/> class.</summary>
    /// <param name="s3">The S3 client.</param>
    /// <param name="storage">Storage options.</param>
    /// <param name="objectLock">Object Lock options.</param>
    public ObjectLockHealthCheck(
        IAmazonS3 s3,
        IOptions<ObjectStorageOptions> storage,
        IOptions<ObjectLockOptions> objectLock)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(objectLock);

        _s3 = s3;
        _storage = storage.Value;
        _lock = objectLock.Value;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_lock.RequireBucketLock)
        {
            return HealthCheckResult.Healthy("Object Lock verification is disabled by configuration.");
        }

        try
        {
            // 1. Reachable, and visible to THIS identity. A bucket that exists but is invisible to
            //    the role this process runs as fails here rather than on the first write.
            _ = await _s3.GetBucketLocationAsync(
                new GetBucketLocationRequest { BucketName = _storage.BucketName }, cancellationToken)
                .ConfigureAwait(false);

            // 2. Object Lock enabled.
            GetObjectLockConfigurationResponse lockConfiguration = await _s3.GetObjectLockConfigurationAsync(
                new GetObjectLockConfigurationRequest { BucketName = _storage.BucketName }, cancellationToken)
                .ConfigureAwait(false);

            if (lockConfiguration.ObjectLockConfiguration?.ObjectLockEnabled != ObjectLockEnabled.Enabled)
            {
                return HealthCheckResult.Unhealthy(
                    $"Bucket '{_storage.BucketName}' does not have Object Lock enabled. It cannot be enabled "
                    + "on an existing bucket: recreate it with `mc mb --with-lock` (or the S3 equivalent).");
            }

            // 3. Versioning enabled. Object Lock implies it, so a bucket reporting lock without
            //    versioning is in a state that should be impossible - which is exactly the kind of
            //    state worth failing on rather than reasoning about at 3am.
            GetBucketVersioningResponse versioning = await _s3.GetBucketVersioningAsync(
                new GetBucketVersioningRequest { BucketName = _storage.BucketName }, cancellationToken)
                .ConfigureAwait(false);

            return versioning.VersioningConfig?.Status == VersionStatus.Enabled
                ? HealthCheckResult.Healthy("Bucket has Object Lock and versioning enabled.")
                : HealthCheckResult.Unhealthy(
                    $"Bucket '{_storage.BucketName}' reports Object Lock but not versioning.");
        }
        catch (AmazonServiceException ex)
        {
            return HealthCheckResult.Unhealthy(
                $"Could not verify Object Lock on bucket '{_storage.BucketName}'.", ex);
        }
    }
}

/// <summary>
/// Readiness: the ciphertext spool directory must be writable, or this worker cannot render.
/// </summary>
/// <remarks>
/// Every write spools through <see cref="SpoolOptions.EffectiveDirectory"/> before its PUT. A
/// worker that cannot write there fails EVERY item it claims - three attempts each - and
/// quarantines the queue while reporting itself healthy. Failing readiness instead means it
/// refuses traffic and a human reads the reason. The probe is a real write-and-delete, not a
/// permissions guess: on the chiseled runtime image the only proof a directory works is using
/// it.
/// </remarks>
public sealed class SpoolDirectoryHealthCheck : IHealthCheck
{
    /// <summary>The registered check name.</summary>
    public const string Name = "spool-directory";

    private readonly SpoolOptions _spool;

    /// <summary>Initialises a new instance of the <see cref="SpoolDirectoryHealthCheck"/> class.</summary>
    /// <param name="spool">Spool configuration.</param>
    public SpoolDirectoryHealthCheck(IOptions<SpoolOptions> spool)
    {
        ArgumentNullException.ThrowIfNull(spool);
        _spool = spool.Value;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        string directory = _spool.EffectiveDirectory;
        string probe = Path.Combine(
            directory,
            string.Create(CultureInfo.InvariantCulture, $"sdp-spool-probe-{Guid.NewGuid():N}"));

        try
        {
            // Creating the directory is part of the probe: a missing-but-creatable path is
            // writable (the guarantee this check exists for); a read-only filesystem fails here.
            _ = Directory.CreateDirectory(directory);

            FileStream stream = new(
                probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 16, FileOptions.DeleteOnClose | FileOptions.Asynchronous);

            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(new ReadOnlyMemory<byte>([1]), cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            return HealthCheckResult.Healthy(
                string.Create(CultureInfo.InvariantCulture, $"spool directory writable: {directory}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return HealthCheckResult.Unhealthy(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"spool directory not writable: {directory}. A worker that cannot spool cannot render, and refusing traffic beats quarantining the queue."),
                ex);
        }
    }
}

/// <summary>Registers the encrypting object store.</summary>
public static class EncryptedObjectStoreExtensions
{
    /// <summary>
    /// Replaces the filesystem content store with the encrypting S3 adapter.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="includeWriter">Whether this service may write. False for the download gateway.</param>
    /// <returns>The builder, for chaining.</returns>
    public static IHostApplicationBuilder AddEncryptedContentStore(
        this IHostApplicationBuilder builder,
        bool includeWriter = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddOptions<ObjectLockOptions>()
            .Bind(builder.Configuration.GetSection(ObjectLockOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // GOVERNANCE IS REFUSED OUTSIDE DEVELOPMENT, and refused here rather than left to a
        // deployment checklist.
        //
        // Requiring the setting to be explicit stops it being wrong by DEFAULT. It does nothing
        // about it being wrong on PURPOSE - a copied config, an environment promoted without its
        // overrides - and the failure is silent and permanent in the worst direction: the service
        // starts green and writes seven years of bypassable retention onto records ADR-0022 calls a
        // regulatory record. Nothing downstream notices, because a GOVERNANCE lock looks exactly
        // like a COMPLIANCE one until the day somebody deletes an object.
        //
        // This repository already answers this exact question twice - the Local key provider throws
        // outside Development (CryptoServiceCollectionExtensions), and so does a development JWT
        // signing key (JwtOptions). A retention mode the brief itself calls irreversible deserves
        // the same treatment, and having the rule in two places and not the third is how it gets
        // forgotten in the third.
        string? mode = builder.Configuration.GetSection(ObjectLockOptions.SectionName)["Mode"];

        if (!builder.Environment.IsDevelopment()
            && !string.Equals(mode, "COMPLIANCE", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"ObjectStorage:Lock:Mode is '{mode}' in environment '{builder.Environment.EnvironmentName}'. Outside Development it must be COMPLIANCE: a retention a privileged caller can bypass is not a regulatory record. See docs/adr/0022-object-lock-compliance-mode.md."));
        }

        builder.Services.AddOptions<RetentionOptions>()
            .Bind(builder.Configuration.GetSection(RetentionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddSingleton<S3StatementContentStore>();
        builder.Services.AddSingleton<IStatementContentStore>(sp => sp.GetRequiredService<S3StatementContentStore>());

        builder.Services.AddOptions<SpoolOptions>()
            .Bind(builder.Configuration.GetSection(SpoolOptions.SectionName));

        if (includeWriter)
        {
            builder.Services.AddSingleton<IStatementContentWriter>(sp => sp.GetRequiredService<S3StatementContentStore>());

            // Writer-only: the download gateway never spools, and a gateway failing readiness over
            // a directory it does not use would be a false outage.
            _ = builder.Services
                .AddHealthChecks()
                .AddCheck<SpoolDirectoryHealthCheck>(
                    SpoolDirectoryHealthCheck.Name,
                    HealthStatus.Unhealthy,
                    tags: ["ready", "storage"]);
        }

        builder.Services
            .AddHealthChecks()
            .AddCheck<ObjectLockHealthCheck>(
                ObjectLockHealthCheck.Name,
                HealthStatus.Unhealthy,
                tags: ["ready", "storage"]);

        return builder;
    }
}

/// <summary>Log messages for the encrypted object store.</summary>
public static partial class EncryptedObjectStoreLog
{
    /// <summary>Logs a decryption failure. THIS SHOULD PAGE SOMEBODY.</summary>
    /// <remarks>
    /// The INNER EXCEPTION is the payload that matters. The caller sees one uniform 404 whatever
    /// happened - that is deliberate - so this log line is the only place the three very different
    /// causes are distinguishable: a destroyed key (the system working), tampered bytes (an
    /// attacker), or a misconfigured deployment (us). Losing it would leave an operator with a
    /// counter and nothing to act on.
    /// </remarks>
    /// <param name="logger">The logger.</param>
    /// <param name="statementId">Which statement.</param>
    /// <param name="reason">Which integrity check failed.</param>
    /// <param name="exception">The cause. NEVER surfaced to the caller.</param>
    [LoggerMessage(
        EventId = 4020,
        Level = LogLevel.Critical,
        Message = "DECRYPTION FAILED for statement {StatementId}: {Reason}. This is not a user error. "
                + "It means the stored object was corrupted, truncated, substituted, or tampered with, "
                + "the row pointing at it was rewritten, the customer's key was destroyed, or this "
                + "deployment holds the wrong key material. The inner exception distinguishes them.")]
    public static partial void DecryptionFailed(ILogger logger, Guid statementId, string reason, Exception exception);
}
