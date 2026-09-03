using System.Globalization;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace StatementDelivery.ServiceDefaults.Storage;

/// <summary>
/// The retention-side view of one object's lock state, as the OBJECT STORE reports it.
/// </summary>
/// <remarks>
/// Hard constraint 4: the store is authoritative on retention. This record exists so the purge
/// worker builds its <c>RetentionContext</c> from what S3 actually says, never from the
/// database's mirror of it.
/// </remarks>
/// <param name="Exists">Whether the object exists at all.</param>
/// <param name="Mode">COMPLIANCE or GOVERNANCE, or null when the object has no retention configured.</param>
/// <param name="RetainUntil">The lock's retain-until date, or null when unlocked.</param>
/// <param name="LegalHold">Whether an object-store legal hold is ON.</param>
public sealed record ObjectRetentionInfo(bool Exists, string? Mode, DateOnly? RetainUntil, bool LegalHold);

/// <summary>One page of keys from a prefix listing.</summary>
/// <param name="Keys">The keys and their sizes.</param>
/// <param name="NextToken">Continuation token for the next page, or null at the end.</param>
public sealed record ObjectKeyPage(IReadOnlyList<StoredObjectKey> Keys, string? NextToken);

/// <summary>A listed object.</summary>
/// <param name="Key">The full storage key.</param>
/// <param name="SizeBytes">The object's size.</param>
public sealed record StoredObjectKey(string Key, long SizeBytes);

/// <summary>
/// Administrative operations on statement objects: deletion, lock inspection, legal holds and
/// enumeration. Deliberately a SEPARATE port from the content reader/writer.
/// </summary>
/// <remarks>
/// <para>
/// Least privilege is the reason it is separate. The download gateway must be able to read bytes
/// and nothing else; the generation worker writes and nothing else; only the retention worker
/// (and, for legal holds, the delivery API) may touch lock state or delete. One combined port
/// would hand every service every capability and leave IAM as the only fence.
/// </para>
/// <para>
/// Sketched as a design note before the retention work began. The signatures differ from that
/// sketch in one deliberate way: no <c>NpgsqlTransaction</c> parameter. Object storage cannot
/// join a database transaction, and a parameter that implies it can is a lie in the signature -
/// the purge worker sequences storage-then-database explicitly instead (ADR-0034).
/// </para>
/// </remarks>
public interface IStatementObjectAdmin
{
    /// <summary>
    /// Deletes every version of one object. Idempotent: a missing object is success, not an
    /// error - the purge path relies on this to make crash-and-retry safe.
    /// </summary>
    /// <param name="key">The storage key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The version ids actually deleted, for the audit record. Empty when the object was already gone.</returns>
    Task<IReadOnlyList<string>> DeleteObjectVersionsAsync(string key, CancellationToken cancellationToken);

    /// <summary>Reads the object's retention and legal-hold state from the store itself.</summary>
    /// <param name="key">The storage key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the store says. The database's opinion does not matter here.</returns>
    Task<ObjectRetentionInfo> GetRetentionAsync(string key, CancellationToken cancellationToken);

    /// <summary>Turns the object-store legal hold on or off for one object.</summary>
    /// <param name="key">The storage key.</param>
    /// <param name="place">True to place the hold, false to release it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task SetLegalHoldAsync(string key, bool place, CancellationToken cancellationToken);

    /// <summary>Lists keys under a prefix, one bounded page at a time.</summary>
    /// <remarks>
    /// The orphan sweep's building block. NEVER enumerate the whole bucket in one pass - at 2.5
    /// billion objects that is S3 Inventory's job in production; locally, shard-prefix iteration
    /// with this resumable cursor. See ADR-0039.
    /// </remarks>
    /// <param name="prefix">Key prefix, for example <c>statements/a7/</c>.</param>
    /// <param name="continuationToken">The previous page's <see cref="ObjectKeyPage.NextToken"/>, or null to start.</param>
    /// <param name="maxKeys">Page size bound.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One page.</returns>
    Task<ObjectKeyPage> ListKeysAsync(string prefix, string? continuationToken, int maxKeys, CancellationToken cancellationToken);
}

/// <summary>S3 implementation of <see cref="IStatementObjectAdmin"/>.</summary>
public sealed class S3ObjectAdminStore : IStatementObjectAdmin
{
    private readonly IAmazonS3 _s3;
    private readonly ObjectStorageOptions _storage;

