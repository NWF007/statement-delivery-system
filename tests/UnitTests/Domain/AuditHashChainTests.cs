using System.Globalization;
using System.Text;
using Shouldly;
using StatementDelivery.Domain.Auditing;
using StatementDelivery.Domain.Exceptions;
using StatementDelivery.Domain.Identifiers;
using Xunit;

namespace UnitTests.Domain;

/// <summary>
/// The audit hash chain, exercised entirely in memory.
/// </summary>
/// <remarks>
/// <para>
/// No database here on purpose. These tests are about the HASH DEFINITION - what is covered, in
/// what order, and whether a change to any of it is detectable. Running them against PostgreSQL
/// would test the same property far more slowly while adding a way for them to fail for reasons
/// that have nothing to do with hashing.
/// </para>
/// <para>
/// "Property-based" here means many generated cases from a FIXED seed rather than a property
/// framework: the repository does not carry FsCheck, and a seeded generator gives the same
/// coverage with reproducible failures. A failing case prints its seed and index.
/// </para>
/// </remarks>
public sealed class AuditHashChainTests
{
    private const short ChainId = 3;
    private const int Seed = 20260827;

    /// <summary>A link in an in-memory chain: the entry plus its computed position and hashes.</summary>
    private sealed record Link(short ChainId, long Seq, AuditEntry Entry, byte[] PreviousHash, byte[] Hash);

    private static AuditEntry SampleEntry(int index, Random random)
    {
        var context = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["index"] = index,
            ["ip_family"] = random.Next(2) == 0 ? "v4" : "v6",
            ["page_size"] = random.Next(1, 100),
        };

