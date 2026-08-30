using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;
using Shouldly;
using StatementDelivery.Domain.Auditing;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Domain.ValueObjects;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// The read path over real HTTP, against the real service.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReadPathSecurityTests
{
    private readonly PostgresFixture _postgres;

    /// <summary>Initialises a new instance of the <see cref="ReadPathSecurityTests"/> class.</summary>
    /// <param name="postgres">The shared PostgreSQL fixture.</param>
    public ReadPathSecurityTests(PostgresFixture postgres) => _postgres = postgres;

    private DeliveryApiFactory CreateFactory() =>
        new(_postgres.ConnectionStringFor("app_delivery"));

    private static HttpClient Authenticated(DeliveryApiFactory factory, Guid subject)
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", DeliveryApiFactory.TokenFor(subject));
        return client;
    }

    private async Task<(Guid Customer, Guid Account, Guid Statement, DateOnly Period)> SeedAsync(
        CancellationToken cancellationToken)
    {
        var customer = Guid.CreateVersion7();
        var account = Guid.CreateVersion7();
        var statement = Guid.CreateVersion7();

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        StatementPeriod period = StatementPeriod.ForMonth(today.Year, today.Month);

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(false);

        _ = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO customer (id, external_ref, status) VALUES (@customer, @ref, 'ACTIVE');
            INSERT INTO account (id, customer_id, account_number_masked, product_type, status, opened_at)
                VALUES (@account, @customer, '****4321', 'SAVINGS', 'ACTIVE', now());
            INSERT INTO statement (
                id, account_id, customer_id, period_start, period_end, version, status,
                storage_key, storage_tier, size_bytes, content_sha256,
                wrapped_dek, dek_algorithm, kek_id,
                retain_until, generated_at)
            VALUES (
                @statement, @account, @customer, @start, @end, 1, 'AVAILABLE',
                'statements/secret/path.pdf', 'STANDARD', 91234,

                -- 32 bytes exactly, required on AVAILABLE rows by V015.
                --
                -- decode(..., 'hex') rather than a bytea literal, and that is not cosmetic: inside
                -- a C# raw string there are no escapes, so the previous '\xdeadbeef' reached
                -- PostgreSQL as ESCAPE-format input and decoded to TEN bytes rather than four -
                -- which is how it sat silently under V013's 40-byte wrapped-key floor.
                decode(repeat('ab', 32), 'hex'),

                -- 61 bytes: the real envelope size (version + nonce + key + tag), so this row has
                -- the shape a real one would and clears ck_statement_dek_is_wrapped.
                decode(repeat('cd', 61), 'hex'), 'AES-256-GCM', 'kek-super-secret',
                @retain, now());
            """,
            new
            {
                customer,
                account,
                statement,
                @ref = customer.ToString("N"),
                start = period.Start,
                end = period.End,
                retain = RetentionPolicy.Default.RetainUntil(period),
            },
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return (customer, account, statement, period.Start);
    }

    private static string Range() =>
        "from=" + DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-2).ToString("yyyy-MM-01", CultureInfo.InvariantCulture)
        + "&to=" + DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(2).ToString("yyyy-MM-01", CultureInfo.InvariantCulture);

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task ListStatements_ForOwnCustomer_Succeeds()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (Guid customer, _, Guid statement, _) = await SeedAsync(cancellationToken).ConfigureAwait(true);

        using DeliveryApiFactory factory = CreateFactory();
        using HttpClient client = Authenticated(factory, customer);

        using HttpResponseMessage response = await client
            .GetAsync(new Uri($"/v1/customers/{customer}/statements?{Range()}&limit=10", UriKind.Relative), cancellationToken)
            .ConfigureAwait(true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
        body.ShouldContain(statement.ToString("D"));
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task ListStatements_ForAnotherCustomer_Returns404NotForbidden()
    {
        // THE IDOR CASE. Token for A, route for B.
        //
        // 404, NOT 403. A 403 would confirm that customer B exists - which is exactly the oracle an
        // enumeration attack needs. Walk the identifier space, collect the 403s, and you have a list
        // of valid customers without reading a byte of their data.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (Guid mine, _, _, _) = await SeedAsync(cancellationToken).ConfigureAwait(true);
        (Guid theirs, _, _, _) = await SeedAsync(cancellationToken).ConfigureAwait(true);

        using DeliveryApiFactory factory = CreateFactory();
        using HttpClient client = Authenticated(factory, mine);

        using HttpResponseMessage response = await client
            .GetAsync(new Uri($"/v1/customers/{theirs}/statements?{Range()}", UriKind.Relative), cancellationToken)
            .ConfigureAwait(true);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden, "403 would confirm the customer exists");

        // And a customer that does not exist at all must be INDISTINGUISHABLE from the one above.
        using HttpResponseMessage nonexistent = await client
            .GetAsync(new Uri($"/v1/customers/{Guid.CreateVersion7()}/statements?{Range()}", UriKind.Relative), cancellationToken)
            .ConfigureAwait(true);

        nonexistent.StatusCode.ShouldBe(response.StatusCode);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task GetStatement_OwnedByAnother_Returns404()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (Guid mine, _, _, _) = await SeedAsync(cancellationToken).ConfigureAwait(true);
        (_, _, Guid theirStatement, DateOnly period) = await SeedAsync(cancellationToken).ConfigureAwait(true);

        using DeliveryApiFactory factory = CreateFactory();
        using HttpClient client = Authenticated(factory, mine);

        using HttpResponseMessage owned = await client
            .GetAsync(new Uri($"/v1/statements/{theirStatement}?period={period:yyyy-MM-dd}", UriKind.Relative), cancellationToken)
            .ConfigureAwait(true);

        using HttpResponseMessage missing = await client
            .GetAsync(new Uri($"/v1/statements/{Guid.CreateVersion7()}?period={period:yyyy-MM-dd}", UriKind.Relative), cancellationToken)
            .ConfigureAwait(true);

        owned.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        owned.StatusCode.ShouldBe(missing.StatusCode, "'not yours' and 'does not exist' must be indistinguishable");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task StatementResponse_NeverContainsStorageKeyOrCryptoFields()
    {
        // The seeded row DOES carry a storage key, a wrapped DEK, a KEK id, an IV and an auth tag.
        // If any of them reaches the wire, an attacker has the storage layout and the envelope
        // encryption structure - the map to everything.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (Guid customer, _, Guid statement, DateOnly period) = await SeedAsync(cancellationToken).ConfigureAwait(true);

        using DeliveryApiFactory factory = CreateFactory();
        using HttpClient client = Authenticated(factory, customer);

        using HttpResponseMessage list = await client
            .GetAsync(new Uri($"/v1/customers/{customer}/statements?{Range()}", UriKind.Relative), cancellationToken)
            .ConfigureAwait(true);
        using HttpResponseMessage single = await client
            .GetAsync(new Uri($"/v1/statements/{statement}?period={period:yyyy-MM-dd}", UriKind.Relative), cancellationToken)
            .ConfigureAwait(true);

        foreach (HttpResponseMessage response in (HttpResponseMessage[])[list, single])
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);

            foreach (string forbidden in (string[])
                     [
                         "storageKey", "storage_key", "statements/secret/path.pdf",
                         "wrappedDek", "wrapped_dek",
                         "kekId", "kek_id", "kek-super-secret",
                         "iv", "authTag", "auth_tag",
                         "contentSha256", "content_sha256",
                         "storageTier", "storage_tier",
                     ])
            {
                body.ShouldNotContain(forbidden, Case.Insensitive, $"'{forbidden}' must never reach the wire");
            }
        }
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task DeniedAccess_IsAudited_WithAnInternalReasonThatIsNeverReturned()
    {
        // AN AUDIT LOG THAT RECORDS ONLY SUCCESSES CANNOT DETECT ENUMERATION. The denials are the
        // signal: the attacker walking identifiers produces nothing but 404s, and 404s are exactly
        // what such a log would omit.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (Guid mine, _, _, _) = await SeedAsync(cancellationToken).ConfigureAwait(true);
        var theirs = Guid.CreateVersion7();

        using DeliveryApiFactory factory = CreateFactory();
        using HttpClient client = Authenticated(factory, mine);

        using HttpResponseMessage response = await client
            .GetAsync(new Uri($"/v1/customers/{theirs}/statements?{Range()}", UriKind.Relative), cancellationToken)
            .ConfigureAwait(true);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
        body.ShouldNotContain(DenialReason.SubjectMismatch, Case.Insensitive, "the denial reason is INTERNAL");
        body.ShouldNotContain(theirs.ToString("D"), Case.Insensitive);

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true);

        var recorded = await connection.QuerySingleOrDefaultAsync<(string Action, string Outcome, string Reason)?>(
            new CommandDefinition(
                """
                SELECT action AS "Action", outcome AS "Outcome", denial_reason_code AS "Reason"
                  FROM audit_event
                 WHERE customer_id = @mine
                   AND outcome = 'DENIED'
                 ORDER BY occurred_at DESC
                 LIMIT 1;
                """,
                new { mine },
                commandTimeout: 30,
                cancellationToken: cancellationToken)).ConfigureAwait(true);

        recorded.ShouldNotBeNull("the denial must be recorded");
        recorded.Value.Action.ShouldBe(AuditAction.AccessDenied);
        recorded.Value.Outcome.ShouldBe(AuditOutcome.Denied);
        recorded.Value.Reason.ShouldBe(DenialReason.SubjectMismatch);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task SuccessfulRead_IsAudited()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (Guid customer, _, _, _) = await SeedAsync(cancellationToken).ConfigureAwait(true);

        using DeliveryApiFactory factory = CreateFactory();
        using HttpClient client = Authenticated(factory, customer);

        using HttpResponseMessage response = await client
            .GetAsync(new Uri($"/v1/customers/{customer}/statements?{Range()}", UriKind.Relative), cancellationToken)
            .ConfigureAwait(true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true);

        int events = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT count(*)::int FROM audit_event
             WHERE customer_id = @customer AND action = @action AND outcome = 'SUCCESS';
            """,
            new { customer, action = AuditAction.StatementListViewed },
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(true);

        events.ShouldBeGreaterThan(0, "every read produces an audit event");
    }

    [Theory(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    [InlineData("")]                                             // no range at all
    [InlineData("from=2026-01-01")]                              // half a range
    [InlineData("from=2026-09-01&to=2026-01-01")]                // inverted
    [InlineData("from=2000-01-01&to=2026-01-01")]                // wider than retention
    [InlineData("from=not-a-date&to=2026-01-01")]                // unparseable
    public async Task InvalidDateRange_Returns400(string query)
    {
        // The range is part of the contract, not a filter. Rejecting a bad one with 400 is what
        // keeps an unbounded scan from ever reaching the database.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (Guid customer, _, _, _) = await SeedAsync(cancellationToken).ConfigureAwait(true);

        using DeliveryApiFactory factory = CreateFactory();
        using HttpClient client = Authenticated(factory, customer);

        using HttpResponseMessage response = await client
            .GetAsync(new Uri($"/v1/customers/{customer}/statements?{query}", UriKind.Relative), cancellationToken)
            .ConfigureAwait(true);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task MalformedCursor_Returns400_NotAnUnhandledException()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (Guid customer, _, _, _) = await SeedAsync(cancellationToken).ConfigureAwait(true);

        using DeliveryApiFactory factory = CreateFactory();
        using HttpClient client = Authenticated(factory, customer);

        using HttpResponseMessage response = await client
            .GetAsync(new Uri($"/v1/customers/{customer}/statements?{Range()}&cursor=!!!not-a-cursor!!!", UriKind.Relative), cancellationToken)
            .ConfigureAwait(true);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "a caller-supplied cursor is input, not a crash");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task GetStatement_WithoutPeriod_Returns400()
    {
        // The period is the partition key. Omitting it would turn a point lookup into a scan of
        // every partition, so it is required rather than optional.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (Guid customer, _, Guid statement, _) = await SeedAsync(cancellationToken).ConfigureAwait(true);

        using DeliveryApiFactory factory = CreateFactory();
        using HttpClient client = Authenticated(factory, customer);

        using HttpResponseMessage response = await client
            .GetAsync(new Uri($"/v1/statements/{statement}", UriKind.Relative), cancellationToken)
            .ConfigureAwait(true);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Unauthenticated_IsRejected()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (Guid customer, _, _, _) = await SeedAsync(cancellationToken).ConfigureAwait(true);

        using DeliveryApiFactory factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client
            .GetAsync(new Uri($"/v1/customers/{customer}/statements?{Range()}", UriKind.Relative), cancellationToken)
            .ConfigureAwait(true);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Pagination_ReturnsAStableCursor()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (Guid customer, Guid account, _, _) = await SeedAsync(cancellationToken).ConfigureAwait(true);

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        await using (NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true))
        {
            for (int offset = -2; offset <= 0; offset++)
            {
                StatementPeriod period = StatementPeriod.ForMonth(
                    today.AddMonths(offset).Year, today.AddMonths(offset).Month);

                _ = await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO statement (id, account_id, customer_id, period_start, period_end,
                                           version, status, storage_key, size_bytes, content_sha256,
                                           wrapped_dek, dek_algorithm, kek_id, retain_until, generated_at)
                    VALUES (@id, @account, @customer, @start, @end, 7, 'AVAILABLE', 'k', 1,
                            decode(repeat('ab', 32), 'hex'),
                            decode(repeat('cd', 61), 'hex'), 'AES-256-GCM', 'kek-test', @retain, now());
                    """,
                    new
                    {
                        id = Guid.CreateVersion7(),
                        account,
                        customer,
                        start = period.Start,
                        end = period.End,
                        retain = RetentionPolicy.Default.RetainUntil(period),
                    },
                    commandTimeout: 30,
                    cancellationToken: cancellationToken)).ConfigureAwait(true);
            }
        }

        using DeliveryApiFactory factory = CreateFactory();
        using HttpClient client = Authenticated(factory, customer);

        using HttpResponseMessage first = await client
            .GetAsync(new Uri($"/v1/customers/{customer}/statements?{Range()}&limit=1", UriKind.Relative), cancellationToken)
            .ConfigureAwait(true);

        JsonElement page = await first.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(true);

        page.GetProperty("hasMore").GetBoolean().ShouldBeTrue();
        string cursor = page.GetProperty("nextCursor").GetString().ShouldNotBeNull();

        Cursor.TryDecode(cursor, out _).ShouldBeTrue("the cursor the API hands out must be one it can read back");

        using HttpResponseMessage second = await client
            .GetAsync(new Uri($"/v1/customers/{customer}/statements?{Range()}&limit=1&cursor={Uri.EscapeDataString(cursor)}", UriKind.Relative), cancellationToken)
            .ConfigureAwait(true);

        second.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonElement nextPage = await second.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(true);
        string firstId = page.GetProperty("items")[0].GetProperty("id").GetString()!;
        string secondId = nextPage.GetProperty("items")[0].GetProperty("id").GetString()!;

        secondId.ShouldNotBe(firstId, "the second page must not repeat the first");
    }
}
