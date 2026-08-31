using System.Diagnostics;
using Dapper;
using Npgsql;
using Shouldly;
using StatementDelivery.Domain.ValueObjects;
using StatementDelivery.Persistence.Connections;
using StatementDelivery.Persistence.Runs;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// The batch subsystem under the failures Part G promises it survives: poison payloads, dead
/// workers, a broken ledger, and a saturated generation pool.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class GenerationResilienceTests
{
    private readonly PostgresFixture _postgres;
    private readonly MinioFixture _minio;

    /// <summary>Initialises a new instance of the <see cref="GenerationResilienceTests"/> class.</summary>
    /// <param name="postgres">The shared PostgreSQL fixture.</param>
    /// <param name="minio">The shared object storage fixture.</param>
    public GenerationResilienceTests(PostgresFixture postgres, MinioFixture minio)
    {
        _postgres = postgres;
        _minio = minio;
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task PoisonAccount_QuarantinesAfterThreeAttempts_RunContinues()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        (StatementRunRepository repo, StatementPeriod period, Guid[] accounts) =
            await GenerationSeed.SeedKnownAccountsAsync(_postgres, 6, ct).ConfigureAwait(true);
        Guid poison = accounts[0];

        using var ledger = new MutableLedgerFactory();
        ledger.SetFaults(("FaultInjection:PoisonAccountIds:0", poison.ToString()));

        using var worker = new GenerationWorkerFactory(
            _postgres.ConnectionStringFor("app_generation"),
            _minio.ServiceUrl,
            () => ledger.Server.CreateHandler());
        _ = worker.CreateClient(); // starts the hosted loops

        StatementRun run = await repo.CreateOrGetAsync(Guid.CreateVersion7(), period, null, ct).ConfigureAwait(true);

        RunCounters counters = await WaitForTerminalAsync(repo, run.Id, expectedTotal: 6, ct).ConfigureAwait(true);

        // ONE unrenderable account must never stop the other five (or thirty million). The poison
        // item burned its three attempts and sits quarantined; everything else finished.
        counters.Done.ShouldBe(5);
        counters.FailedFinal.ShouldBe(1);

        IReadOnlyList<FailedItem> failures = await repo
            .ListFailuresAsync(run.Id, maxAttempts: 3, afterItemId: 0, limit: 10, ct).ConfigureAwait(true);
        failures.Count.ShouldBe(1);
        failures[0].AccountId.ShouldBe(poison);
        failures[0].Attempts.ShouldBe(3);
        failures[0].LastError.ShouldNotBeNull();
        failures[0].LastError!.ShouldContain(
            "PoisonLedgerPayloadException",
            customMessage: "last_error carries the exception TYPE so an operator reads 'fix the data', not 'wait it out'");

        // And the run itself COMPLETED - quarantine is bookkeeping, not failure.
        // The status flip is the ORCHESTRATOR's monitor tick, up to one interval after the
        // counters go terminal - asserting immediately races it.
        await WaitUntilAsync(
            async () => (await repo.FindAsync(run.Id, ct).ConfigureAwait(true))!.Status == RunStatus.Completed,
            TimeSpan.FromSeconds(30), "the run never transitioned to COMPLETED after its items finished", ct).ConfigureAwait(true);

        // Retry is the operator's deliberate act: reset, heal the ledger, and the item completes.
        ledger.SetFaults(); // poison list emptied
        NpgsqlConnectionFactory factory = _postgres.ConnectionFactoryFor("app_generation");
        await using (factory.ConfigureAwait(true))
        {
            var uow = new StatementDelivery.Persistence.Uow.NpgsqlUnitOfWork(factory);
            int reset = await uow.ExecuteAsync(
                (tx, token) => repo.RetryFailuresAsync(run.Id, null, tx, token), ct).ConfigureAwait(true);
            reset.ShouldBe(1);
        }

        _ = await repo.TransitionAsync(run.Id, RunStatus.Completed, RunStatus.Running, ct).ConfigureAwait(true);
        counters = await WaitForTerminalAsync(repo, run.Id, expectedTotal: 6, ct).ConfigureAwait(true);
        counters.Done.ShouldBe(6, "after the cause is fixed, a retried poison item renders like any other");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task LedgerCircuitOpen_PausesRun_DoesNotFailIt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        (StatementRunRepository repo, StatementPeriod period, _) =
            await GenerationSeed.SeedKnownAccountsAsync(_postgres, 40, ct).ConfigureAwait(true);

        using var ledger = new MutableLedgerFactory();

        // MaxAttempts raised for this test: items failing while the breaker decides must not
        // quarantine - the outage is nobody's poison, which is the entire point of pausing.
        using var worker = new GenerationWorkerFactory(
            _postgres.ConnectionStringFor("app_generation"),
            _minio.ServiceUrl,
            () => ledger.Server.CreateHandler(),
            ("Generation:MaxAttempts", "10"));
        _ = worker.CreateClient();

        StatementRun run = await repo.CreateOrGetAsync(Guid.CreateVersion7(), period, null, ct).ConfigureAwait(true);

        // Phase 1: healthy - let real progress accumulate.
        await WaitUntilAsync(
            async () => (await repo.CountersAsync(run.Id, 10, ct).ConfigureAwait(true)).Done >= 5,
            TimeSpan.FromSeconds(60), "the run never made initial progress", ct).ConfigureAwait(true);

        // Phase 2: the ledger dies mid-run.
        ledger.SetFaults(("FaultInjection:ErrorRate", "1.0"));

        await WaitUntilAsync(
            async () => (await repo.FindAsync(run.Id, ct).ConfigureAwait(true))!.Status == RunStatus.Paused,
            TimeSpan.FromSeconds(60), "the run never paused on an open circuit", ct).ConfigureAwait(true);

        RunCounters during = await repo.CountersAsync(run.Id, 10, ct).ConfigureAwait(true);
        during.Done.ShouldBeGreaterThanOrEqualTo(5, "pausing must preserve completed work");
        during.FailedFinal.ShouldBe(0, "an outage must not quarantine items");

        // Phase 3: the ledger recovers; the run resumes and finishes. Nothing was thrown away.
        ledger.SetFaults(("FaultInjection:ErrorRate", "0.0"));

        RunCounters final = await WaitForTerminalAsync(
            repo, run.Id, expectedTotal: 40, ct, timeout: TimeSpan.FromSeconds(180), maxAttempts: 10)
            .ConfigureAwait(true);
        final.Done.ShouldBe(40);
        final.FailedFinal.ShouldBe(0);
        // The status flip is the ORCHESTRATOR's monitor tick, up to one interval after the
        // counters go terminal - asserting immediately races it.
        await WaitUntilAsync(
            async () => (await repo.FindAsync(run.Id, ct).ConfigureAwait(true))!.Status == RunStatus.Completed,
            TimeSpan.FromSeconds(30), "the run never transitioned to COMPLETED after its items finished", ct).ConfigureAwait(true);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task WorkerCrashMidRender_ItemIsRecovered()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        (StatementRunRepository repo, StatementPeriod period, Guid[] accounts) =
            await GenerationSeed.SeedKnownAccountsAsync(_postgres, 1, ct).ConfigureAwait(true);

        StatementRun run = await repo.CreateOrGetAsync(Guid.CreateVersion7(), period, null, ct).ConfigureAwait(true);
        _ = await repo.EnqueueBatchAsync(run.Id, accounts, null, ct).ConfigureAwait(true);
        _ = await repo.MarkRunningAsync(run.Id, ct).ConfigureAwait(true);

        // The "crash": a worker claims the item and is never heard from again. No release path
        // runs - exactly what SIGKILL looks like from the database's side.
        IReadOnlyList<ClaimedItem> claimed = await repo
            .ClaimBatchAsync(run.Id, "doomed-pod", 10, 3, ct).ConfigureAwait(true);
        claimed.Count.ShouldBe(1);

        await using (NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true))
        {
            _ = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE statement_run_item SET claimed_at = now() - INTERVAL '20 minutes' WHERE id = @id;",
                new { id = claimed[0].ItemId }, commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);
        }

        // Now the real machinery: reaper returns it, a healthy worker completes it.
        using var ledger = new MutableLedgerFactory();
        using var worker = new GenerationWorkerFactory(
            _postgres.ConnectionStringFor("app_generation"),
            _minio.ServiceUrl,
            () => ledger.Server.CreateHandler(),
            ("Generation:StaleClaimMinutes", "15"));
        _ = worker.CreateClient();

        RunCounters counters = await WaitForTerminalAsync(repo, run.Id, expectedTotal: 1, ct).ConfigureAwait(true);
        counters.Done.ShouldBe(1);

        // The burned attempt SURVIVED the recovery - attempt 2 completed it.
        (await ScalarAsync<int>(
            "SELECT attempts FROM statement_run_item WHERE id = @id;", new { id = claimed[0].ItemId }, ct)
            .ConfigureAwait(true))
            .ShouldBe(2, "the crash burned attempt 1 at claim time; recovery completed on attempt 2");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task GenerationSaturation_DoesNotDegradeDeliveryLatency()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // THE MONTH-END INCIDENT TEST. The generation fleet saturates ITS pool; the delivery
        // path must not notice, because the pools are partitioned by role. What this proves at
        // the Testcontainers tier is client-pool and role isolation on direct connections; the
        // PgBouncer per-database pool partition on top is compose/production configuration.
        SeededStatement seeded = await DownloadScenario
            .SeedAsync(_postgres, _minio, cancellationToken: ct).ConfigureAwait(true);
        using DeliveryApiFactory api = new(_postgres.ConnectionStringFor("app_delivery"));
        using HttpClient deliveryClient = api.CreateClient();
        deliveryClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", DeliveryApiFactory.TokenFor(seeded.CustomerId));

        // Bracket the seeded period: the from/to range is REQUIRED and capped at 84 months by
        // the API contract, and the old fixed 2020-2030 span was both 120 months wide and
        // nowhere near the seeder's deliberately-historical periods.
        var listUri = new Uri(
            $"/v1/customers/{seeded.CustomerId}/statements?from={seeded.Period.AddMonths(-1):yyyy-MM-dd}&to={seeded.Period.AddMonths(2):yyyy-MM-dd}&limit=10",
            UriKind.Relative);

        // Warm-up so the measurement is the database path, not host startup.
        (await deliveryClient.GetAsync(listUri, ct).ConfigureAwait(true)).EnsureSuccessStatusCode().Dispose();

        // Saturate generation: every slot in a size-8 app_generation pool holds a 6-second query.
        NpgsqlConnectionFactory generation = _postgres.ConnectionFactoryFor("app_generation", maxPoolSize: 8);
        await using (generation.ConfigureAwait(true))
        {
            using var saturation = new CancellationTokenSource();
            Task[] hogs =
            [
                .. Enumerable.Range(0, 8).Select(async _ =>
                {
                    await using NpgsqlConnection c = await generation
                        .OpenAsync(ConnectionIntent.Write, ct)
                        .ConfigureAwait(true);
                    _ = await c.ExecuteAsync(new CommandDefinition(
                        "SELECT pg_sleep(6);", commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);
                }),
            ];

            // While generation is pinned, the delivery path answers inside its SLO.
            var latencies = new List<double>();
            var stopwatch = new Stopwatch();

            for (int i = 0; i < 20; i++)
            {
                stopwatch.Restart();
                using HttpResponseMessage response = await deliveryClient.GetAsync(listUri, ct).ConfigureAwait(true);
                stopwatch.Stop();
                response.EnsureSuccessStatusCode();
                latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
            }

            await Task.WhenAll(hogs).ConfigureAwait(true);

            latencies.Sort();
            double p95 = latencies[(int)(latencies.Count * 0.95) - 1];
            p95.ShouldBeLessThan(1500,
                $"delivery p95 under generation saturation was {p95:F0}ms - the batch fleet is starving the customer path");
        }
    }

    // ---------------------------------------------------------------------------------------------

    private static async Task<RunCounters> WaitForTerminalAsync(
        StatementRunRepository repo,
        Guid runId,
        long expectedTotal,
        CancellationToken ct,
        TimeSpan? timeout = null,
        int maxAttempts = 3)
    {
        var limit = Stopwatch.StartNew();
        TimeSpan budget = timeout ?? TimeSpan.FromSeconds(120);

        while (true)
        {
            RunCounters counters = await repo.CountersAsync(runId, maxAttempts, ct).ConfigureAwait(false);
            if (counters.Terminal >= expectedTotal)
            {
                // Give the orchestrator one beat to flip COMPLETED.
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                return counters;
            }

            limit.Elapsed.ShouldBeLessThan(budget,
                $"run {runId} stalled: {counters.Done} done, {counters.FailedRetryable}+{counters.FailedFinal} failed of {expectedTotal}");
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        }
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition, TimeSpan timeout, string because, CancellationToken ct)
    {
        var limit = Stopwatch.StartNew();
        while (!await condition().ConfigureAwait(false))
        {
            limit.Elapsed.ShouldBeLessThan(timeout, because);
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        }
    }

    private async Task<T?> ScalarAsync<T>(string sql, object args, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<T>(new CommandDefinition(
            sql, args, commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(false);
    }
}

/// <summary>Seeds period-isolated accounts the MOCK LEDGER recognises.</summary>
internal static class GenerationSeed
{
    private static int s_offset;

    /// <summary>
    /// Seeds <paramref name="count"/> eligible accounts, all known to the ledger, in a period no
    /// other test uses. See RunPlanningTests.SeedAsync for the open/close isolation trick.
    /// </summary>
    public static async Task<(StatementRunRepository Repo, StatementPeriod Period, Guid[] Accounts)>
        SeedKnownAccountsAsync(PostgresFixture postgres, int count, CancellationToken ct)
    {
        int offset = Interlocked.Increment(ref s_offset);
        DateOnly anchor = new DateOnly(1950, 1, 1).AddMonths(offset);
        StatementPeriod period = StatementPeriod.ForMonth(anchor.Year, anchor.Month);

        var customer = Guid.CreateVersion7();
        Guid[] accounts = [.. Enumerable.Range(0, count * 2)
            .Select(static _ => Guid.CreateVersion7())
            .Where(static id => MockLedger.Api.LedgerGenerator.IsKnown(id))
            .Take(count)];
        accounts.Length.ShouldBe(count, "could not find enough ledger-known account ids");

        DateTimeOffset opened = new(period.Start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        DateTimeOffset closed = new(period.End.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        await using (NpgsqlConnection connection = await postgres.OpenAdminAsync(ct).ConfigureAwait(false))
        {
            // The isolation anchor lives decades before any provisioned partition, and a render
            // for an unprovisioned month dies at the INSERT ("no partition of relation
            // statement found"). Historical backfill provisions its partitions first in
            // production too; the V003 helper is that procedure.
            _ = await connection.ExecuteAsync(new CommandDefinition(
                "SELECT ensure_range_partitions('statement'::regclass, 'month', 3, @from);",
                new { @from = new DateTimeOffset(period.Start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) },
                commandTimeout: 60, cancellationToken: ct)).ConfigureAwait(false);

            _ = await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO customer (id, external_ref, status) VALUES (@customer, @ref, 'ACTIVE');
                INSERT INTO account (id, customer_id, account_number_masked, product_type, status, opened_at, closed_at)
                SELECT a, @customer, '****0000', 'CURRENT', 'ACTIVE', @opened, @closed FROM unnest(@ids) AS a;
                """,
                new { customer, @ref = customer.ToString("N"), ids = accounts, opened, closed },
                commandTimeout: 60, cancellationToken: ct)).ConfigureAwait(false);
        }

        return (new StatementRunRepository(postgres.ConnectionFactoryFor("app_generation", maxPoolSize: 30)), period, accounts);
    }
}