        return new AuditEntry(
            new AuditEventId(Guid.CreateVersion7()),
            new StatementId(Guid.CreateVersion7()),
            new CustomerId(Guid.CreateVersion7()),
            ActorType.Customer,
            "customer-" + index.ToString(CultureInfo.InvariantCulture),
            AuditAction.StatementListViewed,
            AuditOutcome.Success,
            null,
            "198.51.100." + random.Next(1, 254).ToString(CultureInfo.InvariantCulture),
            Convert.ToHexStringLower(SHA256Of("agent" + index)),
            context,
            new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.Zero).AddSeconds(index));
    }

    private static byte[] SHA256Of(string value) =>
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value));

    /// <summary>Builds a valid chain of <paramref name="length"/> links.</summary>
    private static List<Link> BuildChain(int length, short chainId = ChainId, int seed = Seed)
    {
        var random = new Random(seed);
        var links = new List<Link>(length);
        byte[] previous = AuditHashing.Genesis(chainId);

        for (int i = 0; i < length; i++)
        {
            long seq = i + 1;
            AuditEntry entry = SampleEntry(i, random);
            string canonical = AuditHashing.Canonicalise(chainId, seq, entry);
            byte[] hash = AuditHashing.ComputeHash(previous, canonical);

            links.Add(new Link(chainId, seq, entry, previous, hash));
            previous = hash;
        }

        return links;
    }

    /// <summary>
    /// Re-walks a chain, recomputing every hash from its predecessor. Returns the first broken
    /// sequence number, or null when the chain verifies.
    /// </summary>
    private static long? FirstBreak(IReadOnlyList<Link> links, short chainId = ChainId)
    {
        byte[] previous = AuditHashing.Genesis(chainId);
        long expectedSeq = 1;

        foreach (Link link in links)
        {
            // A gap in the sequence is a deletion, whatever the hashes say.
            if (link.Seq != expectedSeq)
            {
                return link.Seq;
            }

            if (!link.PreviousHash.SequenceEqual(previous))
            {
                return link.Seq;
            }

            byte[] recomputed = AuditHashing.ComputeHash(previous, AuditHashing.Canonicalise(chainId, link.Seq, link.Entry));
            if (!recomputed.SequenceEqual(link.Hash))
            {
                return link.Seq;
            }

            previous = link.Hash;
            expectedSeq++;
        }

        return null;
    }

    [Fact]
    public void AValidChain_Verifies() => FirstBreak(BuildChain(50)).ShouldBeNull();

    [Fact]
    public void AnySingleFieldMutation_IsDetected()
    {
        // The core property. Every field in the canonical form must be covered by the hash - a
        // field that is NOT covered can be edited freely without breaking the chain, which is a
        // silent hole in the evidence.
        var random = new Random(Seed);

        (string Name, Func<AuditEntry, AuditEntry> Mutate)[] mutations =
        [
            ("Id", e => e with { Id = new AuditEventId(Guid.CreateVersion7()) }),
            ("StatementId", e => e with { StatementId = new StatementId(Guid.CreateVersion7()) }),
            ("StatementId->null", e => e with { StatementId = null }),
            ("CustomerId", e => e with { CustomerId = new CustomerId(Guid.CreateVersion7()) }),
            ("CustomerId->null", e => e with { CustomerId = null }),
            ("ActorType", e => e with { ActorType = ActorType.Staff }),
            ("ActorId", e => e with { ActorId = e.ActorId + "x" }),
            ("ActorId->null", e => e with { ActorId = null }),
            ("Action", e => e with { Action = AuditAction.StatementMetadataViewed }),
            ("Outcome", e => e with { Outcome = AuditOutcome.Denied }),
            ("DenialReasonCode", e => e with { DenialReasonCode = DenialReason.NotOwner }),
            ("SourceIp", e => e with { SourceIp = "203.0.113.9" }),
            ("SourceIp->null", e => e with { SourceIp = null }),
            ("UserAgentHash", e => e with { UserAgentHash = "deadbeef" }),
            ("OccurredAt", e => e with { OccurredAt = e.OccurredAt.AddMilliseconds(1) }),
            ("Context-value", e => e with { Context = Replace(e.Context, "page_size", 999) }),
            ("Context-newKey", e => e with { Context = Replace(e.Context, "extra", "added") }),
            ("Context-removedKey", e => e with { Context = Without(e.Context, "index") }),
        ];

        for (int iteration = 0; iteration < 100; iteration++)
        {
            AuditEntry original = SampleEntry(iteration, random);
            long seq = random.Next(1, 1_000_000);
            byte[] previous = SHA256Of("previous" + iteration.ToString(CultureInfo.InvariantCulture));

            byte[] baseline = AuditHashing.ComputeHash(previous, AuditHashing.Canonicalise(ChainId, seq, original));

            foreach ((string name, Func<AuditEntry, AuditEntry> mutate) in mutations)
            {
                AuditEntry mutated = mutate(original);
                byte[] hash = AuditHashing.ComputeHash(previous, AuditHashing.Canonicalise(ChainId, seq, mutated));

                hash.ShouldNotBe(baseline, $"mutating {name} must change the hash (iteration {iteration})");
            }

            // Position is covered too: the same record at a different sequence number hashes
            // differently, so a record cannot be moved within its own chain.
            AuditHashing.ComputeHash(previous, AuditHashing.Canonicalise(ChainId, seq + 1, original))
                .ShouldNotBe(baseline, "chainSeq must be covered by the hash");

            AuditHashing.ComputeHash(previous, AuditHashing.Canonicalise((short)(ChainId + 1), seq, original))
                .ShouldNotBe(baseline, "chainId must be covered by the hash");
        }
    }

    [Fact]
    public void DeletedMiddleRecord_IsDetected()
    {
        List<Link> chain = BuildChain(20);
        chain.RemoveAt(9);

        // The record after the gap still carries the deleted record's hash as its predecessor, so
        // the recomputation diverges immediately.
        FirstBreak(chain).ShouldNotBeNull();
    }

    [Fact]
    public void TruncatedTail_IsDetectedAgainstAKnownTerminalHash()
    {
        // Deleting from the END is the one attack a chain alone cannot see: what remains is a
        // shorter, internally consistent chain. It is detectable ONLY against an independently
        // held terminal hash - which is precisely what IChainAnchor exists to provide, and why
        // the limitation is stated rather than hidden.
        List<Link> full = BuildChain(20);
        byte[] trustedTerminalHash = full[^1].Hash;

        List<Link> truncated = [.. full.Take(15)];

        FirstBreak(truncated).ShouldBeNull("a truncated chain is still internally consistent");
        truncated[^1].Hash.ShouldNotBe(trustedTerminalHash, "but it does not match the anchored terminal hash");
    }

    [Fact]
    public void ReorderedRecords_AreDetected()
    {
        List<Link> chain = BuildChain(20);
        (chain[5], chain[6]) = (chain[6], chain[5]);

        FirstBreak(chain).ShouldNotBeNull();
    }

    [Fact]
    public void RecordFromAnotherChain_DoesNotVerify()
    {
        // GENESIS SEPARATION. With a shared genesis of zeros, record 1 of chain 3 and record 1 of
        // chain 7 hash over the same predecessor, so one could be lifted into the other and both
        // chains would still verify. A chain-specific genesis makes the hashes fail to line up.
        List<Link> chainThree = BuildChain(5, chainId: 3);
        List<Link> chainSeven = BuildChain(5, chainId: 7);

        AuditHashing.Genesis(3).ShouldNotBe(AuditHashing.Genesis(7));
        chainThree[0].Hash.ShouldNotBe(chainSeven[0].Hash);

        // Splice chain 7's first record into chain 3 and re-verify as chain 3.
        List<Link> spliced = [chainSeven[0], .. chainThree.Skip(1)];
        FirstBreak(spliced, chainId: 3).ShouldBe(1);
    }

    [Fact]
    public void Genesis_IsDistinctForEveryChain()
    {
        string[] hashes =
        [
            .. Enumerable.Range(0, AuditHashing.DefaultChainCount)
                .Select(i => Convert.ToHexStringLower(AuditHashing.Genesis((short)i))),
        ];

        hashes.Distinct(StringComparer.Ordinal).Count().ShouldBe(AuditHashing.DefaultChainCount);
        hashes.ShouldAllBe(h => h.Length == 64);
        hashes.ShouldNotContain(new string('0', 64), "genesis must not be zeros");
    }

    [Fact]
    public void ChainAssignment_IsStableAndByEntity()
    {
        // Every event about one statement must land in the SAME chain, so verifying that
        // statement's history is a single-chain walk rather than an N-chain merge.
        var statement = Guid.CreateVersion7();
        var customer = Guid.CreateVersion7();

        short first = AuditHashing.AssignChain(statement, customer);

        for (int i = 0; i < 100; i++)
        {
            AuditHashing.AssignChain(statement, Guid.CreateVersion7()).ShouldBe(
                first,
                "assignment must depend on the statement, not on the other fields");
        }

        AuditHashing.AssignChain(null, customer)
            .ShouldBe(AuditHashing.AssignChain(null, customer), "assignment must be deterministic");
    }

    [Fact]
    public void ChainAssignment_SpreadsAcrossEveryChain()
    {
        // A distribution that clustered would give back the single-hot-row problem sharding exists
        // to solve.
        var counts = new int[AuditHashing.DefaultChainCount];

        for (int i = 0; i < 20_000; i++)
        {
            counts[AuditHashing.AssignChain(Guid.CreateVersion7(), null)]++;
        }

        counts.ShouldAllBe(c => c > 0, "every chain must receive traffic");

        // Loose bound: this is a distribution check, not a chi-squared test.
        int expected = 20_000 / AuditHashing.DefaultChainCount;
        counts.ShouldAllBe(c => c > expected / 3 && c < expected * 3);
    }

    [Fact]
    public void FieldContainingUnitSeparator_IsRejected()
    {
        // REJECTED, NOT ESCAPED. Escaping would move the ambiguity one level down rather than
        // removing it. With a printable delimiter, an actor id of "a,b" and the pair ("a","b")
        // canonicalise identically and therefore hash identically - one could be substituted for
        // the other with the chain still intact.
        AuditEntry poisoned = SampleEntry(0, new Random(Seed)) with
        {
            ActorId = "customer" + AuditHashing.FieldDelimiter + "forged",
        };

        Should.Throw<InvariantViolationException>(() => AuditHashing.Canonicalise(ChainId, 1, poisoned));
    }

    [Fact]
    public void UnitSeparator_IsRejectedInEveryTextField()
    {
        AuditEntry clean = SampleEntry(0, new Random(Seed));
        string poison = "x" + AuditHashing.FieldDelimiter + "y";

        foreach (Func<AuditEntry, AuditEntry> poisonField in (Func<AuditEntry, AuditEntry>[])
                 [
                     e => e with { ActorType = poison },
                     e => e with { ActorId = poison },
                     e => e with { Action = poison },
                     e => e with { Outcome = poison },
                     e => e with { DenialReasonCode = poison },
                     e => e with { SourceIp = poison },
                     e => e with { UserAgentHash = poison },
                 ])
        {
            Should.Throw<InvariantViolationException>(
                () => AuditHashing.Canonicalise(ChainId, 1, poisonField(clean)));
        }
    }

    private static Dictionary<string, object?> Replace(
        IReadOnlyDictionary<string, object?> source, string key, object? value)
    {
        var copy = new Dictionary<string, object?>(source, StringComparer.Ordinal) { [key] = value };
        return copy;
    }

    private static Dictionary<string, object?> Without(
        IReadOnlyDictionary<string, object?> source, string key)
    {
        var copy = new Dictionary<string, object?>(source, StringComparer.Ordinal);
        _ = copy.Remove(key);
        return copy;
    }
}

