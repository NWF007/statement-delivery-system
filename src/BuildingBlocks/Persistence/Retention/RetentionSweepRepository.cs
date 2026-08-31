using Dapper;
using Npgsql;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Persistence.Connections;

namespace StatementDelivery.Persistence.Retention;

/// <summary>A statement the retention sweep is considering.</summary>
/// <param name="Id">The statement.</param>
/// <param name="PeriodStart">Its partition key.</param>
/// <param name="CustomerId">Its owner, for hold and key lookups.</param>
/// <param name="Status">AVAILABLE or ARCHIVED.</param>
/// <param name="StorageKey">Where its bytes live.</param>
/// <param name="RetainUntil">The database's retention date — advisory; the store is authoritative.</param>
public sealed record PurgeCandidate(
    Guid Id,
    DateOnly PeriodStart,
    Guid CustomerId,
    string Status,
    string? StorageKey,
    DateOnly RetainUntil);

/// <summary>A statement's storage location, for bulk operations over a customer.</summary>
/// <param name="Id">The statement.</param>
/// <param name="PeriodStart">Its partition key.</param>
/// <param name="StorageKey">Its object key.</param>
public sealed record StatementStorageRef(Guid Id, DateOnly PeriodStart, string StorageKey);

/// <summary>The purge, archive and erasure sweeps' statement-side adapter.</summary>
public sealed class RetentionSweepRepository
{
    // ORDER BY retain_until: the longest-overdue first, so a stalled sweep that resumes clears
    // the oldest debt first. LIMIT always — 30 million rows crossing their date on the same day
    // is a normal month-end, not an excuse to load them all.
    private const string CandidatesSql = """
        SELECT id           AS Id,
               period_start AS PeriodStart,
               customer_id  AS CustomerId,
               status       AS Status,
               storage_key  AS StorageKey,
               retain_until AS RetainUntil
          FROM statement
         WHERE retain_until < @today
           AND status IN ('AVAILABLE', 'ARCHIVED')
         ORDER BY retain_until
         LIMIT @limit;
        """;

    // The metadata survives; the pointers and the crypto material go. status/storage_key
    // satisfy V006's ck_statement_purged_has_no_storage; purged_at is the proof-of-deletion
    // timestamp. content_sha256 deliberately SURVIVES: a digest is not key material, and it is
    // the one artefact that can later prove WHICH bytes were destroyed.
    private const string MarkPurgedSql = """
        UPDATE statement
           SET status = 'PURGED', purged_at = now(),
               storage_key = NULL, wrapped_dek = NULL, iv = NULL, auth_tag = NULL
         WHERE id = @id AND period_start = @periodStart
           AND status IN ('AVAILABLE', 'ARCHIVED');
        """;

    private const string TombstoneSql = """
        INSERT INTO storage_tombstone (storage_key, statement_id, period_start, customer_id, kind)
        VALUES (@storageKey, @statementId, @periodStart, @customerId, @kind)
        ON CONFLICT (storage_key) DO NOTHING;
        """;

    private const string ArchiveCandidatesSql = """
        SELECT id           AS Id,
               period_start AS PeriodStart,
               customer_id  AS CustomerId,
               status       AS Status,
               storage_key  AS StorageKey,
               retain_until AS RetainUntil
          FROM statement
         WHERE status = 'AVAILABLE'
           AND period_start < @olderThan
         ORDER BY period_start
         LIMIT @limit;
        """;

    private const string MarkArchivedSql = """
        UPDATE statement
           SET status = 'ARCHIVED', storage_tier = 'GLACIER'
         WHERE id = @id AND period_start = @periodStart AND status = 'AVAILABLE';
        """;

    private const string StorageRefsForCustomerSql = """
        SELECT id           AS Id,
               period_start AS PeriodStart,
               storage_key  AS StorageKey
          FROM statement
         WHERE customer_id = @customerId
           AND storage_key IS NOT NULL
           AND (id, period_start) > (@afterId, @afterPeriod)
         ORDER BY id, period_start
         LIMIT @limit;
        """;

    // Erasure's statement pass: every statement of the customer that still points at bytes
    // becomes PURGED. The objects REMAIN in storage — under a Compliance lock nothing could
    // delete them, and nothing needs to: they are ciphertext under a destroyed key,
    // indistinguishable from random bytes. That is what makes crypto-erasure work where
    // deletion cannot. The tombstones written alongside are what tells the orphan sweep so.
    private const string MarkErasedSql = """
        UPDATE statement
           SET status = 'PURGED', purged_at = now(),
               storage_key = NULL, wrapped_dek = NULL, iv = NULL, auth_tag = NULL
         WHERE customer_id = @customerId
           AND status IN ('AVAILABLE', 'ARCHIVED', 'PENDING', 'FAILED')
           AND (storage_key IS NOT NULL OR status IN ('PENDING', 'FAILED'));
        """;

