using System.Text.Json;
using Dapper;
using Npgsql;
using StatementDelivery.Domain.Auditing;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Persistence.Connections;

namespace StatementDelivery.Persistence.Auditing;

/// <summary>
/// Re-walks a chain and recomputes every hash, reporting the first divergence.
/// </summary>
public interface IAuditVerifier
{
    /// <summary>
    /// Verifies a contiguous range of one chain.
    /// </summary>
    /// <param name="chainId">The chain to verify.</param>
    /// <param name="fromSeq">First sequence number, inclusive. Use 1 to start at the genesis.</param>
    /// <param name="toSeq">Last sequence number, inclusive.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The verification result.</returns>
    Task<ChainVerification> VerifyChainAsync(
        short chainId,
        long fromSeq,
        long toSeq,
        CancellationToken cancellationToken);
}

/// <summary>
/// PostgreSQL implementation of <see cref="IAuditVerifier"/>.
/// </summary>
/// <remarks>
/// <para>
/// STREAMS. A chain can hold tens of millions of records; loading one into memory to verify it
/// would need more RAM than the process has, and the verification is a single forward pass that
/// never needs to look back further than one record.
/// </para>
/// <para>
/// WHAT THIS PROVES, AND WHAT IT DOES NOT. It proves that within the verified range no record has
/// been altered and none has been removed from the middle - any such change breaks every hash
/// after it. It does NOT prove the chain has not been TRUNCATED, nor that the whole chain has not
/// been rewritten: both produce a shorter or different but internally consistent chain. Detecting
/// those requires an independently held terminal hash, which is what <see cref="IChainAnchor"/>
/// exists to provide. See docs/adr/0010-sharded-audit-hash-chains.md.
/// </para>
/// </remarks>
public sealed class PostgresAuditVerifier : IAuditVerifier
{
    // Ordered by chain_seq, served by idx_audit_chain_seq. Explicit column list: a SELECT * here
    // would start returning new columns to a recomputation that does not know about them, and the
    // chain would fail to verify for a reason that looks exactly like tampering.
    private const string ReadChainSql = """
        SELECT chain_seq, id, statement_id, customer_id, actor_type, actor_id,
               action, outcome, denial_reason_code, host(source_ip) AS source_ip,
               user_agent_hash, context::text AS context, occurred_at, prev_hash, hash
          FROM audit_event
         WHERE chain_id = @chainId
           AND chain_seq >= @fromSeq
           AND chain_seq <= @toSeq
         ORDER BY chain_seq;
        """;

    private const string ReadPreviousHashSql = """
        SELECT hash
          FROM audit_event
         WHERE chain_id = @chainId
           AND chain_seq = @seq
         LIMIT 1;
        """;

    private readonly IDbConnectionFactory _connections;

    /// <summary>Initialises a new instance of the <see cref="PostgresAuditVerifier"/> class.</summary>
    /// <param name="connections">Connection factory.</param>
    public PostgresAuditVerifier(IDbConnectionFactory connections) => _connections = connections;

    /// <inheritdoc />
    public async Task<ChainVerification> VerifyChainAsync(
        short chainId,
        long fromSeq,
        long toSeq,
        CancellationToken cancellationToken)
    {
        // ReadStrong, never ReadEventual. Verifying against a lagging replica would report a chain
        // as broken simply because the last few records have not replicated yet - a false alarm on
        // the one signal that must never cry wolf.
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        int timeout = _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong);

