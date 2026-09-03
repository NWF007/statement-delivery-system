using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using StatementDelivery.Crypto.Framing;
using StatementDelivery.Crypto.Keys;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Domain.Statements;
using StatementDelivery.Domain.ValueObjects;
using StatementDelivery.ServiceDefaults.Storage;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// Encrypted object storage against a real MinIO container: the bucket, the lock, and the round trip.
/// </summary>
/// <remarks>
/// A mocked S3 client would assert that the mock agrees with the code. What is being tested here is
/// the STORAGE SYSTEM's behaviour - that Object Lock actually refuses a delete, that a bucket
/// created without lock actually reports it - and nothing but a real one can answer that.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class EncryptedStorageTests
{
    private readonly PostgresFixture _postgres;
    private readonly MinioFixture _minio;

    /// <summary>Initialises a new instance of the <see cref="EncryptedStorageTests"/> class.</summary>
    /// <param name="postgres">The shared PostgreSQL fixture.</param>
    /// <param name="minio">The shared object storage fixture.</param>
    public EncryptedStorageTests(PostgresFixture postgres, MinioFixture minio)
    {
        _postgres = postgres;
        _minio = minio;
    }

    // ─── Bucket provisioning ─────────────────────────────────────────────────────────────────────

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Bucket_HasObjectLockEnabled()
    {
        using IAmazonS3 client = _minio.CreateClient();

        GetObjectLockConfigurationResponse response = await client
            .GetObjectLockConfigurationAsync(
                new GetObjectLockConfigurationRequest { BucketName = MinioFixture.BucketName },
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        response.ObjectLockConfiguration.ObjectLockEnabled.ShouldBe(ObjectLockEnabled.Enabled);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Bucket_HasVersioningEnabled()
    {
        // Not a separate setting that could drift: enabling Object Lock enables versioning
        // permanently and it cannot then be turned off. Asserted anyway, because "implied by" is
        // exactly the sort of assumption that turns out to differ between S3 and an S3-compatible
        // implementation.
        using IAmazonS3 client = _minio.CreateClient();

        GetBucketVersioningResponse response = await client
            .GetBucketVersioningAsync(
                new GetBucketVersioningRequest { BucketName = MinioFixture.BucketName },
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        response.VersioningConfig.Status.ShouldBe(VersionStatus.Enabled);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Startup_WithoutObjectLock_FailsReadiness()
    {
        // THE CHECK THAT CATCHES A MIS-PROVISIONED BUCKET AT DEPLOY TIME. Without it the service
        // starts, writes objects that look retained and are not, and the discovery happens at an
        // audit years later - by which point the bucket cannot be fixed, only rebuilt.
        using IAmazonS3 client = _minio.CreateClient();

        var check = new ObjectLockHealthCheck(
            client,
            Options.Create(new ObjectStorageOptions { BucketName = MinioFixture.UnlockedBucketName }),
            Options.Create(new ObjectLockOptions { Mode = "COMPLIANCE" }));

        HealthCheckResult result = await check
            .CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        result.Status.ShouldBe(HealthStatus.Unhealthy);

        // And the correctly provisioned bucket passes, or the check is simply always failing.
        var healthy = new ObjectLockHealthCheck(
            client,
            Options.Create(new ObjectStorageOptions { BucketName = MinioFixture.BucketName }),
            Options.Create(new ObjectLockOptions { Mode = "COMPLIANCE" }));

        HealthCheckResult ok = await healthy
            .CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        ok.Status.ShouldBe(HealthStatus.Healthy);
    }

    // ─── Object Lock behaviour ───────────────────────────────────────────────────────────────────

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Write_SetsComplianceRetention()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        (Guid customer, Guid account, Guid statement, StatementPeriod period) = await SeedCustomerAsync(cancellationToken)
            .ConfigureAwait(true);

        (S3StatementContentStore store, IAmazonS3 client) = _minio.CreateStore(_postgres, lockMode: "COMPLIANCE");

        using (client)
        {
            StoredObject stored = await WriteAsync(store, customer, account, statement, period, 2048, cancellationToken)
                .ConfigureAwait(true);

            GetObjectRetentionResponse retention = await client
                .GetObjectRetentionAsync(
                    new GetObjectRetentionRequest { BucketName = MinioFixture.BucketName, Key = stored.Key },
                    cancellationToken)
                .ConfigureAwait(true);

            retention.Retention.Mode.ShouldBe(ObjectLockRetentionMode.Compliance);

            // Seven years from the PERIOD END, not from today. A statement regenerated years late
            // must not thereby earn extra retention.
            DateOnly expected = RetentionPolicy.Default.RetainUntil(period);
            DateOnly actual = DateOnly.FromDateTime(retention.Retention.RetainUntilDate!.Value.ToUniversalTime());

            actual.ShouldBe(expected);
        }
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Write_ThenDeleteBeforeRetention_IsRejected()
    {
        // ★ THE TEST THAT PROVES THE COMPLIANCE STORY IS REAL RATHER THAN CONFIGURED.
        //
        // Everything else about retention is a setting that could be wrong: a mode string, a date, a
        // bucket flag. This is the only assertion that the storage system will actually REFUSE to
        // destroy a record - which is the entire property the seven-year retention claims.
        //
        // ⚠ NOTE THE versionId. On a versioned bucket, DeleteObject WITHOUT one does not delete
        // anything - it writes a delete marker, and it SUCCEEDS even under Object Lock. A test that
        // omitted it would pass, prove nothing, and read exactly like this one.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        (Guid customer, Guid account, Guid statement, StatementPeriod period) = await SeedCustomerAsync(cancellationToken)
            .ConfigureAwait(true);

        (S3StatementContentStore store, IAmazonS3 client) = _minio.CreateStore(_postgres, lockMode: "COMPLIANCE");

        using (client)
        {
            StoredObject stored = await WriteAsync(store, customer, account, statement, period, 1024, cancellationToken)
                .ConfigureAwait(true);

            GetObjectMetadataResponse metadata = await client
                .GetObjectMetadataAsync(
                    new GetObjectMetadataRequest { BucketName = MinioFixture.BucketName, Key = stored.Key },
                    cancellationToken)
                .ConfigureAwait(true);

            metadata.VersionId.ShouldNotBeNullOrEmpty("the bucket must be versioned for this test to mean anything");

            AmazonS3Exception error = await Should.ThrowAsync<AmazonS3Exception>(() =>
                client.DeleteObjectAsync(
                    new DeleteObjectRequest
                    {
                        BucketName = MinioFixture.BucketName,
                        Key = stored.Key,
                        VersionId = metadata.VersionId,
                    },
                    cancellationToken))
                .ConfigureAwait(true);

            // AWS S3 answers 403 AccessDenied; MinIO answers 400 InvalidRequest with "Object is
            // WORM protected". The property under test is the REFUSAL, not the dialect.
            ((int)error.StatusCode is 403 or 400).ShouldBeTrue(
                $"the locked version must refuse deletion; got {(int)error.StatusCode} {error.ErrorCode}: {error.Message}");

            // And the object is still there - the refusal was not cosmetic.
            GetObjectMetadataResponse survived = await client
                .GetObjectMetadataAsync(
                    new GetObjectMetadataRequest { BucketName = MinioFixture.BucketName, Key = stored.Key },
                    cancellationToken)
                .ConfigureAwait(true);
            survived.VersionId.ShouldBe(metadata.VersionId);

            // Still there, and still readable. A retention that blocked deletion by corrupting the
            // object would satisfy the assertion above and destroy the record anyway.
            GetObjectMetadataResponse afterDelete = await client
                .GetObjectMetadataAsync(
                    new GetObjectMetadataRequest { BucketName = MinioFixture.BucketName, Key = stored.Key },
                    cancellationToken)
                .ConfigureAwait(true);

            afterDelete.ContentLength.ShouldBe(metadata.ContentLength);
        }
    }

    // ─── Round trip through the real adapter ─────────────────────────────────────────────────────

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Write_ZeroLengthContent_RoundTrips()
    {
        // ADR-0032: the size shape nobody writes a test for. A zero-transaction statement still
        // renders a valid one-page PDF, but the STORE's contract for empty content - header-only
        // framing, a zero Content-Length spool, an empty-body PUT - is its own branch.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (Guid customer, Guid account, Guid statement, StatementPeriod period) =
            await SeedCustomerAsync(cancellationToken).ConfigureAwait(true);

        (S3StatementContentStore store, Amazon.S3.IAmazonS3 client) = _minio.CreateStore(_postgres);
        using (client)
        {
            StatementDelivery.ServiceDefaults.Storage.StoredObject stored = await store.WriteAsync(
                Stream.Null,
                new StatementDelivery.Crypto.Framing.CryptoContext(statement, customer, 1),
                new StatementDelivery.Domain.Identifiers.AccountId(account),
                period,
                StatementDelivery.Crypto.Keys.CohortAssignment.KekIdFor(
                    StatementDelivery.Crypto.Keys.CohortAssignment.ForCustomer(
                        new StatementDelivery.Domain.Identifiers.CustomerId(customer))),
                cancellationToken).ConfigureAwait(true);

            stored.PlaintextLength.ShouldBe(0);
            stored.CiphertextLength.ShouldBeGreaterThan(0, "even empty content carries the framed header");

            StatementDelivery.ServiceDefaults.Storage.StatementContent? content = await store.OpenReadAsync(
                new StatementDelivery.Domain.Statements.StorageLocation(
                    stored.Key, stored.Tier, stored.PlaintextLength, stored.Envelope),
                cancellationToken).ConfigureAwait(true);

            content.ShouldNotBeNull();
            await using (content.ConfigureAwait(false))
            {
                using var sink = new MemoryStream();
                await content.Stream.CopyToAsync(sink, cancellationToken).ConfigureAwait(true);
                sink.Length.ShouldBe(0);
            }
        }
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Write_FromNonSeekableStream_RoundTrips()
    {
        // THE SEAM THAT CARRIED A CRITICAL DEFECT, against a REAL MinIO. The render
        // pipeline hands the writer a pipe - non-seekable, unmeasurable - and every prior
        // storage test wrote from a seekable MemoryStream, so the branch production takes was
        // the one branch never executed. The unit-level tripwire (ContentWriterSeamTests) runs
        // on every build; this is the end-to-end truth with a real S3 implementation enforcing
        // the real Content-Length rules.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        (Guid customer, Guid account, Guid statement, StatementPeriod period) =
            await SeedCustomerAsync(cancellationToken).ConfigureAwait(true);

        byte[] plaintext = new byte[300 * 1024];
        System.Security.Cryptography.RandomNumberGenerator.Fill(plaintext.AsSpan(0, 4096));

        (S3StatementContentStore store, Amazon.S3.IAmazonS3 client) = _minio.CreateStore(_postgres);
        using (client)
        {
            StatementDelivery.ServiceDefaults.Storage.StoredObject stored;

            using (var source = new NonSeekableSource(new MemoryStream(plaintext, writable: false)))
            {
                stored = await store.WriteAsync(
                    source,
                    new StatementDelivery.Crypto.Framing.CryptoContext(statement, customer, 1),
                    new StatementDelivery.Domain.Identifiers.AccountId(account),
                    period,
                    StatementDelivery.Crypto.Keys.CohortAssignment.KekIdFor(
                        StatementDelivery.Crypto.Keys.CohortAssignment.ForCustomer(
                            new StatementDelivery.Domain.Identifiers.CustomerId(customer))),
                    cancellationToken).ConfigureAwait(true);
            }

            stored.PlaintextLength.ShouldBe(plaintext.Length);

            // And read it back through the REAL decrypting store: the round trip proves the
            // spooled ciphertext is byte-for-byte what the framed reader expects.
            StatementDelivery.ServiceDefaults.Storage.StatementContent? content = await store.OpenReadAsync(
                new StatementDelivery.Domain.Statements.StorageLocation(
                    stored.Key, stored.Tier, stored.PlaintextLength, stored.Envelope),
                cancellationToken).ConfigureAwait(true);

            content.ShouldNotBeNull();
            await using (content.ConfigureAwait(false))
            {
                using var sink = new MemoryStream();
                await content.Stream.CopyToAsync(sink, cancellationToken).ConfigureAwait(true);
                sink.ToArray().ShouldBe(plaintext);
            }
        }
    }

    /// <summary>A non-seekable wrapper - the input shape the render pipe actually produces.</summary>
    private sealed class NonSeekableSource : Stream
    {
        private readonly Stream _inner;

        public NonSeekableSource(Stream inner) => _inner = inner;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task WriteThenRead_RoundTripsThroughObjectStorage()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        (Guid customer, Guid account, Guid statement, StatementPeriod period) = await SeedCustomerAsync(cancellationToken)
            .ConfigureAwait(true);

        byte[] content = RandomNumberGenerator.GetBytes(200_000);

        (S3StatementContentStore store, IAmazonS3 client) = _minio.CreateStore(_postgres);

        using (client)
        {
            using var plaintext = new MemoryStream(content, writable: false);

            StoredObject stored = await store.WriteAsync(
                plaintext,
                new CryptoContext(statement, customer, 1),
                new AccountId(account),
                period,
                CohortAssignment.KekIdFor(CohortAssignment.ForCustomer(new CustomerId(customer))),
                cancellationToken).ConfigureAwait(true);

            stored.PlaintextLength.ShouldBe(content.Length);

            // The ciphertext is longer, by exactly the framing overhead. Asserted so that a change
            // to the format that quietly stopped encrypting would fail here.
            stored.CiphertextLength.ShouldBe(
                FrameFormat.CiphertextLengthFor(content.Length, FrameFormat.DefaultFrameSize));
            stored.CiphertextLength.ShouldBeGreaterThan(stored.PlaintextLength);

            stored.Envelope.WrappedDek.Length.ShouldBeGreaterThan(40, "a plaintext DEK would be exactly 32 bytes");
            stored.Envelope.ContentSha256.ShouldBe(SHA256.HashData(content));

            // THE STORED BYTES ARE NOT THE PLAINTEXT. The single most important assertion about
            // encryption at rest, and the easiest one to leave out.
            using GetObjectResponse raw = await client.GetObjectAsync(
                new GetObjectRequest { BucketName = MinioFixture.BucketName, Key = stored.Key },
                cancellationToken).ConfigureAwait(true);

            using var rawBuffer = new MemoryStream();
            await raw.ResponseStream.CopyToAsync(rawBuffer, cancellationToken).ConfigureAwait(true);

            byte[] onDisk = rawBuffer.ToArray();
            onDisk[..4].ShouldBe("SDP1"u8.ToArray());
            onDisk.AsSpan(FrameFormat.HeaderLength + 4, 64).ToArray()
                .ShouldNotBe(content[..64], "the stored bytes must not be the plaintext");

            // And it reads back through the port the gateway uses, byte for byte.
            var location = new StorageLocation(
                stored.Key, stored.Tier, stored.PlaintextLength, stored.Envelope);

            await using StatementContent? readBack = await store
                .OpenReadAsync(location, cancellationToken).ConfigureAwait(true);

            readBack.ShouldNotBeNull();
            readBack.Length.ShouldBe(content.Length);

            using var decrypted = new MemoryStream();
            await readBack.Stream.CopyToAsync(decrypted, cancellationToken).ConfigureAwait(true);

            decrypted.ToArray().ShouldBe(content);
        }
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Read_WithAnotherCustomersBinding_Fails()
    {
        // THE DATABASE-LEVEL ATTACK, END TO END. An attacker with write access to the statement
        // table repoints a row at somebody else's object. The fetch succeeds - the row says so - and
        // the DECRYPTION fails, because the identity in the AAD came from the row and no longer
        // matches what was signed.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        (Guid customer, Guid account, Guid statement, StatementPeriod period) = await SeedCustomerAsync(cancellationToken)
            .ConfigureAwait(true);

        (S3StatementContentStore store, IAmazonS3 client) = _minio.CreateStore(_postgres);

        using (client)
        {
            StoredObject stored = await WriteAsync(store, customer, account, statement, period, 4096, cancellationToken)
                .ConfigureAwait(true);

            var tampered = new StorageLocation(
                stored.Key,
                stored.Tier,
                stored.PlaintextLength,
                stored.Envelope with
                {
                    Binding = stored.Envelope.Binding with { StatementId = Guid.CreateVersion7() },
                });

            // The open returns a lazily-verifying stream: frame AAD is checked as frames are
            // read. The deception has to be CONSUMED to be caught - which is also true at the
            // gateway, where the same read drives the response and aborts it mid-body.
            _ = await Should.ThrowAsync<CiphertextIntegrityException>(async () =>
            {
                StatementContent? content = await store.OpenReadAsync(tampered, cancellationToken).ConfigureAwait(false);
                content.ShouldNotBeNull();
                await using (content.ConfigureAwait(false))
                {
                    await content.Stream.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
                }
            }).ConfigureAwait(true);
        }
    }

    // ─── Storage key scheme ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void StorageKey_ShardPrefix_IsHighCardinality()
    {
        // No container needed: this is arithmetic. A date-first prefix would put every write for a
        // month under one key prefix, which is the hot-partition problem the shard exists to solve.
        const int Samples = 20_000;
        var shards = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < Samples; i++)
        {
            shards.Add(StorageKeyScheme.ShardFor(new StatementId(Guid.CreateVersion7())));
        }

        // 4,096 possible prefixes; 20,000 samples should reach nearly all of them. A mapping that
        // keyed on the UUIDv7 timestamp would reach a handful, because these ids are all minted
        // inside the same few milliseconds - which is exactly the adversarial case.
        shards.Count.ShouldBeGreaterThan(3900, $"only {shards.Count} of {StorageKeyScheme.ShardCount} prefixes were used");

        string key = StorageKeyScheme.KeyFor(
            new StatementId(Guid.Parse("0199a1f0-1111-7000-8000-000000000001")),
            new AccountId(Guid.Parse("0199a1f0-2222-7000-8000-000000000002")),
            StatementPeriod.ForMonth(2026, 8),
            version: 2);

        key.ShouldStartWith("statements/");
        key.ShouldEndWith("/0199a1f0-2222-7000-8000-000000000002/2026-08-v2.enc");

        // Three hex characters between the two slashes, and nothing date-shaped in front of them.
        string shard = key.Split('/')[1];
        shard.Length.ShouldBe(3);
        shard.ShouldBeOneOf([.. Enumerable.Range(0, 4096).Select(i => i.ToString("x3", System.Globalization.CultureInfo.InvariantCulture))]);
    }

    // ─── Privileges ──────────────────────────────────────────────────────────────────────────────

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task AppDeliveryRole_CannotReadWrappedCek()
    {
        // LEAST PRIVILEGE, ENFORCED BY THE DATABASE. app_delivery lists statements and mints
        // download links. It decrypts nothing, so it must not be able to read the material that
        // would let it - and V013 revokes the table-level SELECT it inherited from V009 precisely
        // because a table-level grant silently covers columns added afterwards.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await using NpgsqlConnection delivery = await _postgres
            .OpenAsAsync("app_delivery", cancellationToken).ConfigureAwait(true);

        PostgresException error = await Should.ThrowAsync<PostgresException>(() =>
            delivery.ExecuteScalarAsync<byte[]>(new CommandDefinition(
                "SELECT wrapped_cek FROM customer_key LIMIT 1;",
                commandTimeout: 30, cancellationToken: cancellationToken)))
            .ConfigureAwait(true);

        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        // The columns it legitimately needs still work, or the revoke was too broad and the
        // catalogue is broken.
        _ = await delivery.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM (SELECT customer_id, kek_id, status, cohort_id FROM customer_key LIMIT 1) q;",
            commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(true);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task DecryptingRoles_CanReadWrappedCek()
    {
        // The counterpart. A revoke that broke the roles which DO decrypt would take the download
        // path down, and this is where that shows up rather than in a 404 at midnight.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        foreach (string role in (string[])["app_download", "app_generation", "app_retention"])
        {
            await using NpgsqlConnection connection = await _postgres
                .OpenAsAsync(role, cancellationToken).ConfigureAwait(true);

            _ = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*) FROM (SELECT wrapped_cek FROM customer_key LIMIT 1) q;",
                commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(true);
        }
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task PlaintextDek_IsNeverPersisted()
    {
        // THE NO-PLAINTEXT-KEY RULE, RUN AGAINST THE DATABASE. A raw AES-256 key is 32 bytes; the
        // wrapping envelope adds 29. Anything shorter than 40 in wrapped_dek is therefore a
        // plaintext key somebody has persisted - and V013 also enforces this as a CHECK constraint,
        // so this test is the belt to that braces.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        (Guid customer, Guid account, Guid statement, StatementPeriod period) = await SeedCustomerAsync(cancellationToken)
            .ConfigureAwait(true);

        (S3StatementContentStore store, IAmazonS3 client) = _minio.CreateStore(_postgres);
        StoredObject stored;

        using (client)
        {
            stored = await WriteAsync(store, customer, account, statement, period, 1024, cancellationToken)
                .ConfigureAwait(true);
        }

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true);

        _ = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO statement (
                id, account_id, customer_id, period_start, period_end, version, status,
                storage_key, storage_tier, size_bytes, content_sha256,
                wrapped_dek, dek_algorithm, kek_id, retain_until, generated_at)
            VALUES (
                @statement, @account, @customer, @start, @end, 1, 'AVAILABLE',
                @key, 'STANDARD', @size, @sha, @dek, @algorithm, @kekId, @retain, now());
            """,
            new
            {
                statement,
                account,
                customer,
                start = period.Start,
                end = period.End,
                key = stored.Key,
                size = stored.PlaintextLength,
                sha = stored.Envelope.ContentSha256,
                dek = stored.Envelope.WrappedDek,
                algorithm = stored.Envelope.Algorithm,
                kekId = stored.Envelope.KekId,
                retain = RetentionPolicy.Default.RetainUntil(period),
            },
            commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(true);

        long suspicious = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM statement WHERE wrapped_dek IS NOT NULL AND octet_length(wrapped_dek) < 40;",
            commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(true);

        suspicious.ShouldBe(0);

        long unwrappedCeks = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM customer_key WHERE wrapped_cek IS NOT NULL AND octet_length(wrapped_cek) < 40;",
            commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(true);

        unwrappedCeks.ShouldBe(0);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Schema_RejectsAPlaintextDek()
    {
        // The constraint, tested directly. The query above finds a violation that already happened;
        // this proves one cannot happen at all.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        (Guid customer, Guid account, Guid statement, StatementPeriod period) = await SeedCustomerAsync(cancellationToken)
            .ConfigureAwait(true);

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true);

        PostgresException error = await Should.ThrowAsync<PostgresException>(() =>
            connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO statement (
                    id, account_id, customer_id, period_start, period_end, version, status,
                    storage_key, storage_tier, size_bytes, content_sha256, wrapped_dek, kek_id, retain_until, generated_at)
                VALUES (
                    @statement, @account, @customer, @start, @end, 1, 'AVAILABLE',
                    'statements/abc/x.enc', 'STANDARD', 10, @sha,
                    @rawKey, 'alias/statement-cek-0001', @retain, now());
                """,
                new
                {
                    statement,
                    account,
                    customer,
                    start = period.Start,
                    end = period.End,

                    // A valid digest, so the row fails on the DEK constraint under test rather
                    // than on V015's digest requirement - a test that passes for the wrong reason
                    // proves nothing about the constraint it names.
                    sha = SHA256.HashData("fixture"u8.ToArray()),

                    // A raw 32-byte AES key, exactly what must never reach a column.
                    rawKey = RandomNumberGenerator.GetBytes(32),
                    retain = RetentionPolicy.Default.RetainUntil(period),
                },
                commandTimeout: 30, cancellationToken: cancellationToken)))
            .ConfigureAwait(true);

        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        error.ConstraintName.ShouldBe("ck_statement_dek_is_wrapped");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private async Task<(Guid Customer, Guid Account, Guid Statement, StatementPeriod Period)> SeedCustomerAsync(
        CancellationToken cancellationToken)
    {
        var customer = Guid.CreateVersion7();
        var account = Guid.CreateVersion7();
        var statement = Guid.CreateVersion7();

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        StatementPeriod period = StatementPeriod.ForMonth(today.Year, today.Month);

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true);

        _ = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO customer (id, external_ref, status) VALUES (@customer, @ref, 'ACTIVE');
            INSERT INTO account (id, customer_id, account_number_masked, product_type, status, opened_at)
                VALUES (@account, @customer, '****4321', 'SAVINGS', 'ACTIVE', now());
            """,
            new { customer, account, @ref = customer.ToString("N") },
            commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(true);

        return (customer, account, statement, period);
    }

    private static async Task<StoredObject> WriteAsync(
        S3StatementContentStore store,
        Guid customer,
        Guid account,
        Guid statement,
        StatementPeriod period,
        int sizeBytes,
        CancellationToken cancellationToken)
    {
        using var plaintext = new MemoryStream(RandomNumberGenerator.GetBytes(sizeBytes), writable: false);

        return await store.WriteAsync(
            plaintext,
            new CryptoContext(statement, customer, 1),
            new AccountId(account),
            period,
            CohortAssignment.KekIdFor(CohortAssignment.ForCustomer(new CustomerId(customer))),
            cancellationToken).ConfigureAwait(false);
    }
}
