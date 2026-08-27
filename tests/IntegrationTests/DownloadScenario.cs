using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;
using Shouldly;
using StatementDelivery.Domain.ValueObjects;

namespace IntegrationTests;

/// <summary>One seeded statement, with its bytes on disk.</summary>
/// <param name="CustomerId">The owning customer.</param>
/// <param name="AccountId">The account.</param>
/// <param name="StatementId">The statement.</param>
/// <param name="Period">The statement's period start, which is also its partition key.</param>
/// <param name="StorageKey">The content key, relative to the content root.</param>
/// <param name="Content">The exact bytes on disk.</param>
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
    /// <summary>Creates a temporary content root, deleted by the caller.</summary>
    /// <returns>An absolute path to a new empty directory.</returns>
    public static string CreateContentRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "statement-content-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Seeds a customer, an account, one AVAILABLE statement, and its file.</summary>
    /// <param name="postgres">The database fixture.</param>
    /// <param name="contentRoot">Where to write the statement's bytes.</param>
    /// <param name="sizeBytes">How many bytes to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The seeded statement.</returns>
    public static async Task<SeededStatement> SeedAsync(
        PostgresFixture postgres,
        string contentRoot,
        int sizeBytes = 4096,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        var customer = Guid.CreateVersion7();
        var account = Guid.CreateVersion7();
        var statement = Guid.CreateVersion7();

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        StatementPeriod period = StatementPeriod.ForMonth(today.Year, today.Month);

        string storageKey = $"statements/{period.Start:yyyy/MM}/{statement:N}.pdf";

        // Distinctive per statement, so a test that redeems a token for A and receives B's bytes
        // fails on the content rather than passing because both files happened to be zeros.
        byte[] content = new byte[sizeBytes];
        System.Security.Cryptography.RandomNumberGenerator.Fill(content.AsSpan(0, Math.Min(1024, sizeBytes)));

        string path = Path.Combine(contentRoot, storageKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content, cancellationToken).ConfigureAwait(false);

        await using NpgsqlConnection connection = await postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(false);

        _ = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO customer (id, external_ref, status) VALUES (@customer, @ref, 'ACTIVE');
            INSERT INTO account (id, customer_id, account_number_masked, product_type, status, opened_at)
                VALUES (@account, @customer, '****4321', 'SAVINGS', 'ACTIVE', now());
            INSERT INTO statement (
                id, account_id, customer_id, period_start, period_end, version, status,
                storage_key, storage_tier, size_bytes, wrapped_dek, kek_id, iv, auth_tag,
                retain_until, generated_at)
            VALUES (
                @statement, @account, @customer, @start, @end, 1, 'AVAILABLE',
                @storageKey, 'STANDARD', @size,
                '\\xdeadbeef'::bytea, 'kek-test', '\\x0102030405060708090a0b0c'::bytea,
                '\\x0f0e0d0c0b0a09080706050403020100'::bytea,
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
                storageKey,
                size = (long)sizeBytes,
                retain = RetentionPolicy.Default.RetainUntil(period),
            },
            commandTimeout: 60,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new SeededStatement(customer, account, statement, period.Start, storageKey, content);
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
