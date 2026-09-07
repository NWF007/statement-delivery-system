using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using StatementDelivery.Domain.ValueObjects;
using StatementDelivery.Persistence.Connections;

namespace SeedTool;

/// <summary>
/// The demo mode: a small, known dataset with REAL, downloadable statements, produced by driving
/// the running stack's own generation pipeline through its API. Volume seeding writes statement
/// rows without objects behind them, which is right for a benchmark and a 404 in a demo, so demo
/// mode seeds customers and accounts only and lets the generation worker do the rest.
/// </summary>
/// <remarks>
/// Idempotent end to end: the ten demo customers are upserted, run creation is idempotent per
/// period on the API side, and the volume seed is skipped when its marker rows already exist.
/// Runs inside the compose stack as the one-shot <c>seed-demo</c> service, and on a host with
/// <c>dotnet run --project tools/seed -- --demo</c>.
/// </remarks>
internal static class DemoRun
{
    /// <summary>The first documented demo customer; the README names it.</summary>
    public const string FirstDemoCustomerId = "11111111-1111-1111-1111-111111111101";

    private const string StaffSubject = "00000000-0000-0000-0000-000000000001";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Polls the API's readiness endpoint until it answers 200. Compose orders the container
    /// after the services are healthy; on a host this is what makes "up, then seed" safe to type
    /// without waiting.
    /// </summary>
    public static async Task WaitForApiAsync(HttpClient http, ILogger logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        DemoLog.WaitingForApi(logger, http.BaseAddress!);
        var deadline = DateTime.UtcNow.AddMinutes(5);

        while (true)
        {
            try
            {
                using HttpResponseMessage response = await http.GetAsync("health/ready", ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not up yet. Expected during a cold start.
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"{http.BaseAddress}health/ready did not answer 200 within five minutes.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Whether the volume seed for this <paramref name="seed"/> has already been written.</summary>
    public static async Task<bool> AlreadySeededAsync(IDbConnectionFactory connections, int seed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connections);
        await using NpgsqlConnection connection = await connections.OpenAsync(ConnectionIntent.Write, ct).ConfigureAwait(false);
        long existing = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM customer WHERE external_ref LIKE @marker;",
            new { marker = string.Create(CultureInfo.InvariantCulture, $"EXT-{seed}-%") },
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(false);
        return existing > 0;
    }

    /// <summary>
    /// Upserts the ten demo customers, 11111111-1111-1111-1111-111111111101 through ...110, each
    /// with one account 22222222-2222-2222-2222-2222222222NN. Every one of those account ids hashes
    /// to "known" in the mock ledger (LedgerGenerator.IsKnown), so the generation run renders a
    /// real statement for each.
    /// </summary>
    public static async Task EnsureDemoCustomersAsync(IDbConnectionFactory connections, ILogger logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connections);
        await using NpgsqlConnection connection = await connections.OpenAsync(ConnectionIntent.Write, ct).ConfigureAwait(false);

        for (int n = 1; n <= 10; n++)
        {
            string suffix = n.ToString("D2", CultureInfo.InvariantCulture);
            var customerId = Guid.Parse($"11111111-1111-1111-1111-1111111111{suffix}");
            var accountId = Guid.Parse($"22222222-2222-2222-2222-2222222222{suffix}");

            _ = await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO customer (id, external_ref, status)
                VALUES (@customerId, @externalRef, 'ACTIVE')
                ON CONFLICT (id) DO NOTHING;
                INSERT INTO account (id, customer_id, account_number_masked, product_type, status, opened_at, closed_at)
                VALUES (@accountId, @customerId, @masked, 'CURRENT', 'ACTIVE', @opened, NULL)
                ON CONFLICT (id) DO NOTHING;
                """,
                new
                {
                    customerId,
                    externalRef = "DEMO-CUSTOMER-" + suffix,
                    accountId,
                    masked = "****11" + suffix,
                    opened = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
                },
                commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(false);
        }

        DemoLog.DemoCustomersInPlace(logger);
    }

    /// <summary>
    /// Requests a generation run for <paramref name="period"/> through the API, as an operator
    /// would, and waits for the worker to complete it: render, encrypt, upload, audit.
    /// </summary>
    public static async Task GenerateAsync(HttpClient http, StatementPeriod period, ILogger logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);

        string staffToken = await MintTokenAsync(http, StaffSubject, staff: true, ct).ConfigureAwait(false);
        http.DefaultRequestHeaders.Authorization = new("Bearer", staffToken);

        using HttpResponseMessage created = await http.PostAsJsonAsync(
            "v1/statement-runs",
            new { periodStart = period.Start, periodEnd = period.End },
            Json,
            ct).ConfigureAwait(false);
        _ = created.EnsureSuccessStatusCode();

        RunStatus run = (await created.Content.ReadFromJsonAsync<RunStatus>(Json, ct).ConfigureAwait(false))
            ?? throw new InvalidOperationException("POST /v1/statement-runs returned no body.");
        DemoLog.RunRequested(logger, run.RunId, period.Start, period.End);

        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (!string.Equals(run.Status, "COMPLETED", StringComparison.Ordinal))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"Run {run.RunId} is still {run.Status} after ten minutes ({run.Done} done, {run.FailedFinal} quarantined). "
                    + "See: docker compose logs generation-worker --tail 50");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            run = (await http.GetFromJsonAsync<RunStatus>($"v1/statement-runs/{run.RunId}", Json, ct).ConfigureAwait(false))
                ?? throw new InvalidOperationException("GET /v1/statement-runs/{runId} returned no body.");
        }

        DemoLog.RunCompleted(logger, run.RunId, run.Done, run.FailedFinal);
    }

    /// <summary>Prints the demo customers and their statement ids for <paramref name="period"/>, ready to paste.</summary>
    public static async Task PrintAsync(IDbConnectionFactory connections, StatementPeriod period, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connections);
        await using NpgsqlConnection connection = await connections.OpenAsync(ConnectionIntent.Write, ct).ConfigureAwait(false);

        IEnumerable<(Guid CustomerId, Guid StatementId)> rows = await connection.QueryAsync<(Guid, Guid)>(new CommandDefinition(
            """
            SELECT s.customer_id, s.id
              FROM statement s
              JOIN statement_run_item r ON r.statement_id = s.id
             WHERE s.status = 'AVAILABLE'
               AND s.period_start = @periodStart::date
               AND s.customer_id::text LIKE '11111111-1111-1111-1111-1111111111%'
             ORDER BY s.customer_id;
            """,
            // A DateTime, not the DateOnly: this tool wires its own services and does not carry the
            // Dapper DateOnly handler the services register through AddPersistence.
            new { periodStart = period.Start.ToDateTime(TimeOnly.MinValue) },
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"Demo-ready. The ten documented demo customers and their statements for {period.Start:yyyy-MM-dd}:");
        foreach ((Guid customerId, Guid statementId) in rows)
        {
            Console.WriteLine($"CUSTOMER_ID={customerId}  STATEMENT_ID={statementId}");
        }

        Console.WriteLine($"PERIOD={period.Start:yyyy-MM-dd}");
        Console.WriteLine($"Mint a token for any of them:  POST /v1/dev/tokens?customerId={FirstDemoCustomerId}");
    }

    private static async Task<string> MintTokenAsync(HttpClient http, string customerId, bool staff, CancellationToken ct)
    {
        string query = staff ? $"v1/dev/tokens?customerId={customerId}&staff=true" : $"v1/dev/tokens?customerId={customerId}";
        using HttpResponseMessage response = await http.PostAsync(query, content: null, ct).ConfigureAwait(false);
        _ = response.EnsureSuccessStatusCode();

        using JsonDocument body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        return body.RootElement.GetProperty("accessToken").GetString()
            ?? throw new InvalidOperationException("The dev token response carried no accessToken.");
    }

    /// <summary>The subset of the API's run response the demo needs.</summary>
    private sealed record RunStatus(Guid RunId, string Status, long Done, long FailedFinal);
}

internal static partial class DemoLog
{
    [LoggerMessage(EventId = 6010, Level = LogLevel.Information,
        Message = "Demo mode: waiting for {Api}health/ready before seeding.")]
    public static partial void WaitingForApi(ILogger logger, Uri api);

    [LoggerMessage(EventId = 6011, Level = LogLevel.Information,
        Message = "Demo mode: the volume seed is already present; skipping it. For a clean slate: docker compose down -v")]
    public static partial void AlreadySeeded(ILogger logger);

    [LoggerMessage(EventId = 6012, Level = LogLevel.Information,
        Message = "Demo customers 11111111-1111-1111-1111-111111111101 .. 110 are in place.")]
    public static partial void DemoCustomersInPlace(ILogger logger);

    [LoggerMessage(EventId = 6013, Level = LogLevel.Information,
        Message = "Generation run {RunId} requested for {PeriodStart:yyyy-MM-dd}..{PeriodEnd:yyyy-MM-dd} (real render -> encrypt -> upload).")]
    public static partial void RunRequested(ILogger logger, Guid runId, DateOnly periodStart, DateOnly periodEnd);

    [LoggerMessage(EventId = 6014, Level = LogLevel.Information,
        Message = "Generation run {RunId} completed: {Done} rendered, {Quarantined} quarantined.")]
    public static partial void RunCompleted(ILogger logger, Guid runId, long done, long quarantined);

    [LoggerMessage(EventId = 6015, Level = LogLevel.Information,
        Message = "SEED_DEMO is false; leaving the stack empty.")]
    public static partial void DisabledByEnvironment(ILogger logger);
}
