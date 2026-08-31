using System.Diagnostics;
using System.Threading.Channels;
using Generation.Worker.Configuration;
using Generation.Worker.Ledger;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using StatementDelivery.Domain.ValueObjects;
using StatementDelivery.Persistence.Runs;
using StatementDelivery.ServiceDefaults;

namespace Generation.Worker;

/// <summary>
/// The render loop: claim, fan out, render. Runs on EVERY replica, always.
/// </summary>
/// <remarks>
/// <para>
/// Claimer and renderers meet at a BOUNDED channel (<see cref="BoundedChannelFullMode.Wait"/>).
/// When renderers fall behind, the claimer blocks on the write - which is backpressure working,
/// not a bug: work the replica cannot process yet stays in the DATABASE queue where any replica
/// can take it, instead of accumulating invisibly in this replica's memory.
/// </para>
/// <para>
/// SHUTDOWN (Part E4): claiming stops immediately; in-flight renders get the configured grace;
/// whatever is still sitting unprocessed in the channel is drained WITHOUT processing and
/// released back to QUEUED - attempts stay burned, because the claim happened. A SIGKILLed pod
/// runs none of this, which is what the reaper is for.
/// </para>
/// </remarks>
public sealed partial class RenderWorkerService : BackgroundService
{
    private static readonly ActivitySource ActivitySource = new("StatementDelivery.Generation");

    private readonly IStatementRunRepository _runs;
    private readonly RenderPipeline _pipeline;
    private readonly GenerationMetrics _metrics;
    private readonly CircuitBreakerStateProvider _ledgerCircuit;
    private readonly GenerationWorkerOptions _options;
    private readonly ServiceIdentity _identity;
    private readonly TimeProvider _time;
    private readonly ILogger<RenderWorkerService> _logger;

    /// <summary>Initialises a new instance of the <see cref="RenderWorkerService"/> class.</summary>
    /// <param name="runs">The run queue.</param>
    /// <param name="pipeline">The per-item pipeline.</param>
    /// <param name="metrics">Batch metrics.</param>
    /// <param name="ledgerCircuit">The ledger breaker's state.</param>
    /// <param name="options">Worker options.</param>
    /// <param name="identity">This replica's identity - the claimed_by value.</param>
    /// <param name="time">Time source.</param>
    /// <param name="logger">Logger.</param>
    public RenderWorkerService(
        IStatementRunRepository runs,
        RenderPipeline pipeline,
        GenerationMetrics metrics,
        CircuitBreakerStateProvider ledgerCircuit,
        IOptions<GenerationWorkerOptions> options,
        ServiceIdentity identity,
        TimeProvider time,
        ILogger<RenderWorkerService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _runs = runs;
        _pipeline = pipeline;
        _metrics = metrics;
        _ledgerCircuit = ledgerCircuit;
        _options = options.Value;
        _identity = identity;
        _time = time;
        _logger = logger;
        Volatile.Write(ref s_graceTicks, _options.ShutdownGrace.Ticks);
    }

    /// <summary>Shutdown grace in ticks, readable from the static token-registration callback.</summary>
    private static long s_graceTicks = TimeSpan.FromSeconds(20).Ticks;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string workerId = _identity.InstanceId;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                StatementRun? run = (await _runs.ListActiveAsync(stoppingToken).ConfigureAwait(false))
                    .FirstOrDefault(static r => r.Status == RunStatus.Running);

