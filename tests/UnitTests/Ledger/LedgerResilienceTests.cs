using System.Net;
using Generation.Worker.Ledger;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using StatementDelivery.Domain.ValueObjects;
using Xunit;

namespace UnitTests.Ledger;

/// <summary>
/// The ledger client's retry behaviour, observed through a recording handler under the REAL
/// resilience pipeline that <c>AddLedgerClient</c> builds - not a re-implementation of it.
/// </summary>
public sealed class LedgerResilienceTests
{
    [Fact]
    public async Task LedgerTimeout_TriggersRetryWithJitter()
    {
        // A handler that always 503s, timestamping every attempt. The pipeline should make the
        // initial call plus three retries, with growing (exponential + jittered) gaps.
        var recorder = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        ILedgerClient client = BuildClient(recorder, out ServiceProvider provider);
        await using (provider.ConfigureAwait(true))
        {

            _ = await Should.ThrowAsync<HttpRequestException>(
                () => client.GetTransactionsAsync(
                    Guid.NewGuid(), StatementPeriod.ForMonth(2026, 8), CancellationToken.None))
                .ConfigureAwait(true);

            recorder.Attempts.Count.ShouldBe(4, "one initial attempt plus MaxRetryAttempts=3");

            // The delays must come from EXPONENTIAL backoff (base 200ms). Under LIVE jitter
            // the exponential shape is not per-sample assertable: Polly's
            // DecorrelatedJitterBackoffV2 draws each delay from curve position t_n = n + rand_n,
            // so a later gap can legitimately be smaller than an earlier one (t2 = 2.99 then
            // t3 = 3.01 yields a tiny third gap) - the original gap3 > gap1 assertion here
            // flaked ~1 in 5 full-suite runs on exactly that draw (146ms vs 285ms). The fix is
            // the same idiom as injectable TimeProvider: BuildClient pins the randomizer to a
            // midpoint draw (rand = 0.5, so t_n = n + 0.5 exactly), which makes the curve
            // positions strictly increasing by 1 and the gaps deterministically GROW - the
            // pipeline is still the real one, formula included; only the dice are loaded.
            // Expected gaps with the pinned draw: ~179ms, ~218ms, ~407ms.
            TimeSpan gap1 = recorder.Attempts[1] - recorder.Attempts[0];
            TimeSpan gap3 = recorder.Attempts[3] - recorder.Attempts[2];
            TimeSpan total = recorder.Attempts[3] - recorder.Attempts[0];

            gap1.ShouldBeGreaterThan(TimeSpan.FromMilliseconds(100), "retries must back off, not hammer");
            gap3.ShouldBeGreaterThan(gap1, "backoff must be exponential across attempts");
            total.ShouldBeGreaterThan(
                TimeSpan.FromMilliseconds(700),
                "three exponential delays from 200ms base total ~804ms pinned; constant 200ms would total 600ms");
        }
    }

    [Fact]
    public async Task NotFound_DoesNotRetry_AndThrowsUnknownAccount()
    {
        // 404 is an ANSWER, not a failure: retrying it three times would triple the load a
        // missing account puts on the ledger and delay quarantine by two backoffs.
        var recorder = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        ILedgerClient client = BuildClient(recorder, out ServiceProvider provider);
        await using (provider.ConfigureAwait(true))
        {

            _ = await Should.ThrowAsync<LedgerUnknownAccountException>(
                () => client.GetTransactionsAsync(
                    Guid.NewGuid(), StatementPeriod.ForMonth(2026, 8), CancellationToken.None))
                .ConfigureAwait(true);

            recorder.Attempts.Count.ShouldBe(1, "a 404 is deterministic; retrying it is pure waste");
        }
    }

    [Fact]
    public async Task MalformedPayload_SurfacesAsPoisonException()
    {
        var recorder = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                /*lang=json,strict*/ """{"accountId":"not-a-guid","openingBalanceMinorUnits":"NaN""",
                System.Text.Encoding.UTF8,
                "application/json"),
        });
        ILedgerClient client = BuildClient(recorder, out ServiceProvider provider);
        await using (provider.ConfigureAwait(true))
        {

            _ = await Should.ThrowAsync<PoisonLedgerPayloadException>(
                () => client.GetTransactionsAsync(
                    Guid.NewGuid(), StatementPeriod.ForMonth(2026, 8), CancellationToken.None))
                .ConfigureAwait(true);
        }
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>Builds the REAL client + pipeline from AddLedgerClient, over a recording handler.</summary>
    private static ILedgerClient BuildClient(RecordingHandler recorder, out ServiceProvider provider)
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Ledger:BaseUrl"] = "http://ledger.test",
            ["Ledger:AttemptTimeoutSeconds"] = "2",
            ["Ledger:RateLimitPerSecond"] = "1000",
        });

        // rand = 0.5 pins DecorrelatedJitterBackoffV2 to its midpoint curve: deterministic
        // delays, real pipeline. See LedgerTimeout_TriggersRetryWithJitter's comment.
        _ = builder.AddLedgerClient(retryJitterRandomizer: static () => 0.5);
        builder.Services.AddHttpClient(LedgerClientExtensions.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => recorder);

        provider = builder.Services.BuildServiceProvider();
        return provider.GetRequiredService<ILedgerClient>();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<DateTimeOffset> Attempts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Attempts)
            {
                Attempts.Add(DateTimeOffset.UtcNow);
            }

            return Task.FromResult(_respond(request));
        }
    }
}
