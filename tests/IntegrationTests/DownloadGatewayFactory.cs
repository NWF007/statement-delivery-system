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
    private readonly string _serviceUrl;
    private readonly string _bucket;
    private readonly int _redeemPerMinute;
    private readonly int _permitLimit;
    private readonly int _denialFloorMilliseconds;
    private readonly int _maxConcurrentDownloads;

    /// <summary>Initialises a new instance of the <see cref="DownloadGatewayFactory"/> class.</summary>
    /// <param name="connectionString">The app_download connection string for the test container.</param>
    /// <param name="serviceUrl">The MinIO endpoint holding the encrypted objects.</param>
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
    /// <param name="bucket">The bucket holding the encrypted objects.</param>
    public DownloadGatewayFactory(
        string connectionString,
        string serviceUrl,
        int redeemPerMinute = 30,
        int permitLimit = 120,
        int denialFloorMilliseconds = 0,
        int maxConcurrentDownloads = 128,
        string bucket = MinioFixture.BucketName)
    {
        _connectionString = connectionString;
        _serviceUrl = serviceUrl;
        _bucket = bucket;
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

                // PROMPT 4: real object storage, real encryption. The filesystem content store and
                // its ContentStore:RootPath are gone - the gateway reads encrypted objects from
                // MinIO now, exactly as it reads them from S3 in a deployed environment.
                ["ObjectStorage:BucketName"] = _bucket,
                ["ObjectStorage:ServiceUrl"] = _serviceUrl,
                ["ObjectStorage:AccessKey"] = MinioFixture.AccessKey,
                ["ObjectStorage:SecretKey"] = MinioFixture.SecretKey,
                ["ObjectStorage:ForcePathStyle"] = "true",

                // GOVERNANCE, so the storage tests can clean up after themselves. The bucket default
                // is still COMPLIANCE; see ObjectLockOptions for why the two differ by environment.
                ["ObjectStorage:Lock:Mode"] = "GOVERNANCE",

                // The SAME master secret the seeder used. If these ever diverge the failure is a
                // decryption error deep inside a download, which is a very indirect way to discover
                // a typo in a fixture - hence one constant, referenced twice.
                ["Crypto:KeyProvider"] = "Local",
                ["Crypto:LocalKeys:MasterSecret"] = MinioFixture.MasterSecret,

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