/// <summary>
/// Canonical JSON serialisation of the audit context bag.
/// </summary>
public sealed class CanonicalJsonTests
{
    [Fact]
    public void KeyOrderIndependent_ProducesIdenticalBytes()
    {
        // Without this the hash is meaningless: two serialisers - or the same one on two runtimes -
        // can emit semantically identical JSON with different key order, and the record would fail
        // to verify despite nothing having changed.
        var forward = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["alpha"] = 1,
            ["beta"] = "two",
            ["gamma"] = true,
            ["delta"] = null,
        };

        var reversed = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["delta"] = null,
            ["gamma"] = true,
            ["beta"] = "two",
            ["alpha"] = 1,
        };

        AuditHashing.CanonicalJson(forward).ShouldBe(AuditHashing.CanonicalJson(reversed));

        Encoding.UTF8.GetBytes(AuditHashing.CanonicalJson(forward))
            .ShouldBe(Encoding.UTF8.GetBytes(AuditHashing.CanonicalJson(reversed)));
    }

    [Fact]
    public void KeyOrderIndependent_AtEveryNestingLevel()
    {
        var a = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["outer"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["z"] = 1, ["a"] = 2 },
        };

        var b = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["outer"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["a"] = 2, ["z"] = 1 },
        };

        AuditHashing.CanonicalJson(a).ShouldBe(AuditHashing.CanonicalJson(b));
    }

    [Fact]
    public void ArrayOrder_IsPreserved()
    {
        // Order is SEMANTIC in an array. Sorting it would make two genuinely different contexts
        // hash identically, which is the opposite of what canonicalisation is for.
        var first = new Dictionary<string, object?>(StringComparer.Ordinal) { ["items"] = new[] { 1, 2, 3 } };
        var second = new Dictionary<string, object?>(StringComparer.Ordinal) { ["items"] = new[] { 3, 2, 1 } };

        AuditHashing.CanonicalJson(first).ShouldNotBe(AuditHashing.CanonicalJson(second));
    }

    [Theory]
    [InlineData(1, "1")]
    [InlineData(1.0, "1")]
    [InlineData(1.50, "1.5")]
    [InlineData(-0.25, "-0.25")]
    public void Numbers_AreNormalised(object value, string expected)
    {
        var context = new Dictionary<string, object?>(StringComparer.Ordinal) { ["n"] = value };

        AuditHashing.CanonicalJson(context).ShouldBe("{\"n\":" + expected + "}");
    }

    [Fact]
    public void EmptyContext_IsAnEmptyObject()
    {
        AuditHashing.CanonicalJson(null).ShouldBe("{}");
        AuditHashing.CanonicalJson(new Dictionary<string, object?>(StringComparer.Ordinal)).ShouldBe("{}");
    }

    [Fact]
    public void OmitsAllWhitespace() =>
        AuditHashing.CanonicalJson(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["a"] = 1,
            ["b"] = "two",
        }).ShouldBe("{\"a\":1,\"b\":\"two\"}");

    [Fact]
    public void EscapesOnlyWhatJsonRequires()
    {
        // Hand-written escaping rather than a serialiser's default, so the canonical form cannot
        // change because a future runtime altered its escaping policy. Non-ASCII is emitted
        // literally and carried by UTF-8.
        var context = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["quote"] = "a\"b",
            ["newline"] = "a\nb",
            ["unicode"] = "Zürich",
        };

        string json = AuditHashing.CanonicalJson(context);

        json.ShouldContain("a\\\"b");
        json.ShouldContain("a\\nb");
        json.ShouldContain("Zürich", customMessage: "non-ASCII must not be escaped into \\u form");
    }

    [Fact]
    public void IsStableAcrossRepeatedCalls()
    {
        var context = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["b"] = 2,
            ["a"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["y"] = false, ["x"] = "1" },
        };

        string first = AuditHashing.CanonicalJson(context);

        for (int i = 0; i < 20; i++)
        {
            AuditHashing.CanonicalJson(context).ShouldBe(first);
        }
    }
}
