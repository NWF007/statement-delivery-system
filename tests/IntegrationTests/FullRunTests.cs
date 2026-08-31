using System.Diagnostics;
using System.Security.Cryptography;
using Dapper;
using Npgsql;
using Shouldly;
using StatementDelivery.Domain.Auditing;
using StatementDelivery.Domain.ValueObjects;
using StatementDelivery.Persistence.Auditing;
using StatementDelivery.Persistence.Runs;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// J5: the complete loop. Plan, render, encrypt, store, record, relay - then issue a link,
/// download, and verify the bytes. If this passes, the system works.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FullRunTests
{
    private const int Accounts = 1000;

    private readonly PostgresFixture _postgres;
    private readonly MinioFixture _minio;

    /// <summary>Initialises a new instance of the <see cref="FullRunTests"/> class.</summary>
    /// <param name="postgres">The shared PostgreSQL fixture.</param>
    /// <param name="minio">The shared object storage fixture.</param>
    public FullRunTests(PostgresFixture postgres, MinioFixture minio)
    {
        _postgres = postgres;
        _minio = minio;
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task FullRun_1000Accounts_AllStatementsAvailableAndDownloadable()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        (StatementRunRepository repo, StatementPeriod period, Guid[] accounts) =
            await GenerationSeed.SeedKnownAccountsAsync(_postgres, Accounts, ct).ConfigureAwait(true);

        using var ledger = new MutableLedgerFactory();
        using var worker = new GenerationWorkerFactory(
            _postgres.ConnectionStringFor("app_generation"),
            _minio.ServiceUrl,
            () => ledger.Server.CreateHandler(),
            ("Generation:ClaimBatchSize", "50"),
            ("Generation:RenderParallelism", "8"));
        _ = worker.CreateClient();

        // The run is requested the way an operator would: one idempotent create; the lease-holding
        // orchestrator plans it; the render loop drains it.
        var runStopwatch = Stopwatch.StartNew();
        StatementRun run = await repo.CreateOrGetAsync(Guid.CreateVersion7(), period, null, ct).ConfigureAwait(true);

        RunCounters counters = await WaitForCompletionAsync(repo, run.Id, ct).ConfigureAwait(true);
        runStopwatch.Stop();

        // ---- Acceptance 56: counts reconcile ---------------------------------------------------
        counters.Done.ShouldBe(Accounts);
        counters.FailedFinal.ShouldBe(0);
        (await repo.FindAsync(run.Id, ct).ConfigureAwait(true))!.TotalItems.ShouldBe(Accounts);

        // ---- Acceptance 57: statements exist and are encrypted ---------------------------------
        (await ScalarAsync<long>(
            """
            SELECT count(*) FROM statement
             WHERE period_start = @start AND status = 'AVAILABLE' AND wrapped_dek IS NOT NULL
               AND content_sha256 IS NOT NULL;
            """,
            new { start = period.Start }, ct).ConfigureAwait(true))
            .ShouldBe(Accounts);

        // ---- Acceptance 59: issue a link, download, verify the hash ----------------------------
        StatementProbe probe = await ProbeAsync(accounts[42], period, ct).ConfigureAwait(true);

        using var api = new DeliveryApiFactory(_postgres.ConnectionStringFor("app_delivery"));
        IssuedLink link = await DownloadScenario.IssueAsync(
            api,
            new SeededStatement(probe.CustomerId, accounts[42], probe.StatementId, period.Start, probe.StorageKey, []),
            cancellationToken: ct).ConfigureAwait(true);

        using var gateway = new DownloadGatewayFactory(
            _postgres.ConnectionStringFor("app_download"), _minio.ServiceUrl);
        using HttpClient client = gateway.CreateClient();

        byte[] downloaded = await client.GetByteArrayAsync(
            new Uri("/v1/d/" + link.Plaintext, UriKind.Relative), ct).ConfigureAwait(true);

        downloaded.AsSpan(0, 5).ToArray().ShouldBe("%PDF-"u8.ToArray(), "the delivered bytes are a PDF");
        SHA256.HashData(downloaded).ShouldBe(
            probe.ContentSha256,
            "downloaded plaintext must hash to the row's content_sha256 - render, encrypt, store, "
            + "decrypt and stream all agreeing about the same bytes");

        // ---- Acceptance 69: the outbox drains --------------------------------------------------
        var drain = Stopwatch.StartNew();
        while (await ScalarAsync<long>(
            "SELECT count(*) FROM outbox WHERE published_at IS NULL;", new { }, ct).ConfigureAwait(true) > 0)
        {
            drain.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60), "the outbox relay has stalled");
            await Task.Delay(500, ct).ConfigureAwait(true);
        }

        (await ScalarAsync<long>(
            "SELECT count(*) FROM outbox WHERE event_type = 'StatementAvailable' AND published_at IS NOT NULL;",
            new { }, ct).ConfigureAwait(true))
            .ShouldBeGreaterThanOrEqualTo(Accounts, "every rendered statement published its event");

        // ---- Acceptance 70: the audit chain still verifies after the whole run -----------------
        var verifier = new PostgresAuditVerifier(_postgres.ConnectionFactoryFor("app_delivery"));
        IEnumerable<(short ChainId, long LastSeq)> heads = await HeadsAsync(ct).ConfigureAwait(true);

        foreach ((short chainId, long lastSeq) in heads.Where(static h => h.LastSeq > 0))
        {
            ChainVerification verification = await verifier
                .VerifyChainAsync(chainId, 1, lastSeq, ct).ConfigureAwait(true);
            verification.Verified.ShouldBeTrue(
                $"chain {chainId} failed verification after the run - the batch subsystem corrupted the trail");
        }

        // The deliverable's throughput number, printed where the test log keeps it.
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Full run: {Accounts} statements in {runStopwatch.Elapsed.TotalSeconds:F1}s "
            + $"({Accounts / runStopwatch.Elapsed.TotalSeconds:F1} items/sec end to end)");
    }

    // ---------------------------------------------------------------------------------------------

    private static async Task<RunCounters> WaitForCompletionAsync(
        StatementRunRepository repo, Guid runId, CancellationToken ct)
    {
        var limit = Stopwatch.StartNew();
        while (true)
        {
            StatementRun? run = await repo.FindAsync(runId, ct).ConfigureAwait(false);
            if (run is { Status: RunStatus.Completed })
            {
                return await repo.CountersAsync(runId, 3, ct).ConfigureAwait(false);
            }

            RunCounters progress = await repo.CountersAsync(runId, 3, ct).ConfigureAwait(false);
            limit.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(300),
                $"run stalled at {progress.Done} done / {progress.FailedFinal} failed of {run?.TotalItems}");
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
    }

    private sealed record StatementProbe(Guid StatementId, Guid CustomerId, string StorageKey, byte[] ContentSha256);

    private async Task<StatementProbe> ProbeAsync(Guid accountId, StatementPeriod period, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(false);

        return await connection.QuerySingleAsync<StatementProbe>(new CommandDefinition(
            """
            SELECT id AS StatementId, customer_id AS CustomerId, storage_key AS StorageKey,
                   content_sha256 AS ContentSha256
              FROM statement
             WHERE account_id = @accountId AND period_start = @start;
            """,
            new { accountId, start = period.Start },
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(false);
    }

    private async Task<IEnumerable<(short ChainId, long LastSeq)>> HeadsAsync(CancellationToken ct)
    {
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(false);

        IEnumerable<(short, long)> rows = await connection.QueryAsync<(short, long)>(new CommandDefinition(
            "SELECT chain_id, last_seq FROM audit_chain_head ORDER BY chain_id;",
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(false);

        return rows;
    }

    private async Task<T?> ScalarAsync<T>(string sql, object args, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<T>(new CommandDefinition(
            sql, args, commandTimeout: 60, cancellationToken: ct)).ConfigureAwait(false);
    }
}