    /// <summary>Initialises a new instance of the <see cref="S3ObjectAdminStore"/> class.</summary>
    /// <param name="s3">The S3 client.</param>
    /// <param name="storage">Bucket configuration.</param>
    public S3ObjectAdminStore(IAmazonS3 s3, IOptions<ObjectStorageOptions> storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _s3 = s3;
        _storage = storage.Value;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> DeleteObjectVersionsAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var deleted = new List<string>();
        string? keyMarker = null;
        string? versionMarker = null;

        // Versions are listed page by page and deleted page by page: a single huge
        // DeleteObjects call for an object with thousands of versions would exceed the API's
        // 1000-key limit, and buffering all versions first is the pattern this codebase bans.
        while (true)
        {
            ListVersionsResponse versions = await _s3.ListVersionsAsync(
                new ListVersionsRequest
                {
                    BucketName = _storage.BucketName,
                    Prefix = key,
                    KeyMarker = keyMarker,
                    VersionIdMarker = versionMarker,
                    MaxKeys = 500,
                },
                cancellationToken).ConfigureAwait(false);

            // Prefix listing can over-match (key "a/b" also matches "a/b2"): filter to the exact key.
            List<KeyVersion> page = [.. (versions.Versions ?? [])
                .Where(v => string.Equals(v.Key, key, StringComparison.Ordinal))
                .Select(v => new KeyVersion { Key = v.Key, VersionId = v.VersionId })];

            if (page.Count > 0)
            {
                DeleteObjectsResponse response = await _s3.DeleteObjectsAsync(
                    new DeleteObjectsRequest
                    {
                        BucketName = _storage.BucketName,
                        Objects = page,
                        Quiet = false,
                    },
                    cancellationToken).ConfigureAwait(false);

                deleted.AddRange((response.DeletedObjects ?? []).Select(d => d.VersionId ?? "null"));
            }

            if (versions.IsTruncated != true)
            {
                return deleted;
            }

            keyMarker = versions.NextKeyMarker;
            versionMarker = versions.NextVersionIdMarker;
        }
    }

    /// <inheritdoc />
    public async Task<ObjectRetentionInfo> GetRetentionAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        // Three independent reads, because S3 models existence, retention and legal hold as
        // three facts and reports "not configured" for the latter two as errors rather than
        // nulls. A missing OBJECT and a missing LOCK are different answers and the decision
        // engine treats them differently.
        try
        {
            _ = await _s3.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = _storage.BucketName, Key = key },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new ObjectRetentionInfo(Exists: false, Mode: null, RetainUntil: null, LegalHold: false);
        }

        string? mode = null;
        DateOnly? retainUntil = null;
        try
        {
            GetObjectRetentionResponse retention = await _s3.GetObjectRetentionAsync(
                new GetObjectRetentionRequest { BucketName = _storage.BucketName, Key = key },
                cancellationToken).ConfigureAwait(false);

            if (retention.Retention?.RetainUntilDate is { } until)
            {
                mode = retention.Retention.Mode?.Value;
                retainUntil = DateOnly.FromDateTime(until.ToUniversalTime().Date);
            }
        }
        catch (AmazonS3Exception ex) when (IsNoLockConfigured(ex))
        {
            // No retention on this object: legitimately unlocked.
        }

        bool legalHold = false;
        try
        {
            GetObjectLegalHoldResponse hold = await _s3.GetObjectLegalHoldAsync(
                new GetObjectLegalHoldRequest { BucketName = _storage.BucketName, Key = key },
                cancellationToken).ConfigureAwait(false);

            legalHold = hold.LegalHold?.Status == ObjectLockLegalHoldStatus.On;
        }
        catch (AmazonS3Exception ex) when (IsNoLockConfigured(ex))
        {
            // No legal hold configured: off.
        }

        return new ObjectRetentionInfo(Exists: true, mode, retainUntil, legalHold);
    }

    /// <inheritdoc />
    public async Task SetLegalHoldAsync(string key, bool place, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        _ = await _s3.PutObjectLegalHoldAsync(
            new PutObjectLegalHoldRequest
            {
                BucketName = _storage.BucketName,
                Key = key,
                LegalHold = new ObjectLockLegalHold
                {
                    Status = place ? ObjectLockLegalHoldStatus.On : ObjectLockLegalHoldStatus.Off,
                },
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ObjectKeyPage> ListKeysAsync(
        string prefix, string? continuationToken, int maxKeys, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxKeys, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxKeys, 1000);

        ListObjectsV2Response response = await _s3.ListObjectsV2Async(
            new ListObjectsV2Request
            {
                BucketName = _storage.BucketName,
                Prefix = prefix,
                ContinuationToken = continuationToken,
                MaxKeys = maxKeys,
            },
            cancellationToken).ConfigureAwait(false);

        return new ObjectKeyPage(
            [.. (response.S3Objects ?? []).Select(o => new StoredObjectKey(o.Key, o.Size ?? 0))],
            response.IsTruncated == true ? response.NextContinuationToken : null);
    }

    private static bool IsNoLockConfigured(AmazonS3Exception ex) =>
        ex.StatusCode == HttpStatusCode.NotFound
        || string.Equals(ex.ErrorCode, "NoSuchObjectLockConfiguration", StringComparison.Ordinal)
        || string.Equals(ex.ErrorCode, "InvalidRequest", StringComparison.Ordinal)
        || string.Equals(ex.ErrorCode, "ObjectLockConfigurationNotFoundError", StringComparison.Ordinal);
}

/// <summary>Registers the object-admin store.</summary>
public static class ObjectAdminStoreExtensions
{
    /// <summary>
    /// Adds <see cref="IStatementObjectAdmin"/>. Call after <c>AddObjectStorage()</c>; only the
    /// services that legitimately hold lock/delete powers register this.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static IHostApplicationBuilder AddObjectAdminStore(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<IStatementObjectAdmin, S3ObjectAdminStore>();
        return builder;
    }
}
