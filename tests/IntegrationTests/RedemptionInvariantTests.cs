using System.Net;
using Dapper;
using Npgsql;
using Shouldly;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// The two invariants the redemption path must hold when its dependencies misbehave.
/// </summary>
/// <remarks>
/// <para>
/// Both of these were broken in shipped code and both were invisible to the existing suite, for the
/// same reason: every other redemption test runs against one healthy database with a working audit
/// writer, and under those conditions the broken code and the correct code are indistinguishable.
/// </para>
/// <para>
/// A test that only exercises the happy path cannot tell a guarantee from a coincidence.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class RedemptionInvariantTests
{
    private readonly PostgresFixture _postgres;
    private readonly MinioFixture _minio;

    /// <summary>Initialises a new instance of the <see cref="RedemptionInvariantTests"/> class.</summary>
    /// <param name="postgres">The shared PostgreSQL fixture.</param>
    /// <param name="minio">The shared object storage fixture.</param>
    public RedemptionInvariantTests(PostgresFixture postgres, MinioFixture minio)
    {
        _postgres = postgres;
        _minio = minio;
    }

    private DeliveryApiFactory CreateApi() => new(_postgres.ConnectionStringFor("app_delivery"));

    private static Uri Redeem(string plaintext) => new("/v1/d/" + plaintext, UriKind.Relative);

    // =============================================================================================
    //  PART A. THE SECURITY-GATING READ MUST NOT BE ROUTED TO A REPLICA.
    // =============================================================================================

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Redemption_SucceedsEvenWhenReplicaLags()
    {
        // WHAT THIS PROVES, AND WHY IT IS WORTH A SECOND DATABASE.
        //
        // The statement lookup on the redemption path decides whether to serve. It used to run at
        // ConnectionIntent.ReadEventual on its own connection, so with a replica configured it asked
        // the replica. A replica that has not caught up returns nothing for a row that exists.
        //
        // The consume had already succeeded and its transaction still committed, so the failure mode
        // was: customer's single-use token spent, 404 returned, statement perfectly intact on the
        // primary. Nothing in the suite noticed, because no other test configures a replica at all.
        //
        // The lagging replica here holds the schema and none of the rows - the worst case, and the
        // one that makes the assertion unambiguous. With the fix the read runs inside the consume's
        // transaction on the primary, so the replica's contents are irrelevant and this returns 200.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SeededStatement seeded = await DownloadScenario
            .SeedAsync(_postgres, _minio, cancellationToken: cancellationToken).ConfigureAwait(true);

        using DeliveryApiFactory api = CreateApi();
        IssuedLink link = await DownloadScenario
            .IssueAsync(api, seeded, cancellationToken: cancellationToken).ConfigureAwait(true);

        string lagging = await _postgres
            .CreateLaggingReplicaAsync("statements_lagging_read", "app_download", cancellationToken)
            .ConfigureAwait(true);

        using var gateway = new DownloadGatewayFactory(
            _postgres.ConnectionStringFor("app_download"),
            _minio.ServiceUrl,
            replicaConnectionString: lagging);

        using HttpClient client = gateway.CreateClient();
        using HttpResponseMessage response = await client
            .GetAsync(Redeem(link.Plaintext), cancellationToken).ConfigureAwait(true);

        byte[] downloaded = await response.Content
            .ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(true);

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            "the statement exists on the primary; a lagging replica must not be able to deny it");

        // Not just a 200 - the RIGHT BYTES. A 200 with an empty body would satisfy the status
        // assertion while proving nothing about which database the envelope came from.
        downloaded.ShouldBe(
            seeded.Content,
            "the decrypted content must match, which it cannot if the crypto columns came from a stale replica");

        // And the token was spent for a download that actually happened, which is the whole point:
        // before the fix it was spent for a 404.
        (await ConsumedAtAsync(link.LinkId, cancellationToken).ConfigureAwait(true))
            .ShouldNotBeNull("a served download consumes its token");
    }

    // =============================================================================================
    //  PART B. AN OPERATION WITH NO AUDIT RECORD MUST BE IMPOSSIBLE.
    // =============================================================================================

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Redemption_WhenAuditWriteFails_DoesNotConsumeToken()
    {
        // THIS REPLACES UnitOfWork_AuditFailure_RollsBackBusinessOperation, WHICH WAS VACUOUS.
        //
        // That test opened its own transaction and called IAuditWriter.AppendAsync directly - a
        // shape no production code used. It proved NpgsqlUnitOfWork rolls back on exception, which
        // is trivially true, and it did NOT prove the rule stated in its own first line, because
        // every real write path committed its business change in one transaction and appended the
        // audit record in another.
        //
        // This drives the real endpoint instead, so it fails against the code as it was shipped:
        // the token came back consumed, with no audit record and a 500. It can only pass when the
        // audit append shares the consume's transaction.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SeededStatement seeded = await DownloadScenario
            .SeedAsync(_postgres, _minio, cancellationToken: cancellationToken).ConfigureAwait(true);

        using DeliveryApiFactory api = CreateApi();
        IssuedLink link = await DownloadScenario
            .IssueAsync(api, seeded, cancellationToken: cancellationToken).ConfigureAwait(true);

        // ---- Redemption while the audit writer is broken --------------------------------------
        using (var broken = new DownloadGatewayFactory(
            _postgres.ConnectionStringFor("app_download"),
            _minio.ServiceUrl,
            failAuditAppend: true))
        {
            using HttpClient client = broken.CreateClient();
            using HttpResponseMessage response = await client
                .GetAsync(Redeem(link.Plaintext), cancellationToken).ConfigureAwait(true);

            ((int)response.StatusCode).ShouldBeGreaterThanOrEqualTo(
                500,
                "a read that cannot be recorded is a read that does not happen");
        }

        // ---- The invariant --------------------------------------------------------------------
        (await ConsumedAtAsync(link.LinkId, cancellationToken).ConfigureAwait(true))
            .ShouldBeNull(
                "the audit append failed, so the consume must have rolled back with it - otherwise "
                + "an attacker who can break audit writes can spend tokens unobserved");

        (await AuditRowCountAsync(seeded.StatementId, cancellationToken).ConfigureAwait(true))
            .ShouldBe(0, "no audit row may survive a transaction that rolled back");

        // ---- And the rollback was CLEAN ---------------------------------------------------------
        // The assertion that matters most. A token left in a half-consumed or lock-wedged state
        // would satisfy every assertion above and still be worthless to the customer. Redeeming
        // successfully once the audit writer recovers is what proves the row was genuinely restored.
        using (var healthy = new DownloadGatewayFactory(
            _postgres.ConnectionStringFor("app_download"),
            _minio.ServiceUrl))
        {
            using HttpClient client = healthy.CreateClient();
            using HttpResponseMessage response = await client
                .GetAsync(Redeem(link.Plaintext), cancellationToken).ConfigureAwait(true);

            byte[] downloaded = await response.Content
                .ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(true);

            response.StatusCode.ShouldBe(
                HttpStatusCode.OK,
                "the token was never consumed, so it must still redeem once auditing recovers");

            downloaded.ShouldBe(seeded.Content);
        }

        (await ConsumedAtAsync(link.LinkId, cancellationToken).ConfigureAwait(true))
            .ShouldNotBeNull("the successful redemption consumed it");
    }

    private async Task<DateTime?> ConsumedAtAsync(string linkId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT consumed_at FROM download_token WHERE id = @id;",
            new { id = Guid.Parse(linkId) },
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private async Task<int> AuditRowCountAsync(Guid statementId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM audit_event WHERE statement_id = @id AND action = 'DOWNLOAD_STARTED';",
            new { id = statementId },
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
