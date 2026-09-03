using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using StatementDelivery.ServiceDefaults.Storage;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// The statutory payloads, pinned. A 409 with no citation passes a status-code assertion and
/// fails the requirement — these tests assert the FIELDS.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ComplianceResponseTests
{
    private readonly PostgresFixture _postgres;
    private readonly MinioFixture _minio;

    /// <summary>Initialises a new instance of the <see cref="ComplianceResponseTests"/> class.</summary>
    /// <param name="postgres">The shared PostgreSQL fixture.</param>
    /// <param name="minio">The shared object storage fixture.</param>
    public ComplianceResponseTests(PostgresFixture postgres, MinioFixture minio)
    {
        _postgres = postgres;
        _minio = minio;
    }

    private DeliveryApiFactory CreateApi() => new(_postgres.ConnectionStringFor("app_delivery"));

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Erasure_BlockedByHold_Returns409_WithCaseReference()
    {
        // A STATEMENT-scoped hold - the scope the erasure gate was blind to - must block the
        // erasure request AND cite the case, because the reference is the only key that can
        // ever release the block.
        CancellationToken ct = TestContext.Current.CancellationToken;
        (Guid customer, Guid statement) = await SeedPendingWithKeyAsync(retainFuture: true, ct).ConfigureAwait(true);
        await PlaceHoldRowAsync(statement, customer, "CASE-2026-0042", ct).ConfigureAwait(true);

        using DeliveryApiFactory api = CreateApi();
        using HttpClient client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", DeliveryApiFactory.DpoTokenFor(customer));

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/customers/{customer:D}/erasure",
            new { reason = "POPIA s24", requestReference = "DSR-1" },
            ct).ConfigureAwait(true);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(true);
        body.GetProperty("reason").GetString().ShouldBe("LEGAL_HOLD");
        body.GetProperty("caseReference").GetString().ShouldBe(
            "CASE-2026-0042",
            "the citation IS the statutory argument - a bare 409 is a bug report, not a defensible response");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Erasure_BlockedByRetention_Returns409_WithBasisAndRetainUntil()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (Guid customer, _) = await SeedPendingWithKeyAsync(retainFuture: true, ct).ConfigureAwait(true);

        using DeliveryApiFactory api = CreateApi();
        using HttpClient client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", DeliveryApiFactory.DpoTokenFor(customer));

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/customers/{customer:D}/erasure",
            new { reason = "POPIA s24", requestReference = "DSR-2" },
            ct).ConfigureAwait(true);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(true);
        body.GetProperty("reason").GetString().ShouldBe("STATUTORY_RETENTION");
        string basis = body.GetProperty("basis").GetString() ?? "";
        basis.ShouldNotBeNullOrWhiteSpace("the statute must be cited");
        basis.ShouldContain("FICA");
        body.GetProperty("retainUntil").GetDateTime().ShouldBeGreaterThan(
            DateTime.UtcNow.Date, "the caller learns WHEN the obligation ends");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Erasure_Accepted_Returns202_WithScheduledForAndCoolingOffEnds()
    {
        // Scheduled, NOT immediate: 202 with the window's dates, the key row armed, the
        // material untouched.
        CancellationToken ct = TestContext.Current.CancellationToken;
        (Guid customer, _) = await SeedPendingWithKeyAsync(retainFuture: false, ct).ConfigureAwait(true);

        using DeliveryApiFactory api = CreateApi();
        using HttpClient client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", DeliveryApiFactory.DpoTokenFor(customer));

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/customers/{customer:D}/erasure",
            new { reason = "POPIA s24", requestReference = "DSR-3" },
            ct).ConfigureAwait(true);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(true);
        _ = body.GetProperty("erasureId").GetGuid();
        DateTimeOffset scheduledFor = body.GetProperty("scheduledFor").GetDateTimeOffset();
        scheduledFor.ShouldBeGreaterThan(
            DateTimeOffset.UtcNow.AddDays(6), "irreversible operations get a reversal window");
        _ = body.GetProperty("coolingOffEnds").GetDateTimeOffset();

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true);
        (string Status, bool CekIntact) key = await connection.QuerySingleAsync<(string, bool)>(
            new CommandDefinition(
                "SELECT status, wrapped_cek IS NOT NULL FROM customer_key WHERE customer_id = @customer;",
                new { customer },
                commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);

        key.Status.ShouldBe("SCHEDULED_DESTRUCTION", "the fuse is armed");
        key.CekIntact.ShouldBeTrue("and NOTHING is destroyed until the window closes and the executor re-evaluates");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Hold_WithoutCaseReference_IsRejectedWith400()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (Guid customer, Guid statement) = await SeedPendingWithKeyAsync(retainFuture: true, ct).ConfigureAwait(true);

        using DeliveryApiFactory api = CreateApi();
        using HttpClient client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", DeliveryApiFactory.StaffTokenFor(customer));

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/statements/{statement:D}/legal-holds",
            new { reason = "litigation" },
            ct).ConfigureAwait(true);

        response.StatusCode.ShouldBe(
            HttpStatusCode.BadRequest,
            "a hold nobody can trace to a case is a hold nobody will ever dare release - enforced server-side");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task StatementHold_Placement_PopulatesCustomerId()
    {
        // The endpoint's half of the customer_id fix: a statement-scoped hold row carries its
        // customer, which is what makes it visible to the erasure gate (V021, ADR-0040).
        CancellationToken ct = TestContext.Current.CancellationToken;
        (Guid customer, Guid statement) = await SeedPendingWithKeyAsync(retainFuture: true, ct).ConfigureAwait(true);

        using DeliveryApiFactory api = CreateApi();
        using HttpClient client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", DeliveryApiFactory.StaffTokenFor(customer));

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/statements/{statement:D}/legal-holds",
            new { reason = "litigation", caseReference = "CASE-2026-POPULATE" },
            ct).ConfigureAwait(true);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true);
        (Guid? StatementId, Guid? CustomerId) row = await connection.QuerySingleAsync<(Guid?, Guid?)>(
            new CommandDefinition(
                "SELECT statement_id, customer_id FROM legal_hold WHERE case_reference = 'CASE-2026-POPULATE';",
                commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);

        row.StatementId.ShouldBe(statement);
        row.CustomerId.ShouldBe(customer, "a null customer here is the exact row shape the customer-scope defect depended on");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Hold_StorageFailure_DoesNotLeaveDbHoldOrphaned()
    {
        // ADR-0037's ordering under an injected fault: the storage hold comes FIRST, so a
        // storage failure must abort before any database record exists. The dangerous
        // direction - a DB row claiming protection the store does not enforce - must be
        // unreachable.
        CancellationToken ct = TestContext.Current.CancellationToken;
        SeededStatement seeded = await DownloadScenario.SeedAsync(_postgres, _minio, cancellationToken: ct)
            .ConfigureAwait(true);

        using DeliveryApiFactory api = CreateApi();
        using WebApplicationFactoryHelpers.FaultingAdminFactory faulting = new(api);
        using HttpClient client = faulting.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", DeliveryApiFactory.StaffTokenFor(seeded.CustomerId));

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/statements/{seeded.StatementId:D}/legal-holds",
            new { reason = "litigation", caseReference = "CASE-2026-FAULT" },
            ct).ConfigureAwait(true);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true);
        long rows = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM legal_hold WHERE case_reference = 'CASE-2026-FAULT';",
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);

        rows.ShouldBe(0, "storage-set-but-DB-missing is the harmless residue; DB-set-but-storage-missing must be unreachable");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Download_OfErasedStatement_Returns410_Not404()
    {
        // 404 and 410 are materially different facts: the resource EXISTED and was
        // INTENTIONALLY DESTROYED, and the customer is entitled to that fact rather than being
        // told it never existed.
        CancellationToken ct = TestContext.Current.CancellationToken;
        SeededStatement seeded = await DownloadScenario.SeedAsync(_postgres, _minio, cancellationToken: ct)
            .ConfigureAwait(true);

        using DeliveryApiFactory api = CreateApi();
        IssuedLink link = await DownloadScenario.IssueAsync(api, seeded, cancellationToken: ct).ConfigureAwait(true);

        // The erasure lands between issue and click - the executor's statement pass.
        await using (NpgsqlConnection admin = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true))
        {
            _ = await admin.ExecuteAsync(new CommandDefinition(
                """
                UPDATE statement
                   SET status = 'PURGED', purged_at = now(),
                       storage_key = NULL, wrapped_dek = NULL, iv = NULL, auth_tag = NULL
                 WHERE id = @id;
                """,
                new { id = seeded.StatementId },
                commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);
        }

        using var gateway = new DownloadGatewayFactory(
            _postgres.ConnectionStringFor("app_download"), _minio.ServiceUrl, 30, 120, 0);
        using HttpClient client = gateway.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/v1/d/" + link.Plaintext, UriKind.Relative), ct).ConfigureAwait(true);

        response.StatusCode.ShouldBe(HttpStatusCode.Gone, "410, never 404: destroyed is a fact, not an absence");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Erasure_Destroy_RunsVacuum()
    {
        // The NULL update alone leaves the wrapped CEK on the page as a dead tuple; the VACUUM
        // is what removes it. pg_stat's last_vacuum is the observable - the stats collector
        // updates asynchronously, hence the short poll.
        CancellationToken ct = TestContext.Current.CancellationToken;
        (Guid customer, _) = await SeedPendingWithKeyAsync(retainFuture: false, ct).ConfigureAwait(true);

        StatementDelivery.Persistence.Connections.NpgsqlConnectionFactory factory =
            _postgres.ConnectionFactoryFor("app_retention");
        await using (factory.ConfigureAwait(true))
        {
            var store = new StatementDelivery.Persistence.Keys.CustomerKeyRepository(factory);
            (await store.DestroyAsync(
                new StatementDelivery.Domain.Identifiers.CustomerId(customer), "DSR-VACUUM", ct)
                .ConfigureAwait(true)).ShouldBeTrue();
        }

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true);
        DateTime? lastVacuum = null;
        for (int attempt = 0; attempt < 20 && lastVacuum is null; attempt++)
        {
            lastVacuum = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                "SELECT last_vacuum FROM pg_stat_user_tables WHERE relname = 'customer_key';",
                commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);
            if (lastVacuum is null)
            {
                await Task.Delay(250, ct).ConfigureAwait(true);
            }
        }

        lastVacuum.ShouldNotBeNull("DestroyAsync must VACUUM, or the CEK survives in MVCC dead tuples");
    }

    // ─── Seeding ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A customer with an ACTIVE key row and one PENDING statement (no storage key, so the
    /// API's store-side hold walk has nothing to HEAD against the unreachable test endpoint).
    /// </summary>
    private async Task<(Guid Customer, Guid Statement)> SeedPendingWithKeyAsync(
        bool retainFuture, CancellationToken ct)
    {
        var customer = Guid.CreateVersion7();
        var account = Guid.CreateVersion7();
        var statement = Guid.CreateVersion7();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        var period = StatementDelivery.Domain.ValueObjects.StatementPeriod.ForMonth(today.Year, today.Month);

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true);
        _ = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO customer (id, external_ref, status) VALUES (@customer, @ref, 'ACTIVE');
            INSERT INTO account (id, customer_id, account_number_masked, product_type, status, opened_at)
                VALUES (@account, @customer, '****7777', 'CURRENT', 'ACTIVE', now());
            INSERT INTO statement (id, account_id, customer_id, period_start, period_end,
                                   version, status, retain_until)
                VALUES (@statement, @account, @customer, @start, @end, 1, 'PENDING', @retain);
            INSERT INTO customer_key (customer_id, cohort_id, kek_id, wrapped_cek, cek_algorithm, status)
                VALUES (@customer, 7, 'kek-test', @cek, 'AES-256-KeyWrap', 'ACTIVE');
            """,
            new
            {
                customer,
                account,
                statement,
                @ref = customer.ToString("N"),
                start = period.Start,
                end = period.End,
                retain = retainFuture ? period.Start.AddYears(7) : today.AddYears(-1),
                cek = new byte[48],
            },
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);

        return (customer, statement);
    }

    private async Task PlaceHoldRowAsync(Guid statement, Guid customer, string caseRef, CancellationToken ct)
    {
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true);
        _ = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO legal_hold (id, statement_id, customer_id, case_reference, reason, placed_by)
            VALUES (@id, @statement, @customer, @caseRef, 'litigation', 'test');
            """,
            new { id = Guid.CreateVersion7(), statement, customer, caseRef },
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);
    }
}

