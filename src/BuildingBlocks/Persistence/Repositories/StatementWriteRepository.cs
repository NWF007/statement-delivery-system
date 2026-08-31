using Dapper;
using Npgsql;
using StatementDelivery.Domain.Exceptions;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Domain.Statements;

namespace StatementDelivery.Persistence.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IStatementWriteRepository"/>.
/// </summary>
public sealed class StatementWriteRepository : IStatementWriteRepository
{
    // No storage or crypto columns. They are populated by the generation path, and a repository
    // that could write them today would let a caller claim bytes exist before anything wrote them.
    private const string InsertSql = """
        INSERT INTO statement (
            id, account_id, customer_id, period_start, period_end,
            version, status, retain_until, generated_at)
        VALUES (
            @id, @accountId, @customerId, @periodStart, @periodEnd,
            @version, @status, @retainUntil, @generatedAt);
        """;

    // period_start is in the predicate, not because id is insufficient to identify the row, but
    // because without it PostgreSQL must visit every partition to find out which one holds it.
    //
    // ONE STATEMENT, EVERY COLUMN. The status and the content it implies move together or not at
    // all - which is what the three AVAILABLE check constraints demand, and also what makes the
    // row consistent for any reader that sees it.
    //
    // iv AND auth_tag ARE SET TO NULL ON PURPOSE. V006 created them expecting one-shot GCM per
    // object; the framed format that shipped in Prompt 4 gives every frame its own nonce and its
    // own tag, so there is no single IV to record and nothing truthful to put here. Writing them
    // explicitly rather than omitting them keeps a stale value from a previous generation of the
    // same statement from surviving into a row that no longer means it. See ADR-0019.
    //
    // THE STATUS PREDICATE IS LOAD-BEARING, added when Prompt 5 wired the first caller (it was
    // flagged in the Prompts 1-4 audit verification). The domain state machine says AVAILABLE is
    // reached from PENDING (first render) or FAILED (retry), and that a CORRECTED statement is a
    // NEW ROW - V006: "what the customer was originally shown remains provable". Without the
    // predicate, a caller that skips the aggregate could overwrite an already-AVAILABLE row's
    // envelope in place and every CHECK constraint would smile through it. With it, an illegal
    // transition matches zero rows and the caller sees rowsAffected == 0 instead of silent damage.
    private const string MarkAvailableSql = """
        UPDATE statement
           SET status         = 'AVAILABLE',
               storage_key    = @storageKey,
               storage_tier   = @storageTier,
               size_bytes     = @sizeBytes,
               content_sha256 = @contentSha256,
               wrapped_dek    = @wrappedDek,
               dek_algorithm  = @dekAlgorithm,
               kek_id         = @kekId,
               iv             = NULL,
               auth_tag       = NULL,
               generated_at   = @generatedAt
         WHERE id           = @id
           AND period_start = @periodStart
           AND status IN ('PENDING', 'FAILED')

           -- THE WARM-CACHE WRITE GUARD (remediation Part G). A generation worker's cached CEK
           -- outlives key destruction by the cache's MaxAge, and cache invalidation across
           -- processes needs a bus this system does not have - but this UPDATE is transactional
           -- and already exists, so the guard lives here: one indexed primary-key probe inside
           -- a statement that already runs. SCHEDULED_DESTRUCTION counts too: publishing into a
           -- cooling-off window creates data that is about to become unreadable, which is worse
           -- than refusing.
           AND NOT EXISTS (SELECT 1
                             FROM customer_key k
                            WHERE k.customer_id = statement.customer_id
                              AND k.status IN ('DESTROYED', 'SCHEDULED_DESTRUCTION'));
        """;

    private const string KeyBlocksPublishSql = """
        SELECT EXISTS (SELECT 1
                         FROM customer_key k
                         JOIN statement s ON s.customer_id = k.customer_id
                        WHERE s.id = @id AND s.period_start = @periodStart
                          AND k.status IN ('DESTROYED', 'SCHEDULED_DESTRUCTION'));
        """;

    // No storage or crypto columns touched. A failed render produced no bytes, and nulling the
    // columns here would erase the envelope of a PREVIOUS successful generation of the same row.
    // Same reasoning: FAILED is reached from PENDING (render errored) and nowhere else - a
    // terminal PURGED row or a live AVAILABLE one must not be flippable to FAILED by a stray call.
    private const string MarkFailedSql = """
        UPDATE statement
           SET status = 'FAILED'
         WHERE id           = @id
           AND period_start = @periodStart
           AND status = 'PENDING';
        """;

