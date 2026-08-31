using System.Net;
using Dapper;
using Npgsql;
using StatementDelivery.Domain.Auditing;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Domain.Tokens;
using StatementDelivery.Persistence.Connections;
using StatementDelivery.Persistence.Repositories;

namespace StatementDelivery.Persistence.Tokens;

/// <summary>
/// What a successful consume returns: enough to serve the statement, nothing more.
/// </summary>
/// <param name="Id">The link identifier, for the audit trail.</param>
/// <param name="StatementId">The statement the token grants.</param>
/// <param name="CustomerId">The owner the token was bound to.</param>
/// <param name="StatementPeriod">The statement RANGE partition key, so the fetch that follows prunes.</param>
/// <param name="ExpiresAt">The token partition key, needed for any follow-up write.</param>
public readonly record struct TokenConsumption(
    DownloadTokenId Id,
    StatementId StatementId,
    CustomerId CustomerId,
    DateOnly StatementPeriod,
    DateTimeOffset ExpiresAt);

/// <summary>
/// The token store.
/// </summary>
/// <remarks>
/// NO METHOD HERE ACCEPTS OR RETURNS A <see cref="TokenSecret"/>. The repository layer sees hashes
/// and only hashes - which is what makes "the plaintext exists in exactly one place" a structural
/// property rather than a convention. <c>Persistence_ShouldNotReference_TokenSecret</c> asserts it.
/// </remarks>
public interface IDownloadTokenRepository
{
    /// <summary>
    /// Atomically validates and consumes a token. Returns null when it is not redeemable, for any
    /// reason at all.
    /// </summary>
    /// <param name="hash">The hash of the presented token.</param>
    /// <param name="ip">The caller's address, recorded for forensics.</param>
    /// <param name="userAgentHash">Hash of the caller's user agent.</param>
    /// <param name="transaction">The caller's transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The consumption, or null.</returns>
    Task<TokenConsumption?> ConsumeAsync(
        TokenHash hash,
        IPAddress? ip,
        string? userAgentHash,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken);

    /// <summary>
    /// Classifies why a consume returned no rows, for the audit trail only.
    /// </summary>
    /// <param name="hash">The hash of the presented token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One of the token <see cref="DenialReason"/> codes.</returns>
    Task<string> DiagnoseFailureAsync(TokenHash hash, CancellationToken cancellationToken);

    /// <summary>Persists a newly issued token.</summary>
    /// <param name="token">The token, carrying only its hash.</param>
    /// <param name="issuedToIp">The address that requested the link.</param>
    /// <param name="transaction">The caller's transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task InsertAsync(
        DownloadToken token,
        IPAddress? issuedToIp,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken);

    /// <summary>Revokes an unused token belonging to the given owner.</summary>
    /// <param name="id">The link identifier.</param>
    /// <param name="owner">The authenticated subject. Goes into the WHERE clause.</param>
    /// <param name="reason">Why it was revoked. Internal; never returned.</param>
    /// <param name="transaction">The caller's transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a row was revoked.</returns>
    Task<bool> RevokeAsync(
        DownloadTokenId id,
        CustomerId owner,
        string reason,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken);
}

