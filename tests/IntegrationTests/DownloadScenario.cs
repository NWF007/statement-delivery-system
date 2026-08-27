using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;
using Shouldly;
using StatementDelivery.Domain.ValueObjects;

namespace IntegrationTests;

/// <summary>One seeded statement, with its bytes ENCRYPTED in object storage.</summary>
/// <param name="CustomerId">The owning customer.</param>
/// <param name="AccountId">The account.</param>
/// <param name="StatementId">The statement.</param>
/// <param name="Period">The statement period start, which is also its partition key.</param>
/// <param name="StorageKey">The object key, computed by StorageKeyScheme.</param>
/// <param name="Content">The exact PLAINTEXT bytes, for comparison against what is downloaded.</param>
public sealed record SeededStatement(
    Guid CustomerId,
    Guid AccountId,
    Guid StatementId,
    DateOnly Period,
    string StorageKey,
    byte[] Content);

/// <summary>One issued link, including the plaintext that exists nowhere else.</summary>
/// <param name="LinkId">The link identifier, used to revoke.</param>
/// <param name="Url">The full download URL.</param>
/// <param name="Plaintext">The token itself - the last path segment of the URL.</param>
/// <param name="ExpiresAt">When it stops working.</param>
public sealed record IssuedLink(string LinkId, string Url, string Plaintext, DateTimeOffset ExpiresAt);

/// <summary>
/// Seeds statements and drives the real issue endpoint, so redemption tests start from a token that
/// was minted the way production mints them.
/// </summary>
/// <remarks>
/// Inserting a token row directly would be faster and would test nothing about issuance. Going
/// through <c>POST /v1/statements/{id}/download-links</c> means every redemption test is also,
/// incidentally, a test that the two services agree about the token format.
/// </remarks>
public static class DownloadScenario
{
    /// <summary>
    /// Seeds a customer, an account, one AVAILABLE statement, and its ENCRYPTED object.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE OBJECT IS WRITTEN THROUGH THE PRODUCTION WRITE PATH, not by a fixture that knows the
    /// format. It mints a real CEK through the real key service, wraps a real DEK under it, and
    /// encrypts with the real framed cipher - so every redemption test is also, incidentally, a test
    /// that the writer and the reader agree about the format, the key hierarchy and the AAD.
    /// </para>
    /// <para>
    /// A hand-rolled seeder would be shorter and would assert only that the test agrees with itself.
    /// </para>
    /// <para>
    /// ORDER MATTERS: the customer row goes in FIRST, because customer_key has a foreign key to it
    /// and the write path mints a CEK before it encrypts anything.
    /// </para>
    /// </remarks>
    /// <param name="postgres">The database fixture.</param>
    /// <param name="minio">The object storage fixture.</param>
    /// <param name="sizeBytes">How many plaintext bytes to generate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The seeded statement.</returns>
    public static async Task<SeededStatement> SeedAsync(
        PostgresFixture postgres,
        MinioFixture minio,
        int sizeBytes = 4096,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        ArgumentNullException.ThrowIfNull(minio);

        var customer = Guid.CreateVersion7();
        var account = Guid.CreateVersion7();
        var statement = Guid.CreateVersion7();

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        StatementPeriod period = StatementPeriod.ForMonth(today.Year, today.Month);

        // Distinctive per statement, so a test that redeems a token for A and receives B's bytes
        // fails on the content rather than passing because both objects happened to be zeros.
        byte[] content = new byte[sizeBytes];
        System.Security.Cryptography.RandomNumberGenerator.Fill(content.AsSpan(0, Math.Min(1024, sizeBytes)));

        await using NpgsqlConnection connection = await postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(false);

        _ = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO customer (id, external_ref, status) VALUES (@customer, @ref, 'ACTIVE');
            INSERT INTO account (id, customer_id, account_number_masked, product_type, status, opened_at)
                VALUES (@account, @customer, '****4321', 'SAVINGS', 'ACTIVE', now());
            """,
            new { customer, account, @ref = customer.ToString("N") },
            commandTimeout: 60,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        (StatementDelivery.ServiceDefaults.Storage.S3StatementContentStore store, Amazon.S3.IAmazonS3 client) =
            minio.CreateStore(postgres);

        StatementDelivery.ServiceDefaults.Storage.StoredObject stored;

        using (client)
        {
            using var plaintext = new MemoryStream(content, writable: false);

            stored = await store.WriteAsync(
                plaintext,
                new StatementDelivery.Crypto.Framing.CryptoContext(statement, customer, 1),
                new StatementDelivery.Domain.Identifiers.AccountId(account),
                period,
                StatementDelivery.Crypto.Keys.CohortAssignment.KekIdFor(
                    StatementDelivery.Crypto.Keys.CohortAssignment.ForCustomer(
                        new StatementDelivery.Domain.Identifiers.CustomerId(customer))),
                cancellationToken).ConfigureAwait(false);
        }

        // size_bytes is the PLAINTEXT length. It is what Content-Length must say, and the ciphertext
        // is longer by the framing overhead - a client told the ciphertext length would wait forever
        // for bytes that do not exist.
        _ = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO statement (
                id, account_id, customer_id, period_start, period_end, version, status,
                storage_key, storage_tier, size_bytes, content_sha256,
                wrapped_dek, dek_algorithm, kek_id,
                retain_until, generated_at)
            VALUES (
                @statement, @account, @customer, @start, @end, 1, 'AVAILABLE',
                @storageKey, 'STANDARD', @size, @sha,
                @wrappedDek, @algorithm, @kekId,
                @retain, now());
            """,
            new
            {
                customer,
                account,
                statement,
                start = period.Start,
                end = period.End,
                storageKey = stored.Key,
                size = stored.PlaintextLength,
                sha = stored.Envelope.ContentSha256,
                wrappedDek = stored.Envelope.WrappedDek,
                algorithm = stored.Envelope.Algorithm,
                kekId = stored.Envelope.KekId,
                retain = RetentionPolicy.Default.RetainUntil(period),
            },
            commandTimeout: 60,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new SeededStatement(customer, account, statement, period.Start, stored.Key, content);
    }

