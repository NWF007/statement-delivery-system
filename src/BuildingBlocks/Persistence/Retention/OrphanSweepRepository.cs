using Dapper;
using Npgsql;
using StatementDelivery.Persistence.Connections;

namespace StatementDelivery.Persistence.Retention;

/// <summary>The orphan sweep's saved position: which shard prefix, and where inside it.</summary>
/// <param name="ShardPrefix">The two-hex-character shard being walked, or null before the first sweep.</param>
/// <param name="ContinuationToken">The listing cursor inside that shard, or null at a shard boundary.</param>
public sealed record OrphanSweepCursor(string? ShardPrefix, string? ContinuationToken);

/// <summary>How a listed object relates to the database.</summary>
public enum StorageKeyAccounting
{
    /// <summary>A statement row points at exactly this key. All is well.</summary>
    Referenced,

    /// <summary>A tombstone accounts for it: purged (should be gone) or erased (lawfully remains).</summary>
    TombstonedErased,

    /// <summary>A PURGED tombstone exists — the object should NOT exist any more.</summary>
    TombstonedPurged,

    /// <summary>Nothing accounts for it. An orphan.</summary>
    Unaccounted,
}

/// <summary>The orphan sweep's database side: cursor persistence, key accounting, report rows.</summary>
public sealed class OrphanSweepRepository
{
    private const string ReadCursorSql = """
        SELECT shard_prefix AS ShardPrefix, continuation_token AS ContinuationToken
          FROM orphan_sweep_state
         WHERE singleton;
        """;

    private const string SaveCursorSql = """
        INSERT INTO orphan_sweep_state (singleton, shard_prefix, continuation_token, updated_at)
        VALUES (TRUE, @shardPrefix, @continuationToken, now())
        ON CONFLICT (singleton)
        DO UPDATE SET shard_prefix = @shardPrefix, continuation_token = @continuationToken, updated_at = now();
        """;

    private const string AccountSql = """
        SELECT CASE
                 WHEN EXISTS (SELECT 1 FROM statement WHERE storage_key = @storageKey) THEN 'REFERENCED'
                 WHEN EXISTS (SELECT 1 FROM storage_tombstone
                               WHERE storage_key = @storageKey AND kind = 'ERASED') THEN 'ERASED'
                 WHEN EXISTS (SELECT 1 FROM storage_tombstone
                               WHERE storage_key = @storageKey AND kind = 'PURGED') THEN 'PURGED'
                 ELSE 'NONE'
               END;
        """;

    private const string ReportSql = """
        INSERT INTO orphan_report (storage_key, size_bytes, reason)
        VALUES (@storageKey, @sizeBytes, @reason);
        """;

    private readonly IDbConnectionFactory _connections;

    /// <summary>Initialises a new instance of the <see cref="OrphanSweepRepository"/> class.</summary>
    /// <param name="connections">Connection factory.</param>
    public OrphanSweepRepository(IDbConnectionFactory connections) => _connections = connections;

    /// <summary>Reads the saved cursor, or an empty one before the first sweep.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cursor.</returns>
    public async Task<OrphanSweepCursor> ReadCursorAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        OrphanSweepCursor? cursor = await connection.QuerySingleOrDefaultAsync<OrphanSweepCursor>(
            new CommandDefinition(
                ReadCursorSql,
                commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return cursor ?? new OrphanSweepCursor(null, null);
    }

    /// <summary>Saves the cursor so the next tick resumes rather than restarts.</summary>
    /// <param name="cursor">The position to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task SaveCursorAsync(OrphanSweepCursor cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.Write, cancellationToken).ConfigureAwait(false);

        _ = await connection.ExecuteAsync(new CommandDefinition(
            SaveCursorSql,
            new { shardPrefix = cursor.ShardPrefix, continuationToken = cursor.ContinuationToken },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Accounts for one listed key: referenced, tombstoned, or orphaned.</summary>
    /// <param name="storageKey">The key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The accounting.</returns>
    public async Task<StorageKeyAccounting> AccountForKeyAsync(string storageKey, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        string verdict = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            AccountSql,
            new { storageKey },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false) ?? "NONE";

        return verdict switch
        {
            "REFERENCED" => StorageKeyAccounting.Referenced,
            "ERASED" => StorageKeyAccounting.TombstonedErased,
            "PURGED" => StorageKeyAccounting.TombstonedPurged,
            _ => StorageKeyAccounting.Unaccounted,
        };
    }

    /// <summary>Writes one orphan-report row. Never deletes anything, permanently (ADR-0039).</summary>
    /// <param name="storageKey">The orphaned key.</param>
    /// <param name="sizeBytes">Its size, for the exposure metric.</param>
    /// <param name="reason">NO_ROW or KEY_MISMATCH.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task ReportAsync(
        string storageKey, long sizeBytes, string reason, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.Write, cancellationToken).ConfigureAwait(false);

        _ = await connection.ExecuteAsync(new CommandDefinition(
            ReportSql,
            new { storageKey, sizeBytes, reason },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
