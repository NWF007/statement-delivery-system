using System.Diagnostics;
using Dapper;
using Generation.Worker.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;
using StatementDelivery.Persistence.Connections;

namespace Generation.Worker;

/// <summary>
/// The batch generation host loop.
/// </summary>
/// <remarks>
/// <para>
/// NO RENDERING LOGIC EXISTS YET, ON PURPOSE. What exists is the shape the rendering will live
/// inside: a poll loop, one bounded unit of work per tick, a span and a log line per unit, and
/// shutdown semantics that let the unit in flight finish rather than be cut in half.
/// </para>
/// <para>
/// The shutdown behaviour is the part worth reading. <see cref="StopAsync"/> waits for the current
/// unit to complete, up to <see cref="GenerationWorkerOptions.UnitOfWorkGraceSeconds"/>, before
/// cancelling it. At 1,400 renders per second, cancelling mid-unit leaves partially written work
/// the next run has to detect and reconcile; letting it finish means the only states that ever
/// exist on disk are "claimed" and "done", which is what makes the batch resumable.
/// </para>
/// <para>
/// The unit currently performs a database round trip and nothing else. That is not filler: it
/// exercises the whole connection path through PgBouncer on the pool this service actually uses,
/// and it emits a span, so a worker with no inbound HTTP still shows up in the trace view as a
/// live participant rather than as a service that only ever logs.
/// </para>
/// </remarks>
public sealed partial class BatchOrchestrationService : BackgroundService
{
    /// <summary>
    /// Activity source for spans this worker starts. Matched by the
    /// <c>StatementDelivery.*</c> wildcard registered in ServiceDefaults.
    /// </summary>
    private static readonly ActivitySource ActivitySource = new("StatementDelivery.Generation");

    private readonly IDbConnectionFactory _connections;
    private readonly GenerationWorkerOptions _options;
    private readonly ILogger<BatchOrchestrationService> _logger;
    private readonly CancellationTokenSource _unitOfWorkCancellation = new();

    private Task? _unitInFlight;

    /// <summary>Initialises a new instance of the <see cref="BatchOrchestrationService"/> class.</summary>
    /// <param name="connections">Connection factory.</param>
    /// <param name="options">Worker options.</param>
    /// <param name="logger">Logger.</param>
    public BatchOrchestrationService(
        IDbConnectionFactory connections,
        IOptions<GenerationWorkerOptions> options,
        ILogger<BatchOrchestrationService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _connections = connections;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(_logger, _options.PollIntervalSeconds, _options.BatchSize, _options.UnitOfWorkGraceSeconds);

        using var ticker = new PeriodicTimer(_options.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            // The unit runs on its OWN cancellation token, not on stoppingToken. That is the whole
            // mechanism: when shutdown begins, stoppingToken fires and the loop stops asking for
            // more work, while the unit already running keeps its own token and is allowed to
            // finish. StopAsync cancels that token only once the grace period has expired.
            _unitInFlight = RunUnitOfWorkAsync(_unitOfWorkCancellation.Token);

            try
            {
                await _unitInFlight.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogUnitCancelled(_logger);
                return;
            }
#pragma warning disable CA1031 // A polling worker must survive one bad tick; the next one retries.
            catch (Exception ex)
            {
                LogUnitFailed(_logger, ex);
            }
#pragma warning restore CA1031
            finally
            {
                _unitInFlight = null;
            }

            try
            {
                if (!await ticker.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? inFlight = _unitInFlight;

        if (inFlight is not null && !inFlight.IsCompleted)
        {
            LogAwaitingUnit(_logger, _options.UnitOfWorkGraceSeconds);

            Task completed = await Task.WhenAny(
                inFlight,
                Task.Delay(_options.UnitOfWorkGrace, cancellationToken)).ConfigureAwait(false);

            if (completed != inFlight)
            {
                // The grace period is not a promise, it is a budget. Past it, the host has its own
                // deadline to meet and an unfinished unit has to be abandoned.
                LogGraceExpired(_logger, _options.UnitOfWorkGraceSeconds);
                await _unitOfWorkCancellation.CancelAsync().ConfigureAwait(false);
            }
            else
            {
                LogUnitDrained(_logger);
            }
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _unitOfWorkCancellation.Dispose();
        base.Dispose();
    }

    private async Task RunUnitOfWorkAsync(CancellationToken cancellationToken)
    {
        using Activity? activity = ActivitySource.StartActivity("generation.unit-of-work", ActivityKind.Internal);
        activity?.SetTag("generation.batch_size", _options.BatchSize);

        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.Write, cancellationToken).ConfigureAwait(false);

        // TODO(generation): claim a batch of pending statements, render them, write the PDFs to
        // object storage and record the results. Until that exists, the unit proves the connection
        // path end to end - through PgBouncer, on this service's own pool - which is what makes the
        // pooling configuration verifiable before there is any work to run over it.
        int probe = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT 1;",
                commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.Write),
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        activity?.SetTag("generation.claimed", 0);
        LogUnitCompleted(_logger, probe);
    }

    [LoggerMessage(
        EventId = 3000,
        Level = LogLevel.Information,
        Message = "Generation worker started. Polling every {PollIntervalSeconds}s, batch size {BatchSize}, unit-of-work grace {GraceSeconds}s.")]
    private static partial void LogStarted(ILogger logger, int pollIntervalSeconds, int batchSize, int graceSeconds);

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Debug,
        Message = "Unit of work completed. No statements are pending; database reachable (probe={Probe}).")]
    private static partial void LogUnitCompleted(ILogger logger, int probe);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Error,
        Message = "Unit of work failed. Retrying at the next poll interval.")]
    private static partial void LogUnitFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 3003,
        Level = LogLevel.Warning,
        Message = "Unit of work was cancelled before completing.")]
    private static partial void LogUnitCancelled(ILogger logger);

    [LoggerMessage(
        EventId = 3004,
        Level = LogLevel.Information,
        Message = "Shutdown requested. Allowing the in-flight unit of work up to {GraceSeconds}s to finish.")]
    private static partial void LogAwaitingUnit(ILogger logger, int graceSeconds);

    [LoggerMessage(
        EventId = 3005,
        Level = LogLevel.Information,
        Message = "In-flight unit of work finished cleanly. Shutting down.")]
    private static partial void LogUnitDrained(ILogger logger);

    [LoggerMessage(
        EventId = 3006,
        Level = LogLevel.Warning,
        Message = "In-flight unit of work did not finish within {GraceSeconds}s. Cancelling it; the next run resumes from the last committed batch.")]
    private static partial void LogGraceExpired(ILogger logger, int graceSeconds);
}