                if (run is null)
                {
                    await Task.Delay(_options.PollInterval, _time, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await ServeRunAsync(run, workerId, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The loop must survive anything - a dead render loop on a healthy replica is
                // capacity silently missing at month-end. Log, breathe, retry.
                LogLoopError(_logger, ex);
                await Task.Delay(_options.PollInterval, _time, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ServeRunAsync(StatementRun run, string workerId, CancellationToken stoppingToken)
    {
        StatementPeriod period = StatementPeriod.Create(run.PeriodStart, run.PeriodEnd);

        Channel<ClaimedItem> channel = Channel.CreateBounded<ClaimedItem>(new BoundedChannelOptions(_options.ChannelCapacity)
        {
            // Wait, never DropX: dropping a claimed item strands it in RENDERING until the
            // reaper; waiting is the backpressure this design wants.
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = false,
        });

        Task claimer = ClaimLoopAsync(run.Id, workerId, channel.Writer, stoppingToken);

        Task[] renderers =
        [
            .. Enumerable.Range(0, _options.RenderParallelism)
                .Select(_ => RenderLoopAsync(run.Id, period, workerId, channel.Reader, stoppingToken)),
        ];

        await claimer.ConfigureAwait(false);
        await Task.WhenAll(renderers).ConfigureAwait(false);

        // Shutdown drain (E4): anything the renderers left in the channel was claimed but never
        // started. Give it back - scoped to OUR claims, attempts intact.
        if (stoppingToken.IsCancellationRequested)
        {
            var unstarted = new List<long>();
            while (channel.Reader.TryRead(out ClaimedItem? leftover))
            {
                unstarted.Add(leftover.ItemId);
            }

            if (unstarted.Count > 0)
            {
                int released = await _runs
                    .ReleaseClaimsAsync(unstarted, workerId, CancellationToken.None).ConfigureAwait(false);
                LogClaimsReleased(_logger, released);
            }
        }
    }

    private async Task ClaimLoopAsync(
        Guid runId, string workerId, ChannelWriter<ClaimedItem> writer, CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // LOCAL BACKPRESSURE ON A BROKEN LEDGER: while the breaker is open every claim
                // would burn an attempt on an immediate failure - three ticks of that and the
                // whole queue is quarantined by a ledger outage. Stop claiming; the orchestrator
                // flips the run to PAUSED for the operator's benefit; claims resume when the
                // breaker closes. See ADR-0030.
                if (_ledgerCircuit.CircuitState != CircuitState.Closed)
                {
                    await Task.Delay(_options.PollInterval, _time, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                IReadOnlyList<ClaimedItem> batch = await _runs.ClaimBatchAsync(
                    runId, workerId, _options.ClaimBatchSize, _options.MaxAttempts, stoppingToken)
                    .ConfigureAwait(false);

                if (batch.Count == 0)
                {
                    // Drained - or everything left is quarantined or claimed elsewhere. The
                    // orchestrator decides whether the run is COMPLETE; this replica just rests.
                    await Task.Delay(_options.PollInterval, _time, stoppingToken).ConfigureAwait(false);

                    // Re-check the run still wants us before claiming again.
                    StatementRun? current = await _runs.FindAsync(runId, stoppingToken).ConfigureAwait(false);
                    if (current is null || current.Status != RunStatus.Running)
                    {
                        return;
                    }

                    continue;
                }

                foreach (ClaimedItem item in batch)
                {
                    await writer.WriteAsync(item, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown: stop claiming immediately. Fall through to complete the writer.
        }
        finally
        {
            writer.Complete();
        }
    }

    private async Task RenderLoopAsync(
        Guid runId, StatementPeriod period, string workerId, ChannelReader<ClaimedItem> reader, CancellationToken stoppingToken)
    {
        // In-flight renders get the grace window on shutdown rather than the axe: cancelling a
        // render mid-encrypt wastes everything it already did, and the grace is bounded well
        // inside the host's shutdown budget.
        await foreach (ClaimedItem item in ReadUntilStoppedAsync(reader, stoppingToken).ConfigureAwait(false))
        {
            // NOT linked directly to stoppingToken - that would axe an in-flight render the
            // instant SIGTERM lands, which is the opposite of E4. Instead the render's token
            // fires GRACE after shutdown begins: an item mid-flight when SIGTERM arrives gets
            // the budget to finish; one starting after shutdown began gets the remaining budget.
            using var grace = new CancellationTokenSource();
            using CancellationTokenRegistration onStop = stoppingToken.Register(
                static (state, _) => ((CancellationTokenSource)state!).CancelAfter(
                    TimeSpan.FromTicks(Volatile.Read(ref s_graceTicks))),
                grace);

            if (stoppingToken.IsCancellationRequested)
            {
                grace.CancelAfter(_options.ShutdownGrace);
            }

            // Restore the PLANNING trace so this render joins the run's trace across the queue
            // boundary - one traceable story from POST /statement-runs to the stored object.
            ActivityContext parent = default;
            if (item.TraceParent is not null)
            {
                _ = ActivityContext.TryParse(item.TraceParent, null, out parent);
            }

            using Activity? activity = ActivitySource.StartActivity(
                "generation.render-item", ActivityKind.Consumer, parent);
            _ = activity?.SetTag("generation.item_id", item.ItemId);
            _ = activity?.SetTag("generation.attempt", item.Attempts);

            try
            {
                await _pipeline.ProcessAsync(runId, period, item, workerId, grace.Token).ConfigureAwait(false);
            }
            catch (StaleClaimSupersededException)
            {
                // Not an item failure: a successor owns the item and is rendering it. Counted on
                // its own metric - a non-zero rate means the stale window undercuts real render
                // times - and NOT written to the item, whose bookkeeping belongs to the successor.
                _metrics.StaleCompletion();
                LogStaleCompletion(_logger, item.ItemId);
            }
            catch (Exception ex)
            {
                _ = activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
                _metrics.ItemFailed(ex.GetType().Name);

                // Message and TYPE only - never a stack trace, never content (Part G1).
                string reason = $"{ex.GetType().Name}: {ex.Message}";
                int recorded = await _runs
                    .FailItemAsync(item.ItemId, reason, workerId, CancellationToken.None).ConfigureAwait(false);

                if (recorded == 0)
                {
                    // Reaped mid-render AND the render failed: the successor owns the item, so
                    // there is no row of ours to mark. Same tuning signal as the success variant.
                    _metrics.StaleCompletion();
                    LogStaleCompletion(_logger, item.ItemId);
                }

                LogItemFailed(_logger, item.ItemId, item.Attempts, ex.GetType().Name);
            }
        }
    }

    private static async IAsyncEnumerable<ClaimedItem> ReadUntilStoppedAsync(
        ChannelReader<ClaimedItem> reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken stoppingToken)
    {
        // Reads until the CHANNEL completes or shutdown begins - not ReadAllAsync(stoppingToken),
        // which would throw and skip the drain path in ServeRunAsync.
        while (!stoppingToken.IsCancellationRequested)
        {
            ClaimedItem item;
            try
            {
                if (!await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
                {
                    yield break;
                }

                if (!reader.TryRead(out item!))
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                yield break;
            }

            yield return item;
        }
    }

    [LoggerMessage(EventId = 5010, Level = LogLevel.Error, Message = "Render loop error; retrying")]
    private static partial void LogLoopError(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 5011,
        Level = LogLevel.Warning,
        Message = "Item {ItemId} failed on attempt {Attempt}: {ExceptionType}")]
    private static partial void LogItemFailed(ILogger logger, long itemId, int attempt, string exceptionType);

    [LoggerMessage(
        EventId = 5013,
        Level = LogLevel.Warning,
        Message = "Item {ItemId}: render outlived its claim (reaped and reclaimed). If this recurs, Generation:StaleClaimMinutes is shorter than real render times.")]
    private static partial void LogStaleCompletion(ILogger logger, long itemId);

    [LoggerMessage(
        EventId = 5012,
        Level = LogLevel.Information,
        Message = "Shutdown: released {Count} unstarted claims back to the queue")]
    private static partial void LogClaimsReleased(ILogger logger, int count);
}