    private readonly IDbConnectionFactoryTimeouts _timeouts;

    /// <summary>Initialises a new instance of the <see cref="StatementWriteRepository"/> class.</summary>
    /// <param name="timeouts">Supplies the command timeout budget for write intent.</param>
    public StatementWriteRepository(IDbConnectionFactoryTimeouts timeouts) => _timeouts = timeouts;

    /// <inheritdoc />
    public async Task InsertAsync(Statement statement, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(transaction);

        _ = await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            InsertSql,
            new
            {
                id = statement.Id.Value,
                accountId = statement.AccountId.Value,
                customerId = statement.CustomerId.Value,
                periodStart = statement.Period.Start,
                periodEnd = statement.Period.End,
                version = statement.Version,
                status = statement.Status.ToString().ToUpperInvariant(),
                retainUntil = statement.RetainUntil,
                generatedAt = statement.GeneratedAt,
            },
            transaction: transaction,
            commandTimeout: _timeouts.WriteCommandTimeoutSeconds,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> MarkAvailableAsync(
        StatementId id,
        DateOnly partitionKey,
        StorageLocation location,
        DateTimeOffset generatedAt,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(transaction);

        // Checked here rather than left to the database. The constraint would catch it either way,
        // but as SQLSTATE 23514 from inside a batch of 50,000 - which names the constraint and not
        // the statement, and reads like a crypto fault. This names the actual mistake.
        CryptoEnvelope envelope = location.Envelope
            ?? throw new ArgumentException(
                "An AVAILABLE statement requires a crypto envelope: ck_statement_available_has_key_material "
                + "(V013) and ck_statement_available_has_digest (V015) both reject a row without one.",
                nameof(location));

        byte[] contentSha256 = envelope.ContentSha256
            ?? throw new ArgumentException(
                "The envelope carries no content digest, which ck_statement_available_has_digest (V015) "
                + "requires on every AVAILABLE row - it is what lets a reader detect a substituted object.",
                nameof(location));

        int affected = await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            MarkAvailableSql,
            new
            {
                id = id.Value,
                periodStart = partitionKey,
                storageKey = location.Key,
                storageTier = location.Tier,
                sizeBytes = location.SizeBytes,
                contentSha256,
                wrappedDek = envelope.WrappedDek,
                dekAlgorithm = envelope.Algorithm,
                kekId = envelope.KekId,
                generatedAt,
            },
            transaction: transaction,
            commandTimeout: _timeouts.WriteCommandTimeoutSeconds,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (affected == 0)
        {
            // Distinguish "the state predicate refused" (the caller's normal zero-rows contract)
            // from "the key is destroyed or scheduled" - the latter is deterministic, and the
            // run item must fail TERMINALLY rather than retry into the same wall.
            bool keyBlocks = await transaction.Connection!.ExecuteScalarAsync<bool>(new CommandDefinition(
                KeyBlocksPublishSql,
                new { id = id.Value, periodStart = partitionKey },
                transaction: transaction,
                commandTimeout: _timeouts.WriteCommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (keyBlocks)
            {
                throw new CustomerKeyDestroyedException(
                    $"Statement {id.Value:D}: the customer's key is destroyed or scheduled for destruction; publishing is refused (the Part G write guard).");
            }
        }

        return affected;
    }

    /// <inheritdoc />
    public async Task<int> MarkFailedAsync(
        StatementId id,
        DateOnly partitionKey,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            MarkFailedSql,
            new { id = id.Value, periodStart = partitionKey },
            transaction: transaction,
            commandTimeout: _timeouts.WriteCommandTimeoutSeconds,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}

/// <summary>
/// Exposes the configured command-timeout budgets to components that operate on a caller-supplied
/// transaction rather than opening their own connection.
/// </summary>
/// <remarks>
/// A write repository never calls <c>OpenAsync</c> - it is handed a transaction - so it cannot get
/// its timeout from the connection string the way a read repository does. It still must set one:
/// "every query has an explicit timeout" has no exceptions, because a query with no timeout holds
/// a pooled connection that the whole fleet shares.
/// </remarks>
public interface IDbConnectionFactoryTimeouts
{
    /// <summary>Gets the command timeout for write intent, in seconds.</summary>
    int WriteCommandTimeoutSeconds { get; }
}
