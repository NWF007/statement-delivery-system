using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// The download token lifecycle over real HTTP, against both real services and a real database.
/// </summary>
/// <remarks>
/// Issue runs through Delivery.Api as <c>app_delivery</c>; redemption runs through Download.Gateway
/// as <c>app_download</c>. Two hosts, two roles, one database - the same separation that exists in
/// production, so a privilege the gateway must not have cannot be accidentally available here.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class DownloadLifecycleTests
{
    private readonly PostgresFixture _postgres;
    private readonly MinioFixture _minio;

    /// <summary>Initialises a new instance of the <see cref="DownloadLifecycleTests"/> class.</summary>
    /// <param name="postgres">The shared PostgreSQL fixture.</param>
    /// <param name="minio">The shared object storage fixture.</param>
    /// <remarks>
    /// THE MOVE TO OBJECT STORAGE REPLACED THE TEMP DIRECTORY WITH A BUCKET. These tests are
    /// otherwise unchanged: the same assertions, over the same endpoints, against content that is
    /// now encrypted at rest. That they needed no other edit is the clearest evidence available
    /// that the port was the right shape - the fixture changed, the expectations did not.
    /// </remarks>
    public DownloadLifecycleTests(PostgresFixture postgres, MinioFixture minio)
    {
        _postgres = postgres;
        _minio = minio;
    }

    private DeliveryApiFactory CreateApi() => new(_postgres.ConnectionStringFor("app_delivery"));

    private DownloadGatewayFactory CreateGateway(
        int redeemPerMinute = 30,
        int permitLimit = 120,
        int denialFloorMilliseconds = 0) =>
        new(
            _postgres.ConnectionStringFor("app_download"),
            _minio.ServiceUrl,
            redeemPerMinute,
            permitLimit,
            denialFloorMilliseconds);

    private static Uri Redeem(string plaintext) => new("/v1/d/" + plaintext, UriKind.Relative);

    // =============================================================================================
    //  THE CRITICAL CONCURRENCY TEST
    // =============================================================================================

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task ConcurrentRedemption_ExactlyOneSucceeds()
    {
        // THE SINGLE MOST PERSUASIVE TEST IN THE REPOSITORY. It proves the atomic consume holds
        // under contention, which is the property every other guarantee rests on. A read-then-write
        // implementation passes every other test in this file and fails this one.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const int Attempts = 50;

        SeededStatement seeded = await DownloadScenario
            .SeedAsync(_postgres, _minio, cancellationToken: cancellationToken).ConfigureAwait(true);

        using DeliveryApiFactory api = CreateApi();
        IssuedLink link = await DownloadScenario
            .IssueAsync(api, seeded, cancellationToken: cancellationToken).ConfigureAwait(true);

        // Limits raised deliberately: fifty requests from one address would otherwise be measuring
        // the rate limiter, and the rate limiter is not what is under test here.
        using DownloadGatewayFactory gateway = CreateGateway(redeemPerMinute: 500, permitLimit: 1000);

        // The losers' audit records carry no statement_id - the consume returned nothing, so there
        // was nothing to attribute them to - which means they can only be counted by time. Tests in
        // this collection run sequentially, so a window opened here belongs to this test alone.
        DateTimeOffset since = await NowAsync(cancellationToken).ConfigureAwait(true);

        // All fifty clients built and warmed BEFORE the barrier, so the release is as close to
        // simultaneous as a single process can make it. Building them inside the tasks would spread
        // the requests over host startup and the contention would never actually occur.
        HttpClient[] clients = [.. Enumerable.Range(0, Attempts).Select(_ => gateway.CreateClient())];

        try
        {
            using var barrier = new SemaphoreSlim(0, Attempts);

            Task<HttpStatusCode>[] attempts =
            [
                .. clients.Select(async client =>
                {
                    await barrier.WaitAsync(cancellationToken).ConfigureAwait(false);
                    using HttpResponseMessage response = await client
                        .GetAsync(Redeem(link.Plaintext), cancellationToken).ConfigureAwait(false);

                    // Drain the body, so a success is a completed transfer rather than a header.
                    _ = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                    return response.StatusCode;
                }),
            ];

            barrier.Release(Attempts);
            HttpStatusCode[] results = await Task.WhenAll(attempts).ConfigureAwait(true);

            results.Count(static status => status == HttpStatusCode.OK)
                .ShouldBe(1, "exactly one redemption may succeed");
            results.Count(static status => status == HttpStatusCode.NotFound)
                .ShouldBe(Attempts - 1, "every other attempt must be an indistinguishable 404");
        }
        finally
        {
            foreach (HttpClient client in clients)
            {
                client.Dispose();
            }
        }

        // The database agrees: one row, consumed exactly once.
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true);

        int consumed = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM download_token WHERE id = @id AND consumed_at IS NOT NULL;",
            new { id = Guid.Parse(link.LinkId) },
            commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(true);
        consumed.ShouldBe(1);

        // And the audit trail agrees: one grant, forty-nine refusals, each recording WHY.
        int started = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT count(*) FROM audit_event
             WHERE statement_id = @statement AND action = 'DOWNLOAD_STARTED' AND outcome = 'SUCCESS';
            """,
            new { statement = seeded.StatementId },
            commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(true);
        started.ShouldBe(1, "exactly one access may be granted");

        int denied = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT count(*) FROM audit_event
             WHERE action = 'ACCESS_DENIED'
               AND outcome = 'DENIED'
               AND denial_reason_code = 'CONSUMED'
               AND occurred_at >= @since;
            """,
            new { since },
            commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(true);
        denied.ShouldBe(Attempts - 1, "every loser must be recorded as CONSUMED, not merely as denied");
    }

    // =============================================================================================
    //  SECURITY
    // =============================================================================================

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task ExpiredConsumedRevokedUnknown_ProduceByteIdenticalResponses()
    {
        // The four failures a caller can produce must be indistinguishable. Not "similar" - the
        // same status, the same bytes, the same headers. Any difference is an oracle.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        using DeliveryApiFactory api = CreateApi();
        using DownloadGatewayFactory gateway = CreateGateway();

        SeededStatement seeded = await DownloadScenario
            .SeedAsync(_postgres, _minio, cancellationToken: cancellationToken).ConfigureAwait(true);

        // --- consumed ---
        IssuedLink consumedLink = await DownloadScenario.IssueAsync(api, seeded, cancellationToken: cancellationToken).ConfigureAwait(true);
        using (HttpClient warm = gateway.CreateClient())
        {
            using HttpResponseMessage first = await warm.GetAsync(Redeem(consumedLink.Plaintext), cancellationToken).ConfigureAwait(true);
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // --- revoked ---
        IssuedLink revokedLink = await DownloadScenario.IssueAsync(api, seeded, cancellationToken: cancellationToken).ConfigureAwait(true);
        using (HttpResponseMessage revoke = await DownloadScenario
            .RevokeAsync(api, revokedLink.LinkId, seeded.CustomerId, cancellationToken).ConfigureAwait(true))
        {
            revoke.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // --- expired ---
        IssuedLink expiredLink = await DownloadScenario.IssueAsync(api, seeded, cancellationToken: cancellationToken).ConfigureAwait(true);
        await DownloadScenario.ExpireAsync(_postgres, expiredLink.LinkId, cancellationToken).ConfigureAwait(true);

        // --- never existed, and malformed ---
        string unknown = Convert.ToBase64String(Guid.CreateVersion7().ToByteArray().Concat(Guid.CreateVersion7().ToByteArray()).ToArray())
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        (string Label, string Token)[] cases =
        [
            ("consumed", consumedLink.Plaintext),
            ("revoked", revokedLink.Plaintext),
            ("expired", expiredLink.Plaintext),
            ("unknown", unknown),
            ("malformed", "not-a-token"),
        ];

        var responses = new List<(string Label, HttpStatusCode Status, string Body, string Headers)>();

        using HttpClient client = gateway.CreateClient();
        foreach ((string label, string token) in cases)
        {
            using HttpResponseMessage response = await client.GetAsync(Redeem(token), cancellationToken).ConfigureAwait(true);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);

            // Date and Server vary by wall clock and host and say nothing about the token.
            string headers = string.Join(
                '|',
                response.Headers.Concat(response.Content.Headers)
                    .Where(static header => !string.Equals(header.Key, "Date", StringComparison.OrdinalIgnoreCase))
                    .Where(static header => !string.Equals(header.Key, "Server", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(static header => header.Key, StringComparer.Ordinal)
                    .Select(static header => header.Key + "=" + string.Join(',', header.Value)));

            responses.Add((label, response.StatusCode, body, headers));
        }

        (string Label, HttpStatusCode Status, string Body, string Headers) reference = responses[0];

        foreach ((string label, HttpStatusCode status, string body, string headers) in responses)
        {
            status.ShouldBe(reference.Status, $"'{label}' returned a different status");
            body.ShouldBe(reference.Body, $"'{label}' returned a different body");
            headers.ShouldBe(reference.Headers, $"'{label}' returned different headers");
        }

        reference.Status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task CorruptedFirstFrame_Returns404_AuditsDecryptionFailed_AndCountsIt()
    {
        // ★ THE FAILURE THAT IS NOT THE CALLER'S FAULT, caught BEFORE the response starts.
        //
        // Everything else in this file is a denial the caller caused: a spent token, a revoked one,
        // somebody else's statement. This is corruption or tampering - the token was valid,
        // ownership was proven, the row was found, and the BYTES did not authenticate.
        //
        // Corrupting the FIRST body frame means the failure surfaces on the first read, before a
        // single byte has been written, so the uniform denial is still expressible. Three things
        // must hold and each fails differently if missing: the caller gets the SAME 404 as every
        // other denial (or the response is an oracle), the audit trail records DECRYPTION_FAILED
        // (or the only evidence of tampering is a log line nobody kept), and the counter moves (or
        // nobody is paged, which is the point of a control alerted on any non-zero value).
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SeededStatement seeded = await DownloadScenario
            .SeedAsync(_postgres, _minio, sizeBytes: 8 * 1024, cancellationToken: cancellationToken).ConfigureAwait(true);

        // Byte 64 is inside the first frame's ciphertext: 48-byte header, then a 4-byte length
        // prefix, so the payload starts at 52.
        await CorruptObjectByteAsync(seeded.StorageKey, 64, cancellationToken).ConfigureAwait(true);

        using DeliveryApiFactory api = CreateApi();
        using DownloadGatewayFactory gateway = CreateGateway();

        IssuedLink link = await DownloadScenario.IssueAsync(api, seeded, cancellationToken: cancellationToken).ConfigureAwait(true);
        DateTimeOffset since = await NowAsync(cancellationToken).ConfigureAwait(true);

        long failures = 0;
        using MeterListener meterListener = ListenForDecryptionFailures(() => Interlocked.Increment(ref failures));

        using HttpClient client = gateway.CreateClient();
        using HttpResponseMessage response = await client
            .GetAsync(Redeem(link.Plaintext), cancellationToken).ConfigureAwait(true);

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
        meterListener.Dispose();

        // NOT a 500. A problem document carrying a traceId would say "the failure was ours, not your
        // token's" - which is exactly the distinction an attacker probing the storage layer wants.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        body.ShouldNotContain("traceId", Case.Sensitive);

        // And byte-identical to the ordinary denials, headers included. A 404 that differs in
        // Content-Length or Content-Disposition is still an oracle.
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        response.Content.Headers.ContentDisposition.ShouldBeNull("the response must not still look like a PDF download");

        Interlocked.Read(ref failures)
            .ShouldBe(1, "statement_decryption_failure_total must move - it is alerted on any non-zero value");

        await AssertDecryptionAuditedAsync(seeded.StatementId, since, cancellationToken).ConfigureAwait(true);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task CorruptedLaterFrame_AbortsTheTransfer_AndIsStillAudited()
    {
        // ★ THE HARD HALF, AND THE ONE A FIRST-FRAME TEST CANNOT REACH.
        //
        // Here bytes are already on the wire when the tag fails. The response cannot be turned into
        // a 404 any more - that is the limitation ADR-0019 names and accepts - so the only thing
        // still under our control is whether the client can TELL. Ending cleanly would give it a
        // 200 with a Content-Length it never reached: a silently truncated statement, which is the
        // precise failure the whole framed format exists to prevent. Aborting makes it unambiguous.
        //
        // The audit record and the counter must fire on this path too, and that is what regressed
        // silently before: CiphertextIntegrityException is neither IOException nor
        // CryptographicException, so the pre-existing catch never saw it.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // Thirty-two full frames plus a tail: the corruption sits in the LAST full frame, so
        // roughly 2 MiB must stream first - far past any response buffering, which is what
        // guarantees the client has real bytes in hand when the abort lands. (The original two
        // frames fit entirely inside the server's buffers; the client saw zero bytes and the
        // test could not tell the abort path from the pre-response path.)
        const int FullFrames = 32;
        const int SizeBytes = (FullFrames * 65536) + 512;

        SeededStatement seeded = await DownloadScenario
            .SeedAsync(_postgres, _minio, sizeBytes: SizeBytes, cancellationToken: cancellationToken).ConfigureAwait(true);

        // Header(48) + N-1 whole frames (4 + 65536 + 16 each) is where the last full frame
        // begins; +4 skips its length prefix, +100 lands inside its ciphertext.
        const int LastFramePayload = 48 + ((FullFrames - 1) * (4 + 65536 + 16)) + 4 + 100;
        await CorruptObjectByteAsync(seeded.StorageKey, LastFramePayload, cancellationToken).ConfigureAwait(true);

        using DeliveryApiFactory api = CreateApi();
        using DownloadGatewayFactory gateway = CreateGateway();

        IssuedLink link = await DownloadScenario.IssueAsync(api, seeded, cancellationToken: cancellationToken).ConfigureAwait(true);
        DateTimeOffset since = await NowAsync(cancellationToken).ConfigureAwait(true);

        long failures = 0;
        using MeterListener meterListener = ListenForDecryptionFailures(() => Interlocked.Increment(ref failures));

        using HttpClient client = gateway.CreateClient();

        long received = 0;
        bool transferFailed = false;
        HttpStatusCode? observedStatus = null;

        try
        {
            using HttpResponseMessage response = await client
                .GetAsync(Redeem(link.Plaintext), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(true);

            observedStatus = response.StatusCode;

            // The headers went out before the corruption was reachable, so this really is a 200.
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(true);
            received = await DrainAsync(stream, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            transferFailed = true;
        }

        meterListener.Dispose();

        // THE ASSERTION THAT MATTERS: the client must not have received a complete statement. Either
        // the connection died (the expected path) or the body was short - never a clean, full-length
        // transfer of authentic-but-tampered content.
        (transferFailed || received < SizeBytes)
            .ShouldBeTrue($"the transfer delivered all {SizeBytes} bytes cleanly despite a corrupt frame");

        // And the ABORT branch is the one exercised, distinguished by STATUS rather than by
        // delivered bytes: the pre-response path answers a uniform 404
        // (CorruptedFirstFrame pins that), while this path commits a 200 before the corrupt
        // frame is reachable. Byte counts cannot make the distinction through TestServer's
        // in-memory transport - the server can write the whole body and the abort into the
        // unbounded pipe before the client's read loop is ever scheduled, so a zero here
        // measures scheduling, not the product.
        if (observedStatus is { } status)
        {
            status.ShouldBe(
                HttpStatusCode.OK,
                "a non-200 means the failure was served before the response committed - the pre-response path, not the abort path");
        }
        else
        {
            transferFailed.ShouldBeTrue("no status and no failure means the request never completed at all");
        }

        Interlocked.Read(ref failures).ShouldBe(1);

        await AssertDecryptionAuditedAsync(seeded.StatementId, since, cancellationToken).ConfigureAwait(true);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task RepointedStatementRow_FailsTheFrameAad_AndIsAudited()
    {
        // ★ THE DATABASE-LEVEL ATTACK, ISOLATED TO THE AAD.
        //
        // An attacker with write access to the statement table repoints a row at a different object.
        // The subtlety, and the reason the obvious version of this test proves the wrong thing:
        // repointing at ANOTHER CUSTOMER's object fails at the CEK unwrap, two tiers above the AAD -
        // a real defence, but not this one, and the test would pass with the AAD removed entirely.
        //
        // So the decoy object is written for the SAME customer. The CEK matches, the DEK unwraps
        // cleanly, the header authenticates - and the only thing that still disagrees is the
        // statement id bound into every frame. That isolates the frame AAD as the thing under test,
        // which is what ADR-0021 actually claims.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SeededStatement victim = await DownloadScenario
            .SeedAsync(_postgres, _minio, cancellationToken: cancellationToken).ConfigureAwait(true);

        // A second object for the SAME customer, under a different statement id. It has no row of
        // its own - it exists only to be pointed at.
        byte[] decoyContent = System.Security.Cryptography.RandomNumberGenerator.GetBytes(6144);
        var decoyStatementId = Guid.CreateVersion7();

        (StatementDelivery.ServiceDefaults.Storage.S3StatementContentStore store, Amazon.S3.IAmazonS3 s3) =
            _minio.CreateStore(_postgres);

        StatementDelivery.ServiceDefaults.Storage.StoredObject decoy;

        using (s3)
        {
            using var plaintext = new MemoryStream(decoyContent, writable: false);

            decoy = await store.WriteAsync(
                plaintext,
                new StatementDelivery.Crypto.Framing.CryptoContext(decoyStatementId, victim.CustomerId, 1),
                new StatementDelivery.Domain.Identifiers.AccountId(victim.AccountId),
                StatementDelivery.Domain.ValueObjects.StatementPeriod.Create(victim.Period, victim.Period.AddMonths(1).AddDays(-1)),
                StatementDelivery.Crypto.Keys.CohortAssignment.KekIdFor(
                    StatementDelivery.Crypto.Keys.CohortAssignment.ForCustomer(
                        new StatementDelivery.Domain.Identifiers.CustomerId(victim.CustomerId))),
                cancellationToken).ConfigureAwait(true);
        }

        await using (NpgsqlConnection admin = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true))
        {
            // Everything needed to look legitimate: the decoy's key, size, digest and envelope. A
            // mismatched wrapped DEK would fail at the key layer and prove nothing about the AAD.
            _ = await admin.ExecuteAsync(new CommandDefinition(
                """
                UPDATE statement
                   SET storage_key    = @key,
                       size_bytes     = @size,
                       content_sha256 = @sha,
                       wrapped_dek    = @dek,
                       dek_algorithm  = @algorithm,
                       kek_id         = @kekId
                 WHERE id = @victim AND period_start = @period;
                """,
                new
                {
                    victim = victim.StatementId,
                    period = victim.Period,
                    key = decoy.Key,
                    size = decoy.PlaintextLength,
                    sha = decoy.Envelope.ContentSha256,
                    dek = decoy.Envelope.WrappedDek,
                    algorithm = decoy.Envelope.Algorithm,
                    kekId = decoy.Envelope.KekId,
                },
                commandTimeout: 30,
                cancellationToken: cancellationToken)).ConfigureAwait(true);
        }

        using DeliveryApiFactory api = CreateApi();
        using DownloadGatewayFactory gateway = CreateGateway();

        IssuedLink link = await DownloadScenario.IssueAsync(api, victim, cancellationToken: cancellationToken).ConfigureAwait(true);
        DateTimeOffset since = await NowAsync(cancellationToken).ConfigureAwait(true);

        using HttpClient client = gateway.CreateClient();
        using HttpResponseMessage response = await client
            .GetAsync(Redeem(link.Plaintext), cancellationToken).ConfigureAwait(true);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The decoy content is 6 KiB of random bytes, well under one frame, so if the AAD were not
        // checked the WHOLE of it would have been released in a single authentic-looking frame.
        // Searching the response for any 32-byte run of it is therefore a real assertion, not the
        // vacuous "a problem document is not a PDF" it would be against a multi-frame object.
        byte[] received = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(true);
        IndexOfSequence(received, decoyContent.AsSpan(0, 32).ToArray())
            .ShouldBe(-1, "not one byte of the decoy object may reach the wire");

        await AssertDecryptionAuditedAsync(victim.StatementId, since, cancellationToken).ConfigureAwait(true);
    }

    private async Task CorruptObjectByteAsync(string storageKey, int offset, CancellationToken cancellationToken)
    {
        using Amazon.S3.IAmazonS3 s3 = _minio.CreateClient();

        using Amazon.S3.Model.GetObjectResponse original = await s3.GetObjectAsync(
            new Amazon.S3.Model.GetObjectRequest { BucketName = MinioFixture.BucketName, Key = storageKey },
            cancellationToken).ConfigureAwait(true);

        using var buffer = new MemoryStream();
        await original.ResponseStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(true);

        byte[] bytes = buffer.ToArray();
        bytes.Length.ShouldBeGreaterThan(offset, "the fixture object must be long enough to corrupt at this offset");
        bytes[offset] ^= 0x01;

        using var corrupted = new MemoryStream(bytes, writable: false);
        _ = await s3.PutObjectAsync(
            new Amazon.S3.Model.PutObjectRequest
            {
                BucketName = MinioFixture.BucketName,
                Key = storageKey,
                InputStream = corrupted,
            },
            cancellationToken).ConfigureAwait(true);
    }

    private static MeterListener ListenForDecryptionFailures(Action onMeasurement)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == "statement_decryption_failure_total")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>((_, _, _, _) => onMeasurement());
        listener.Start();

        return listener;
    }

    private async Task AssertDecryptionAuditedAsync(Guid statementId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true);

        int audited = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT count(*) FROM audit_event
             WHERE statement_id = @statement
               AND action = 'ACCESS_DENIED'
               AND denial_reason_code = 'DECRYPTION_FAILED'
               AND occurred_at >= @since;
            """,
            new { statement = statementId, since },
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(true);

        audited.ShouldBe(1, "a decryption failure is an incident and must be in the audit trail");

        // And no DOWNLOAD_COMPLETED. A run recording both would mean the audit trail says a customer
        // received a statement they did not.
        int completed = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT count(*) FROM audit_event
             WHERE statement_id = @statement AND action = 'DOWNLOAD_COMPLETED' AND occurred_at >= @since;
            """,
            new { statement = statementId, since },
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(true);

        completed.ShouldBe(0);
    }

    private static int IndexOfSequence(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
    }


    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Denials_ArePaddedToTheConfiguredTimingFloor()
    {
        // "Comparable timing" is the fourth clause of the identical-response rule, and it is the one
        // an assertion on status and body cannot reach. Without the floor, "unknown token" costs one
        // index probe and "already consumed" costs a probe plus a diagnosis query - a difference a
        // caller can measure.
        //
        // WHAT THIS TEST DOES NOT CLAIM: that timing leakage is eliminated. Padding to a floor still
        // leaks through the tail. See DownloadOptions for the honest account; the primary protection
        // is that enumerating a 256-bit space is pointless whatever the timings say.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const int FloorMs = 250;

        SeededStatement seeded = await DownloadScenario
            .SeedAsync(_postgres, _minio, cancellationToken: cancellationToken).ConfigureAwait(true);

        using DeliveryApiFactory api = CreateApi();
        using DownloadGatewayFactory gateway = CreateGateway(denialFloorMilliseconds: FloorMs);
        using HttpClient client = gateway.CreateClient();

        // The cheapest failure the endpoint has: it never touches the database.
        IssuedLink consumedLink = await DownloadScenario
            .IssueAsync(api, seeded, cancellationToken: cancellationToken).ConfigureAwait(true);

        using (HttpResponseMessage first = await client
            .GetAsync(Redeem(consumedLink.Plaintext), cancellationToken).ConfigureAwait(true))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // The most expensive: a consume that matches nothing, then a diagnosis query to classify it.
        (string Label, string Token)[] cases =
        [
            ("malformed", "not-a-token"),
            ("unknown", RandomToken()),
            ("consumed", consumedLink.Plaintext),
        ];

        var elapsed = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach ((string label, string token) in cases)
        {
            // Warm first. A cold connection pool or a first-touch JIT on one branch would dominate
            // the measurement and this test would be timing the runtime rather than the endpoint.
            using (HttpResponseMessage warm = await client.GetAsync(Redeem(token), cancellationToken).ConfigureAwait(true))
            {
                warm.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            }

            long start = System.Diagnostics.Stopwatch.GetTimestamp();

            using HttpResponseMessage response = await client.GetAsync(Redeem(token), cancellationToken).ConfigureAwait(true);
            _ = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);

            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            elapsed[label] = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        foreach ((string label, double milliseconds) in elapsed)
        {
            // A small tolerance below the floor: Task.Delay rounds to the timer resolution, and a
            // test that demanded exactly the floor would be flaky for a reason that is not a defect.
            milliseconds.ShouldBeGreaterThan(
                FloorMs - 30,
                $"the '{label}' denial returned in {milliseconds:F0} ms, under the {FloorMs} ms floor");
        }

        // And the branches are within a fraction of the floor of each other, which is the property
        // the floor exists to produce - the cheap failure no longer finishes visibly sooner.
        double spread = elapsed.Values.Max() - elapsed.Values.Min();

        spread.ShouldBeLessThan(
            FloorMs,
            $"denial timings differ by {spread:F0} ms across {string.Join(", ", elapsed.Keys)}; "
            + "that spread is the oracle the floor exists to close");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task DownloadLink_ForAnotherCustomersStatement_Returns404()
    {
        // The IDOR case on the ISSUE side. A token for someone else's statement must never be
        // mintable, and the refusal must not confirm that the statement exists.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SeededStatement theirs = await DownloadScenario.SeedAsync(_postgres, _minio, cancellationToken: cancellationToken).ConfigureAwait(true);
        SeededStatement mine = await DownloadScenario.SeedAsync(_postgres, _minio, cancellationToken: cancellationToken).ConfigureAwait(true);

        using DeliveryApiFactory api = CreateApi();

        using HttpResponseMessage response = await DownloadScenario
            .IssueRawAsync(api, theirs, asCustomer: mine.CustomerId, cancellationToken: cancellationToken)
            .ConfigureAwait(true);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, "not 403 - a 403 would confirm the statement exists");

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true);
        int tokens = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM download_token WHERE statement_id = @statement;",
            new { statement = theirs.StatementId },
            commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(true);

        tokens.ShouldBe(0, "a refused issue must not leave a token behind");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Token_ForStatementA_CannotAccessStatementB()
    {
        // The token is bound to ONE statement. Redeeming it must yield exactly that statement's
        // bytes - not whatever the gateway would have found by another route.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SeededStatement a = await DownloadScenario.SeedAsync(_postgres, _minio, cancellationToken: cancellationToken).ConfigureAwait(true);
        SeededStatement b = await DownloadScenario.SeedAsync(_postgres, _minio, cancellationToken: cancellationToken).ConfigureAwait(true);

        using DeliveryApiFactory api = CreateApi();
        using DownloadGatewayFactory gateway = CreateGateway();

        IssuedLink link = await DownloadScenario.IssueAsync(api, a, cancellationToken: cancellationToken).ConfigureAwait(true);

        using HttpClient client = gateway.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(Redeem(link.Plaintext), cancellationToken).ConfigureAwait(true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        byte[] delivered = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(true);

        delivered.ShouldBe(a.Content);
        delivered.ShouldNotBe(b.Content);

        // And the token row still names only A.
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true);
        Guid statementId = await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            "SELECT statement_id FROM download_token WHERE id = @id;",
            new { id = Guid.Parse(link.LinkId) },
            commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(true);

        statementId.ShouldBe(a.StatementId);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task RevokedToken_CannotBeRedeemed()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SeededStatement seeded = await DownloadScenario.SeedAsync(_postgres, _minio, cancellationToken: cancellationToken).ConfigureAwait(true);

        using DeliveryApiFactory api = CreateApi();
        using DownloadGatewayFactory gateway = CreateGateway();

        IssuedLink link = await DownloadScenario.IssueAsync(api, seeded, cancellationToken: cancellationToken).ConfigureAwait(true);

        using (HttpResponseMessage revoke = await DownloadScenario
            .RevokeAsync(api, link.LinkId, seeded.CustomerId, cancellationToken).ConfigureAwait(true))
        {
            revoke.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using HttpClient client = gateway.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(Redeem(link.Plaintext), cancellationToken).ConfigureAwait(true);

        // Immediate: revocation takes effect on the NEXT attempt because every redemption validates
        // against this database. A presigned URL could not do this without rotating the key.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true);
        int stillUnconsumed = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM download_token WHERE id = @id AND revoked_at IS NOT NULL AND consumed_at IS NULL;",
            new { id = Guid.Parse(link.LinkId) },
            commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(true);

        stillUnconsumed.ShouldBe(1, "a revoked token must not be consumed by the attempt that was refused");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task ConsumedToken_SecondAttempt_Returns404()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SeededStatement seeded = await DownloadScenario.SeedAsync(_postgres, _minio, cancellationToken: cancellationToken).ConfigureAwait(true);

        using DeliveryApiFactory api = CreateApi();
        using DownloadGatewayFactory gateway = CreateGateway();

        IssuedLink link = await DownloadScenario.IssueAsync(api, seeded, cancellationToken: cancellationToken).ConfigureAwait(true);

        using HttpClient client = gateway.CreateClient();

        using (HttpResponseMessage first = await client.GetAsync(Redeem(link.Plaintext), cancellationToken).ConfigureAwait(true))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using HttpResponseMessage second = await client.GetAsync(Redeem(link.Plaintext), cancellationToken).ConfigureAwait(true);
        second.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task ExpiredToken_Returns404()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SeededStatement seeded = await DownloadScenario.SeedAsync(_postgres, _minio, cancellationToken: cancellationToken).ConfigureAwait(true);

        using DeliveryApiFactory api = CreateApi();
        using DownloadGatewayFactory gateway = CreateGateway();

        IssuedLink link = await DownloadScenario.IssueAsync(api, seeded, cancellationToken: cancellationToken).ConfigureAwait(true);
        await DownloadScenario.ExpireAsync(_postgres, link.LinkId, cancellationToken).ConfigureAwait(true);

        using HttpClient client = gateway.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(Redeem(link.Plaintext), cancellationToken).ConfigureAwait(true);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("AAAA")]
    [InlineData("%20%20%20")]
    [InlineData("....................................!!!!!!")]
    public async Task MalformedToken_Returns404_NotBadRequest(string token)
    {
        // A 400 would tell an attacker their encoding was right, which is free information about
        // the token format. Every malformed value gets the same 404 as a wrong one.
        Assert.SkipUnless(DockerAvailability.IsAvailable, DockerAvailability.SkipReason);

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using DownloadGatewayFactory gateway = CreateGateway();
        using HttpClient client = gateway.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(Redeem(token), cancellationToken).ConfigureAwait(true);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task AllSecurityHeaders_ArePresentOnSuccess()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SeededStatement seeded = await DownloadScenario.SeedAsync(_postgres, _minio, cancellationToken: cancellationToken).ConfigureAwait(true);

        using DeliveryApiFactory api = CreateApi();
        using DownloadGatewayFactory gateway = CreateGateway();

        IssuedLink link = await DownloadScenario.IssueAsync(api, seeded, cancellationToken: cancellationToken).ConfigureAwait(true);

        using HttpClient client = gateway.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(Redeem(link.Plaintext), cancellationToken).ConfigureAwait(true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string Header(string name) =>
            response.Headers.TryGetValues(name, out IEnumerable<string>? values)
                ? string.Join(',', values)
                : response.Content.Headers.TryGetValues(name, out IEnumerable<string>? contentValues)
                    ? string.Join(',', contentValues)
                    : string.Empty;

        Header("Cache-Control").ShouldContain("no-store");
        Header("Cache-Control").ShouldContain("private");
        Header("Pragma").ShouldContain("no-cache");
        Header("Expires").ShouldBe("0");
        Header("Content-Disposition").ShouldStartWith("attachment;");
        Header("X-Content-Type-Options").ShouldBe("nosniff");
        Header("X-Frame-Options").ShouldBe("DENY");

        // THE ONE THAT MATTERS MOST AND IS EASIEST TO FORGET. Without it, a statement URL rendered
        // in a page context sends the token - the entire credential - in the Referer header to
        // every third-party resource that page loads.
        Header("Referrer-Policy").ShouldBe("no-referrer");

        Header("Cross-Origin-Resource-Policy").ShouldBe("same-origin");
        Header("Strict-Transport-Security").ShouldBe("max-age=63072000; includeSubDomains; preload");

        // A decision, not an oversight: a resumed transfer is a second request against a token that
        // is already consumed. See ADR-0016.
        Header("Accept-Ranges").ShouldBe("none");

        // And a Range request is ignored rather than honoured: a 206 would mean the transfer could
        // be resumed, which a single-use token cannot support.
        using HttpClient ranged = gateway.CreateClient();
        IssuedLink second = await DownloadScenario.IssueAsync(api, seeded, cancellationToken: cancellationToken).ConfigureAwait(true);
        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, Redeem(second.Plaintext));
        rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 99);

        using HttpResponseMessage rangeResponse = await ranged.SendAsync(rangeRequest, cancellationToken).ConfigureAwait(true);
        rangeResponse.StatusCode.ShouldBe(HttpStatusCode.OK, "a 206 would mean ranges were honoured");
        rangeResponse.Content.Headers.ContentLength.ShouldBe(seeded.Content.Length, "the whole object, not a slice");

        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/pdf");
        response.Content.Headers.ContentLength.ShouldBe(seeded.Content.Length);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task IdempotencyTable_RemainsEmpty_AfterLinkIssue()
    {
        // Two halves. First: no idempotency store exists at all, so nothing can be persisting the
        // response body. Second: the endpoint carries the exemption a future middleware must
        // honour. Asserting only the first would pass right up until the middleware lands.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SeededStatement seeded = await DownloadScenario.SeedAsync(_postgres, _minio, cancellationToken: cancellationToken).ConfigureAwait(true);

        using DeliveryApiFactory api = CreateApi();
        IssuedLink link = await DownloadScenario.IssueAsync(api, seeded, cancellationToken: cancellationToken).ConfigureAwait(true);

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true);

        string? table = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT to_regclass('public.idempotency_key')::text;",
            commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(true);

        table.ShouldBeNull("no idempotency store may exist while link issue returns a secret - see ADR-0014");

        // And the marker is on the endpoint.
        Microsoft.AspNetCore.Routing.EndpointDataSource endpoints = api.Services
            .GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>();

        Microsoft.AspNetCore.Http.Endpoint issue = endpoints.Endpoints
            .First(endpoint => string.Equals(
                endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IEndpointNameMetadata>()?.EndpointName,
                "IssueDownloadLink",
                StringComparison.Ordinal));

        issue.Metadata.GetMetadata<Delivery.Api.Downloads.SkipIdempotencyAttribute>()
            .ShouldNotBeNull("the issue endpoint must be marked exempt from idempotency replay");

        link.Plaintext.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task TokenPlaintext_NeverAppearsInAnyDatabaseColumn()
    {
        // Every text-like and binary column in the schema, searched for the plaintext. Not just
        // download_token: the point is that no code path anywhere wrote it down, including the
        // audit trail's JSONB detail, which is the most likely accident.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SeededStatement seeded = await DownloadScenario.SeedAsync(_postgres, _minio, cancellationToken: cancellationToken).ConfigureAwait(true);

        using DeliveryApiFactory api = CreateApi();
        using DownloadGatewayFactory gateway = CreateGateway();

        IssuedLink link = await DownloadScenario.IssueAsync(api, seeded, cancellationToken: cancellationToken).ConfigureAwait(true);

        using (HttpClient client = gateway.CreateClient())
        {
            using HttpResponseMessage ok = await client.GetAsync(Redeem(link.Plaintext), cancellationToken).ConfigureAwait(true);
            ok.StatusCode.ShouldBe(HttpStatusCode.OK);

            // And a failure path too, in case only the denial branch is careless.
            using HttpResponseMessage denied = await client.GetAsync(Redeem(link.Plaintext), cancellationToken).ConfigureAwait(true);
            denied.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true);

        // Only the base tables. Partitions hold the same rows as their parents and searching both
        // would double the work for no additional coverage.
        var columns = (await connection.QueryAsync<(string Table, string Column, string Type)>(new CommandDefinition(
            """
            SELECT c.table_name, c.column_name, c.data_type
              FROM information_schema.columns c
              JOIN pg_class     cls ON cls.relname = c.table_name
              JOIN pg_namespace ns  ON ns.oid = cls.relnamespace AND ns.nspname = 'public'
             WHERE c.table_schema = 'public'
               AND cls.relispartition = false
               AND c.data_type IN ('text', 'character varying', 'character', 'bytea', 'jsonb', 'json', 'inet')
             ORDER BY c.table_name, c.column_name;
            """,
            commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(true)).ToList();

        columns.ShouldNotBeEmpty("the schema must be readable for this scan to prove anything");

        var offenders = new List<string>();

        foreach ((string table, string column, string type) in columns)
        {
            string expression = string.Equals(type, "bytea", StringComparison.Ordinal)
                ? $"encode(\"{column}\", 'escape')"
                : $"\"{column}\"::text";

            long hits = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                $"SELECT count(*) FROM \"{table}\" WHERE {expression} LIKE @pattern;",
                new { pattern = "%" + link.Plaintext + "%" },
                commandTimeout: 60,
                cancellationToken: cancellationToken)).ConfigureAwait(true);

            if (hits > 0)
            {
                offenders.Add($"{table}.{column}");
            }
        }

        offenders.ShouldBeEmpty("the plaintext token must exist in NO database column");

        // The hash, by contrast, must be there - or the scan above is passing because nothing was
        // stored at all.
        int stored = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM download_token WHERE id = @id;",
            new { id = Guid.Parse(link.LinkId) },
            commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(true);

        stored.ShouldBe(1);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task RedeemRateLimit_BitesPerAddress_AndAlwaysReturnsRetryAfter()
    {
        // PER IP IS THE ANTI-ENUMERATION CONTROL. A guessing script presents a different token on
        // every attempt, so no per-token counter would ever reach two - the address is the only
        // thing it cannot vary for free. See ADR-0018.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const int Budget = 5;
        const int Attempts = 12;

        using DownloadGatewayFactory gateway = CreateGateway(redeemPerMinute: Budget, permitLimit: 1000);
        using HttpClient client = gateway.CreateClient();

        var statuses = new List<HttpResponseMessage>();

        try
        {
            for (int attempt = 0; attempt < Attempts; attempt++)
            {
                // A different token every time, exactly as a guessing script would. If the limit
                // were scoped to the token rather than the address, none of these would count.
                statuses.Add(await client
                    .GetAsync(Redeem(RandomToken()), cancellationToken).ConfigureAwait(true));
            }

            statuses.Count(static response => response.StatusCode == HttpStatusCode.NotFound)
                .ShouldBe(Budget, "the first requests within budget are ordinary denials");

            HttpResponseMessage[] refused =
                [.. statuses.Where(static response => response.StatusCode == HttpStatusCode.TooManyRequests)];

            refused.Length.ShouldBe(Attempts - Budget, "everything past the budget must be refused");

            foreach (HttpResponseMessage response in refused)
            {
                // RETRY-AFTER ON EVERY 429, WITHOUT EXCEPTION. A 429 with no Retry-After makes the
                // client guess, and the guess is usually "immediately" - which turns a rate limit
                // into a retry storm against the endpoint it was meant to protect.
                response.Headers.RetryAfter.ShouldNotBeNull();
                (response.Headers.RetryAfter!.Delta?.TotalSeconds ?? 0).ShouldBeGreaterThan(0);

                string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);

                // The rule that was hit is operator information. Telling the caller whether they
                // tripped the per-minute or the per-hour budget hands a script the shape of the
                // control it is trying to stay under.
                body.ShouldNotContain("redeem-address-minute");
            }
        }
        finally
        {
            foreach (HttpResponseMessage response in statuses)
            {
                response.Dispose();
            }
        }
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task ConcurrencyLimitRejection_AlsoCarriesRetryAfter()
    {
        // The 429 that is easiest to ship without a Retry-After. A concurrency limiter cannot know
        // when a permit will free, so it publishes no RetryAfter metadata - and a handler that only
        // sets the header when metadata exists emits a bare 429 for precisely the case that happens
        // under load, which is when a retry storm hurts most.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        using DownloadGatewayFactory gateway = new(
            _postgres.ConnectionStringFor("app_download"),
            _minio.ServiceUrl,
            redeemPerMinute: 1000,
            permitLimit: 1000,

            // Long enough that every request is still in flight when the next arrives, so the
            // concurrency permit - not the window budget - is what runs out.
            denialFloorMilliseconds: 1500,
            maxConcurrentDownloads: 1);

        using HttpClient client = gateway.CreateClient();

        // MaxConcurrentDownloads is 1 for this host only; anything past the first in-flight request
        // is refused by the concurrency limiter rather than by a window.
        HttpResponseMessage[] responses = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => client.GetAsync(Redeem(RandomToken()), cancellationToken)))
            .ConfigureAwait(true);

        try
        {
            HttpResponseMessage[] refused =
                [.. responses.Where(static response => response.StatusCode == HttpStatusCode.TooManyRequests)];

            refused.ShouldNotBeEmpty("the concurrency limiter must have refused something");

            foreach (HttpResponseMessage response in refused)
            {
                response.Headers.RetryAfter.ShouldNotBeNull(
                    "a 429 with no Retry-After makes the client guess, and the guess is 'immediately'");
                (response.Headers.RetryAfter!.Delta?.TotalSeconds ?? 0).ShouldBeGreaterThan(0);
            }
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }
    }

    private static string RandomToken() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    // =============================================================================================
    //  J4. STREAMING AND BEHAVIOUR
    // =============================================================================================

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task LargeStatement_StreamsWithoutHeapGrowth()
    {
        // 200 MB through a process that must not grow by more than a few. If the response were
        // buffered - ReadToEndAsync, ToArray, a MemoryStream - the delta would be 200 MB and the
        // allocation would land on the large object heap, which is collected rarely and compacted
        // almost never.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const int SizeBytes = 200 * 1024 * 1024;

        SeededStatement seeded = await DownloadScenario
            .SeedAsync(_postgres, _minio, SizeBytes, cancellationToken).ConfigureAwait(true);

        using DeliveryApiFactory api = CreateApi();
        using DownloadGatewayFactory gateway = CreateGateway();

        IssuedLink link = await DownloadScenario.IssueAsync(api, seeded, cancellationToken: cancellationToken).ConfigureAwait(true);

        using HttpClient client = gateway.CreateClient();

        // Settle first, so the measurement is of the transfer and not of host startup.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        long before = GC.GetTotalMemory(forceFullCollection: true);

        long received;
        using (HttpResponseMessage response = await client
            .GetAsync(Redeem(link.Plaintext), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(true))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Content.Headers.ContentLength.ShouldBe(SizeBytes);

            // Counted rather than buffered, on purpose: buffering here would make the TEST the
            // thing that allocates 200 MB and the assertion below meaningless.
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(true);
            received = await DrainAsync(stream, cancellationToken).ConfigureAwait(true);
        }

        received.ShouldBe(SizeBytes);

        long after = GC.GetTotalMemory(forceFullCollection: true);
        long delta = after - before;

        delta.ShouldBeLessThan(
            10L * 1024 * 1024,
            $"streaming 200 MB grew the managed heap by {delta / (1024 * 1024)} MB; memory must scale with concurrency, not statement size");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task EndToEndDownload_200MB_UsesConstantMemory()
    {
        // THE ENCRYPTED-STORAGE SUCCESSOR TO THE PLAINTEXT STREAMING TEST, and the reason it is a
        // separate test rather than a rename: it asserts one thing more.
        //
        // LargeStatement_StreamsWithoutHeapGrowth proves the transfer is O(1) in memory. This proves
        // that it is O(1) in memory AND that what came out is byte-for-byte what went in - through a
        // 3,200-frame decryption, every frame separately authenticated. Constant memory over
        // corrupted output would be a passing test and a broken product.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const int SizeBytes = 200 * 1024 * 1024;

        SeededStatement seeded = await DownloadScenario
            .SeedAsync(_postgres, _minio, SizeBytes, cancellationToken).ConfigureAwait(true);

        byte[] expectedDigest = System.Security.Cryptography.SHA256.HashData(seeded.Content);

        using DeliveryApiFactory api = CreateApi();
        using DownloadGatewayFactory gateway = CreateGateway();

        IssuedLink link = await DownloadScenario.IssueAsync(api, seeded, cancellationToken: cancellationToken).ConfigureAwait(true);

        using HttpClient client = gateway.CreateClient();

        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        long before = GC.GetTotalMemory(forceFullCollection: true);

        long received;
        byte[] actualDigest;

        using (HttpResponseMessage response = await client
            .GetAsync(Redeem(link.Plaintext), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(true))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            // The PLAINTEXT length, from the row. The stored object is larger by 48 bytes plus 20
            // per frame; a client told the ciphertext length would wait forever for bytes that do
            // not exist.
            response.Content.Headers.ContentLength.ShouldBe(SizeBytes);

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(true);

            using var digest = System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256);

            byte[] buffer = new byte[64 * 1024];
            received = 0;

            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(true)) > 0)
            {
                digest.AppendData(buffer.AsSpan(0, read));
                received += read;
            }

            actualDigest = digest.GetHashAndReset();
        }

        received.ShouldBe(SizeBytes);
        actualDigest.ShouldBe(expectedDigest, "the decrypted bytes must be exactly what was encrypted");

        long after = GC.GetTotalMemory(forceFullCollection: true);
        long delta = after - before;

        delta.ShouldBeLessThan(
            10L * 1024 * 1024,
            $"an encrypted 200 MB download grew the managed heap by {delta / (1024 * 1024)} MB; framed decryption must stay O(1)");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task ClientDisconnect_AbortsRead_AndAuditsIncomplete()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const int SizeBytes = 64 * 1024 * 1024;

        SeededStatement seeded = await DownloadScenario
            .SeedAsync(_postgres, _minio, SizeBytes, cancellationToken).ConfigureAwait(true);

        using DeliveryApiFactory api = CreateApi();
        using DownloadGatewayFactory gateway = CreateGateway();

        IssuedLink link = await DownloadScenario.IssueAsync(api, seeded, cancellationToken: cancellationToken).ConfigureAwait(true);

        using HttpClient client = gateway.CreateClient();
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            using HttpResponseMessage response = await client
                .GetAsync(Redeem(link.Plaintext), HttpCompletionOption.ResponseHeadersRead, abort.Token)
                .ConfigureAwait(true);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            await using Stream stream = await response.Content.ReadAsStreamAsync(abort.Token).ConfigureAwait(true);

            // Read a little, then hang up mid-transfer.
            byte[] buffer = new byte[32 * 1024];
            _ = await stream.ReadAsync(buffer, abort.Token).ConfigureAwait(true);
            await abort.CancelAsync().ConfigureAwait(true);

            _ = await stream.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException)
        {
            // Expected: the client hung up. That is the scenario.
        }

        (int Incomplete, int Started) counts = await WaitForOutcomeAsync(
            seeded.StatementId, cancellationToken).ConfigureAwait(true);

        counts.Started.ShouldBe(1, "access was granted before any byte was sent, and that must be recorded");
        counts.Incomplete.ShouldBe(1, "an aborted transfer must be recorded as DOWNLOAD_INCOMPLETE");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Download_ConsumesToken_EvenIfStreamAborts()
    {
        // ADR-0017. If an abort released the token, an attacker would replay it indefinitely by
        // killing the connection every time - and every abort would leave no evidence that access
        // had been granted at all.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const int SizeBytes = 64 * 1024 * 1024;

        SeededStatement seeded = await DownloadScenario
            .SeedAsync(_postgres, _minio, SizeBytes, cancellationToken).ConfigureAwait(true);

        using DeliveryApiFactory api = CreateApi();
        using DownloadGatewayFactory gateway = CreateGateway();

        IssuedLink link = await DownloadScenario.IssueAsync(api, seeded, cancellationToken: cancellationToken).ConfigureAwait(true);

        using HttpClient client = gateway.CreateClient();
        using (var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            try
            {
                using HttpResponseMessage response = await client
                    .GetAsync(Redeem(link.Plaintext), HttpCompletionOption.ResponseHeadersRead, abort.Token)
                    .ConfigureAwait(true);

                await using Stream stream = await response.Content.ReadAsStreamAsync(abort.Token).ConfigureAwait(true);
                byte[] buffer = new byte[8 * 1024];
                _ = await stream.ReadAsync(buffer, abort.Token).ConfigureAwait(true);
                await abort.CancelAsync().ConfigureAwait(true);
                _ = await stream.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException)
            {
                // Expected.
            }
        }

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true);

        int consumed = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM download_token WHERE id = @id AND consumed_at IS NOT NULL;",
            new { id = Guid.Parse(link.LinkId) },
            commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(true);

        consumed.ShouldBe(1, "the token must stay consumed after an abort");

        // And the retry gets nothing.
        using HttpResponseMessage retry = await client.GetAsync(Redeem(link.Plaintext), cancellationToken).ConfigureAwait(true);
        retry.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // =============================================================================================
    //  Audit
    // =============================================================================================

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task AuditChain_StillVerifies_AfterFullLifecycle()
    {
        // Issue, redeem, replay, revoke, and a guess at a token that never existed - the whole
        // lifecycle, writing to every chain the events hash to. Then verify.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        SeededStatement seeded = await DownloadScenario.SeedAsync(_postgres, _minio, cancellationToken: cancellationToken).ConfigureAwait(true);

        using DeliveryApiFactory api = CreateApi();
        using DownloadGatewayFactory gateway = CreateGateway();
        using HttpClient client = gateway.CreateClient();

        IssuedLink redeemed = await DownloadScenario.IssueAsync(api, seeded, cancellationToken: cancellationToken).ConfigureAwait(true);
        using (HttpResponseMessage ok = await client.GetAsync(Redeem(redeemed.Plaintext), cancellationToken).ConfigureAwait(true))
        {
            ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (HttpResponseMessage replay = await client.GetAsync(Redeem(redeemed.Plaintext), cancellationToken).ConfigureAwait(true))
        {
            replay.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        IssuedLink revoked = await DownloadScenario.IssueAsync(api, seeded, cancellationToken: cancellationToken).ConfigureAwait(true);
        using (HttpResponseMessage revoke = await DownloadScenario
            .RevokeAsync(api, revoked.LinkId, seeded.CustomerId, cancellationToken).ConfigureAwait(true))
        {
            revoke.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using (HttpResponseMessage guess = await client.GetAsync(Redeem("nonsense"), cancellationToken).ConfigureAwait(true))
        {
            guess.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        // Through the real staff endpoint, not by calling the verifier directly - the endpoint's
        // authorisation is part of what is being checked.
        using HttpClient staff = api.CreateClient();
        staff.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", DeliveryApiFactory.StaffTokenFor(seeded.CustomerId));

        using HttpResponseMessage verification = await staff
            .GetAsync(new Uri("/v1/audit/verify", UriKind.Relative), cancellationToken).ConfigureAwait(true);

        verification.StatusCode.ShouldBe(HttpStatusCode.OK, "a 409 here means a chain failed to verify");

        System.Text.Json.JsonElement body = await verification.Content
            .ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken).ConfigureAwait(true);

        body.GetProperty("verified").GetBoolean().ShouldBeTrue();
        body.GetProperty("eventsChecked").GetInt64().ShouldBeGreaterThan(0);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task AuditVerify_WithoutStaffScope_IsForbidden()
    {
        // Every authenticated caller on this platform is a CUSTOMER. Behind authentication alone,
        // the audit trail would be readable by everyone the system exists to serve.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        using DeliveryApiFactory api = CreateApi();
        using HttpClient client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", DeliveryApiFactory.TokenFor(Guid.CreateVersion7()));

        using HttpResponseMessage response = await client
            .GetAsync(new Uri("/v1/audit/verify", UriKind.Relative), cancellationToken).ConfigureAwait(true);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>Reads the DATABASE clock, not the test host's.</summary>
    /// <remarks>
    /// Audit timestamps come from <c>now()</c> inside PostgreSQL. Comparing them against
    /// <c>DateTimeOffset.UtcNow</c> would work only while the container's clock and the host's agree,
    /// which is exactly the assumption that fails on a loaded machine.
    /// </remarks>
    private async Task<DateTimeOffset> NowAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(false);

        // Read as DateTime: Dapper's scalar path converts with Convert.ChangeType, which has
        // no DateTime-to-DateTimeOffset conversion and throws InvalidCastException.
        DateTime now = await connection.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            "SELECT now();", commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new DateTimeOffset(now, TimeSpan.Zero);
    }

    private static async Task<long> DrainAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[80 * 1024];
        long total = 0;
        int read;

        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
        }

        return total;
    }

    /// <summary>
    /// Waits for the transfer-outcome audit records, which are written outside the request.
    /// </summary>
    /// <remarks>
    /// DOWNLOAD_COMPLETED and DOWNLOAD_INCOMPLETE are recorded after the response body has finished,
    /// so a client that has already seen its last byte can observe the database before they land.
    /// Polling with a deadline rather than sleeping a fixed interval keeps the test both reliable
    /// and fast when the write is prompt.
    /// </remarks>
    private async Task<(int Incomplete, int Started)> WaitForOutcomeAsync(Guid statementId, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);

        while (true)
        {
            await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(false);

            (int incomplete, int started) = await connection.QuerySingleAsync<(int, int)>(new CommandDefinition(
                """
                SELECT
                    count(*) FILTER (WHERE action = 'DOWNLOAD_INCOMPLETE') AS incomplete,
                    count(*) FILTER (WHERE action = 'DOWNLOAD_STARTED')    AS started
                  FROM audit_event
                 WHERE statement_id = @statement;
                """,
                new { statement = statementId },
                commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (incomplete > 0 || DateTime.UtcNow > deadline)
            {
                return (incomplete, started);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }
    }
}
