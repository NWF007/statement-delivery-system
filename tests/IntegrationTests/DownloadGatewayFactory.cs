using Download.Gateway.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace IntegrationTests;

/// <summary>
/// Hosts the REAL Download.Gateway against the Testcontainers PostgreSQL instance.
/// </summary>
/// <remarks>
/// <para>
/// Connects as <c>app_download</c>, the least-privileged role, and not as an administrator. That
/// matters more here than anywhere else in the repository: the gateway's inability to INSERT into
/// <c>download_token</c> is a real control, and a test host connecting as a superuser would prove
/// nothing about it while quietly passing.
/// </para>
/// <para>
/// The generic parameter is <see cref="GatewayRateLimitOptions"/> rather than <c>Program</c>.
/// Top-level statements generate an internal <c>Program</c> in the global namespace, so two
/// service assemblies referenced by one test project cannot both expose it. Any public type from
/// the right assembly locates the entry point equally well.
/// </para>
/// </remarks>
public sealed class DownloadGatewayFactory : WebApplicationFactory<GatewayRateLimitOptions>
{
    private readonly string _connectionString;
    private readonly string _contentRoot;
    private readonly int _redeemPerMinute;
    private readonly int _permitLimit;
    private readonly int _denialFloorMilliseconds;
    private readonly int _maxConcurrentDownloads;

    /// <summary>Initialises a new instance of the <see cref="DownloadGatewayFactory"/> class.</summary>
    /// <param name="connectionString">The app_download connection string for the test container.</param>
    /// <param name="contentRoot">The directory holding statement files.</param>
    /// <param name="redeemPerMinute">
    /// The fleet-wide per-address redemption budget. Raised by the concurrency test, which fires
    /// fifty requests from one address on purpose and would otherwise be measuring the rate limiter
    /// rather than the atomic consume.
    /// </param>
    /// <param name="permitLimit">The in-process per-address request budget.</param>
    /// <param name="denialFloorMilliseconds">
    /// The uniform-denial timing floor. Zero by default so that a fifty-way concurrency test does
    /// not spend three seconds sleeping; <c>Denials_ArePaddedToTheConfiguredTimingFloor</c> raises
    /// it and asserts the padding actually happens.
    /// </param>
    /// <param name="maxConcurrentDownloads">
    /// The process-wide streaming concurrency permit count. Lowered to 1 by the test that proves a
    /// concurrency-limiter rejection still carries Retry-After.
    /// </param>
    public DownloadGatewayFactory(
        string connectionString,
        string contentRoot,
        int redeemPerMinute = 30,
        int permitLimit = 120,
        int denialFloorMilliseconds = 0,
        int maxConcurrentDownloads = 128)
    {
        _connectionString = connectionString;
        _contentRoot = contentRoot;
        _redeemPerMinute = redeemPerMinute;
        _permitLimit = permitLimit;
        _denialFloorMilliseconds = denialFloorMilliseconds;
        _maxConcurrentDownloads = maxConcurrentDownloads;
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
                ["Postgres:MaxPoolSize"] = "60",

                ["ContentStore:RootPath"] = _contentRoot,

                // Download.Gateway calls AddObjectStorage(), whose BucketName is [Required] and
                // validated on start - so without this the host refuses to boot and every test in
                // this file fails at construction rather than on its assertion. Prompt 3 uses the
                // filesystem content store and never touches the bucket; Prompt 4 makes it real.
                ["ObjectStorage:BucketName"] = "statements-test",
                ["ObjectStorage:ServiceUrl"] = "http://localhost:9000",

                ["Audit:ChainCount"] = "16",
                ["Partitioning:MaintenanceEnabled"] = "false",

                // No Redis: the distributed limiter falls back to per-process counters with the
                // same limits, which is the documented fail-closed behaviour.
                ["Cache:ConnectionString"] = string.Empty,
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = string.Empty,

                ["RateLimiting:PermitLimit"] = _permitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["RateLimiting:RedeemPerAddressPerMinute"] = _redeemPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["RateLimiting:MaxConcurrentDownloads"] =
                    _maxConcurrentDownloads.ToString(System.Globalization.CultureInfo.InvariantCulture),

                ["Download:DenialFloorMilliseconds"] =
                    _denialFloorMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }));
    }
}
