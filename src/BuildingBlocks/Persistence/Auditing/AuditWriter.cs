using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using StatementDelivery.Domain.Auditing;

namespace StatementDelivery.Persistence.Auditing;

/// <summary>Configuration for the audit subsystem.</summary>
public sealed class AuditOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Audit";

    /// <summary>
    /// Gets or sets the number of independent hash chains.
    /// </summary>
    /// <remarks>
    /// MUST MATCH THE NUMBER OF HEADS SEEDED BY MIGRATION V007 (sixteen). Raising it without a new
    /// migration to seed the extra heads makes every event assigned to a new chain fail to append,
    /// which - because the audit write shares the business transaction - fails the operation it was
    /// recording. The writer therefore refuses loudly on a missing head rather than inventing a
    /// genesis, because an invented genesis silently starts a second, unverifiable chain.
    /// </remarks>
    [Range(1, 256)]
    public int ChainCount { get; set; } = AuditHashing.DefaultChainCount;
}

/// <summary>
/// Appends records to the audit hash chain, inside the caller's transaction.
/// </summary>
public interface IAuditWriter
{
    /// <summary>
    /// Appends one record.
    /// </summary>
    /// <remarks>
    /// Takes the caller's transaction rather than opening one, so that the record and the operation
    /// it describes commit or roll back together. Call it LAST in the transaction: it holds a row
    /// lock that serialises every other writer on the same chain.
    /// </remarks>
    /// <param name="entry">The record to append.</param>
    /// <param name="transaction">The caller's transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The chain position and hash of the appended record.</returns>
    Task<AuditReceipt> AppendAsync(AuditEntry entry, NpgsqlTransaction transaction, CancellationToken cancellationToken);
}

/// <summary>
/// PostgreSQL implementation of <see cref="IAuditWriter"/>.
/// </summary>
public sealed class PostgresAuditWriter : IAuditWriter
{
    /// <summary>
    /// Locks this chain's head row.
    /// </summary>
    /// <remarks>
    /// <c>FOR UPDATE</c> IS WHAT MAKES CONCURRENT APPENDS SAFE, and it is the entire concurrency
    /// design in one line. Two writers appending to the same chain would otherwise both read
    /// <c>last_seq = N</c>, both compute sequence N+1, and both hash over the same predecessor -
    /// producing two records claiming the same position, with the second silently orphaning the
    /// first. The row lock serialises them: the second waits, then reads N+1 and builds on it.
    /// <para>
    /// It serialises ONLY this chain. That is why there are sixteen: a single global chain would
    /// make this lock the throughput ceiling for every write path in the system.
    /// </para>
    /// </remarks>
    private const string LockHeadSql = """
        SELECT last_seq  AS LastSeq,
               last_hash AS LastHash
          FROM audit_chain_head
         WHERE chain_id = @chainId
           FOR UPDATE;
        """;

    private const string InsertSql = """
        INSERT INTO audit_event (
            chain_id, chain_seq, id, statement_id, customer_id, token_id,
            actor_type, actor_id, action, outcome, denial_reason_code,
            source_ip, user_agent_hash, context, occurred_at, prev_hash, hash)
        VALUES (
            @chainId, @chainSeq, @id, @statementId, @customerId, NULL,
            @actorType, @actorId, @action, @outcome, @denialReasonCode,
            @sourceIp::inet, @userAgentHash, @context::jsonb, @occurredAt, @prevHash, @hash);
        """;

    private const string AdvanceHeadSql = """
        UPDATE audit_chain_head
           SET last_seq = @chainSeq, last_hash = @hash, updated_at = now()
         WHERE chain_id = @chainId;
        """;

    private readonly AuditOptions _options;
    private readonly int _commandTimeoutSeconds;

    /// <summary>Initialises a new instance of the <see cref="PostgresAuditWriter"/> class.</summary>
    /// <param name="options">Audit options.</param>
    /// <param name="timeouts">Command timeout budgets.</param>
    public PostgresAuditWriter(IOptions<AuditOptions> options, Repositories.IDbConnectionFactoryTimeouts timeouts)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeouts);

        _options = options.Value;
        _commandTimeoutSeconds = timeouts.WriteCommandTimeoutSeconds;
    }

    /// <inheritdoc />
    public async Task<AuditReceipt> AppendAsync(
        AuditEntry entry,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(transaction);

        NpgsqlConnection connection = transaction.Connection
            ?? throw new InvalidOperationException("The audit transaction has no connection.");

        // Assignment is by SUBJECT ENTITY, so every event about one statement lands in one chain
        // and that statement's history is a single-chain walk.
        short chainId = AuditHashing.AssignChain(
            entry.StatementId?.Value,
            entry.CustomerId?.Value,
            _options.ChainCount);

        ChainHeadRow? head = await connection.QuerySingleOrDefaultAsync<ChainHeadRow>(new CommandDefinition(
            LockHeadSql,
            new { chainId },
            transaction: transaction,
            commandTimeout: _commandTimeoutSeconds,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (head is null)
        {
            // Loud, not silent. Inventing a genesis here would start a second chain that no
            // verifier can ever reconcile with the first.
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"audit_chain_head has no row for chain {chainId}. Audit:ChainCount is {_options.ChainCount}, but migration V007 seeds {AuditHashing.DefaultChainCount} heads. Add a migration seeding the missing heads before raising the chain count."));
        }

        long chainSeq = head.LastSeq + 1;
        string canonical = AuditHashing.Canonicalise(chainId, chainSeq, entry);
        byte[] hash = AuditHashing.ComputeHash(head.LastHash, canonical);

        // The canonical JSON is stored rather than an arbitrary serialisation of the context, so
        // what was hashed and what was persisted are the same text.
        string context = AuditHashing.CanonicalJson(entry.Context);

        _ = await connection.ExecuteAsync(new CommandDefinition(
            InsertSql,
            new
            {
                chainId,
                chainSeq,
                id = entry.Id.Value,
                statementId = entry.StatementId?.Value,
                customerId = entry.CustomerId?.Value,
                actorType = entry.ActorType,
                actorId = entry.ActorId,
                action = entry.Action,
                outcome = entry.Outcome,
                denialReasonCode = entry.DenialReasonCode,
                sourceIp = entry.SourceIp,
                userAgentHash = entry.UserAgentHash,
                context,
                // The truncation the canonical form applies (AuditHashing.TruncateToMicroseconds):
                // PostgreSQL rounds sub-microsecond input, and a stored value one microsecond above
                // the hashed one fails verification forever.
                occurredAt = AuditHashing.TruncateToMicroseconds(entry.OccurredAt.ToUniversalTime()),
                prevHash = head.LastHash,
                hash,
            },
            transaction: transaction,
            commandTimeout: _commandTimeoutSeconds,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        _ = await connection.ExecuteAsync(new CommandDefinition(
            AdvanceHeadSql,
            new { chainId, chainSeq, hash },
            transaction: transaction,
            commandTimeout: _commandTimeoutSeconds,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new AuditReceipt(chainId, chainSeq, hash);
    }

    private sealed record ChainHeadRow
    {
        public long LastSeq { get; init; }

        public byte[] LastHash { get; init; } = [];
    }
}
