using System.ComponentModel.DataAnnotations;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace StatementDelivery.ServiceDefaults.Storage;

/// <summary>
/// S3-compatible object storage configuration.
/// </summary>
/// <remarks>
/// The same code talks to MinIO locally and to S3 in a deployed environment. The only differences
/// are <see cref="ServiceUrl"/> and <see cref="ForcePathStyle"/>, both of which are configuration.
/// Writing against the AWS SDK rather than a MinIO client is what makes that true.
/// </remarks>
public sealed class ObjectStorageOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "ObjectStorage";

    /// <summary>
    /// Gets or sets the explicit service endpoint. Set for MinIO; leave empty to use the real S3
    /// endpoint for <see cref="Region"/>.
    /// </summary>
    public string? ServiceUrl { get; set; }

    /// <summary>Gets or sets the bucket holding rendered statements.</summary>
    [Required(AllowEmptyStrings = false)]
    public string BucketName { get; set; } = string.Empty;

    /// <summary>Gets or sets the region. Ignored when <see cref="ServiceUrl"/> is set.</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// Gets or sets a value indicating whether to address buckets as a path segment rather than a
    /// host prefix. Required for MinIO, which does not do virtual-host-style addressing.
    /// </summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>
    /// Gets or sets the access key.
    /// </summary>
    /// <remarks>
    /// Local development only, supplied from the environment. A deployed environment must leave
    /// this empty and let the SDK use the ambient credential chain - an instance role, a workload
    /// identity - so no long-lived key exists to be leaked in the first place.
    /// </remarks>
    public string? AccessKey { get; set; }

    /// <summary>Gets or sets the secret key. See the remarks on <see cref="AccessKey"/>.</summary>
    public string? SecretKey { get; set; }
}

/// <summary>
/// Readiness check confirming the statements bucket is reachable and visible to this identity.
/// </summary>
/// <remarks>
/// Deliberately a metadata call rather than a listing. "Never LIST-style unbounded scans" is a
/// standing rule in this system: every object is reached by a key computed from the database, and
/// a health check that listed a bucket holding two and a half billion objects would be an outage
/// of its own making.
/// </remarks>
public sealed class ObjectStorageHealthCheck : IHealthCheck
{
    /// <summary>The registered name of this check.</summary>
    public const string Name = "object-storage";

    private readonly IAmazonS3 _client;
    private readonly ObjectStorageOptions _options;

    /// <summary>Initialises a new instance of the <see cref="ObjectStorageHealthCheck"/> class.</summary>
    /// <param name="client">The S3 client.</param>
    /// <param name="options">Storage options.</param>
    public ObjectStorageHealthCheck(IAmazonS3 client, IOptions<ObjectStorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await _client.GetBucketLocationAsync(
                new GetBucketLocationRequest { BucketName = _options.BucketName },
                cancellationToken).ConfigureAwait(false);

            return HealthCheckResult.Healthy("Bucket is reachable.");
        }
        catch (AmazonServiceException ex)
        {
            return HealthCheckResult.Unhealthy(
                $"Bucket '{_options.BucketName}' is not reachable.",
                ex);
        }
    }
}

/// <summary>
/// Registers the S3-compatible object storage client and its readiness check.
/// </summary>
public static class ObjectStorageExtensions
{
    /// <summary>
    /// Binds and validates <see cref="ObjectStorageOptions"/> and registers
    /// <see cref="IAmazonS3"/> plus a readiness health check.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static IHostApplicationBuilder AddObjectStorage(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddOptions<ObjectStorageOptions>()
            .Bind(builder.Configuration.GetSection(ObjectStorageOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddSingleton<IAmazonS3>(provider =>
        {
            ObjectStorageOptions options = provider.GetRequiredService<IOptions<ObjectStorageOptions>>().Value;

            var config = new AmazonS3Config
            {
                ForcePathStyle = options.ForcePathStyle,
                AuthenticationRegion = options.Region,
            };

            if (string.IsNullOrWhiteSpace(options.ServiceUrl))
            {
                config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(options.Region);
            }
            else
            {
                config.ServiceURL = options.ServiceUrl;
            }

            // Explicit keys are the local-development path. Everywhere else the SDK's default
            // credential chain resolves an instance role or workload identity, so there is no
            // long-lived secret in configuration to leak.
            return string.IsNullOrWhiteSpace(options.AccessKey) || string.IsNullOrWhiteSpace(options.SecretKey)
                ? new AmazonS3Client(config)
                : new AmazonS3Client(options.AccessKey, options.SecretKey, config);
        });

        builder.Services
            .AddHealthChecks()
            .AddCheck<ObjectStorageHealthCheck>(
                ObjectStorageHealthCheck.Name,
                HealthStatus.Unhealthy,
                tags: ["ready", "storage"]);

        return builder;
    }
}