    // Staff lookup by id ALONE - no partition key, no owner scope. This deliberately walks the
    // partition indexes (one probe per partition): legal holds are rare staff operations, and
    // demanding the period in a litigation-hold request is an ergonomics bug lawyers would pay
    // for in mistakes. Customer-facing paths must never use this; they carry the period.
    private const string FindByIdSql = """
        SELECT id           AS Id,
               period_start AS PeriodStart,
               customer_id  AS CustomerId,
               status       AS Status,
               storage_key  AS StorageKey,
               retain_until AS RetainUntil
          FROM statement
         WHERE id = @id
         LIMIT 1;
        """;

    // Reconciliation's sample: the newest AVAILABLE statements (UUIDv7 ids order by time), each
    // to be HEAD-checked against the store. A bounded sample, honestly - full verification of
    // 2.5 billion objects is S3 Inventory's job, and the sample catches systemic breakage.
    private const string AvailableSampleSql = """
        SELECT id           AS Id,
               period_start AS PeriodStart,
               customer_id  AS CustomerId,
               status       AS Status,
               storage_key  AS StorageKey,
               retain_until AS RetainUntil
          FROM statement
         WHERE status = 'AVAILABLE' AND storage_key IS NOT NULL
         ORDER BY id DESC
         LIMIT @limit;
        """;

    // H2: split by hold coverage so a large litigation hold does not read as a stalled purge
    // worker. Post-V021 the hold predicate is one indexed check per row; the store's LOCKS
    // cannot be counted from here (only the store knows them) - the pass observes those.
    private const string EligibleCountSql = """
        SELECT count(*) FILTER (WHERE NOT held) AS Unblocked,
               count(*) FILTER (WHERE held)     AS HoldBlocked
          FROM (SELECT EXISTS (SELECT 1
                                 FROM legal_hold lh
                                WHERE lh.released_at IS NULL
                                  AND (lh.statement_id = s.id
                                       OR (lh.customer_id = s.customer_id AND lh.statement_id IS NULL))) AS held
                  FROM statement s
                 WHERE s.retain_until < @today
                   AND s.status IN ('AVAILABLE', 'ARCHIVED')) eligible;
        """;

    private const string MaxRetainUntilForCustomerSql = """
        SELECT max(retain_until)
          FROM statement
         WHERE customer_id = @customerId
           AND status IN ('AVAILABLE', 'ARCHIVED', 'PENDING');
        """;

    private readonly IDbConnectionFactory _connections;

    /// <summary>Initialises a new instance of the <see cref="RetentionSweepRepository"/> class.</summary>
    /// <param name="connections">Connection factory.</param>
    public RetentionSweepRepository(IDbConnectionFactory connections) => _connections = connections;

