using System.Globalization;
using System.Security.Cryptography;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Domain.ValueObjects;

namespace StatementDelivery.Domain.Statements;

/// <summary>
/// How a statement's object key is computed.
/// </summary>
/// <remarks>
/// <para>
/// <c>statements/{shard}/{accountId}/{period}-v{version}.enc</c>, where <c>shard</c> is the first
/// three hex characters of <c>SHA-256(statementId)</c> - 4,096 leading prefixes.
/// </para>
/// <para>
/// WHY THE SHARD LEADS RATHER THAN THE DATE. An obvious key such as
/// <c>statements/2026-08/{accountId}/...</c> reads beautifully and concentrates EVERY WRITE FOR A
/// MONTH under one prefix. S3 partitions its index by key prefix and scales per partition, so at
/// month-end - when 30 million objects arrive in a few hours - that single prefix is the hot
/// partition and the writes throttle. A high-cardinality leading component spreads the same writes
/// across thousands of partitions from the first object. The date is still in the key, just not in
/// front. See docs/adr/0023-high-cardinality-storage-key-prefix.md.
/// </para>
/// <para>
/// THE KEY IS ALWAYS COMPUTED FROM DATABASE METADATA. It is never discovered by listing and never
/// taken from a client. Listing is not merely slow at 2.5 billion objects - <c>ListObjectsV2</c>
/// returns a thousand keys per call, so a full enumeration is two and a half million round trips -
/// it is not an operation that finishes. And a client-influenced key is a path-traversal and SSRF
/// primitive pointed straight at the bucket.
/// </para>
/// </remarks>
public static class StorageKeyScheme
{
    /// <summary>The number of distinct leading prefixes: 16^<see cref="ShardWidth"/>.</summary>
    public const int ShardCount = 4096;

    /// <summary>The shard prefix's width in hex characters. Every consumer that iterates or formats shards derives from THIS, never a local literal - a constant duplicated in two files is how the orphan sweep went blind (audit HIGH 2).</summary>
    public const int ShardWidth = 3;

    /// <summary>The object key suffix. Encrypted content, and the name says so.</summary>
    public const string Extension = ".enc";

    /// <summary>Computes the object key for a statement.</summary>
    /// <param name="statementId">The statement.</param>
    /// <param name="accountId">The owning account.</param>
    /// <param name="period">The statement period.</param>
    /// <param name="version">The generation version.</param>
    /// <returns>The object key.</returns>
    public static string KeyFor(StatementId statementId, AccountId accountId, StatementPeriod period, int version)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"statements/{ShardFor(statementId)}/{accountId.Value:D}/{period.Start:yyyy-MM}-v{version}{Extension}");
    }

    /// <summary>Computes the leading shard prefix for a statement.</summary>
    /// <param name="statementId">The statement.</param>
    /// <returns>Three lowercase hex characters.</returns>
    public static string ShardFor(StatementId statementId)
    {
        // Hashed rather than taken from the UUID directly. UUIDv7 leads with a millisecond
        // timestamp, so its first bytes are near-identical for statements created in the same
        // window - using them would reproduce the hot-prefix problem the shard exists to solve,
        // while looking like it had been solved.
        Span<byte> bytes = stackalloc byte[16];
        _ = statementId.Value.TryWriteBytes(bytes, bigEndian: true, out _);

        Span<byte> hash = stackalloc byte[32];
        _ = SHA256.HashData(bytes, hash);

        return Convert.ToHexStringLower(hash[..2])[..ShardWidth];
    }
}
