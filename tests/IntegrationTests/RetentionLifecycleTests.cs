using System.Diagnostics.Metrics;
using Amazon.S3;
using Amazon.S3.Model;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Retention.Worker;
using Retention.Worker.Configuration;
using Shouldly;
using StatementDelivery.Crypto.Framing;
using StatementDelivery.Crypto.Keys;
using StatementDelivery.Domain.Abstractions;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Domain.Statements;
using StatementDelivery.Domain.ValueObjects;
using StatementDelivery.Persistence.Auditing;
using StatementDelivery.Persistence.Connections;
using StatementDelivery.Persistence.Ids;
using StatementDelivery.Persistence.Keys;
using StatementDelivery.Persistence.Repositories;
using StatementDelivery.Persistence.Retention;
using StatementDelivery.Persistence.Uow;
using StatementDelivery.ServiceDefaults;
using StatementDelivery.ServiceDefaults.Auditing;
using StatementDelivery.ServiceDefaults.Storage;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// The retention lifecycle, end to end against real PostgreSQL and MinIO: purge under the decision
/// engine, legal holds in both layers, crypto-erasure with re-evaluation, restores, and
/// reconciliation findings.
/// </summary>
/// <remarks>
/// One piece of scaffolding needs an honest label: the test bucket uses GOVERNANCE mode, and
/// several tests simulate "the lock expired" by shortening an object's governance retention with
/// the bypass permission — the thing GOVERNANCE exists to allow and COMPLIANCE to forbid.
/// Production purges objects whose locks expired naturally; no bypass exists anywhere in
/// production code, and <see cref="EncryptedStorageTests"/> separately proves the locks hold.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class RetentionLifecycleTests
{
    private readonly PostgresFixture _postgres;
    private readonly MinioFixture _minio;

    /// <summary>Initialises a new instance of the <see cref="RetentionLifecycleTests"/> class.</summary>
    /// <param name="postgres">The shared PostgreSQL fixture.</param>
    /// <param name="minio">The shared object storage fixture.</param>
    public RetentionLifecycleTests(PostgresFixture postgres, MinioFixture minio)
    {
        _postgres = postgres;
        _minio = minio;
    }

    // ─── the purge ───────────────────────────────────────────────────────────────────────────────

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Purge_DeletesStorage_MarksRow_AndRetainsMetadata()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Published published = await PublishAsync(ct).ConfigureAwait(true);
        await BackdateRetainUntilAsync(published, ct).ConfigureAwait(true);
        await UnlockObjectAsync(published.StorageKey, ct).ConfigureAwait(true);

        using Harness harness = CreateHarness();
        int purged = await harness.Purge.RunAsync(fenceToken: 1, ct).ConfigureAwait(true);

        purged.ShouldBeGreaterThanOrEqualTo(1);

        // Storage first: the object is gone, every version of it.
        ObjectRetentionInfo info = await harness.Objects.GetRetentionAsync(published.StorageKey, ct)
            .ConfigureAwait(true);
        info.Exists.ShouldBeFalse("purge must delete all object versions");

        // The metadata SURVIVES as the proof of deletion: row present, purged_at set, pointers
        // and crypto material nulled, digest kept.
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true);
        (string Status, bool HasPurgedAt, string? StorageKey, byte[]? WrappedDek) row =
            await connection.QuerySingleAsync<(string, bool, string?, byte[]?)>(new CommandDefinition(
                """
                SELECT status, purged_at IS NOT NULL, storage_key, wrapped_dek
                  FROM statement WHERE id = @id AND period_start = @period;
                """,
                new { id = published.StatementId, period = published.Period },
                commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);

        row.Status.ShouldBe("PURGED");
        row.HasPurgedAt.ShouldBeTrue("purged_at is the proof-of-deletion timestamp");
        row.StorageKey.ShouldBeNull();
        row.WrappedDek.ShouldBeNull();

        // The audit records the act, with the deleted version ids in its detail.
        long audits = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM audit_event WHERE statement_id = @id AND action = 'RETENTION_PURGED';",
            new { id = published.StatementId },
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);
        audits.ShouldBe(1);

        // And the tombstone tells the orphan sweep the key is accounted for.
        string kind = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT kind FROM storage_tombstone WHERE storage_key = @key;",
            new { key = published.StorageKey },
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true) ?? "";
        kind.ShouldBe("PURGED");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Purge_CrashAfterDelete_IsSafeToRetry()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Published published = await PublishAsync(ct).ConfigureAwait(true);
        await BackdateRetainUntilAsync(published, ct).ConfigureAwait(true);
        await UnlockObjectAsync(published.StorageKey, ct).ConfigureAwait(true);

        using Harness harness = CreateHarness();

        // Simulate the crash point: storage deleted, database never marked.
        _ = await harness.Objects.DeleteObjectVersionsAsync(published.StorageKey, ct).ConfigureAwait(true);

        // The retry: a missing object is SUCCESS, and the row still gets marked. This is why
        // storage-before-database is the recoverable ordering (ADR-0034).
        int purged = await harness.Purge.RunAsync(fenceToken: 2, ct).ConfigureAwait(true);
        purged.ShouldBeGreaterThanOrEqualTo(1);

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true);
        string status = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM statement WHERE id = @id AND period_start = @period;",
            new { id = published.StatementId, period = published.Period },
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true) ?? "";
        status.ShouldBe("PURGED");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Purge_SkipsHeldStatements_AndAuditsTheSkip()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Published published = await PublishAsync(ct).ConfigureAwait(true);
        await BackdateRetainUntilAsync(published, ct).ConfigureAwait(true);
        await UnlockObjectAsync(published.StorageKey, ct).ConfigureAwait(true);

        using Harness harness = CreateHarness();
        await PlaceDbHoldAsync(harness, published, "CASE-2026-HOLD", ct).ConfigureAwait(true);

        _ = await harness.Purge.RunAsync(fenceToken: 3, ct).ConfigureAwait(true);

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true);
        string status = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM statement WHERE id = @id AND period_start = @period;",
            new { id = published.StatementId, period = published.Period },
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true) ?? "";
        status.ShouldBe("AVAILABLE", "an active hold outranks an expired retention date");

        long skips = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM audit_event WHERE statement_id = @id AND action = 'RETENTION_SKIPPED_LEGAL_HOLD';",
            new { id = published.StatementId },
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);
        skips.ShouldBeGreaterThanOrEqualTo(1, "a refusal is a decision, and decisions are audited");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Purge_RespectsObjectLock_EvenIfDbSaysExpired()
    {
        // Hard constraint 4. The object keeps its full governance retention (written at PUT);
        // ONLY the database date is backdated. The store's answer must win.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Published published = await PublishAsync(ct).ConfigureAwait(true);
        await BackdateRetainUntilAsync(published, ct).ConfigureAwait(true);

        using Harness harness = CreateHarness();
        _ = await harness.Purge.RunAsync(fenceToken: 4, ct).ConfigureAwait(true);

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true);
        string status = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM statement WHERE id = @id AND period_start = @period;",
            new { id = published.StatementId, period = published.Period },
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true) ?? "";
        status.ShouldBe("AVAILABLE", "if S3 says locked, the database's opinion does not matter");

        long skips = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM audit_event WHERE statement_id = @id AND action = 'RETENTION_SKIPPED_OBJECT_LOCK';",
            new { id = published.StatementId },
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);
        skips.ShouldBeGreaterThanOrEqualTo(1);

        ObjectRetentionInfo info = await harness.Objects.GetRetentionAsync(published.StorageKey, ct)
            .ConfigureAwait(true);
        info.Exists.ShouldBeTrue("the locked object must not have been touched");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Purge_IsBounded_ToBatchSize()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Published a = await PublishAsync(ct).ConfigureAwait(true);
        Published b = await PublishAsync(ct).ConfigureAwait(true);
        foreach (Published published in new[] { a, b })
        {
            await BackdateRetainUntilAsync(published, ct).ConfigureAwait(true);
            await UnlockObjectAsync(published.StorageKey, ct).ConfigureAwait(true);
        }

        using Harness harness = CreateHarness(options => options.PurgeBatchSize = 1);
        int purged = await harness.Purge.RunAsync(fenceToken: 5, ct).ConfigureAwait(true);

        // Exactly one, even though at least two are eligible: the batch bound holds. (Other
        // tests' leftovers may be eligible too, which is why this asserts equality on the
        // RETURN VALUE, not on table state.)
        purged.ShouldBe(1);
    }

    // ─── legal holds, both layers ────────────────────────────────────────────────────────────────

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Hold_PlacedInBothDbAndObjectStore()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Published published = await PublishAsync(ct).ConfigureAwait(true);

        using Harness harness = CreateHarness();

        // The endpoint's order: storage first, then the database row (ADR-0037).
        await harness.Objects.SetLegalHoldAsync(published.StorageKey, place: true, ct).ConfigureAwait(true);
        await PlaceDbHoldAsync(harness, published, "CASE-2026-0042", ct).ConfigureAwait(true);

        ObjectRetentionInfo info = await harness.Objects.GetRetentionAsync(published.StorageKey, ct)
            .ConfigureAwait(true);
        info.LegalHold.ShouldBeTrue("the physical layer must hold");

        string? caseRef = await harness.Holds.ActiveCaseReferenceForStatementAsync(
            new StatementId(published.StatementId), new CustomerId(published.CustomerId), ct)
            .ConfigureAwait(true);
        caseRef.ShouldBe("CASE-2026-0042", "the policy layer must hold too");

        // Cleanup for other tests: release both layers.
        await harness.Objects.SetLegalHoldAsync(published.StorageKey, place: false, ct).ConfigureAwait(true);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task CustomerHold_AppliesToStatementsGeneratedAfterPlacement()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Published first = await PublishAsync(ct).ConfigureAwait(true);

        using Harness harness = CreateHarness();

        // Customer-scoped hold placed BEFORE the second statement exists.
        var unitOfWork = new NpgsqlUnitOfWork(harness.Factory);
        await unitOfWork.ExecuteAsync(
            (NpgsqlTransaction tx, CancellationToken token) =>
                harness.Holds.PlaceAsync(
                    new LegalHoldRow(
                        Guid.CreateVersion7(), null, first.CustomerId, "CASE-2026-FUTURE", "litigation",
                        "test", DateTimeOffset.UtcNow, null),
                    tx, token),
            ct).ConfigureAwait(true);

        // A NEW statement for the same customer, generated after the hold.
        Published second = await PublishAsync(ct, existingCustomer: first.CustomerId).ConfigureAwait(true);
        await BackdateRetainUntilAsync(second, ct).ConfigureAwait(true);
        await UnlockObjectAsync(second.StorageKey, ct).ConfigureAwait(true);

        _ = await harness.Purge.RunAsync(fenceToken: 6, ct).ConfigureAwait(true);

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true);
        string status = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM statement WHERE id = @id AND period_start = @period;",
            new { id = second.StatementId, period = second.Period },
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true) ?? "";

        // Hold status is checked AT PURGE TIME, not at generation time - that is what makes a
        // customer hold cover statements that did not exist when it was placed.
        status.ShouldBe("AVAILABLE");
    }

    // ─── crypto-erasure ──────────────────────────────────────────────────────────────────────────

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Erasure_Executes_ObjectSurvives_ReadPathDies_AuditPreserved()
    {
        // Key destroyed, object survives, read path dies, audit preserved: THE PAYOFF.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Published published = await PublishAsync(ct).ConfigureAwait(true);

        using Harness harness = CreateHarness();
        await ScheduleErasureDueNowAsync(harness, published.CustomerId, ct).ConfigureAwait(true);

        int completed = await harness.Erasure.RunAsync(fenceToken: 7, ct).ConfigureAwait(true);
        completed.ShouldBeGreaterThanOrEqualTo(1);

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true);

        // The key row: DESTROYED, material gone (V018's constraints also enforce the shape).
        (string KeyStatus, bool CekIsNull) key = await connection
            .QuerySingleAsync<(string, bool)>(new CommandDefinition(
                "SELECT status, wrapped_cek IS NULL FROM customer_key WHERE customer_id = @id;",
                new { id = published.CustomerId },
                commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);
        key.KeyStatus.ShouldBe("DESTROYED");
        key.CekIsNull.ShouldBeTrue("erasure without removing the material is a renamed string, not an erasure");

        // The statement row: PURGED bookkeeping, object UNTOUCHED in storage.
        string status = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM statement WHERE id = @id AND period_start = @period;",
            new { id = published.StatementId, period = published.Period },
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true) ?? "";
        status.ShouldBe("PURGED");

        ObjectRetentionInfo info = await harness.Objects.GetRetentionAsync(published.StorageKey, ct)
            .ConfigureAwait(true);
        info.Exists.ShouldBeTrue("the object REMAINS - it is now undecryptable ciphertext under its lock");

        // The read path is dead, from a fresh service exactly as the gateway would build it.
        var keyService = new CustomerKeyService(
            new LocalKeyProvider(Options.Create(new LocalKeyProviderOptions { MasterSecret = MinioFixture.MasterSecret })),
            new CustomerKeyRepository(harness.Factory),
            NullLogger<CustomerKeyService>.Instance);
        _ = await Should.ThrowAsync<CryptoErasedException>(
            () => keyService.UnwrapCekAsync(new CustomerId(published.CustomerId), ct)).ConfigureAwait(true);

        // The audit trail SURVIVES the erasure - the record of the erasure is not itself erased.
        long audits = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM audit_event WHERE customer_id = @id;",
            new { id = published.CustomerId },
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);
        audits.ShouldBeGreaterThanOrEqualTo(1);

        long completedAudits = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM audit_event WHERE customer_id = @id AND action = 'ERASURE_COMPLETED';",
            new { id = published.CustomerId },
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);
        completedAudits.ShouldBe(1);

        // And the remnant is tombstoned ERASED, so the orphan sweep knows it is lawful.
        string kind = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT kind FROM storage_tombstone WHERE storage_key = @key;",
            new { key = published.StorageKey },
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true) ?? "";
        kind.ShouldBe("ERASED");
    }

    /// <summary>The scope a hold is placed with, for the both-scopes theories.</summary>
    public enum HoldScope
    {
        /// <summary>A hold on the whole customer (statement_id NULL).</summary>
        Customer,

        /// <summary>A hold on one statement (statement_id set). THE scope the erasure gate was blind to.</summary>
        Statement,
    }

    [Theory(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    [InlineData(HoldScope.Customer)]
    [InlineData(HoldScope.Statement)]
    public async Task Erasure_ReEvaluatesConflicts_AtExecutionTime(HoldScope scope)
    {
        // A hold placed DURING the cooling-off window must stop the executor: the decision made
        // seven days ago is not trusted. BOTH scopes, because the original version of this test
        // placed only a customer-scoped hold and so encoded the very defect it should have
        // caught - the statement-scoped case was the one erasure could not see. This is the test
        // that should have existed; against the pre-V021 code its Statement row goes red.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Published published = await PublishAsync(ct).ConfigureAwait(true);

        using Harness harness = CreateHarness();
        await ScheduleErasureDueNowAsync(harness, published.CustomerId, ct).ConfigureAwait(true);

        await PlaceHoldAsync(harness, published, scope, "CASE-2026-COOLOFF", ct).ConfigureAwait(true);

        int completed = await harness.Erasure.RunAsync(fenceToken: 8, ct).ConfigureAwait(true);
        completed.ShouldBe(0, "the re-evaluation must block the destruction");

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true);
        (string KeyStatus, string RequestStatus) state = await connection
            .QuerySingleAsync<(string, string)>(new CommandDefinition(
                """
                SELECT k.status, r.status
                  FROM customer_key k
                  JOIN erasure_request r ON r.customer_id = k.customer_id
                 WHERE k.customer_id = @id;
                """,
                new { id = published.CustomerId },
                commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);

        state.KeyStatus.ShouldBe("SCHEDULED_DESTRUCTION", "blocked, not destroyed - and not cancelled either");
        state.RequestStatus.ShouldBe("SCHEDULED", "the request retries after the hold clears");

        long blocked = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM audit_event WHERE customer_id = @id AND action = 'ERASURE_BLOCKED';",
            new { id = published.CustomerId },
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);
        blocked.ShouldBeGreaterThanOrEqualTo(1, "a refusal to act on an irreversible order is audited");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Erasure_CanBeCancelled_DuringCoolingOff()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Published published = await PublishAsync(ct).ConfigureAwait(true);

        using Harness harness = CreateHarness();

        // As app_delivery: scheduling and cancelling are the DPO endpoint's acts (V018 grants
        // INSERT on erasure_request to the API role alone).
        var unitOfWork = new NpgsqlUnitOfWork(harness.DeliveryFactory);
        await unitOfWork.ExecuteAsync(
            async (NpgsqlTransaction tx, CancellationToken token) =>
                _ = await harness.Erasures.ScheduleAsync(
                    new ErasureRequestRow(
                        Guid.CreateVersion7(), published.CustomerId, "POPIA s24", "DSR-CANCEL", "dpo",
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7), "SCHEDULED"),
                    tx, token).ConfigureAwait(false),
            ct).ConfigureAwait(true);

        Guid? cancelled = null;
        await unitOfWork.ExecuteAsync(
            async (NpgsqlTransaction tx, CancellationToken token) =>
                cancelled = await harness.Erasures.CancelAsync(
                    new CustomerId(published.CustomerId), "dpo", tx, token).ConfigureAwait(false),
            ct).ConfigureAwait(true);

        cancelled.ShouldNotBeNull();

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true);
        string keyStatus = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM customer_key WHERE customer_id = @id;",
            new { id = published.CustomerId },
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true) ?? "";
        keyStatus.ShouldBe("ACTIVE", "cancellation must defuse the key row, not just the request");
    }

    // ─── restore ─────────────────────────────────────────────────────────────────────────────────

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Restore_Completes_PublishesEvent_AndAdmitsTheDownloadPath()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Published published = await PublishAsync(ct).ConfigureAwait(true);

        using Harness harness = CreateHarness();

        // Archive it, then request a restore that is already due (the simulated latency elapsed).
        await using (NpgsqlConnection admin = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true))
        {
            _ = await admin.ExecuteAsync(new CommandDefinition(
                "UPDATE statement SET status='ARCHIVED', storage_tier='GLACIER' WHERE id=@id AND period_start=@period;",
                new { id = published.StatementId, period = published.Period },
                commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);
        }

        var restoreId = Guid.CreateVersion7();

        // As app_delivery: a restore request is created by the customer-facing API (V018 gives
        // INSERT on restore_request to it alone; the worker only completes and expires them).
        var unitOfWork = new NpgsqlUnitOfWork(harness.DeliveryFactory);
        await unitOfWork.ExecuteAsync(
            (NpgsqlTransaction tx, CancellationToken token) =>
                harness.Restores.CreateAsync(
                    new RestoreRequestRow(
                        restoreId, published.StatementId, published.Period, published.CustomerId,
                        "customer", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(-1),
                        "PENDING", null, null),
                    tx, token),
            ct).ConfigureAwait(true);

        int completed = await harness.RestoreCompleter.RunAsync(fenceToken: 9, ct).ConfigureAwait(true);
        completed.ShouldBeGreaterThanOrEqualTo(1);

        // The download path's question: is there a live restore? Yes, with an expiry.
        RestoreRequestRow? live = await harness.Restores.FindLiveAsync(
            new StatementId(published.StatementId), ct).ConfigureAwait(true);
        live.ShouldNotBeNull();
        live.ExpiresAt.ShouldNotBeNull("restored copies are temporary, like real Glacier restores");

        // And the outbox carries statement.restored, committed with the completion.
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true);
        long events = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM outbox WHERE event_type = 'StatementRestored' AND payload->>'RestoreId' = @rid;",
            new { rid = restoreId.ToString("D") },
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);
        events.ShouldBe(1);
    }

    // ─── reconciliation ──────────────────────────────────────────────────────────────────────────

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Reconciliation_DetectsMissingObject_AndLegalHoldDrift()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Fault 1: an AVAILABLE statement whose object was deleted out from under it.
        Published missing = await PublishAsync(ct).ConfigureAwait(true);
        await UnlockObjectAsync(missing.StorageKey, ct).ConfigureAwait(true);
        using Harness harness = CreateHarness();
        _ = await harness.Objects.DeleteObjectVersionsAsync(missing.StorageKey, ct).ConfigureAwait(true);

        // Fault 2: a database hold whose object carries no store-side hold.
        Published drifted = await PublishAsync(ct).ConfigureAwait(true);
        await PlaceDbHoldAsync(harness, drifted, "CASE-2026-DRIFT", ct).ConfigureAwait(true);

        var runId = Guid.CreateVersion7();
        await harness.Reconciliations.EnqueueAsync(runId, "test", ct).ConfigureAwait(true);
        (await harness.Reconciliation.RunAsync(ct).ConfigureAwait(true)).ShouldBeTrue();

        IReadOnlyList<ReconciliationFinding> findings = await harness.Reconciliations
            .ListFindingsAsync(runId, 500, ct).ConfigureAwait(true);

        findings.ShouldContain(
            f => f.CheckName == "MISSING_OBJECT" && f.Subject == missing.StatementId.ToString("D"),
            "a customer would get a 500 on download - CHECK 1 must fire");
        findings.ShouldContain(
            f => f.CheckName == "LEGAL_HOLD_DRIFT" && f.Severity == "CRITICAL",
            "the physical layer is not enforcing the legal record - CHECK 3 must fire");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Reconciliation_DetectsIncompleteErasure()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Published published = await PublishAsync(ct).ConfigureAwait(true);

        using Harness harness = CreateHarness();

        // Inject the CHECK-6 state: a customer whose key row says DESTROYED while a statement
        // still carries a wrapped DEK. (The key row itself is inserted VALID - V013/V018's
        // constraints check new rows - so the injectable half is the statement side, which is
        // exactly the bookkeeping the executor could die before finishing.)
        await using (NpgsqlConnection admin = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true))
        {
            _ = await admin.ExecuteAsync(new CommandDefinition(
                """
                UPDATE customer_key
                   SET wrapped_cek = NULL, status = 'DESTROYED', destroyed_at = now(),
                       destruction_reason = 'test-injection', destruction_due_at = NULL
                 WHERE customer_id = @id;
                """,
                new { id = published.CustomerId },
                commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);
        }

        var runId = Guid.CreateVersion7();
        await harness.Reconciliations.EnqueueAsync(runId, "test", ct).ConfigureAwait(true);
        _ = await harness.Reconciliation.RunAsync(ct).ConfigureAwait(true);

        IReadOnlyList<ReconciliationFinding> findings = await harness.Reconciliations
            .ListFindingsAsync(runId, 500, ct).ConfigureAwait(true);

        findings.ShouldContain(
            f => f.CheckName == "INCOMPLETE_ERASURE" && f.Severity == "CRITICAL",
            "an erasure the system believes completed but did not is invisible until audited - CHECK 6 exists for this");
    }

    [Theory(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    [InlineData(HoldScope.Statement)]
    [InlineData(HoldScope.Customer)]
    public async Task Erasure_BlockedBy_Hold(HoldScope scope)
    {
        // Erasure_BlockedBy_StatementScopedHold - the scope the erasure gate missed - and its
        // customer-scoped regression twin, at the API evaluation layer: a hold on ONE
        // statement must block the erasure of the WHOLE customer, because destroying the CEK
        // destroys that statement's readability. Before V021, the Statement row of this theory
        // schedules the erasure.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Published published = await PublishAsync(ct).ConfigureAwait(true);

        using Harness harness = CreateHarness();
        await PlaceHoldAsync(harness, published, scope, "CASE-2026-BLOCK", ct).ConfigureAwait(true);

        StatementDelivery.ServiceDefaults.Retention.HoldState holds = await harness.HoldResolution
            .ResolveForCustomerAsync(new CustomerId(published.CustomerId), ct).ConfigureAwait(true);

        holds.IsHeld.ShouldBeTrue("a hold in ANY scope must be visible to the erasure gate");
        holds.HasDbHold.ShouldBeTrue();
        holds.EffectiveCaseReference.ShouldBe("CASE-2026-BLOCK");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Erasure_BlockedBy_StoreOnlyHold()
    {
        // The store-only hold gap. Hold placement is storage-first, so its designed crash
        // residue is a store hold with no database row. That residue blocked purge and NOT
        // erasure; now both destructive paths share one resolver, and the executor must refuse.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Published published = await PublishAsync(ct).ConfigureAwait(true);

        using Harness harness = CreateHarness();
        await harness.Objects.SetLegalHoldAsync(published.StorageKey, place: true, ct).ConfigureAwait(true);
        try
        {
            await ScheduleErasureDueNowAsync(harness, published.CustomerId, ct).ConfigureAwait(true);

            int completed = await harness.Erasure.RunAsync(fenceToken: 18, ct).ConfigureAwait(true);
            completed.ShouldBe(0, "a store-side hold with no DB record must still stop destruction");

            await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true);
            string keyStatus = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT status FROM customer_key WHERE customer_id = @id;",
                new { id = published.CustomerId },
                commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true) ?? "";
            keyStatus.ShouldBe("SCHEDULED_DESTRUCTION", "blocked, not destroyed");

            StatementDelivery.ServiceDefaults.Retention.HoldState holds = await harness.HoldResolution
                .ResolveForCustomerAsync(new CustomerId(published.CustomerId), ct).ConfigureAwait(true);
            holds.IsDrift.ShouldBeTrue("physically held with no legal record IS the drift state");
        }
        finally
        {
            await harness.Objects.SetLegalHoldAsync(published.StorageKey, place: false, ct).ConfigureAwait(true);
        }
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Purge_BlockedBy_CustomerScopedHold()
    {
        // The mirror image of the statement-scoped hold gap: the per-statement purge gate must
        // see a hold placed on the whole customer.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Published published = await PublishAsync(ct).ConfigureAwait(true);
        await BackdateRetainUntilAsync(published, ct).ConfigureAwait(true);
        await UnlockObjectAsync(published.StorageKey, ct).ConfigureAwait(true);

        using Harness harness = CreateHarness();
        await PlaceHoldAsync(harness, published, HoldScope.Customer, "CASE-2026-CUSTWIDE", ct).ConfigureAwait(true);

        _ = await harness.Purge.RunAsync(fenceToken: 19, ct).ConfigureAwait(true);

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true);
        string status = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM statement WHERE id = @id AND period_start = @period;",
            new { id = published.StatementId, period = published.Period },
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true) ?? "";
        status.ShouldBe("AVAILABLE");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Purge_NotBlockedBy_SiblingStatementHold()
    {
        // The hazard V021's denormalisation could have introduced: every hold row now carries
        // customer_id, so a naive customer-branch predicate would let a hold on statement S1
        // block purging sibling S2 - over-protection that silently repeals retention for the
        // whole customer. The scope predicate (statement_id IS NULL) prevents it; this pins it.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Published held = await PublishAsync(ct).ConfigureAwait(true);
        Published sibling = await PublishAsync(ct, existingCustomer: held.CustomerId).ConfigureAwait(true);
        await BackdateRetainUntilAsync(sibling, ct).ConfigureAwait(true);
        await UnlockObjectAsync(sibling.StorageKey, ct).ConfigureAwait(true);

        using Harness harness = CreateHarness();
        await PlaceHoldAsync(harness, held, HoldScope.Statement, "CASE-2026-SIBLING", ct).ConfigureAwait(true);

        _ = await harness.Purge.RunAsync(fenceToken: 20, ct).ConfigureAwait(true);

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true);
        string siblingStatus = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM statement WHERE id = @id AND period_start = @period;",
            new { id = sibling.StatementId, period = sibling.Period },
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true) ?? "";
        siblingStatus.ShouldBe("PURGED", "a statement-scoped hold protects THAT statement, not its siblings");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task BlockedErasure_AuditsOnce_NotOncePerTick()
    {
        // V022: audit on transition, not on evaluation. Two consecutive passes over the same
        // block append ONE chain entry, not two.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Published published = await PublishAsync(ct).ConfigureAwait(true);

        using Harness harness = CreateHarness();
        await ScheduleErasureDueNowAsync(harness, published.CustomerId, ct).ConfigureAwait(true);
        await PlaceHoldAsync(harness, published, HoldScope.Customer, "CASE-2026-SPAM", ct).ConfigureAwait(true);

        _ = await harness.Erasure.RunAsync(fenceToken: 30, ct).ConfigureAwait(true);
        _ = await harness.Erasure.RunAsync(fenceToken: 31, ct).ConfigureAwait(true);

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true);
        long audits = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM audit_event WHERE customer_id = @id AND action = 'ERASURE_BLOCKED';",
            new { id = published.CustomerId },
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);
        audits.ShouldBe(1, "an unchanged block is one fact, not one fact per tick");

        string? reason = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT last_blocked_reason FROM erasure_request WHERE customer_id = @id;",
            new { id = published.CustomerId },
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);
        reason.ShouldBe("BlockedByLegalHold");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task BlockedErasure_ReAudits_WhenBlockReasonChanges()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Published published = await PublishAsync(ct).ConfigureAwait(true);

        using Harness harness = CreateHarness();
        await ScheduleErasureDueNowAsync(harness, published.CustomerId, ct).ConfigureAwait(true);
        await PlaceHoldAsync(harness, published, HoldScope.Customer, "CASE-2026-CHANGE", ct).ConfigureAwait(true);

        _ = await harness.Erasure.RunAsync(fenceToken: 32, ct).ConfigureAwait(true);

        // THE TRANSITION. Under the corrected semantics retention never blocks an erasure
        // (crypto-erasure is what reconciles the two statutes), so the reachable state change
        // is hold released -> erasure completes. That transition is a new fact and must land
        // on the chain, exactly as the blocked state did.
        await using (NpgsqlConnection admin = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true))
        {
            _ = await admin.ExecuteAsync(new CommandDefinition(
                """
                UPDATE legal_hold SET released_at = now(), released_by = 'test'
                 WHERE case_reference = 'CASE-2026-CHANGE';
                """,
                new { id = published.CustomerId },
                commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);
        }

        _ = await harness.Erasure.RunAsync(fenceToken: 33, ct).ConfigureAwait(true);

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true);
        (long blocked, long completed) = await connection.QuerySingleAsync<(long, long)>(new CommandDefinition(
            """
            SELECT count(*) FILTER (WHERE action = 'ERASURE_BLOCKED'),
                   count(*) FILTER (WHERE action = 'ERASURE_COMPLETED')
              FROM audit_event WHERE customer_id = @id;
            """,
            new { id = published.CustomerId },
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);
        blocked.ShouldBe(1, "the hold blocked exactly one pass, audited once");
        completed.ShouldBe(1, "the release is a new fact and its completion must land on the chain");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Generation_ForErasedCustomer_IsRejected_EvenWithWarmCache()
    {
        // The destroyed-key write guard. The dangerous sequence, end to end: the key is
        // DESTROYED in the database, but a store with a WARM cached CEK will happily encrypt and
        // upload - that is the window. What must close it is the DATABASE-side publish guard, in
        // the same transaction as the publish itself.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Published published = await PublishAsync(ct).ConfigureAwait(true);

        // A second PENDING statement for the same customer - the item a worker would render next.
        var nextStatement = Guid.CreateVersion7();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        StatementPeriod period = StatementPeriod.ForMonth(today.Year, today.Month);
        await using (NpgsqlConnection admin = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true))
        {
            _ = await admin.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO statement (id, account_id, customer_id, period_start, period_end,
                                       version, status, retain_until)
                VALUES (@id, @account, @customer, @start, @end, 2, 'PENDING', @retain);
                """,
                new
                {
                    id = nextStatement,
                    account = published.AccountId,
                    customer = published.CustomerId,
                    start = period.Start,
                    end = period.End,
                    retain = period.Start.AddYears(7),
                },
                commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);
        }

        // Warm the cache, then destroy the key BEHIND it.
        (S3StatementContentStore warmStore, IAmazonS3 client) = _minio.CreateStore(_postgres);
        using (client)
        {
            byte[] content = new byte[4096];
            using (var source = new MemoryStream(content, writable: false))
            {
                _ = await warmStore.WriteAsync(
                    source, new CryptoContext(published.StatementId, published.CustomerId, 1),
                    new AccountId(published.AccountId), period,
                    CohortAssignment.KekIdFor(CohortAssignment.ForCustomer(new CustomerId(published.CustomerId))),
                    ct).ConfigureAwait(true);
            }

            NpgsqlConnectionFactory retention = _postgres.ConnectionFactoryFor("app_retention");
            await using (retention.ConfigureAwait(true))
            {
                _ = await new CustomerKeyRepository(retention).DestroyAsync(
                    new CustomerId(published.CustomerId), "DSR-WARM", ct).ConfigureAwait(true);
            }

            // The warm cache STILL encrypts and uploads - the danger is real, not hypothetical.
            StoredObject stored;
            using (var source = new MemoryStream(new byte[4096], writable: false))
            {
                stored = await warmStore.WriteAsync(
                    source, new CryptoContext(nextStatement, published.CustomerId, 2),
                    new AccountId(published.AccountId), period,
                    CohortAssignment.KekIdFor(CohortAssignment.ForCustomer(new CustomerId(published.CustomerId))),
                    ct).ConfigureAwait(true);
            }

            // ... and the PUBLISH is where the guard refuses, transactionally.
            NpgsqlConnectionFactory generation = _postgres.ConnectionFactoryFor("app_generation");
            await using (generation.ConfigureAwait(true))
            {
                var unitOfWork = new NpgsqlUnitOfWork(generation);
                var repository = new StatementWriteRepository(generation);

                _ = await Should.ThrowAsync<StatementDelivery.Domain.Exceptions.CustomerKeyDestroyedException>(
                    () => unitOfWork.ExecuteAsync(
                        (NpgsqlTransaction tx, CancellationToken token) =>
                            repository.MarkAvailableAsync(
                                new StatementId(nextStatement), period.Start,
                                new StorageLocation(stored.Key, stored.Tier, stored.CiphertextLength, stored.Envelope),
                                DateTimeOffset.UtcNow, tx, token),
                        ct)).ConfigureAwait(true);
            }
        }
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Generation_DuringCoolingOff_IsRejected()
    {
        // SCHEDULED_DESTRUCTION blocks publishing too: a statement landed during the window
        // becomes unreadable the day the window closes, which is worse than refusing now.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Published published = await PublishAsync(ct).ConfigureAwait(true);

        using Harness harness = CreateHarness();
        await ScheduleErasureAsync(harness, published.CustomerId, DateTimeOffset.UtcNow.AddDays(7), ct)
            .ConfigureAwait(true);

        var nextStatement = Guid.CreateVersion7();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        StatementPeriod period = StatementPeriod.ForMonth(today.Year, today.Month);
        await using (NpgsqlConnection admin = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true))
        {
            _ = await admin.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO statement (id, account_id, customer_id, period_start, period_end,
                                       version, status, retain_until)
                VALUES (@id, @account, @customer, @start, @end, 2, 'PENDING', @retain);
                """,
                new
                {
                    id = nextStatement,
                    account = published.AccountId,
                    customer = published.CustomerId,
                    start = period.Start,
                    end = period.End,
                    retain = period.Start.AddYears(7),
                },
                commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);
        }

        NpgsqlConnectionFactory generation = _postgres.ConnectionFactoryFor("app_generation");
        await using (generation.ConfigureAwait(true))
        {
            var unitOfWork = new NpgsqlUnitOfWork(generation);
            var repository = new StatementWriteRepository(generation);
            var envelope = new CryptoEnvelope(
                new byte[61], "kek-test", "AES-256-GCM", new byte[32],
                new ContentBinding(nextStatement, published.CustomerId, 2));

            _ = await Should.ThrowAsync<StatementDelivery.Domain.Exceptions.CustomerKeyDestroyedException>(
                () => unitOfWork.ExecuteAsync(
                    (NpgsqlTransaction tx, CancellationToken token) =>
                        repository.MarkAvailableAsync(
                            new StatementId(nextStatement), period.Start,
                            new StorageLocation("statements/won-t-land/x.enc", "STANDARD", 4096, envelope),
                            DateTimeOffset.UtcNow, tx, token),
                    ct)).ConfigureAwait(true);
        }
    }

    // ─── the orphan sweep ────────────────────────────────────────────────────────────────────────

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task OrphanSweep_FindsObjectAtProductionKeyShape()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // An object nothing references: the write-failure leak the sweep exists to find. The
        // key comes from the PRODUCTION constructor - the first version of this test planted a
        // hand-built two-hex key no writer produces and passed while the sweep was blind to every
        // real object - the shard-prefix width defect, and the convenient-shape trap ADR-0032 bans.
        string orphanKey = StorageKeyScheme.KeyFor(
            new StatementId(Guid.CreateVersion7()),
            new AccountId(Guid.CreateVersion7()),
            StatementPeriod.ForMonth(2026, 8),
            version: 1);
        using (IAmazonS3 client = _minio.CreateClient())
        {
            _ = await client.PutObjectAsync(
                new PutObjectRequest
                {
                    BucketName = MinioFixture.BucketName,
                    Key = orphanKey,
                    ContentBody = "leaked-bytes",
                },
                ct).ConfigureAwait(true);
        }

        // Enough pages to cover every shard in one tick, so the test does not depend on where
        // the cursor happens to be pointing after other runs. The scheme is 4096 shards
        // (StorageKeyScheme.ShardCount) and an empty shard still costs its walk one page.
        using Harness harness = CreateHarness(options =>
        {
            options.OrphanPagesPerTick = StatementDelivery.Domain.Statements.StorageKeyScheme.ShardCount + 64;
            options.OrphanPageSize = 1000;
        });

        int reported = await harness.OrphanSweep.RunAsync(ct).ConfigureAwait(true);
        reported.ShouldBeGreaterThanOrEqualTo(1);

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true);
        long rows = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM orphan_report WHERE storage_key = @key;",
            new { key = orphanKey },
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);
        rows.ShouldBeGreaterThanOrEqualTo(1, "the leak must land in the report for a human");

        // REPORT-ONLY (ADR-0039): the object itself is untouched.
        ObjectRetentionInfo info = await harness.Objects.GetRetentionAsync(orphanKey, ct)
            .ConfigureAwait(true);
        info.Exists.ShouldBeTrue("the sweep must never delete - a comparison bug becomes a wrong number, not data loss");
    }

    // ─── Harness ─────────────────────────────────────────────────────────────────────────────────

    private sealed record Published(
        Guid CustomerId, Guid AccountId, Guid StatementId, DateOnly Period, string StorageKey);

    /// <summary>Everything the passes need, wired from real adapters over the fixtures.</summary>
    private sealed class Harness : IDisposable
    {
        public required NpgsqlConnectionFactory Factory { get; init; }

        /// <summary>The API's role, for acts the design assigns to it - scheduling erasures.</summary>
        public required NpgsqlConnectionFactory DeliveryFactory { get; init; }

        public required RetentionSweepRepository Statements { get; init; }

        public required LegalHoldRepository Holds { get; init; }

        public required ErasureRepository Erasures { get; init; }

        public required RestoreRequestRepository Restores { get; init; }

        public required ReconciliationRepository Reconciliations { get; init; }

        public required S3ObjectAdminStore Objects { get; init; }

        public required StatementDelivery.ServiceDefaults.Retention.HoldResolution HoldResolution { get; init; }

        public required PurgePass Purge { get; init; }

        public required ErasureExecutor Erasure { get; init; }

        public required RestoreCompleter RestoreCompleter { get; init; }

        public required ReconciliationPass Reconciliation { get; init; }

        public required OrphanSweep OrphanSweep { get; init; }

        public required IDisposable[] Owned { get; init; }

        public void Dispose()
        {
            foreach (IDisposable disposable in Owned)
            {
                disposable.Dispose();
            }
        }
    }

    private Harness CreateHarness(Action<RetentionWorkerOptions>? configure = null)
    {
        NpgsqlConnectionFactory factory = _postgres.ConnectionFactoryFor("app_retention");
        IAmazonS3 client = _minio.CreateClient();

        var options = new RetentionWorkerOptions();
        configure?.Invoke(options);

        var statements = new RetentionSweepRepository(factory);
        var holds = new LegalHoldRepository(factory);
        var erasures = new ErasureRepository(factory);
        var restores = new RestoreRequestRepository(factory);
        var reconciliations = new ReconciliationRepository(factory);
        var objects = new S3ObjectAdminStore(
            client,
            Options.Create(new ObjectStorageOptions { BucketName = MinioFixture.BucketName, ServiceUrl = _minio.ServiceUrl }));

        var unitOfWork = new NpgsqlUnitOfWork(factory);
        var ids = new UuidV7Generator();
        var audit = new SystemAudit(
            new PostgresAuditWriter(Options.Create(new StatementDelivery.Persistence.Auditing.AuditOptions()), factory),
            ids,
            TimeProvider.System,
            new ServiceIdentity("retention-tests", "0.0", "test-host", "Development"));
        var metrics = new RetentionMetrics(new TestMeterFactory());
        var keyService = new CustomerKeyService(
            new LocalKeyProvider(Options.Create(new LocalKeyProviderOptions { MasterSecret = MinioFixture.MasterSecret })),
            new CustomerKeyRepository(factory),
            NullLogger<CustomerKeyService>.Instance);

        var holdResolution = new StatementDelivery.ServiceDefaults.Retention.HoldResolution(
            holds, statements, objects);
        var purge = new PurgePass(
            statements, holdResolution, new CustomerKeyRepository(factory), objects, unitOfWork, audit,
            metrics, Options.Create(options), TimeProvider.System, NullLogger<PurgePass>.Instance);
        var erasure = new ErasureExecutor(
            erasures, statements, holdResolution, keyService,
            new DataKeyCache(keyService, Options.Create(new DataKeyCacheOptions()), TimeProvider.System),
            unitOfWork, audit,
            metrics, Options.Create(options), TimeProvider.System, NullLogger<ErasureExecutor>.Instance);
        var restoreCompleter = new RestoreCompleter(
            restores, unitOfWork, new StatementDelivery.Messaging.Outbox.OutboxEventPublisher(),
            audit, metrics, Options.Create(options), ids, TimeProvider.System);
        var orphanSweep = new OrphanSweep(
            new OrphanSweepRepository(factory), objects, metrics,
            Options.Create(options), NullLogger<OrphanSweep>.Instance);
        var reconciliation = new ReconciliationPass(
            reconciliations, statements, holds, objects,
            new PostgresAuditVerifier(factory),
            Options.Create(new StatementDelivery.Persistence.Auditing.AuditOptions()),
            metrics, Options.Create(options), TimeProvider.System, NullLogger<ReconciliationPass>.Instance);

        return new Harness
        {
            Factory = factory,
            DeliveryFactory = _postgres.ConnectionFactoryFor("app_delivery"),
            Statements = statements,
            Holds = holds,
            Erasures = erasures,
            Restores = restores,
            Reconciliations = reconciliations,
            Objects = objects,
            HoldResolution = holdResolution,
            Purge = purge,
            Erasure = erasure,
            RestoreCompleter = restoreCompleter,
            Reconciliation = reconciliation,
            OrphanSweep = orphanSweep,
            Owned = [metrics, client],
        };
    }

    private async Task<Published> PublishAsync(CancellationToken ct, Guid? existingCustomer = null)
    {
        var customer = existingCustomer ?? Guid.CreateVersion7();
        var account = Guid.CreateVersion7();
        var statement = Guid.CreateVersion7();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        StatementPeriod period = StatementPeriod.ForMonth(today.Year, today.Month);

        await using (NpgsqlConnection admin = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true))
        {
            if (existingCustomer is null)
            {
                _ = await admin.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO customer (id, external_ref, status) VALUES (@customer, @ref, 'ACTIVE');",
                    new { customer, @ref = customer.ToString("N") },
                    commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);
            }

            _ = await admin.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO account (id, customer_id, account_number_masked, product_type, status, opened_at)
                VALUES (@account, @customer, '****9876', 'CURRENT', 'ACTIVE', now());
                INSERT INTO statement (id, account_id, customer_id, period_start, period_end,
                                       version, status, retain_until)
                VALUES (@statement, @account, @customer, @start, @end, 1, 'PENDING', @retain);
                """,
                new
                {
                    account,
                    customer,
                    statement,
                    start = period.Start,
                    end = period.End,
                    retain = period.Start.AddYears(7),
                },
                commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);
        }

        // Real bytes through the real encrypting store, so the purge deletes what the writer wrote.
        (S3StatementContentStore store, IAmazonS3 seedClient) = _minio.CreateStore(_postgres);
        StoredObject stored;
        using (seedClient)
        {
            byte[] content = new byte[16 * 1024];
            Random.Shared.NextBytes(content);
            using var source = new MemoryStream(content, writable: false);
            stored = await store.WriteAsync(
                source,
                new CryptoContext(statement, customer, 1),
                new AccountId(account),
                period,
                CohortAssignment.KekIdFor(CohortAssignment.ForCustomer(new CustomerId(customer))),
                ct).ConfigureAwait(true);
        }

        NpgsqlConnectionFactory factory = _postgres.ConnectionFactoryFor("app_generation");
        await using (factory.ConfigureAwait(true))
        {
            var unitOfWork = new NpgsqlUnitOfWork(factory);
            var repository = new StatementWriteRepository(factory);
            _ = await unitOfWork.ExecuteAsync(
                (NpgsqlTransaction tx, CancellationToken token) =>
                    repository.MarkAvailableAsync(
                        new StatementId(statement), period.Start,
                        new StorageLocation(stored.Key, stored.Tier, stored.CiphertextLength, stored.Envelope),
                        DateTimeOffset.UtcNow, tx, token),
                ct).ConfigureAwait(true);
        }

        return new Published(customer, account, statement, period.Start, stored.Key);
    }

    private async Task BackdateRetainUntilAsync(Published published, CancellationToken ct)
    {
        await using NpgsqlConnection admin = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true);
        _ = await admin.ExecuteAsync(new CommandDefinition(
            "UPDATE statement SET retain_until = @past WHERE id = @id AND period_start = @period;",
            new
            {
                past = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-1),
                id = published.StatementId,
                period = published.Period,
            },
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true);
    }

    /// <summary>
    /// TEST SCAFFOLDING, honestly labelled: shortens the object's GOVERNANCE retention with the
    /// bypass permission to simulate "the lock expired", which nothing can wait seven years for.
    /// GOVERNANCE exists precisely to allow an authorised principal to do this; COMPLIANCE (the
    /// production mode) forbids it, and no production code path carries the bypass flag.
    /// </summary>
    private async Task UnlockObjectAsync(string storageKey, CancellationToken ct)
    {
        using IAmazonS3 client = _minio.CreateClient();

        // Every version: the purge deletes versions, so every one must be unlockable.
        ListVersionsResponse versions = await client.ListVersionsAsync(
            new ListVersionsRequest { BucketName = MinioFixture.BucketName, Prefix = storageKey },
            ct).ConfigureAwait(true);

        foreach (S3ObjectVersion version in versions.Versions ?? [])
        {
            if (!string.Equals(version.Key, storageKey, StringComparison.Ordinal))
            {
                continue;
            }

            _ = await client.PutObjectRetentionAsync(
                new PutObjectRetentionRequest
                {
                    BucketName = MinioFixture.BucketName,
                    Key = storageKey,
                    VersionId = version.VersionId,
                    BypassGovernanceRetention = true,
                    Retention = new ObjectLockRetention
                    {
                        Mode = ObjectLockRetentionMode.Governance,
                        RetainUntilDate = DateTime.UtcNow.AddSeconds(1),
                    },
                },
                ct).ConfigureAwait(true);
        }

        // The shortened locks expire almost immediately; the delay covers clock skew between the
        // test host and the MinIO container.
        await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(true);
    }

    private static Task PlaceDbHoldAsync(
        Harness harness, Published published, string caseReference, CancellationToken ct) =>
        PlaceHoldAsync(harness, published, HoldScope.Statement, caseReference, ct);

    // Rows are built the way the ENDPOINTS build them post-V021: customer_id ALWAYS set, scope
    // expressed by statement_id alone. A hand-rolled null-customer statement hold here would
    // re-create the exact row shape that made a statement-scoped hold invisible to the erasure
    // gate - and the database now rejects it.
    private static async Task PlaceHoldAsync(
        Harness harness, Published published, HoldScope scope, string caseReference, CancellationToken ct)
    {
        var unitOfWork = new NpgsqlUnitOfWork(harness.Factory);
        await unitOfWork.ExecuteAsync(
            (NpgsqlTransaction tx, CancellationToken token) =>
                harness.Holds.PlaceAsync(
                    new LegalHoldRow(
                        Guid.CreateVersion7(),
                        scope == HoldScope.Statement ? published.StatementId : null,
                        published.CustomerId,
                        caseReference,
                        "litigation",
                        "test",
                        DateTimeOffset.UtcNow,
                        null),
                    tx, token),
            ct).ConfigureAwait(true);
    }

    private static Task ScheduleErasureDueNowAsync(Harness harness, Guid customerId, CancellationToken ct) =>
        ScheduleErasureAsync(harness, customerId, DateTimeOffset.UtcNow.AddSeconds(-1), ct);

    private static async Task ScheduleErasureAsync(
        Harness harness, Guid customerId, DateTimeOffset dueAt, CancellationToken ct)
    {
        // Runs as app_delivery, the way the real DPO endpoint does: V018 grants INSERT on
        // erasure_request to the API role alone, and the retention role's 42501 on the first
        // real execution was this helper impersonating the wrong actor.
        var unitOfWork = new NpgsqlUnitOfWork(harness.DeliveryFactory);
        bool armed = false;
        await unitOfWork.ExecuteAsync(
            async (NpgsqlTransaction tx, CancellationToken token) =>
                armed = await harness.Erasures.ScheduleAsync(
                    new ErasureRequestRow(
                        Guid.CreateVersion7(), customerId, "POPIA s24 data subject request", "DSR-2026-0117",
                        "dpo", DateTimeOffset.UtcNow, dueAt, "SCHEDULED"),
                    tx, token).ConfigureAwait(false),
            ct).ConfigureAwait(true);
        armed.ShouldBeTrue("the seeded customer key must be ACTIVE and schedulable");
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (Meter meter in _meters)
            {
                meter.Dispose();
            }
        }
    }
}