        // Starting mid-chain needs the real predecessor hash; starting at the beginning uses the
        // chain-specific genesis. Using zeros instead would let a record from another chain verify
        // here, which is exactly what the genesis separation exists to prevent.
        byte[] previousHash;
        if (fromSeq <= 1)
        {
            previousHash = AuditHashing.Genesis(chainId);
        }
        else
        {
            byte[]? anchor = await connection.ExecuteScalarAsync<byte[]?>(new CommandDefinition(
                ReadPreviousHashSql,
                new { chainId, seq = fromSeq - 1 },
                commandTimeout: timeout,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (anchor is null)
            {
                return new ChainVerification(chainId, false, 0, fromSeq - 1, null, null);
            }

            previousHash = anchor;
        }

        long checkedCount = 0;
        long expectedSeq = fromSeq <= 1 ? 1 : fromSeq;

        // The unbuffered overload takes the SQL directly rather than a CommandDefinition, so the
        // cancellation token is applied to the ENUMERATION below instead of to the command. The
        // timeout still bounds the server side.
        IAsyncEnumerable<AuditRow> rows = connection.QueryUnbufferedAsync<AuditRow>(
            ReadChainSql,
            new { chainId, fromSeq, toSeq },
            commandTimeout: timeout);

        await foreach (AuditRow row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            // A GAP IS A DELETION. Checked before the hashes, because a missing record shows up
            // first as a sequence that skips - and saying "record 41 is missing" is a far more
            // useful finding than "record 42's hash is wrong".
            if (row.ChainSeq != expectedSeq)
            {
                return new ChainVerification(chainId, false, checkedCount, expectedSeq, null, null);
            }

            // The stored predecessor must be the hash we actually carried forward. Catches a record
            // spliced in from elsewhere even when its own hash is internally consistent.
            if (!row.PrevHash.AsSpan().SequenceEqual(previousHash))
            {
                return new ChainVerification(chainId, false, checkedCount, row.ChainSeq, previousHash, row.PrevHash);
            }

            byte[] expected = AuditHashing.ComputeHash(
                previousHash,
                AuditHashing.Canonicalise(chainId, row.ChainSeq, row.ToEntry()));

            if (!expected.AsSpan().SequenceEqual(row.Hash))
            {
                return new ChainVerification(chainId, false, checkedCount, row.ChainSeq, expected, row.Hash);
            }

            previousHash = row.Hash;
            checkedCount++;
            expectedSeq++;
        }

        return ChainVerification.Ok(chainId, checkedCount);
    }

    private sealed record AuditRow
    {
        public long ChainSeq { get; init; }

        public Guid Id { get; init; }

        public Guid? StatementId { get; init; }

        public Guid? CustomerId { get; init; }

        public string ActorType { get; init; } = string.Empty;

        public string? ActorId { get; init; }

        public string Action { get; init; } = string.Empty;

        public string Outcome { get; init; } = string.Empty;

        public string? DenialReasonCode { get; init; }

        public string? SourceIp { get; init; }

        public string? UserAgentHash { get; init; }

        public string Context { get; init; } = "{}";

        public DateTime OccurredAt { get; init; }

        public byte[] PrevHash { get; init; } = [];

        public byte[] Hash { get; init; } = [];

        /// <summary>
        /// Rebuilds the entry so its canonical form can be recomputed.
        /// </summary>
        /// <remarks>
        /// The context is parsed back into <see cref="JsonElement"/> values rather than into CLR
        /// primitives. Guessing at types on the way back - was that 1 an int or a long? - would
        /// change the canonical form and break verification for records nobody touched.
        /// </remarks>
        public AuditEntry ToEntry()
        {
            using var document = JsonDocument.Parse(Context);

            var context = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                context[property.Name] = property.Value.Clone();
            }

            return new AuditEntry(
                new AuditEventId(Id),
                StatementId is null ? null : new StatementId(StatementId.Value),
                CustomerId is null ? null : new StatementDelivery.Domain.Identifiers.CustomerId(CustomerId.Value),
                ActorType,
                ActorId,
                Action,
                Outcome,
                DenialReasonCode,
                SourceIp,
                UserAgentHash,
                context,
                new DateTimeOffset(OccurredAt, TimeSpan.Zero));
        }
    }
}

/// <summary>
/// Publishes chain terminal hashes somewhere the database cannot reach.
/// </summary>
/// <remarks>
/// <para>
/// THE LIMIT OF THE HASH CHAIN, STATED PLAINLY. The chain proves that no record has been deleted or
/// altered WITHIN a chain - given a trusted terminal hash. But the chain heads live in the SAME
/// DATABASE as the events. An attacker with enough privilege to rewrite both the events and the
/// heads can produce a forgery that verifies perfectly, because they control both sides of the
/// comparison.
/// </para>
/// <para>
/// Closing that gap means anchoring terminal hashes OUTSIDE the database: periodically writing them
/// to append-only object storage under Object Lock, or to an account with independent credentials,
/// so that verification compares against something the database's attacker never held.
/// </para>
/// <para>
/// TODO(security): the implementation is deferred; only this seam and a no-op exist. Naming the
/// limit of your own control - and scaffolding the thing that closes it - is the point. Until an
/// anchor ships, treat chain verification as evidence against application bugs and opportunistic
/// tampering, NOT against a privileged insider.
/// </para>
/// </remarks>
public interface IChainAnchor
{
    /// <summary>Publishes a chain's terminal hash to external, append-only storage.</summary>
    /// <param name="chainId">The chain.</param>
    /// <param name="seq">The terminal sequence number.</param>
    /// <param name="hash">The terminal hash.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AnchorAsync(short chainId, long seq, byte[] hash, CancellationToken cancellationToken);
}

/// <summary>
/// The deliberate no-op. Registered so that call sites exist and are wired before the real
/// implementation lands.
/// </summary>
/// <remarks>
/// TODO(security): replace with an Object Lock-backed implementation. This type existing must never
/// be read as the gap being closed - it is a placeholder that makes the gap visible in the
/// dependency graph rather than only in a document.
/// </remarks>
public sealed class NoOpChainAnchor : IChainAnchor
{
    /// <inheritdoc />
    public Task AnchorAsync(short chainId, long seq, byte[] hash, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
