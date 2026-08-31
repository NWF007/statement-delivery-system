using System.Diagnostics;
using System.Globalization;
using Dapper;
using Generation.Worker.Ledger;
using Npgsql;
using StatementDelivery.Contracts.Events;
using StatementDelivery.Crypto.Framing;
using StatementDelivery.Domain.Abstractions;
using StatementDelivery.Domain.Auditing;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Domain.Rendering;
using StatementDelivery.Domain.Statements;
using StatementDelivery.Domain.ValueObjects;
using StatementDelivery.Messaging;
using StatementDelivery.Persistence.Connections;
using StatementDelivery.Persistence.Repositories;
using StatementDelivery.Persistence.Runs;
using StatementDelivery.Persistence.Uow;
using StatementDelivery.ServiceDefaults.Auditing;
using StatementDelivery.ServiceDefaults.Storage;

namespace Generation.Worker;

/// <summary>
/// Renders one claimed item end to end: ledger, document, PDF, encryption, storage, and the one
/// transaction that makes it all real.
/// </summary>
/// <remarks>
/// <para>
/// STEP 4 IS A SINGLE STREAMING PIPELINE, delegated to <see cref="RenderStreamBridge"/>: the
/// renderer writes PDF bytes into a bounded pipe; the encrypting store reads from the other end,
/// frames, encrypts and uploads AS THE RENDERER WRITES. The full PDF exists nowhere in this
/// process: not as a byte[], not as a MemoryStream. 360 workers each holding a multi-megabyte
/// buffer is how the fleet OOMs, and the constant-memory property Prompts 3 and 4 proved on the
/// read path would die quietly right here. The bridge also owns the failure contract - see its
/// remarks; the Prompt 5 audit's HIGH 1 lived there.
/// </para>
/// <para>
/// STEP 5 IS ONE TRANSACTION: statement row (insert PENDING, publish AVAILABLE), outbox event,
/// run-item DONE, audit append LAST. An item can never be DONE without its statement, a
/// statement can never exist without its outbox event, and nothing commits without its audit
/// record (ADR-0025). Lock order is business rows before the chain head - the same global order
/// every other write path observes, so no cycle is possible.
/// </para>
/// </remarks>
public sealed partial class RenderPipeline
{
    private const string AccountContextSql = """
        SELECT a.account_number_masked AS AccountNumberMasked,
               a.customer_id           AS CustomerId,
               c.external_ref          AS CustomerExternalRef
          FROM account a
          JOIN customer c ON c.id = a.customer_id
         WHERE a.id = @accountId;
        """;

    // COALESCE(MAX)+1 inside the finalize transaction. The unique constraint
    // (account_id, period_start, version) is the backstop if two writers ever race the same
    // account - which the run's UNIQUE(run_id, account_id) already makes a bug, not a plan.
    private const string NextVersionSql = """
        SELECT COALESCE(MAX(version), 0) + 1
          FROM statement
         WHERE account_id = @accountId AND period_start = @periodStart;
        """;

    private readonly ILedgerClient _ledger;
    private readonly IStatementRenderer _renderer;
    private readonly IStatementContentWriter _contentWriter;
    private readonly IStatementWriteRepository _statements;
    private readonly IStatementRunRepository _runs;
    private readonly IIntegrationEventPublisher _events;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDbConnectionFactory _connections;
    private readonly SystemAudit _audit;
    private readonly GenerationMetrics _metrics;
    private readonly IIdGenerator _ids;
    private readonly TimeProvider _time;
    private readonly ILogger<RenderPipeline> _logger;