    /// <summary>Statements past their database retention date, bounded.</summary>
    /// <param name="today">The sweep's date.</param>
    /// <param name="limit">Batch bound.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Candidates, longest overdue first.</returns>
    public async Task<IReadOnlyList<PurgeCandidate>> ListPurgeCandidatesAsync(
        DateOnly today, int limit, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        IEnumerable<PurgeCandidate> rows = await connection.QueryAsync<PurgeCandidate>(new CommandDefinition(
            CandidatesSql,
            new { today, limit },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows];
    }

    /// <summary>Marks one statement purged and writes its tombstone, in the caller's transaction.</summary>
    /// <param name="candidate">The statement, as selected.</param>
    /// <param name="tombstoneKind">PURGED when the versions were deleted; ERASED when the object lawfully remains.</param>
    /// <param name="transaction">The transaction the RETENTION_PURGED audit also joins.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when this call marked it; false when another pass already had.</returns>
    public async Task<bool> MarkPurgedAsync(
        PurgeCandidate candidate, string tombstoneKind,
        NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(transaction);

        int updated = await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            MarkPurgedSql,
            new { id = candidate.Id, periodStart = candidate.PeriodStart },
            transaction,
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (updated == 1 && candidate.StorageKey is not null)
        {
            _ = await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                TombstoneSql,
                new
                {
                    storageKey = candidate.StorageKey,
                    statementId = candidate.Id,
                    periodStart = candidate.PeriodStart,
                    customerId = candidate.CustomerId,
                    kind = tombstoneKind,
                },
                transaction,
                commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        return updated == 1;
    }

    /// <summary>AVAILABLE statements old enough for the cold tier, bounded.</summary>
    /// <param name="olderThan">Archive statements whose period started before this.</param>
    /// <param name="limit">Batch bound.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Candidates, oldest first.</returns>
    public async Task<IReadOnlyList<PurgeCandidate>> ListArchiveCandidatesAsync(
        DateOnly olderThan, int limit, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        IEnumerable<PurgeCandidate> rows = await connection.QueryAsync<PurgeCandidate>(new CommandDefinition(
            ArchiveCandidatesSql,
            new { olderThan, limit },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows];
    }

    /// <summary>Marks one statement ARCHIVED, in the caller's transaction.</summary>
    /// <param name="statementId">The statement.</param>
    /// <param name="periodStart">Its partition key.</param>
    /// <param name="transaction">The transaction the STATEMENT_ARCHIVED audit also joins.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when this call archived it.</returns>
    public async Task<bool> MarkArchivedAsync(
        Guid statementId, DateOnly periodStart, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        int updated = await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            MarkArchivedSql,
            new { id = statementId, periodStart },
            transaction,
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return updated == 1;
    }

    /// <summary>One page of a customer's statements that still point at bytes.</summary>
    /// <param name="customerId">The customer.</param>
    /// <param name="afterId">Keyset cursor; <see cref="Guid.Empty"/> for the first page.</param>
    /// <param name="afterPeriod">Keyset cursor; <see cref="DateOnly.MinValue"/> for the first page.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One page.</returns>
    public async Task<IReadOnlyList<StatementStorageRef>> ListStorageRefsForCustomerAsync(
        CustomerId customerId, Guid afterId, DateOnly afterPeriod, int limit, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        IEnumerable<StatementStorageRef> rows = await connection.QueryAsync<StatementStorageRef>(new CommandDefinition(
            StorageRefsForCustomerSql,
            new { customerId = customerId.Value, afterId, afterPeriod, limit },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows];
    }

    /// <summary>Marks every statement of an erased customer PURGED, in the caller's transaction.</summary>
    /// <param name="customerId">The customer.</param>
    /// <param name="transaction">The transaction the ERASURE_COMPLETED audit also joins.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many rows changed.</returns>
    public async Task<int> MarkCustomerStatementsErasedAsync(
        CustomerId customerId, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            MarkErasedSql,
            new { customerId = customerId.Value },
            transaction,
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Writes one tombstone, in the caller's transaction.</summary>
    /// <param name="storageKey">The object key that no row will reference any more.</param>
    /// <param name="statementId">The statement it belonged to.</param>
    /// <param name="periodStart">Its partition key.</param>
    /// <param name="customerId">Its owner.</param>
    /// <param name="kind">PURGED or ERASED.</param>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task WriteTombstoneAsync(
        string storageKey, Guid statementId, DateOnly periodStart, Guid customerId, string kind,
        NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        _ = await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            TombstoneSql,
            new { storageKey, statementId, periodStart, customerId, kind },
            transaction,
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Staff lookup of one statement by id alone. See the SQL's comment for why no period.</summary>
    /// <param name="statementId">The statement.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The statement's retention-relevant columns, or null.</returns>
    public async Task<PurgeCandidate?> FindStatementRefAsync(Guid statementId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<PurgeCandidate>(new CommandDefinition(
            FindByIdSql,
            new { id = statementId },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>A bounded sample of the newest AVAILABLE statements, for reconciliation.</summary>
    /// <param name="limit">Sample size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The sample, newest first.</returns>
    public async Task<IReadOnlyList<PurgeCandidate>> ListAvailableSampleAsync(
        int limit, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        IEnumerable<PurgeCandidate> rows = await connection.QueryAsync<PurgeCandidate>(new CommandDefinition(
            AvailableSampleSql,
            new { limit },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows];
    }

    /// <summary>How many statements are past their date and not yet purged — the stalled-sweep gauge, split by hold coverage.</summary>
    /// <param name="today">The date.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Eligible-and-unheld, and eligible-but-held counts.</returns>
    public async Task<(long Unblocked, long HoldBlocked)> CountEligibleForPurgeAsync(
        DateOnly today, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadEventual, cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleAsync<(long, long)>(new CommandDefinition(
            EligibleCountSql,
            new { today },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadEventual),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>The latest retention date across a customer's undestroyed statements, or null when none.</summary>
    /// <param name="customerId">The customer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The max retain-until, or null.</returns>
    public async Task<DateOnly?> MaxRetainUntilForCustomerAsync(
        CustomerId customerId, CancellationToken cancellationToken)
    {
        // ReadStrong: this read gates erasure. ADR-0024's rule again.
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<DateOnly?>(new CommandDefinition(
            MaxRetainUntilForCustomerSql,
            new { customerId = customerId.Value },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
