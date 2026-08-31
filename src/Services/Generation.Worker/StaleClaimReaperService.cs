using Generation.Worker.Configuration;
using Microsoft.Extensions.Options;
using StatementDelivery.Persistence.Leasing;
using StatementDelivery.Persistence.Runs;

namespace Generation.Worker;

/// <summary>
/// Returns stale RENDERING claims to the queue. Lease-gated; runs every sixty seconds.
/// </summary>
/// <remarks>
/// <para>
/// The graceful-shutdown path releases claims politely - but a pod can be SIGKILLed, a node can
/// vanish, and neither runs a line of cleanup. This is the belt to that braces: anything sitting
/// in RENDERING longer than the stale threshold is presumed orphaned and re-queued.
/// </para>
/// <para>
/// attempts IS DELIBERATELY UNTOUCHED, and that is the entire design. It was incremented at
/// CLAIM time - so an item whose worker keeps dying arrives back here at attempt 2, then 3, and
/// then the claim query's <c>attempts &lt; max</c> quarantines it. Increment-on-completion would
/// reset that march to nowhere: a crash never reaches completion code, the count never moves,
/// and one poison item cycles through worker corpses forever. See ADR-0027.
/// </para>
/// </remarks>
public sealed partial class StaleClaimReaperService : BackgroundService
{
    private const string LeaseName = "generation-reaper";

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly ILeaseManager _leases;
    private readonly IStatementRunRepository _runs;
    private readonly GenerationMetrics _metrics;
    private readonly GenerationWorkerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<StaleClaimReaperService> _logger;

    /// <summary>Initialises a new instance of the <see cref="StaleClaimReaperService"/> class.</summary>
    /// <param name="leases">Lease manager.</param>
    /// <param name="runs">The run store.</param>
    /// <param name="metrics">Batch metrics.</param>
    /// <param name="options">Worker options.</param>
    /// <param name="time">Time source.</param>
    /// <param name="logger">Logger.</param>
    public StaleClaimReaperService(
        ILeaseManager leases,
        IStatementRunRepository runs,
        GenerationMetrics metrics,
        IOptions<GenerationWorkerOptions> options,
        TimeProvider time,
        ILogger<StaleClaimReaperService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _leases = leases;
        _runs = runs;
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

                if (lease is not null)
                {
                    await using (lease.ConfigureAwait(false))
                    {
                        using CancellationTokenSource linked =
                            CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.LeaseLost);

                        while (!linked.Token.IsCancellationRequested)
                        {
                            int reaped = await _runs
                                .ReapStaleClaimsAsync(_options.StaleClaimAfter, linked.Token)
                                .ConfigureAwait(false);

                            if (reaped > 0)
                            {
                                _metrics.ItemsReaped(reaped);

                                // A steady non-zero rate here means workers are DYING - worth an
                                // alert of its own, independent of run progress, because a run
                                // limping to completion over worker corpses still completes.
                                LogReaped(_logger, reaped);
                            }

                            await Task.Delay(Interval, _time, linked.Token).ConfigureAwait(false);
                        }
                    }
                }
                else
                {
                    await Task.Delay(Interval, _time, stoppingToken).ConfigureAwait(false);
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
                LogReaperError(_logger, ex);
                await Task.Delay(Interval, _time, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(
        EventId = 5030,
        Level = LogLevel.Warning,
        Message = "Reaped {Count} stale claims back to QUEUED (attempts preserved). Workers are dying somewhere.")]
    private static partial void LogReaped(ILogger logger, int count);

    [LoggerMessage(EventId = 5031, Level = LogLevel.Error, Message = "Reaper error; retrying")]
    private static partial void LogReaperError(ILogger logger, Exception exception);
}