/// <summary>Factory helpers for fault injection.</summary>
internal static class WebApplicationFactoryHelpers
{
    /// <summary>A Delivery.Api host whose object-admin store always fails — the S3 outage.</summary>
    internal sealed class FaultingAdminFactory : IDisposable
    {
        private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Delivery.Api.Configuration.JwtOptions> _inner;

        public FaultingAdminFactory(DeliveryApiFactory api) =>
            _inner = api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.AddSingleton<IStatementObjectAdmin>(new ThrowingObjectAdmin())));

        public HttpClient CreateClient() => _inner.CreateClient();

        public void Dispose() => _inner.Dispose();
    }

    private sealed class ThrowingObjectAdmin : IStatementObjectAdmin
    {
        public Task<IReadOnlyList<string>> DeleteObjectVersionsAsync(string key, CancellationToken cancellationToken) =>
            throw new Amazon.S3.AmazonS3Exception("injected outage");

        public Task<ObjectRetentionInfo> GetRetentionAsync(string key, CancellationToken cancellationToken) =>
            throw new Amazon.S3.AmazonS3Exception("injected outage");

        public Task SetLegalHoldAsync(string key, bool place, CancellationToken cancellationToken) =>
            throw new Amazon.S3.AmazonS3Exception("injected outage");

        public Task<ObjectKeyPage> ListKeysAsync(string prefix, string? continuationToken, int maxKeys, CancellationToken cancellationToken) =>
            throw new Amazon.S3.AmazonS3Exception("injected outage");
    }
}