/// <summary>
/// PostgreSQL implementation of <see cref="IDownloadTokenRepository"/>.
/// </summary>
public sealed class DownloadTokenRepository : IDownloadTokenRepository
{
    // =========================================================================================
    //  THE ATOMIC CONSUME. The single most important statement in this codebase.
    //
    //  VALIDATION AND CONSUMPTION ARE THE SAME STATEMENT. There is no read, no check in C#, and
    //  no second round trip. If you are ever tempted to write
    //
    //      var token = await repo.FindAsync(hash);
    //      if (token.ConsumedAt is null) { await repo.MarkConsumedAsync(token.Id); }
    //
    //  stop. That is a time-of-check-to-time-of-use race: two concurrent requests both read
    //  ConsumedAt == null, both proceed, and a statement promised once is delivered twice.
    //
    //  ---------------------------------------------------------------------------------------
    //  PROPERTY                  MECHANISM
    //  ---------------------------------------------------------------------------------------
    //  No TOCTOU race            Validation and consumption are one statement. PostgreSQL takes a
    //                            row lock for the UPDATE; the loser re-evaluates the WHERE clause
    //                            against the winner's committed row, sees consumed_at IS NOT NULL,
    //                            and matches nothing.
    //
    //  No information leakage    Expired, consumed, revoked and never-existed ALL return zero
    //                            rows. The statement cannot tell the caller which it was, because
    //                            it does not know either.
    //
    //  No distributed lock       The row lock IS the coordination primitive. No Redis lock, no
    //                            lease, no advisory lock - all of which would add a failure mode
    //                            and none of which would be stronger than the row itself.
    //
    //  Correct under replication ConnectionIntent.Write - see below.
    //  ---------------------------------------------------------------------------------------
    //
    //  ⚠ ConnectionIntent.Write IS MANDATORY AND IS A CORRECTNESS ISSUE, NOT A PERFORMANCE ONE.
    //
    //  ReadStrong looks superficially adequate - it also routes to the primary today. It is not
    //  adequate, because the guarantee this statement needs is not "read fresh data", it is
    //  "execute where the row lock lives". If this ever routed to a replica, replication lag would
    //  let a token be consumed on the replica while still unconsumed on the primary, and the same
    //  link would work twice. Declaring Write makes the requirement explicit and makes the routing
    //  wrong-by-construction rather than wrong-by-accident.
    // =========================================================================================
    //  ---------------------------------------------------------------------------------------
    //  WHY THERE IS NO `expires_at <= now() + INTERVAL '1 hour'` HERE, THOUGH RevokeSql HAS ONE.
    //
    //  It looks like an oversight and it is not. download_token is partitioned daily on
    //  expires_at with seven days created ahead, so `expires_at > now()` prunes the past and
    //  leaves roughly eight future partitions in the plan. An upper bound would cut that to two.
    //  RevokeSql carries exactly that bound, so the asymmetry is real and it is deliberate.
    //
    //  THE BOUND WOULD BE UNSAFE HERE, because the two sides of it are measured by DIFFERENT
    //  CLOCKS:
    //
    //    - ck_token_ttl guarantees expires_at <= issued_at + INTERVAL '1 hour'.
    //    - issued_at is NOT the database's now(). InsertSql supplies it explicitly, from
    //      TimeProvider.GetUtcNow() on the issuing API host. The DEFAULT now() on the column is
    //      never reached.
    //    - now() in this predicate is the DATABASE clock at redemption time.
    //
    //  So the bound holds only while app_clock <= db_clock. Let the API host run S ahead of the
    //  database. A token issued at the cap - and DownloadLinkOptions.MaxTtlSeconds is 3600, so a
    //  caller can ask for exactly that - gets expires_at = db_now + S + 1 hour, which is outside
    //  `now() + INTERVAL '1 hour'` for any S > 0. The predicate would match nothing and a valid,
    //  unexpired, unconsumed token would return the uniform 404.
    //
    //  That failure is worse than the scan it would save: it is intermittent, it depends on NTP
    //  drift, it worsens with the requested TTL, and it presents as "the link didn't work" with a
    //  denial reason of UNKNOWN_TOKEN - which is also what a guessing attack looks like.
    //
    //  RevokeSql is safe with the same bound only because revoking an already-expired token is a
    //  no-op anyway: there, excluding a row costs nothing. Here it costs a customer their
    //  statement.
    //
    //  TO MAKE IT SAFE, pick one and prove it, do not just add the line:
    //    (a) stop supplying issued_at and let the column DEFAULT to now() - but expires_at is
    //        still app-clock, so ck_token_ttl then rejects inserts when the app clock LAGS, which
    //        trades a read bug for a write bug;
    //    (b) widen the bound by an explicit, documented skew budget and alert when skew
    //        approaches it.
    //  ---------------------------------------------------------------------------------------
    private const string ConsumeSql = """
        UPDATE download_token
        SET    consumed_at         = now(),
               consumed_by_ip      = @ip,
               consumed_by_ua_hash = @uaHash
        WHERE  token_sha256 = @hash
          AND  expires_at   > now()
          AND  consumed_at  IS NULL
          AND  revoked_at   IS NULL
        RETURNING id, statement_id AS StatementId, customer_id AS CustomerId,
                  statement_period AS StatementPeriod, expires_at AS ExpiresAt;
        """;

    /// <summary>
    /// Classifies a failed consume, for the AUDIT TRAIL ONLY.
    /// </summary>
    /// <remarks>
    /// Runs AFTER the consume has already failed and on its own connection, so it holds no locks
    /// and cannot delay the caller's transaction. It is pure diagnosis: nothing it returns
    /// influences the response, which is byte-identical in every case.
    /// <para>
    /// This one column is the whole principle in miniature - AUDIT RICHLY, RESPOND OPAQUELY. An
    /// investigator needs to distinguish a replayed token from a guessed one; the caller must not
    /// be able to.
    /// </para>
    /// </remarks>
    private const string DiagnoseSql = """
        SELECT consumed_at IS NOT NULL AS WasConsumed,
               revoked_at  IS NOT NULL AS WasRevoked,
               expires_at <= now()     AS WasExpired
          FROM download_token
         WHERE token_sha256 = @hash
         LIMIT 1;
        """;

    private const string InsertSql = """
        INSERT INTO download_token (
            id, statement_id, customer_id, statement_period, token_sha256,
            issued_at, expires_at, single_use, issued_to_ip)
        VALUES (
            @id, @statementId, @customerId, @statementPeriod, @hash,
            @issuedAt, @expiresAt, @singleUse, @issuedToIp);
        """;

