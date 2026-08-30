using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StatementDelivery.Crypto.Framing;
using StatementDelivery.Crypto.Keys;
using StatementDelivery.Persistence.Keys;
using StatementDelivery.ServiceDefaults.Storage;
using Testcontainers.Minio;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// A real MinIO container with a statements bucket created WITH Object Lock.
/// </summary>
/// <remarks>
/// <para>
/// THE BUCKET IS CREATED WITH LOCK, NOT CONFIGURED WITH IT AFTERWARDS, because that is the only way
/// it can be done: MinIO and S3 both require object locking at bucket-creation time. A fixture that
/// created a plain bucket and then tried to enable locking would fail in the same confusing way a
/// mis-provisioned production bucket does - which is the failure this fixture exists to make
/// impossible to ship.
/// </para>
/// <para>
/// A second, deliberately UNLOCKED bucket is created alongside it, so the readiness check can be
/// tested against a bucket that is wrong. A health check nobody has ever seen fail is a health check
/// nobody knows works.
/// </para>
/// </remarks>
public sealed class MinioFixture : IAsyncLifetime
{
    /// <summary>The bucket that is correctly provisioned.</summary>
    public const string BucketName = "statements-test";

    /// <summary>A bucket created WITHOUT Object Lock, for the negative readiness test.</summary>
    public const string UnlockedBucketName = "statements-unlocked";

    /// <summary>Root credentials for the container.</summary>
    public const string AccessKey = "integration-test-user";

    /// <summary>Root credentials for the container.</summary>
    public const string SecretKey = "integration-test-password";

    /// <summary>
    /// The development master secret. Shared by the seeder and by the gateway under test.
    /// </summary>
    /// <remarks>
    /// They MUST agree. The seeder wraps a CEK under a cohort key derived from this secret, and the
    /// gateway unwraps it under a cohort key derived from the same one. A mismatch would fail as a
    /// decryption error deep inside a download, which is a very indirect way to discover a typo in a
    /// test fixture.
    /// </remarks>
    public const string MasterSecret = "aW50ZWdyYXRpb24tdGVzdC1tYXN0ZXItc2VjcmV0LTMyLWJ5dGVzLW1pbmltdW0h";

    private MinioContainer? _container;

    /// <summary>Gets the container endpoint, or empty when Docker is unavailable.</summary>
    public string ServiceUrl { get; private set; } = string.Empty;

    /// <summary>Gets a value indicating whether the container started.</summary>
    public bool Started { get; private set; }

    /// <summary>Creates an S3 client pointed at the container.</summary>
    /// <returns>A client. The caller disposes it.</returns>
    public IAmazonS3 CreateClient() => new AmazonS3Client(
        AccessKey,
        SecretKey,
        new AmazonS3Config
        {
            ServiceURL = ServiceUrl,

            // MinIO does not do virtual-host-style addressing. This is the one setting that differs
            // between the local stack and real S3, and it is configuration rather than code - which
            // is what lets the same adapter serve both.
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
        });

    /// <summary>Builds the real content store, wired to real key services against the test database.</summary>
    /// <param name="postgres">The database fixture, for the customer_key table.</param>
    /// <param name="lockMode">COMPLIANCE or GOVERNANCE.</param>
    /// <param name="bucket">Which bucket to use.</param>
    /// <returns>A store and the client it owns.</returns>
    /// <remarks>
    /// Built from the SAME types the services register, not from a test double. Seeding through a
    /// hand-rolled encryptor would prove that the test agrees with itself; seeding through
    /// <see cref="S3StatementContentStore"/> proves the gateway can read what the writer wrote.
    /// </remarks>
    public (S3StatementContentStore Store, IAmazonS3 Client) CreateStore(
        PostgresFixture postgres,
        string lockMode = "GOVERNANCE",
        string? bucket = null)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        IAmazonS3 client = CreateClient();

        var provider = new LocalKeyProvider(Options.Create(new LocalKeyProviderOptions { MasterSecret = MasterSecret }));

        // app_generation, because that is the only role V013 grants INSERT on customer_key - and a
        // seeder that connected as a superuser would hide a missing grant until deployment.
        var store = new CustomerKeyRepository(postgres.ConnectionFactoryFor("app_generation"));

        var customerKeys = new CustomerKeyService(provider, store, NullLogger<CustomerKeyService>.Instance);
        var cache = new DataKeyCache(customerKeys, Options.Create(new DataKeyCacheOptions()), TimeProvider.System);

        var contentStore = new S3StatementContentStore(
            client,
            cache,
            Options.Create(new ObjectStorageOptions { BucketName = bucket ?? BucketName, ServiceUrl = ServiceUrl }),
            Options.Create(new ObjectLockOptions { Mode = lockMode }),
            Options.Create(new CipherOptions()),

            // Supplied explicitly, not left to the optional parameter's default. The store now takes
            // its retention period from configuration, and a test that let it fall back to
            // RetentionPolicy.Default would exercise a path production does not use - and would keep
            // passing if the binding were removed entirely.
            Options.Create(new RetentionOptions()));

        return (contentStore, client);
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        // The image is pinned, matching the compose stack and the PostgreSQL fixture. An unpinned
        // container image is an unpinned dependency: the test suite would change behaviour on a day
        // nobody committed anything.
        _container = new MinioBuilder("minio/minio:RELEASE.2025-09-07T16-13-09Z")
            .WithUsername(AccessKey)
            .WithPassword(SecretKey)
            .Build();

        await _container.StartAsync().ConfigureAwait(false);
        ServiceUrl = _container.GetConnectionString();

        using IAmazonS3 client = CreateClient();

        // ObjectLockEnabledForBucket, at creation. This is the SDK equivalent of `mc mb --with-lock`
        // and it is the only moment at which it can be set.
        _ = await client.PutBucketAsync(
            new PutBucketRequest { BucketName = BucketName, ObjectLockEnabledForBucket = true })
            .ConfigureAwait(false);

        // The wrong bucket, on purpose. See the remarks on this class.
        _ = await client.PutBucketAsync(new PutBucketRequest { BucketName = UnlockedBucketName })
            .ConfigureAwait(false);

        Started = true;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }
}
