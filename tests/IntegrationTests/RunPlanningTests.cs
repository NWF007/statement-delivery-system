using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using StatementDelivery.Domain.ValueObjects;
using StatementDelivery.Persistence.Connections;
using StatementDelivery.Persistence.Leasing;
using StatementDelivery.Persistence.Runs;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// Run planning: idempotent, resumable, streaming, and singly-orchestrated.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RunPlanningTests
{
    private readonly PostgresFixture _postgres;

    /// <summary>Initialises a new instance of the <see cref="RunPlanningTests"/> class.</summary>
    /// <param name="postgres">The shared PostgreSQL fixture.</param>
    public RunPlanningTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Planning_IsIdempotent_OnRerun()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (StatementRunRepository repo, StatementPeriod period, _) =
            await SeedAsync(accounts: 120, ct).ConfigureAwait(true);

        StatementRun run = await repo.CreateOrGetAsync(Guid.CreateVersion7(), period, null, ct).ConfigureAwait(true);
        long firstTotal = await PlanAsync(repo, run.Id, period, ct).ConfigureAwait(true);

        // The whole plan again, from the top - as a restarted orchestrator would.
        long secondTotal = await PlanAsync(repo, run.Id, period, ct).ConfigureAwait(true);

        firstTotal.ShouldBe(120);
        secondTotal.ShouldBe(120, "re-planning must find every item already enqueued and add none");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Planning_ResumesAfterInterruption_WithoutDuplicates()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (StatementRunRepository repo, StatementPeriod period, _) =
            await SeedAsync(accounts: 100, ct).ConfigureAwait(true);

        StatementRun run = await repo.CreateOrGetAsync(Guid.CreateVersion7(), period, null, ct).ConfigureAwait(true);

        // Simulate the orchestrator dying mid-plan: enqueue only the first batch, then "restart"
        // and run the whole plan. ON CONFLICT (run_id, account_id) DO NOTHING is what makes the
        // second pass harmless.
        List<IReadOnlyList<Guid>> batches = [];
        await foreach (IReadOnlyList<Guid> batch in repo.StreamEligibleAccountsAsync(period, 30, ct).ConfigureAwait(true))
        {
            batches.Add(batch);
        }

        _ = await repo.EnqueueBatchAsync(run.Id, batches[0], null, ct).ConfigureAwait(true);

        long total = await PlanAsync(repo, run.Id, period, ct).ConfigureAwait(true);
        total.ShouldBe(100);

        // And genuinely no duplicates - the constraint would have thrown, but count anyway.
        (await ScalarAsync<long>(
            "SELECT count(*) FROM statement_run_item WHERE run_id = @runId;", new { runId = run.Id }, ct)
            .ConfigureAwait(true)).ShouldBe(100);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Run_ForExistingCompletedPeriod_IsNoOp()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (StatementRunRepository repo, StatementPeriod period, _) =
            await SeedAsync(accounts: 5, ct).ConfigureAwait(true);

        StatementRun original = await repo.CreateOrGetAsync(Guid.CreateVersion7(), period, null, ct).ConfigureAwait(true);
        _ = await PlanAsync(repo, original.Id, period, ct).ConfigureAwait(true);
        _ = await repo.TransitionAsync(original.Id, RunStatus.Running, RunStatus.Completed, ct).ConfigureAwait(true);

        // Re-trigger: SAME run id back, totals untouched, status still COMPLETED. Acceptance 55.
        StatementRun again = await repo.CreateOrGetAsync(Guid.CreateVersion7(), period, null, ct).ConfigureAwait(true);

        again.Id.ShouldBe(original.Id, "re-triggering a period returns the existing run, never a duplicate");
        again.Status.ShouldBe(RunStatus.Completed);
        again.TotalItems.ShouldBe(5);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Planning_StreamsAccounts_WithoutLoadingAll()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const int Accounts = 30_000;
        const int BatchSize = 1_000;

        (StatementRunRepository repo, StatementPeriod period, _) =
            await SeedAsync(Accounts, ct).ConfigureAwait(true);

        // Memory bound: streaming 30k ids in 1k batches must not allocate anywhere near the
        // whole set. The threshold is deliberately generous (16 MB for ~480KB of live ids) -
        // it exists to catch the O(all-accounts) regression, not to measure the GC.
        long before = GC.GetTotalAllocatedBytes(precise: true);

        long seen = 0;
        int largestBatch = 0;
        await foreach (IReadOnlyList<Guid> batch in repo
            .StreamEligibleAccountsAsync(period, BatchSize, ct).ConfigureAwait(true))
        {
            seen += batch.Count;
            largestBatch = Math.Max(largestBatch, batch.Count);
        }

        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        seen.ShouldBe(Accounts);
        largestBatch.ShouldBeLessThanOrEqualTo(BatchSize, "no batch may exceed the requested size");
        allocated.ShouldBeLessThan(64 * 1024 * 1024,
            "streaming allocations must be proportional to the batch, not the table");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task OnlyOneReplica_AcquiresOrchestratorLease()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        NpgsqlConnectionFactory a = _postgres.ConnectionFactoryFor("app_generation");
        await using (a.ConfigureAwait(false))
        {
            NpgsqlConnectionFactory b = _postgres.ConnectionFactoryFor("app_generation");
            await using (b.ConfigureAwait(false))
            {
                // Distinct holder ids: both managers live in this one test process, and the
                // default identity is per-process - identical ids make the second acquire a
                // legitimate same-holder renewal, which is not what this test is about.
                var replicaA = new PostgresLeaseManager(
                    a, Options.Create(new LeaseOptions { TimeToLiveSeconds = 15, HolderId = "replica-a" }), NullLoggerFactory.Instance);
                var replicaB = new PostgresLeaseManager(
                    b, Options.Create(new LeaseOptions { TimeToLiveSeconds = 15, HolderId = "replica-b" }), NullLoggerFactory.Instance);

                ILeaseHandle? first = await replicaA.TryAcquireAsync("generation-orchestrator-test", ct).ConfigureAwait(true);
                ILeaseHandle? second = await replicaB.TryAcquireAsync("generation-orchestrator-test", ct).ConfigureAwait(true);

                try
                {
                    first.ShouldNotBeNull();
                    second.ShouldBeNull("two replicas must never both hold the orchestrator lease");
                }
                finally
                {
                    if (first is not null)
                    {
                        await first.DisposeAsync().ConfigureAwait(true);
                    }
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>Runs the orchestrator's planning sequence exactly as RunOrchestratorService does.</summary>
    private static async Task<long> PlanAsync(
        StatementRunRepository repo, Guid runId, StatementPeriod period, CancellationToken ct)
    {
        await foreach (IReadOnlyList<Guid> batch in repo.StreamEligibleAccountsAsync(period, 1000, ct).ConfigureAwait(false))
        {
            _ = await repo.EnqueueBatchAsync(runId, batch, null, ct).ConfigureAwait(false);
        }

        return await repo.MarkRunningAsync(runId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Seeds eligible accounts under ONE customer in a UNIQUE period, so tests sharing the
    /// container cannot see each other's accounts (eligibility is period-scoped via opened_at).
    /// </summary>
    private async Task<(StatementRunRepository Repo, StatementPeriod Period, Guid CustomerId)> SeedAsync(
        int accounts, CancellationToken ct)
    {
        // Far-past periods, one per test invocation, so period-scoped eligibility isolates tests.
        int offset = Interlocked.Increment(ref s_offset);
        DateOnly anchor = new DateOnly(1980, 1, 1).AddMonths(offset);
        StatementPeriod period = StatementPeriod.ForMonth(anchor.Year, anchor.Month);

        var customer = Guid.CreateVersion7();

        await using (NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(false))
        {
            _ = await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO customer (id, external_ref, status) VALUES (@customer, @ref, 'ACTIVE');",
                new { customer, @ref = customer.ToString("N") },
                commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(false);

            // AIRTIGHT PERIOD ISOLATION on a shared container. Eligibility is
            // opened_at <= period_end AND (closed_at IS NULL OR closed_at > period_start), so an
            // account opened in period P would leak into every LATER test's period. Opening at
            // period start AND closing one day after period end makes each account eligible for
            // exactly its own month: later periods see closed_at <= their start, earlier periods
            // see opened_at > their end.
            DateTimeOffset opened = new(period.Start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            DateTimeOffset closed = new(period.End.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

            foreach (Guid[] chunk in Enumerable.Range(0, accounts)
                .Select(static _ => Guid.CreateVersion7())
                .Chunk(5000))
            {
                _ = await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO account (id, customer_id, account_number_masked, product_type, status, opened_at, closed_at)
                    SELECT a, @customer, '****0000', 'CURRENT', 'ACTIVE', @opened, @closed FROM unnest(@ids) AS a;
                    """,
                    new { ids = chunk, customer, opened, closed },
                    commandTimeout: 60, cancellationToken: ct)).ConfigureAwait(false);
            }
        }

        return (new StatementRunRepository(_postgres.ConnectionFactoryFor("app_generation")), period, customer);
    }

    private static int s_offset;

    private async Task<T?> ScalarAsync<T>(string sql, object args, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<T>(new CommandDefinition(
            sql, args, commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(false);
    }
}
