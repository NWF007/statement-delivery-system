using System.Globalization;
using StatementDelivery.Domain.Statements;

namespace Retention.Worker;

/// <summary>The orphan sweep's shard iteration, extracted so a test can hold it against the key scheme.</summary>
/// <remarks>
/// Every value here DERIVES from <see cref="StorageKeyScheme"/> - count, width, format. The
/// original version carried its own literals (256, "x2") beside the scheme's (4096, three hex
/// chars), and the sweep spent its life listing prefixes no writer ever produced - it matched
/// nothing and reported nothing, which is indistinguishable from a clean bucket.
/// <c>OrphanSweep_PrefixSet_MatchesStorageKeyScheme</c> fails if the two ever disagree again.
/// </remarks>
public static class OrphanShardWalk
{
    // Declared BEFORE FirstShard: static initializers run top to bottom, and FirstShard
    // consumes this. (The first draft had them swapped and produced a one-prefix "cycle".)
    private static readonly string ShardFormat =
        string.Create(CultureInfo.InvariantCulture, $"x{StorageKeyScheme.ShardWidth}");

    /// <summary>The first shard prefix of a cycle.</summary>
    public static readonly string FirstShard = 0.ToString(ShardFormat, CultureInfo.InvariantCulture);

    /// <summary>Advances to the next shard, wrapping at the end of the cycle.</summary>
    /// <param name="shard">The current shard, or a stale cursor of the WRONG width - a leftover
    /// from before the width was derived - which resets to the first shard rather than throwing
    /// the sweep into a parse loop.</param>
    /// <returns>The next shard prefix.</returns>
    public static string Next(string shard)
    {
        if (shard.Length != StorageKeyScheme.ShardWidth
            || !int.TryParse(shard, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int current))
        {
            return FirstShard;
        }

        return ((current + 1) % StorageKeyScheme.ShardCount).ToString(ShardFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>Normalises a saved cursor shard: wrong width (a pre-fix leftover) restarts the cycle.</summary>
    /// <param name="saved">The persisted shard, or null before the first sweep.</param>
    /// <returns>A valid shard to resume from.</returns>
    public static string Resume(string? saved) =>
        saved is not null && saved.Length == StorageKeyScheme.ShardWidth ? saved : FirstShard;

    /// <summary>The listing prefix for one shard.</summary>
    /// <param name="shard">The shard.</param>
    /// <returns>The S3 key prefix.</returns>
    public static string PrefixFor(string shard) =>
        string.Create(CultureInfo.InvariantCulture, $"statements/{shard}/");

    /// <summary>Every prefix of one full cycle, in walk order.</summary>
    /// <returns>The prefixes.</returns>
    public static IEnumerable<string> AllPrefixes()
    {
        string shard = FirstShard;
        do
        {
            yield return PrefixFor(shard);
            shard = Next(shard);
        }
        while (!string.Equals(shard, FirstShard, StringComparison.Ordinal));
    }
}
