using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MockLedger.Api;
using Shouldly;
using Xunit;

namespace UnitTests.Ledger;

/// <summary>
/// The mock ledger's contract: deterministic data, honest faults.
/// </summary>
/// <remarks>
/// Hosted with WebApplicationFactory - no Docker - so the determinism contract (identical bytes for
/// identical requests) is verified on every build on every machine.
/// </remarks>
public sealed class MockLedgerTests
{
    [Fact]
    public async Task SameAccountAndPeriod_ProducesIdenticalBytes()
    {
        // Byte equality of the raw response, not structural equality of the parsed model: the
        // renderer's determinism chain starts at these bytes.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new MockLedgerFactory();
        using HttpClient client = factory.CreateClient();

        var accountId = KnownAccountId();
        var uri = new Uri(
            $"/ledger/v1/accounts/{accountId}/transactions?from=2026-08-01&to=2026-08-31",
            UriKind.Relative);

        byte[] first = await client.GetByteArrayAsync(uri, cancellationToken).ConfigureAwait(true);
        byte[] second = await client.GetByteArrayAsync(uri, cancellationToken).ConfigureAwait(true);

        Convert.ToHexString(SHA256.HashData(second))
            .ShouldBe(Convert.ToHexString(SHA256.HashData(first)));
    }

    [Fact]
    public async Task Response_ReconcilesAndUsesLongMinorUnits()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new MockLedgerFactory();
        using HttpClient client = factory.CreateClient();

        LedgerResponse? response = await client.GetFromJsonAsync<LedgerResponse>(
            new Uri(
                $"/ledger/v1/accounts/{KnownAccountId()}/transactions?from=2026-08-01&to=2026-08-31",
                UriKind.Relative),
            cancellationToken).ConfigureAwait(true);

        response.ShouldNotBeNull();
        long sum = response.Transactions.Sum(static t => t.AmountMinorUnits);
        (response.OpeningBalanceMinorUnits + sum).ShouldBe(
            response.ClosingBalanceMinorUnits,
            "the ledger must reconcile - opening + transactions == closing");
    }

    [Fact]
    public async Task UnknownAccount_Returns404()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new MockLedgerFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri(
                $"/ledger/v1/accounts/{UnknownAccountId()}/transactions?from=2026-08-01&to=2026-08-31",
                UriKind.Relative),
            cancellationToken).ConfigureAwait(true);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RateLimit_Returns429WithRetryAfter()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new MockLedgerFactory(("FaultInjection:RateLimitPerSecond", "3"));
        using HttpClient client = factory.CreateClient();

        var uri = new Uri(
            $"/ledger/v1/accounts/{KnownAccountId()}/transactions?from=2026-08-01&to=2026-08-31",
            UriKind.Relative);

        HttpStatusCode last = HttpStatusCode.OK;
        string? retryAfter = null;

        // Blow through the ceiling; the fourth-or-later request inside one second must be shed.
        for (int i = 0; i < 10 && last != HttpStatusCode.TooManyRequests; i++)
        {
            using HttpResponseMessage response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(true);
            last = response.StatusCode;
            retryAfter = response.Headers.RetryAfter?.ToString();
        }

        last.ShouldBe(HttpStatusCode.TooManyRequests);
        retryAfter.ShouldNotBeNullOrWhiteSpace("a 429 without Retry-After teaches clients to guess");
    }

    [Fact]
    public async Task ErrorRate_One_MakesEveryRequest503()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new MockLedgerFactory(("FaultInjection:ErrorRate", "1.0"));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri(
                $"/ledger/v1/accounts/{KnownAccountId()}/transactions?from=2026-08-01&to=2026-08-31",
                UriKind.Relative),
            cancellationToken).ConfigureAwait(true);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task PoisonAccount_ReturnsMalformedPayload()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Guid poison = KnownAccountId();
        using var factory = new MockLedgerFactory(("FaultInjection:PoisonAccountIds:0", poison.ToString()));
        using HttpClient client = factory.CreateClient();

        var uri = new Uri(
            $"/ledger/v1/accounts/{poison}/transactions?from=2026-08-01&to=2026-08-31",
            UriKind.Relative);

        // A 200 whose body does not deserialise - the exact shape the quarantine path must
        // survive: not an HTTP failure the retry policy sees, but a payload failure it cannot.
        using HttpResponseMessage response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(true);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await Should.ThrowAsync<System.Text.Json.JsonException>(
            () => client.GetFromJsonAsync<LedgerResponse>(uri, cancellationToken)).ConfigureAwait(true);
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>A deterministic account id the ledger recognises.</summary>
    internal static Guid KnownAccountId()
    {
        for (int i = 0; ; i++)
        {
            var candidate = new Guid(i, 0, 0, [1, 2, 3, 4, 5, 6, 7, 8]);
            if (LedgerGenerator.IsKnown(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>A deterministic account id the ledger reports as unknown.</summary>
    internal static Guid UnknownAccountId()
    {
        for (int i = 0; ; i++)
        {
            var candidate = new Guid(i, 0, 0, [9, 9, 9, 9, 9, 9, 9, 9]);
            if (!LedgerGenerator.IsKnown(candidate))
            {
                return candidate;
            }
        }
    }
}

/// <summary>Hosts the real MockLedger.Api in-process, with per-test fault configuration.</summary>
public sealed class MockLedgerFactory : WebApplicationFactory<FaultInjectionOptions>
{
    private readonly (string Key, string? Value)[] _settings;

    /// <summary>Initialises a new instance of the <see cref="MockLedgerFactory"/> class.</summary>
    /// <param name="settings">Configuration overrides - the fault-injection knobs.</param>
    public MockLedgerFactory(params (string Key, string? Value)[] settings) => _settings = settings;

    /// <inheritdoc />
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(Environments.Development); // AspNetCore.Hosting extension

        // Latency off by default in tests: the fault under test is dialled in explicitly, and a
        // 20ms ambient delay on every request just slows the suite.
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["FaultInjection:LatencyMs:P50"] = "0",
                ["FaultInjection:LatencyMs:P99"] = "0",
            }.Concat(_settings.Select(s =>
                new KeyValuePair<string, string?>(s.Key, s.Value)))));
    }
}
