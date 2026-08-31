using Dapper;
using Generation.Worker.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;
using StatementDelivery.Persistence.Connections;
using StatementDelivery.Persistence.Leasing;

namespace Generation.Worker;

/// <summary>
/// The outbox relay: reads unpublished events, publishes them, marks them published.
/// Lease-gated; polls every 500 milliseconds.
/// </summary>
/// <remarks>
/// <para>
/// THE TRANSPORT IS A LOGGING SINK, AND THAT IS DELIBERATE, NOT UNFINISHED. The outbox pattern's
/// hard part - atomicity with the business write, at-least-once delivery, ordered draining,
/// observable lag - is all here and all real. The easy part, the final send, is one line behind
/// TODO(transport): nothing in this system consumes the events yet (docs/LIMITATIONS.md, "Simulated in local development"), and standing up a broker
/// nothing reads would be infrastructure theatre. When a consumer arrives, the sink swaps for a
/// producer without touching the pattern. See ADR-0026.
/// </para>
/// <para>
/// POLLING INTERVAL: 500ms is adequate below roughly 10,000 events/second (500 rows per tick =
/// a drain ceiling far above the write rate, with sub-second latency). Above that, log-based CDC
/// - Debezium - earns its place, at the cost of running connector state and schema evolution as
/// production infrastructure. That trade is recorded where it belongs, in the ADR.
/// </para>
/// </remarks>
public sealed partial class OutboxRelayService : BackgroundService
{
    private const string LeaseName = "outbox-relay";

    // Explicit columns, never SELECT * (the brief's sketch used SELECT *; the query-discipline
    // architecture test forbids it, and it is wrong here anyway - payload is the only wide
    // column and the relay needs every row's copy of it exactly once).
    //
    // FOR UPDATE SKIP LOCKED for the same reason as the claim query: a second relay instance
    // during lease handover skips rather than blocks, and no event is published twice by two
    // relays holding the same rows. (At-least-once still applies across crashes: a relay that
    // dies between publish and UPDATE re-publishes on the next tick. Consumers dedup on id.)
    private const string ClaimBatchSql = """
        SELECT id, created_at AS CreatedAt, event_type AS EventType,
               payload::text AS payload, trace_parent AS TraceParent
          FROM outbox
         WHERE published_at IS NULL
         ORDER BY created_at, id
           FOR UPDATE SKIP LOCKED
         LIMIT @batchSize;
        """;

    private const string MarkPublishedSql = """
        UPDATE outbox
           SET published_at = now(), attempt_count = attempt_count + 1
         WHERE id = ANY(@ids) AND published_at IS NULL;
        """;

    private const string OldestUnpublishedSql = """
        SELECT EXTRACT(EPOCH FROM (now() - min(created_at)))::bigint
          FROM outbox
         WHERE published_at IS NULL;
        """;

    private readonly ILeaseManager _leases;
    private readonly IDbConnectionFactory _connections;
    private readonly GenerationMetrics _metrics;
    private readonly GenerationWorkerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<OutboxRelayService> _logger;

    /// <summary>Initialises a new instance of the <see cref="OutboxRelayService"/> class.</summary>
    /// <param name="leases">Lease manager.</param>
    /// <param name="connections">Connection factory.</param>
    /// <param name="metrics">Batch metrics.</param>
    /// <param name="options">Worker options.</param>
    /// <param name="time">Time source.</param>
    /// <param name="logger">Logger - currently also the transport.</param>
    public OutboxRelayService(
        ILeaseManager leases,
        IDbConnectionFactory connections,
        GenerationMetrics metrics,
        IOptions<GenerationWorkerOptions> options,
        TimeProvider time,
        ILogger<OutboxRelayService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _leases = leases;
        _connections = connections;
        _metrics = metrics;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ILeaseHandle? lease = await _leases.TryAcquireAsync(LeaseName, stoppingToken).ConfigureAwait(false);

                if (lease is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _time, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await using (lease.ConfigureAwait(false))
                {
                    using CancellationTokenSource linked =
                        CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.LeaseLost);

                    while (!linked.Token.IsCancellationRequested)
                    {
                        int published = await RelayOnceAsync(linked.Token).ConfigureAwait(false);
                        await ObserveLagAsync(linked.Token).ConfigureAwait(false);

                        // Drain hard while there is a backlog; breathe when there is not.
                        if (published < _options.RelayBatchSize)
                        {
                            await Task.Delay(_options.RelayInterval, _time, linked.Token).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // Lease lost; rejoin the standby pool.
            }
            catch (Exception ex)
            {
                LogRelayError(_logger, ex);
                await Task.Delay(TimeSpan.FromSeconds(5), _time, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<int> RelayOnceAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.Write, cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        List<OutboxRow> batch = [.. await connection.QueryAsync<OutboxRow>(new CommandDefinition(
            ClaimBatchSql,
            new { batchSize = _options.RelayBatchSize },
            transaction: transaction,
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
            cancellationToken: cancellationToken)).ConfigureAwait(false)];

        if (batch.Count == 0)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return 0;
        }

        foreach (OutboxRow row in batch)
        {
            // TODO(transport): the send (docs/LIMITATIONS.md, "Next" item 5). Today the sink is the structured log - the pattern is
            // proven, the transport swaps in behind this one line when a consumer exists.
            LogEventPublished(_logger, row.EventType, row.Id, row.TraceParent);
        }

        _ = await connection.ExecuteAsync(new CommandDefinition(
            MarkPublishedSql,
            new { ids = batch.Select(static r => r.Id).ToArray() },
            transaction: transaction,
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return batch.Count;
    }

    private async Task ObserveLagAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

        long? oldest = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            OldestUnpublishedSql,
            commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        _metrics.OutboxAge(oldest ?? 0);
    }

    private sealed record OutboxRow
    {
        public Guid Id { get; init; }

        public DateTime CreatedAt { get; init; }

        public string EventType { get; init; } = string.Empty;

        public string Payload { get; init; } = string.Empty;

        public string? TraceParent { get; init; }
    }

    [LoggerMessage(
        EventId = 5040,
        Level = LogLevel.Information,
        Message = "outbox publish: {EventType} {EventId} (traceparent: {TraceParent})")]
    private static partial void LogEventPublished(
        ILogger logger, string eventType, Guid eventId, string? traceParent);

    [LoggerMessage(EventId = 5041, Level = LogLevel.Error, Message = "Outbox relay error; retrying")]
    private static partial void LogRelayError(ILogger logger, Exception exception);
}
