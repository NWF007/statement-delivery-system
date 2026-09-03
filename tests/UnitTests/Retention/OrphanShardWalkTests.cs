using Retention.Worker;
using Shouldly;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Domain.Statements;
using Xunit;

namespace UnitTests.Retention;

/// <summary>
/// The sweep's prefix set held against the key scheme — the test that fails if either changes
/// alone.
/// </summary>
/// <remarks>
/// <c>ShardFor</c> produces THREE hex characters (4096 shards) and the
/// sweep walked TWO-hex prefixes, so <c>statements/00/</c> never prefix-matched
/// <c>statements/00f/…</c> and the sweep listed zero objects forever while its metric read a
/// confident clean. A constant duplicated in two files is how it happened; these tests make the
/// two agree or fail.
/// </remarks>
public static class OrphanShardWalkTests
{
    [Fact]
    public static void OrphanSweep_PrefixSet_MatchesStorageKeyScheme()
    {
        var prefixes = OrphanShardWalk.AllPrefixes().ToHashSet(StringComparer.Ordinal);

        prefixes.Count.ShouldBe(
            StorageKeyScheme.ShardCount,
            "every shard the scheme can produce must be walked exactly once per cycle");

        // Real shard values from the real hash, not synthetic ones: every key the writer can
        // produce must fall under a prefix the sweep will visit.
        for (int i = 0; i < 256; i++)
        {
            var statement = new StatementId(Guid.CreateVersion7());
            string shard = StorageKeyScheme.ShardFor(statement);

            prefixes.ShouldContain(
                $"statements/{shard}/",
                $"shard '{shard}' (from {statement.Value:D}) would never be listed - the sweep would be blind to that object forever");
        }
    }

    [Fact]
    public static void StorageKeyScheme_ShardWidth_IsPinned()
    {
        // Changing either value silently remaps which prefix an object lives under and desyncs
        // every consumer that iterates shards. This pin turns that into a deliberate red build.
        StorageKeyScheme.ShardCount.ShouldBe(4096);
        StorageKeyScheme.ShardWidth.ShouldBe(3);
        StorageKeyScheme.ShardFor(new StatementId(Guid.CreateVersion7()))
            .Length.ShouldBe(StorageKeyScheme.ShardWidth);
    }
}