    /// <summary>
    /// Revokes an unused token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE PARTITION KEY IS IN THE PREDICATE, as a bounded RANGE rather than an exact value.
    /// <c>download_token</c> is partitioned daily on <c>expires_at</c>; a predicate on <c>id</c>
    /// alone would scan every daily partition to find one row.
    /// </para>
    /// <para>
    /// The bound comes free from the domain rule: a token that can still be revoked is by
    /// definition unexpired, and the schema caps a lifetime at one hour - so its <c>expires_at</c>
    /// must lie in <c>(now, now + 1 hour]</c>. That is a pruning predicate covering at most two
    /// daily partitions, and it needs no extra parameter on the HTTP request. Revoking an already
    /// expired token is a no-op anyway, so excluding them costs nothing.
    /// </para>
    /// <para>
    /// Ownership is a predicate, not a post-check: another customer's link simply does not match.
    /// </para>
    /// </remarks>
    private const string RevokeSql = """
        UPDATE download_token
        SET    revoked_at     = now(),
               revoked_reason = @reason
        WHERE  id          = @id
          AND  expires_at  >  now()
          AND  expires_at  <= now() + INTERVAL '1 hour'
          AND  customer_id = @owner
          AND  consumed_at IS NULL
          AND  revoked_at  IS NULL
        RETURNING id;
        """;

    private readonly IDbConnectionFactory _connections;
    private readonly IDbConnectionFactoryTimeouts _timeouts;

    /// <summary>Initialises a new instance of the <see cref="DownloadTokenRepository"/> class.</summary>
    /// <param name="connections">Connection factory, used only for the out-of-band diagnosis.</param>
    /// <param name="timeouts">Command timeout budgets.</param>
    public DownloadTokenRepository(IDbConnectionFactory connections, IDbConnectionFactoryTimeouts timeouts)
    {
        _connections = connections;
        _timeouts = timeouts;
    }

    /// <inheritdoc />
    public async Task<TokenConsumption?> ConsumeAsync(
        TokenHash hash,
        IPAddress? ip,
        string? userAgentHash,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        NpgsqlConnection connection = transaction.Connection
            ?? throw new InvalidOperationException("The consume transaction has no connection.");

        ConsumeRow? row = await connection.QuerySingleOrDefaultAsync<ConsumeRow>(new CommandDefinition(
            ConsumeSql,
            new
            {
                hash = hash.ToArray(),
                ip = ip?.ToString(),
                uaHash = userAgentHash,
            },
            transaction: transaction,
            commandTimeout: _timeouts.WriteCommandTimeoutSeconds,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row is null
            ? null
            : new TokenConsumption(
                new DownloadTokenId(row.Id),
                new StatementId(row.StatementId),
                new CustomerId(row.CustomerId),
                row.StatementPeriod,
                new DateTimeOffset(row.ExpiresAt, TimeSpan.Zero));
    }

    /// <inheritdoc />
    public async Task<string> DiagnoseFailureAsync(TokenHash hash, CancellationToken cancellationToken)
    {
        // ReadStrong, not ReadEventual: a replica lagging behind the consume that just happened
        // would classify a genuine replay as UNKNOWN_TOKEN, which is the one classification an
        // investigator most needs to be right.
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        DiagnosisRow? row = await connection.QuerySingleOrDefaultAsync<DiagnosisRow>(new CommandDefinition(
            DiagnoseSql,
            new { hash = hash.ToArray() },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (row is null)
        {
            // No row has ever existed for this hash. In volume, this is what a guessing attack
            // looks like - and it is the reason denials are audited at all.
            return DenialReason.UnknownToken;
        }

        // Order matters: a revoked token that later expires is still, for an investigation, a
        // revocation. Report the deliberate act ahead of the passage of time.
        if (row.WasRevoked)
        {
            return DenialReason.Revoked;
        }

        return row.WasConsumed ? DenialReason.Consumed
             : row.WasExpired ? DenialReason.Expired
             : DenialReason.UnknownToken;
    }

    /// <inheritdoc />
    public async Task InsertAsync(
        DownloadToken token,
        IPAddress? issuedToIp,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(transaction);

        _ = await transaction.Connection!.ExecuteAsync(new CommandDefinition(
            InsertSql,
            new
            {
                id = token.Id.Value,
                statementId = token.StatementId.Value,
                statementPeriod = token.StatementPeriod,
                customerId = token.CustomerId.Value,

                // The HASH. There is no overload of this method that could take the plaintext.
                hash = token.Hash.ToArray(),
                issuedAt = token.IssuedAt,
                expiresAt = token.ExpiresAt,
                singleUse = token.SingleUse,
                issuedToIp = issuedToIp?.ToString(),
            },
            transaction: transaction,
            commandTimeout: _timeouts.WriteCommandTimeoutSeconds,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(
        DownloadTokenId id,
        CustomerId owner,
        string reason,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        Guid? revoked = await transaction.Connection!.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            RevokeSql,
            new { id = id.Value, owner = owner.Value, reason },
            transaction: transaction,
            commandTimeout: _timeouts.WriteCommandTimeoutSeconds,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return revoked is not null;
    }

    private sealed record ConsumeRow
    {
        public Guid Id { get; init; }

        public Guid StatementId { get; init; }

        public Guid CustomerId { get; init; }

        public DateOnly StatementPeriod { get; init; }

        public DateTime ExpiresAt { get; init; }
    }

    private sealed record DiagnosisRow
    {
        public bool WasConsumed { get; init; }

        public bool WasRevoked { get; init; }

        public bool WasExpired { get; init; }
    }
}
