using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using StatementDelivery.Domain.Auditing;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Persistence.Auditing;
using StatementDelivery.Persistence.Connections;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// The audit chain against a real PostgreSQL instance.
/// </summary>
/// <remarks>
/// The property under test belongs to the database: row locking, trigger enforcement, and the
/// atomicity of read-modify-write on the chain head. None of it can be tested against a fake.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class AuditChainIntegrationTests
{
    private const int ConcurrentWriters = 50;

    private readonly PostgresFixture _postgres;

    /// <summary>Initialises a new instance of the <see cref="AuditChainIntegrationTests"/> class.</summary>
    /// <param name="postgres">The shared PostgreSQL fixture.</param>
    public AuditChainIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

    private static PostgresAuditWriter Writer(NpgsqlConnectionFactory factory) =>
        new(Options.Create(new AuditOptions { ChainCount = AuditHashing.DefaultChainCount }), factory);

    private static AuditEntry Entry(Guid statementId, Guid customerId, int index) => new(
        new AuditEventId(Guid.CreateVersion7()),
        new StatementId(statementId),
        new CustomerId(customerId),
        ActorType.Customer,
        $"customer-{index}",
        AuditAction.StatementMetadataViewed,
        AuditOutcome.Success,
        null,
        "198.51.100.7",
        "0badc0de",
        new Dictionary<string, object?>(StringComparer.Ordinal) { ["index"] = index },
        DateTimeOffset.UtcNow);

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Genesis_MatchesTheCSharpDefinitionByteForByte()
    {
        // The seed in V007 is computed in SQL and the verifier computes it in C#. If those two ever
        // disagree, every chain fails to verify from record one - and it would look like tampering.
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        // A head equals its genesis only while the chain is untouched, and sibling tests
        // legitimately advance chains in the shared database. Either way the seed is still
        // provable: an unmoved head IS the seed, and a moved chain's first record carries the
        // seed as its immutable prev_hash.
        List<(short ChainId, long LastSeq, byte[] LastHash)> heads =
        [
            .. await connection.QueryAsync<(short, long, byte[])>(
                "SELECT chain_id, last_seq, last_hash FROM audit_chain_head ORDER BY chain_id;").ConfigureAwait(true),
        ];

        heads.Count.ShouldBe(AuditHashing.DefaultChainCount);

        foreach ((short chainId, long lastSeq, byte[] lastHash) in heads)
        {
            byte[] seeded = lastSeq == 0
                ? lastHash
                : await connection.QuerySingleAsync<byte[]>(
                    "SELECT prev_hash FROM audit_event WHERE chain_id = @chainId AND chain_seq = 1;",
                    new { chainId }).ConfigureAwait(true);

            seeded.ShouldBe(AuditHashing.Genesis(chainId), $"chain {chainId} genesis must match the C# definition");
        }

        // And the definitions must all differ, or a record could be lifted between chains
        // undetected.
        Enumerable.Range(0, AuditHashing.DefaultChainCount)
            .Select(static chain => Convert.ToHexStringLower(AuditHashing.Genesis((short)chain)))
            .Distinct(StringComparer.Ordinal).Count()
            .ShouldBe(AuditHashing.DefaultChainCount);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task ConcurrentWritersSameChain_ProduceValidChain()
    {
        // THE TEST THAT PROVES THE DESIGN.
        //
        // Fifty writers append to ONE chain simultaneously. Without FOR UPDATE on the head row they
        // would all read last_seq = N, all compute N+1, and all hash over the same predecessor -
        // producing duplicate sequence numbers and a chain that cannot verify. If it verifies after
        // this, the serialisation is correct.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // The write timeout is sized to the test's own queue: fifty appends serialise on one
        // chain head by design, so the last writer legitimately waits for the other forty-nine,
        // and on a loaded runner that tail exceeds the default thirty seconds.
        NpgsqlConnectionFactory factory = _postgres.ConnectionFactoryFor(
            "app_generation", maxPoolSize: 60, writeTimeoutSeconds: 120);
        await using (factory.ConfigureAwait(false))
        {
            PostgresAuditWriter writer = Writer(factory);

            // One statement id for all of them, so AssignChain sends every event to the same chain.
            var statementId = Guid.CreateVersion7();
            var customerId = Guid.CreateVersion7();
            short chainId = AuditHashing.AssignChain(statementId, customerId);

            long startSeq = await CurrentSeqAsync(chainId, cancellationToken).ConfigureAwait(true);

            // Pre-warm ONE connection before the storm: Npgsql bootstraps its type catalogue on
            // the data source's first physical open, and fifty first-opens racing that one-time
            // bootstrap on a loaded runner blow the five-second connect timeout. One quiet open
            // pays the cost once; production pools warm the same way.
            await using (NpgsqlConnection warmup =
                await factory.OpenAsync(ConnectionIntent.Write, cancellationToken).ConfigureAwait(true))
            {
            }

            using var barrier = new Barrier(ConcurrentWriters);

            AuditReceipt[] receipts = await Task.WhenAll(Enumerable.Range(0, ConcurrentWriters).Select(index =>
                Task.Run(
                    async () =>
                    {
                        await using NpgsqlConnection connection =
                            await factory.OpenAsync(ConnectionIntent.Write, cancellationToken).ConfigureAwait(false);
                        await using NpgsqlTransaction transaction =
                            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                        // Release all fifty at the same instant. Staggered starts would let them
                        // serialise naturally and the test would pass without proving anything.
                        _ = barrier.SignalAndWait(TimeSpan.FromSeconds(30));

                        AuditReceipt receipt = await writer
                            .AppendAsync(Entry(statementId, customerId, index), transaction, cancellationToken)
                            .ConfigureAwait(false);

                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        return receipt;
                    },
                    cancellationToken))).ConfigureAwait(true);

            receipts.Length.ShouldBe(ConcurrentWriters);
            receipts.ShouldAllBe(r => r.ChainId == chainId, "every event about one statement lands in one chain");

            // No duplicate sequence numbers, and no gaps.
            long[] sequences = [.. receipts.Select(r => r.Seq).Order()];
            sequences.Distinct().Count().ShouldBe(ConcurrentWriters, "duplicate chain_seq means the head lock did not hold");
            sequences.ShouldBe([.. Enumerable.Range(1, ConcurrentWriters).Select(i => startSeq + i)]);

            // And the chain itself must verify end to end.
            var verifier = new PostgresAuditVerifier(_postgres.ConnectionFactoryFor("app_retention"));
            ChainVerification verification = await verifier
                .VerifyChainAsync(chainId, 1, startSeq + ConcurrentWriters, cancellationToken)
                .ConfigureAwait(true);

            verification.Verified.ShouldBeTrue(verification.ToString());
            verification.EventsChecked.ShouldBe(startSeq + ConcurrentWriters);
        }
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task ConcurrentWritersDifferentChains_DoNotBlockEachOther()
    {
        // Asserted DETERMINISTICALLY rather than by racing two workloads and comparing durations -
        // a throughput comparison on shared CI hardware is a coin toss with extra steps.
        //
        // Instead: hold chain A's head lock open in one transaction, then prove an append to
        // chain B succeeds while an append to chain A blocks. That IS the property - the lock is
        // per-chain, so sixteen chains give sixteen independent write paths.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        NpgsqlConnectionFactory factory = _postgres.ConnectionFactoryFor("app_generation", maxPoolSize: 10);
        await using (factory.ConfigureAwait(false))
        {
            PostgresAuditWriter writer = Writer(factory);

            (Guid StatementA, Guid StatementB) subjects = FindStatementsOnDifferentChains();
            short chainA = AuditHashing.AssignChain(subjects.StatementA, null);
            short chainB = AuditHashing.AssignChain(subjects.StatementB, null);
            chainA.ShouldNotBe(chainB);

            // Hold chain A's head lock open.
            await using NpgsqlConnection holder =
                await factory.OpenAsync(ConnectionIntent.Write, cancellationToken).ConfigureAwait(true);
            await using NpgsqlTransaction holding =
                await holder.BeginTransactionAsync(cancellationToken).ConfigureAwait(true);

            _ = await holder.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT last_seq FROM audit_chain_head WHERE chain_id = @chainA FOR UPDATE;",
                new { chainA },
                transaction: holding,
                commandTimeout: 10,
                cancellationToken: cancellationToken)).ConfigureAwait(true);

            // A different chain proceeds immediately.
            await using (NpgsqlConnection other =
                await factory.OpenAsync(ConnectionIntent.Write, cancellationToken).ConfigureAwait(true))
            {
                await using NpgsqlTransaction transaction =
                    await other.BeginTransactionAsync(cancellationToken).ConfigureAwait(true);

                AuditReceipt receipt = await writer
                    .AppendAsync(Entry(subjects.StatementB, Guid.CreateVersion7(), 1), transaction, cancellationToken)
                    .ConfigureAwait(true);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(true);
                receipt.ChainId.ShouldBe(chainB);
            }

            // The same chain blocks. A short lock_timeout turns "waits forever" into a precise,
            // fast assertion instead of a hung test.
            await using (NpgsqlConnection blocked =
                await factory.OpenAsync(ConnectionIntent.Write, cancellationToken).ConfigureAwait(true))
            {
                await using NpgsqlTransaction transaction =
                    await blocked.BeginTransactionAsync(cancellationToken).ConfigureAwait(true);

                _ = await blocked.ExecuteAsync(new CommandDefinition(
                    "SET LOCAL lock_timeout = '750ms';",
                    transaction: transaction,
                    commandTimeout: 10,
                    cancellationToken: cancellationToken)).ConfigureAwait(true);

                PostgresException error = await Should.ThrowAsync<PostgresException>(
                    () => writer.AppendAsync(Entry(subjects.StatementA, Guid.CreateVersion7(), 2), transaction, cancellationToken))
                    .ConfigureAwait(true);

                // 55P03 is lock_not_available: it waited for the head lock and gave up, which is
                // exactly the serialisation the design relies on.
                error.SqlState.ShouldBe("55P03");
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(true);
            }

            await holding.RollbackAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Update_IsRejectedByTrigger()
    {
        // Run as the SUPERUSER, so the grant cannot be what refuses it. This isolates the trigger:
        // the two mechanisms are independent, and each must work on its own.
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        PostgresException error = await Should.ThrowAsync<PostgresException>(
            () => connection.ExecuteAsync("UPDATE audit_event SET action = 'TAMPERED';")).ConfigureAwait(true);

        error.MessageText.ShouldContain("append-only");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Delete_IsRejectedByTrigger()
    {
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        PostgresException error = await Should.ThrowAsync<PostgresException>(
            () => connection.ExecuteAsync("DELETE FROM audit_event;")).ConfigureAwait(true);

        error.MessageText.ShouldContain("append-only");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Truncate_IsRejectedByTrigger()
    {
        // TRUNCATE bypasses row-level triggers entirely. Without a statement-level trigger,
        // "append-only" would be one TRUNCATE away from an empty table.
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        PostgresException error = await Should.ThrowAsync<PostgresException>(
            () => connection.ExecuteAsync("TRUNCATE audit_event;")).ConfigureAwait(true);

        error.MessageText.ShouldContain("append-only");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task AppDeliveryRole_CannotUpdateAuditEvent()
    {
        // The other mechanism, in isolation: the grant. An attacker holding this credential is
        // refused before the trigger is even consulted.
        await using NpgsqlConnection connection =
            await _postgres.OpenAsAsync("app_delivery", TestContext.Current.CancellationToken).ConfigureAwait(true);

        PostgresException update = await Should.ThrowAsync<PostgresException>(
            () => connection.ExecuteAsync("UPDATE audit_event SET action = 'TAMPERED';")).ConfigureAwait(true);
        update.SqlState.ShouldBe("42501");

        PostgresException delete = await Should.ThrowAsync<PostgresException>(
            () => connection.ExecuteAsync("DELETE FROM audit_event;")).ConfigureAwait(true);
        delete.SqlState.ShouldBe("42501");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task TamperedRecord_FailsVerification()
    {
        // Proves the chain does its job. The trigger blocks an ordinary UPDATE, so the tamper is
        // performed the way a real attacker would have to - by disabling it first, which requires
        // privileges no service holds.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // ITS OWN DATABASE, deliberately. This test plants real corruption to prove the chain
        // detects it - and the moment appends started working for the whole suite, every later
        // whole-chain verify (the full-lifecycle test, reconciliation's C6) found the planted
        // tamper and honestly reported the shared trail broken. An attack rehearsal does not
        // belong in evidence other tests rely on.
        string generationConnectionString = await _postgres
            .CreateLaggingReplicaAsync("audit_tamper_db", "app_generation", cancellationToken).ConfigureAwait(true);

        NpgsqlConnectionFactory factory = TamperDbFactory(generationConnectionString, "app_generation");
        await using (factory.ConfigureAwait(false))
        {
            PostgresAuditWriter writer = Writer(factory);
            var statementId = Guid.CreateVersion7();
            short chainId = AuditHashing.AssignChain(statementId, null);

            // A freshly migrated database: every chain sits at its genesis.
            const long StartSeq = 0;

            for (int i = 0; i < 5; i++)
            {
                await using NpgsqlConnection connection =
                    await factory.OpenAsync(ConnectionIntent.Write, cancellationToken).ConfigureAwait(true);
                await using NpgsqlTransaction transaction =
                    await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(true);
                _ = await writer.AppendAsync(Entry(statementId, Guid.CreateVersion7(), i), transaction, cancellationToken)
                    .ConfigureAwait(true);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(true);
            }

            long target = StartSeq + 3;

            var adminToTamperDb = new NpgsqlConnectionStringBuilder(_postgres.AdminConnectionString)
            {
                Database = "audit_tamper_db",
            };
            await using (var admin = new NpgsqlConnection(adminToTamperDb.ConnectionString))
            {
                await admin.OpenAsync(cancellationToken).ConfigureAwait(true);
                _ = await admin.ExecuteAsync(new CommandDefinition(
                    """
                    ALTER TABLE audit_event DISABLE TRIGGER trg_audit_no_update;
                    UPDATE audit_event SET action = 'TAMPERED' WHERE chain_id = @chainId AND chain_seq = @target;
                    ALTER TABLE audit_event ENABLE TRIGGER trg_audit_no_update;
                    """,
                    new { chainId, target },
                    commandTimeout: 30,
                    cancellationToken: cancellationToken)).ConfigureAwait(true);
            }

            var verifier = new PostgresAuditVerifier(TamperDbFactory(generationConnectionString, "app_retention"));
            ChainVerification verification = await verifier
                .VerifyChainAsync(chainId, 1, StartSeq + 5, cancellationToken).ConfigureAwait(true);

            verification.Verified.ShouldBeFalse("an altered record must break the chain");
            verification.FirstBrokenSeq.ShouldBe(target, "and the break must be reported at the altered record");
        }
    }

    /// <summary>Builds a connection factory for a role against the tamper-isolation database.</summary>
    private static NpgsqlConnectionFactory TamperDbFactory(string connectionString, string role)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Username = role,
        };

        return new NpgsqlConnectionFactory(
            Microsoft.Extensions.Options.Options.Create(new StatementDelivery.Persistence.Connections.PostgresOptions
            {
                PrimaryConnectionString = builder.ConnectionString,
                ApplicationName = $"integration-tests:tamper:{role}",
                MaxPoolSize = 5,
                MinPoolSize = 0,
                MaxAutoPrepare = 0,
            }),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NpgsqlConnectionFactory>.Instance);
    }

    private static (Guid StatementA, Guid StatementB) FindStatementsOnDifferentChains()
    {
        var a = Guid.CreateVersion7();
        short chainA = AuditHashing.AssignChain(a, null);

        for (int attempt = 0; attempt < 1000; attempt++)
        {
            var b = Guid.CreateVersion7();
            if (AuditHashing.AssignChain(b, null) != chainA)
            {
                return (a, b);
            }
        }

        throw new UnreachableException("Failed to find two identifiers on different chains.");
    }

    private async Task<long> CurrentSeqAsync(short chainId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT last_seq FROM audit_chain_head WHERE chain_id = @chainId;",
            new { chainId },
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
