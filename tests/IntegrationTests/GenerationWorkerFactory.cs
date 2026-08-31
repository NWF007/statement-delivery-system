using Generation.Worker.Configuration;
using Generation.Worker.Ledger;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace IntegrationTests;

/// <summary>
/// Hosts the REAL Generation.Worker - all four hosted loops - against the Testcontainers stack,
/// with its ledger HTTP traffic routed into an in-process MockLedger.
/// </summary>
/// <remarks>
/// <para>
/// The worker's <c>ILedgerClient</c> is a real <c>HttpClient</c> with the full resilience
/// pipeline. Pointing it at the mock without opening a socket means swapping the PRIMARY handler
/// for the mock server's in-memory handler - the resilience handler stack (rate limit, retry,
/// breaker, timeouts) stays exactly as production builds it, wrapped around the swap. What is
/// NOT exercised this way is the TCP layer itself; the compose stack covers that.
/// </para>
/// <para>
/// Connects as <c>app_generation</c>, the role the fleet actually runs as - so a privilege the
/// migration forgot to grant fails here, not at month-end.
/// </para>
/// </remarks>
public sealed class GenerationWorkerFactory : WebApplicationFactory<GenerationWorkerOptions>
{
    private readonly string _connectionString;
    private readonly string _minioServiceUrl;
    private readonly Func<HttpMessageHandler> _ledgerHandler;
    private readonly (string Key, string? Value)[] _settings;

    /// <summary>Initialises a new instance of the <see cref="GenerationWorkerFactory"/> class.</summary>
    /// <param name="connectionString">The app_generation connection string.</param>
    /// <param name="minioServiceUrl">The MinIO endpoint.</param>
    /// <param name="ledgerHandler">Factory for the mock ledger's in-memory handler.</param>
    /// <param name="settings">Configuration overrides.</param>
    public GenerationWorkerFactory(
        string connectionString,
        string minioServiceUrl,
        Func<HttpMessageHandler> ledgerHandler,
        params (string Key, string? Value)[] settings)
    {
        _connectionString = connectionString;
        _minioServiceUrl = minioServiceUrl;
        _ledgerHandler = ledgerHandler;
        _settings = settings;
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(Environments.Development);

        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Postgres:PrimaryConnectionString"] = _connectionString,
                ["Postgres:MaxPoolSize"] = "30",

                ["ObjectStorage:BucketName"] = MinioFixture.BucketName,
                ["ObjectStorage:ServiceUrl"] = _minioServiceUrl,
                ["ObjectStorage:AccessKey"] = MinioFixture.AccessKey,
                ["ObjectStorage:SecretKey"] = MinioFixture.SecretKey,
                ["ObjectStorage:ForcePathStyle"] = "true",
                ["ObjectStorage:Lock:Mode"] = "GOVERNANCE",

                ["Crypto:KeyProvider"] = "Local",
                ["Crypto:LocalKeys:MasterSecret"] = MinioFixture.MasterSecret,

                ["Rendering:License"] = "Community",

                // The base address must parse; the swapped handler ignores the host.
                ["Ledger:BaseUrl"] = "http://mockledger.test",
                ["Ledger:RateLimitPerSecond"] = "500",
                ["Ledger:BreakDurationSeconds"] = "2",

                // Tight loops so tests observe transitions in seconds, not minutes.
                ["Generation:PollIntervalSeconds"] = "1",
                ["Generation:MonitorIntervalSeconds"] = "1",
                ["Generation:ClaimBatchSize"] = "10",
                ["Generation:RenderParallelism"] = "4",
                ["Generation:MaxAttempts"] = "3",

                ["Audit:ChainCount"] = "16",
                ["Partitioning:MaintenanceEnabled"] = "false",
                ["Lease:TimeToLiveSeconds"] = "10",
                ["Cache:ConnectionString"] = string.Empty,
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = string.Empty,
            }.Concat(_settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))));

        // Route the ledger client's transport into the in-process mock. Registered in
        // ConfigureTestServices so it wins; the resilience handler added by AddLedgerClient is
        // keyed to the same client name and continues to wrap this handler.
        builder.ConfigureTestServices(services =>
            services.AddHttpClient(LedgerClientExtensions.ClientName)
                .ConfigurePrimaryHttpMessageHandler(_ledgerHandler));
    }
}

/// <summary>
/// Hosts MockLedger.Api in-process with faults that can be CHANGED MID-TEST.
/// </summary>
/// <remarks>
/// The circuit-recovery test needs the ledger to fail hard, then heal, within one factory
/// lifetime - a static configuration cannot do that. This wraps an in-memory configuration
/// provider whose values can be swapped and reloaded, which flows through
/// <c>IOptionsMonitor&lt;FaultInjectionOptions&gt;</c> into the fault injector on the next request.
/// </remarks>
public sealed class MutableLedgerFactory : WebApplicationFactory<MockLedger.Api.FaultInjectionOptions>
{
    private readonly MutableConfigurationSource _mutable = new();

    /// <summary>Replaces the fault settings and notifies the options monitor.</summary>
    /// <param name="settings">The new FaultInjection values, as configuration pairs.</param>
    public void SetFaults(params (string Key, string? Value)[] settings) => _mutable.Replace(settings);

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(Environments.Development);

        builder.ConfigureAppConfiguration((context, configuration) =>
        {
            _ = configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["FaultInjection:LatencyMs:P50"] = "0",
                ["FaultInjection:LatencyMs:P99"] = "0",
            });
            configuration.Add(_mutable);
        });
    }

    private sealed class MutableConfigurationSource : IConfigurationSource
    {
        private readonly MutableConfigurationProvider _provider = new();

        public void Replace((string Key, string? Value)[] settings) => _provider.Replace(settings);

        public IConfigurationProvider Build(IConfigurationBuilder builder) => _provider;

        private sealed class MutableConfigurationProvider : ConfigurationProvider
        {
            public void Replace((string Key, string? Value)[] settings)
            {
                Data.Clear();
                foreach ((string key, string? value) in settings)
                {
                    Data[key] = value;
                }

                OnReload();
            }
        }
    }
}