    /// <summary>Issues a link through the real endpoint and returns the plaintext token.</summary>
    /// <param name="api">The Delivery.Api factory.</param>
    /// <param name="seeded">The statement to issue against.</param>
    /// <param name="ttlSeconds">Requested lifetime, or null for the default.</param>
    /// <param name="asCustomer">The subject to authenticate as; defaults to the owner.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The issued link.</returns>
    public static async Task<IssuedLink> IssueAsync(
        DeliveryApiFactory api,
        SeededStatement seeded,
        int? ttlSeconds = null,
        Guid? asCustomer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(seeded);

        using HttpResponseMessage response = await IssueRawAsync(
            api, seeded, ttlSeconds, asCustomer, cancellationToken).ConfigureAwait(false);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created);

        JsonElement body = await response.Content
            .ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);

        string url = body.GetProperty("url").GetString()!;

        return new IssuedLink(
            body.GetProperty("linkId").GetString()!,
            url,
            url[(url.LastIndexOf('/') + 1)..],
            body.GetProperty("expiresAt").GetDateTimeOffset());
    }

    /// <summary>Issues a link and returns the raw response, for the failure cases.</summary>
    /// <param name="api">The Delivery.Api factory.</param>
    /// <param name="seeded">The statement to issue against.</param>
    /// <param name="ttlSeconds">Requested lifetime, or null for the default.</param>
    /// <param name="asCustomer">The subject to authenticate as; defaults to the owner.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response.</returns>
    public static async Task<HttpResponseMessage> IssueRawAsync(
        DeliveryApiFactory api,
        SeededStatement seeded,
        int? ttlSeconds = null,
        Guid? asCustomer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(seeded);

        using HttpClient client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", DeliveryApiFactory.TokenFor(asCustomer ?? seeded.CustomerId));

        string period = seeded.Period.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var uri = new Uri(
            $"/v1/statements/{seeded.StatementId:D}/download-links?period={period}",
            UriKind.Relative);

        return await client
            .PostAsJsonAsync(uri, new { ttlSeconds }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Revokes a link through the real endpoint.</summary>
    /// <param name="api">The Delivery.Api factory.</param>
    /// <param name="linkId">The link identifier.</param>
    /// <param name="asCustomer">The subject to authenticate as.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response.</returns>
    public static async Task<HttpResponseMessage> RevokeAsync(
        DeliveryApiFactory api,
        string linkId,
        Guid asCustomer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);

        using HttpClient client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", DeliveryApiFactory.TokenFor(asCustomer));

        return await client
            .DeleteAsync(new Uri($"/v1/download-links/{linkId}", UriKind.Relative), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Backdates a token so it is already expired, without waiting for its TTL.</summary>
    /// <remarks>
    /// Both timestamps move, because <c>ck_token_ttl</c> requires <c>expires_at &gt; issued_at</c>
    /// and no more than an hour beyond it. Expiring a token by editing only one column would be
    /// rejected by the same constraint the production path relies on.
    /// </remarks>
    /// <param name="postgres">The database fixture.</param>
    /// <param name="linkId">The link to expire.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public static async Task ExpireAsync(PostgresFixture postgres, string linkId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        await using NpgsqlConnection connection = await postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(false);

        int updated = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE download_token
               SET issued_at  = now() - interval '10 minutes',
                   expires_at = now() - interval '5 seconds'
             WHERE id = @id;
            """,
            new { id = Guid.Parse(linkId) },
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        updated.ShouldBe(1, "the token to expire must exist");
    }
}
