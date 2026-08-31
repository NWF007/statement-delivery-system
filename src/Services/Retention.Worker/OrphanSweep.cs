using System.Globalization;
using Microsoft.Extensions.Options;
using Retention.Worker.Configuration;
using StatementDelivery.Persistence.Retention;
using StatementDelivery.ServiceDefaults.Storage;

namespace Retention.Worker;

/// <summary>The weekly orphan sweep: report objects nothing accounts for. Never deletes.</summary>
/// <remarks>
/// <para>
/// REPORT-ONLY, PERMANENTLY (ADR-0039), for two reasons: objects under a Compliance lock cannot
/// be deleted anyway, and automatic deletion driven by an inventory comparison is precisely the
/// kind of job that destroys real data when the comparison logic has a bug.
/// </para>
/// <para>
/// NEVER THE WHOLE BUCKET IN ONE PASS. At 2.5 billion objects a full enumeration is S3
/// Inventory's job in production (a daily manifest, diffed offline); locally the sweep walks
/// the scheme's shard prefixes (<c>statements/000/</c> … <c>statements/fff/</c> - all
/// <see cref="StatementDelivery.Domain.Statements.StorageKeyScheme.ShardCount"/> of them,
/// derived, never a local literal) a bounded number of pages per tick, cursor persisted so the
/// next tick RESUMES rather than restarts. THE COST IS REAL: one full cycle is 4,096 LIST
/// calls at minimum - fine against local MinIO, and exactly why production uses Inventory
/// (docs/SCALE.md). The walk stays resumable so a long cycle can be interrupted anywhere.
/// </para>
/// </remarks>
public sealed partial class OrphanSweep
{
    private readonly OrphanSweepRepository _repository;
    private readonly IStatementObjectAdmin _objects;
    private readonly RetentionMetrics _metrics;
    private readonly RetentionWorkerOptions _options;
    private readonly ILogger<OrphanSweep> _logger;

    /// <summary>Initialises a new instance of the <see cref="OrphanSweep"/> class.</summary>
    /// <param name="repository">Cursor, accounting and report rows.</param>
    /// <param name="objects">The object store's listing surface.</param>
    /// <param name="metrics">Metrics.</param>
    /// <param name="options">Worker options.</param>
    /// <param name="logger">Logger.</param>
    public OrphanSweep(
        OrphanSweepRepository repository,
        IStatementObjectAdmin objects,
        RetentionMetrics metrics,
        IOptions<RetentionWorkerOptions> options,
        ILogger<OrphanSweep> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _repository = repository;
        _objects = objects;
        _metrics = metrics;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Walks a bounded number of listing pages, resuming from the saved cursor.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many orphans were reported this tick.</returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        OrphanSweepCursor cursor = await _repository.ReadCursorAsync(cancellationToken).ConfigureAwait(false);
        string shard = OrphanShardWalk.Resume(cursor.ShardPrefix);
        string? token = cursor.ContinuationToken;

        int orphans = 0;
        for (int page = 0; page < _options.OrphanPagesPerTick; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string prefix = OrphanShardWalk.PrefixFor(shard);
            ObjectKeyPage listing = await _objects.ListKeysAsync(
                prefix, token, _options.OrphanPageSize, cancellationToken).ConfigureAwait(false);

            foreach (StoredObjectKey key in listing.Keys)
            {
                StorageKeyAccounting accounting = await _repository.AccountForKeyAsync(key.Key, cancellationToken)
                    .ConfigureAwait(false);

                switch (accounting)
                {
                    case StorageKeyAccounting.Referenced:
                    case StorageKeyAccounting.TombstonedErased:
                        // A row points at it, or it is a lawful erasure remnant. Not findings.
                        break;

                    case StorageKeyAccounting.TombstonedPurged:
                        // The purge deleted this object's versions and tombstoned the key - yet
                        // here it is. Either the delete silently failed or something re-created
                        // the key. Report it: unaccounted bytes are unaccounted bytes.
                        await ReportAsync(key, "NO_ROW", cancellationToken).ConfigureAwait(false);
                        orphans++;
                        break;

                    case StorageKeyAccounting.Unaccounted:
                        await ReportAsync(key, "NO_ROW", cancellationToken).ConfigureAwait(false);
                        orphans++;
                        break;

                    default:
                        throw new InvalidOperationException($"Unhandled accounting {accounting}.");
                }
            }

            if (listing.NextToken is not null)
            {
                token = listing.NextToken;
            }
            else
            {
                // Shard exhausted: advance, wrapping at the end of the cycle. The arithmetic
                // lives in OrphanShardWalk, derived from StorageKeyScheme - never a local
                // literal (audit HIGH 2's cause).
                shard = OrphanShardWalk.Next(shard);
                token = null;
            }
        }

        await _repository.SaveCursorAsync(new OrphanSweepCursor(shard, token), cancellationToken)
            .ConfigureAwait(false);

        if (orphans > 0)
        {
            LogOrphansFound(_logger, orphans, shard);
        }

        return orphans;
    }

    private async Task ReportAsync(StoredObjectKey key, string reason, CancellationToken ct)
    {
        await _repository.ReportAsync(key.Key, key.SizeBytes, reason, ct).ConfigureAwait(false);
        _metrics.Orphan(key.SizeBytes);
    }

    [LoggerMessage(
        EventId = 4040,
        Level = LogLevel.Warning,
        Message = "Orphan sweep reported {Count} unaccounted objects (cursor now at shard {Shard}). Report-only by design - see orphan_report and ADR-0039.")]
    private static partial void LogOrphansFound(ILogger logger, int count, string shard);
}