    /// <summary>Initialises a new instance of the <see cref="RenderPipeline"/> class.</summary>
    /// <param name="ledger">The resilient ledger client.</param>
    /// <param name="renderer">The PDF renderer.</param>
    /// <param name="contentWriter">The encrypting object-store writer.</param>
    /// <param name="statements">Statement writes.</param>
    /// <param name="runs">The run queue.</param>
    /// <param name="events">The transactional outbox.</param>
    /// <param name="unitOfWork">Transaction scope for the finalize.</param>
    /// <param name="connections">Connection factory, for the read-only context lookups.</param>
    /// <param name="audit">System-actor audit.</param>
    /// <param name="metrics">Batch metrics.</param>
    /// <param name="ids">Identifier generator.</param>
    /// <param name="time">Time source.</param>
    /// <param name="logger">Logger.</param>
    public RenderPipeline(
        ILedgerClient ledger,
        IStatementRenderer renderer,
        IStatementContentWriter contentWriter,
        IStatementWriteRepository statements,
        IStatementRunRepository runs,
        IIntegrationEventPublisher events,
        IUnitOfWork unitOfWork,
        IDbConnectionFactory connections,
        SystemAudit audit,
        GenerationMetrics metrics,
        IIdGenerator ids,
        TimeProvider time,
        ILogger<RenderPipeline> logger)
    {
        _ledger = ledger;
        _renderer = renderer;
        _contentWriter = contentWriter;
        _statements = statements;
        _runs = runs;
        _events = events;
        _unitOfWork = unitOfWork;
        _connections = connections;
        _audit = audit;
        _metrics = metrics;
        _ids = ids;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Processes one claimed item. Throws on failure; the caller records the failed attempt.
    /// </summary>
    /// <param name="runId">The run.</param>
    /// <param name="period">The run's statement period.</param>
    /// <param name="item">The claimed item.</param>
    /// <param name="workerId">This worker - the claim's owner. Completion is scoped to it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="StaleClaimSupersededException">
    /// The claim was reaped and reclaimed mid-render; everything this render did was rolled back.
    /// </exception>
    public async Task ProcessAsync(
        Guid runId, StatementPeriod period, ClaimedItem item, string workerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        // 1. LEDGER. Rate-limited, circuit-broken, retried with jitter - all inside the client.
        long stageStart = _time.GetTimestamp();
        LedgerStatementDto ledgerData = await _ledger
            .GetTransactionsAsync(item.AccountId, period, cancellationToken).ConfigureAwait(false);
        double ledgerSeconds = _time.GetElapsedTime(stageStart).TotalSeconds;
        _metrics.StageCompleted(GenerationMetrics.Stage.Ledger, ledgerSeconds);
        _metrics.LedgerRequest(ledgerSeconds);

        // 2. DOCUMENT. Balance reconciliation happens in the constructor; a ledger that fails to
        //    reconcile throws here and burns the attempt - by design, that is a poison shape.
        AccountContext account = await LoadAccountContextAsync(item.AccountId, cancellationToken)
            .ConfigureAwait(false);

        var document = new StatementDocument(
            account.AccountNumberMasked,
            string.Create(CultureInfo.InvariantCulture, $"Customer {account.CustomerExternalRef}"),
            period,
            ledgerData.OpeningBalanceMinorUnits,
            ledgerData.ClosingBalanceMinorUnits,
            "ZAR",
            [.. ledgerData.Transactions.Select(static t =>
                new StatementLine(t.PostedOn, t.Description, t.AmountMinorUnits))],
            period.End);

        // 3. VERSION - before encryption, because the version is sealed into every frame's AAD.
        var statementId = new StatementId(_ids.NewId());
        int version = await NextVersionAsync(item.AccountId, period, cancellationToken).ConfigureAwait(false);

        // 4. RENDER -> ENCRYPT -> UPLOAD, one streaming pass through a bounded pipe.
        //
        // FAILURE AFTER THIS POINT AND BEFORE THE COMMIT LEAVES AN ORPHANED OBJECT - and under a
        // Compliance-mode Object Lock an orphan is a seven-year storage bill nothing can cancel.
        // Two mitigations, one real and one honest accounting:
        //   - The key is DETERMINISTIC (StorageKeyScheme: shard/account/period-vVERSION), so the
        //     retry that follows a failed commit OVERWRITES the same key rather than minting a
        //     second orphan per attempt. Retries converge on one object.
        //   - The residue is quantified, not hand-waved: at a 0.01% commit-failure rate on a
        //     30M/month run, ~3,000 orphans/month at ~200KB is ~600MB/month of unreclaimable
        //     storage until the lock expires. Recorded in docs/LIMITATIONS.md.
        // The other half of that reconciliation is the retention worker's weekly orphan sweep
        // (Prompt 6, ADR-0039): statement_content_missing_total covers rows without objects;
        // the sweep covers objects without rows, report-only, with the storage_tombstone
        // ledger distinguishing leaked objects from lawful erasure remnants.
        stageStart = _time.GetTimestamp();

        StoredObject stored = await RenderStreamBridge.ExecuteAsync(
            _renderer,
            _contentWriter,
            document,
            new CryptoContext(statementId.Value, account.CustomerId, version),
            new AccountId(item.AccountId),
            period,
            StatementDelivery.Crypto.Keys.CohortAssignment.KekIdFor(
                StatementDelivery.Crypto.Keys.CohortAssignment.ForCustomer(
                    new CustomerId(account.CustomerId))),
            seconds => _metrics.StageCompleted(GenerationMetrics.Stage.Render, seconds),
            _time,
            cancellationToken).ConfigureAwait(false);

        _metrics.StageCompleted(
            GenerationMetrics.Stage.EncryptUpload, _time.GetElapsedTime(stageStart).TotalSeconds);

        // 5. THE TRANSACTION. Everything or nothing; audit LAST for the chain-head lock.
        stageStart = _time.GetTimestamp();
        DateTimeOffset now = _time.GetUtcNow();

        Statement statement = Statement.Create(
            statementId,
            new AccountId(item.AccountId),
            new CustomerId(account.CustomerId),
            period,
            RetentionPolicy.Default,
            version);

        var location = new StorageLocation(stored.Key, stored.Tier, stored.PlaintextLength, stored.Envelope);

        // Domain-validates the transition (and the envelope) before anything touches the database.
        _ = statement.MarkAvailable(location, now);

        await _unitOfWork.ExecuteAsync(
            async (NpgsqlTransaction transaction, CancellationToken token) =>
            {
                await _statements.InsertAsync(statement, transaction, token).ConfigureAwait(false);

                int published = await _statements.MarkAvailableAsync(
                    statementId, period.Start, location, now, transaction, token).ConfigureAwait(false);

                if (published != 1)
                {
                    // The status predicate refused the transition - a row already exists in a
                    // state this pipeline must not overwrite. Roll everything back and fail loud.
                    throw new InvalidOperationException(
                        $"Statement {statementId} could not transition to AVAILABLE (rows affected: {published}).");
                }

                await _events.PublishAsync(
                    new StatementAvailable(
                        _ids.NewId(), now, statementId.Value, item.AccountId,
                        account.CustomerId, period.Start, period.End, version),
                    transaction, token).ConfigureAwait(false);

                int completed = await _runs
                    .CompleteItemAsync(item.ItemId, statementId.Value, workerId, transaction, token)
                    .ConfigureAwait(false);

                if (completed != 1)
                {
                    // The claim was reaped from under this render and a successor owns the item.
                    // ROLL EVERYTHING BACK, deliberately: the successor will produce its own
                    // statement at the same version (deterministic bytes, deterministic key - its
                    // upload overwrote or will overwrite ours harmlessly), and committing here
                    // would race it into a duplicate row that only the version unique constraint
                    // could stop. Losing this render's transaction costs a re-render; winning it
                    // by constraint violation costs an investigation.
                    throw new StaleClaimSupersededException(item.ItemId);
                }

                // LAST: the append takes FOR UPDATE on the chain head.
                _ = await _audit.RecordAsync(
                    AuditAction.StatementGenerated, AuditOutcome.Success,
                    new CustomerId(account.CustomerId), statementId, transaction,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["run_id"] = runId.ToString("D"),
                        ["version"] = version,
                        ["size_bytes"] = stored.PlaintextLength,
                        ["attempt"] = item.Attempts,
                    },
                    token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        _metrics.StageCompleted(
            GenerationMetrics.Stage.Finalize, _time.GetElapsedTime(stageStart).TotalSeconds);
        _metrics.ItemCompleted(runId);

        LogItemRendered(_logger, item.ItemId, statementId.Value, version, stored.PlaintextLength);
    }

    private async Task<AccountContext> LoadAccountContextAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        AccountContext? context = await connection.QuerySingleOrDefaultAsync<AccountContext>(new CommandDefinition(
            AccountContextSql,
            new { accountId },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return context ?? throw new InvalidOperationException(
            $"Account {accountId:D} is queued for rendering but no longer exists.");
    }

    private async Task<int> NextVersionAsync(
        Guid accountId, StatementPeriod period, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            NextVersionSql,
            new { accountId, periodStart = period.Start },
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private sealed record AccountContext
    {
        public string AccountNumberMasked { get; init; } = string.Empty;

        public Guid CustomerId { get; init; }

        public string CustomerExternalRef { get; init; } = string.Empty;
    }

    [LoggerMessage(
        EventId = 5001,
        Level = LogLevel.Debug,
        Message = "Rendered item {ItemId}: statement {StatementId} v{Version}, {SizeBytes} bytes")]
    private static partial void LogItemRendered(
        ILogger logger, long itemId, Guid statementId, int version, long sizeBytes);
}

/// <summary>
/// This render finished, but its claim had been reaped and reclaimed by another worker.
/// </summary>
/// <remarks>
/// Not a failure of the ITEM - the successor is rendering it - so the caller records
/// <c>generation_stale_completion_total</c> and a warning rather than burning bookkeeping on a
/// FailItem that would match zero rows anyway. A non-zero rate is the tuning signal that
/// <c>Generation:StaleClaimMinutes</c> is shorter than real render times.
/// </remarks>
public sealed class StaleClaimSupersededException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="StaleClaimSupersededException"/> class.</summary>
    /// <param name="itemId">The superseded item.</param>
    public StaleClaimSupersededException(long itemId)
        : base($"Item {itemId}: the claim was reaped and reclaimed while this render was in flight; the transaction was rolled back in the successor's favour.")
        => ItemId = itemId;

    /// <summary>Initialises a new instance of the <see cref="StaleClaimSupersededException"/> class.</summary>
    public StaleClaimSupersededException()
    {
    }

    /// <summary>Initialises a new instance of the <see cref="StaleClaimSupersededException"/> class.</summary>
    /// <param name="message">Message.</param>
    public StaleClaimSupersededException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance of the <see cref="StaleClaimSupersededException"/> class.</summary>
    /// <param name="message">Message.</param>
    /// <param name="inner">Inner exception.</param>
    public StaleClaimSupersededException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>Gets the superseded item, when known.</summary>
    public long ItemId { get; }
}
