using Dapper;
using Npgsql;
using Shouldly;
using StatementDelivery.Domain.ValueObjects;
using StatementDelivery.Persistence.Connections;
using StatementDelivery.Persistence.Runs;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// The batch claim, under the conditions that break naive queues.
/// </summary>
/// <remarks>
/// <para>
/// THE BRIEF ORDERS THIS TESTED BEFORE THE RENDER PIPELINE IS BUILT, AND FOR ONCE THE DRAMA IS
/// EARNED: a claim bug produces duplicate statements, and duplicate statements live under a
/// Compliance-mode Object Lock that nothing can delete for seven years. Every other bug in this
/// subsystem is recoverable; this one is a storage bill with a legal signature.
/// </para>
/// <para>
/// <see cref="ConcurrentWorkers_NeverClaimSameItem"/> is the proof that FOR UPDATE SKIP LOCKED
/// does what the comment above the SQL says it does. Fifty workers race over a thousand items;
/// every item must be claimed exactly once across all of them, and no worker may block another.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class RunClaimTests
{
    private const int MaxAttempts = 3;

    private readonly PostgresFixture _postgres;

    /// <summary>Initialises a new instance of the <see cref="RunClaimTests"/> class.</summary>
    /// <param name="postgres">The shared PostgreSQL fixture.</param>
    public RunClaimTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task ConcurrentWorkers_NeverClaimSameItem()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const int Workers = 50;
        const int Items = 1000;
        const int BatchSize = 7; // deliberately odd, so batches straddle worker boundaries

        (IStatementRunRepository repository, Guid runId) =
            await SeedRunAsync(Items, cancellationToken).ConfigureAwait(true);

        // Every worker claims in a tight loop until the queue is drained, all racing. The barrier
        // releases them together so the contention is real rather than accidental serialization.
        using var barrier = new SemaphoreSlim(0, Workers);

        Task<List<long>>[] workers =
        [
            .. Enumerable.Range(0, Workers).Select(async w =>
            {
                var mine = new List<long>();
                await barrier.WaitAsync(cancellationToken).ConfigureAwait(false);

                while (true)
                {
                    IReadOnlyList<ClaimedItem> claimed = await repository
                        .ClaimBatchAsync(runId, $"worker-{w}", BatchSize, MaxAttempts, cancellationToken)
                        .ConfigureAwait(false);

                    if (claimed.Count == 0)
                    {
                        return mine;
                    }

                    mine.AddRange(claimed.Select(static c => c.ItemId));
                }
            }),
        ];

        barrier.Release(Workers);
        List<long>[] results = await Task.WhenAll(workers).ConfigureAwait(true);

        long[] all = [.. results.SelectMany(static r => r)];

        // Exactly once, across all fifty workers. A duplicate here is THE bug this table exists
        // to make impossible; a shortfall means SKIP LOCKED skipped rows nobody held.
        all.Length.ShouldBe(Items, "every item must be claimed");
        all.Distinct().Count().ShouldBe(Items, "no item may be claimed twice");

        // And every claim burned exactly one attempt.
        (await ScalarAsync<long>(
            "SELECT count(*) FROM statement_run_item WHERE run_id = @runId AND attempts = 1 AND status = 'RENDERING';",
            new { runId }, cancellationToken).ConfigureAwait(true))
            .ShouldBe(Items);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Claim_IncrementsAttempts_EvenIfRenderFails()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (IStatementRunRepository repository, Guid runId) =
            await SeedRunAsync(1, cancellationToken).ConfigureAwait(true);

        // Claim, then fail - the shape of every poison item's life.
        IReadOnlyList<ClaimedItem> first = await repository
            .ClaimBatchAsync(runId, "w1", 10, MaxAttempts, cancellationToken).ConfigureAwait(true);
        first.Count.ShouldBe(1);
        first[0].Attempts.ShouldBe(1, "the attempt is burned at claim time, before any render code runs");

        _ = await repository
            .FailItemAsync(first[0].ItemId, "InvalidOperationException: boom", "w1", cancellationToken)
            .ConfigureAwait(true);

        // Reclaim: attempts marches on even though nothing ever completed.
        IReadOnlyList<ClaimedItem> second = await repository
            .ClaimBatchAsync(runId, "w2", 10, MaxAttempts, cancellationToken).ConfigureAwait(true);
        second.Count.ShouldBe(1);
        second[0].ItemId.ShouldBe(first[0].ItemId);
        second[0].Attempts.ShouldBe(2);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Claim_SkipsItemsAtMaxAttempts()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (IStatementRunRepository repository, Guid runId) =
            await SeedRunAsync(1, cancellationToken).ConfigureAwait(true);

        // Burn every attempt.
        for (int i = 0; i < MaxAttempts; i++)
        {
            IReadOnlyList<ClaimedItem> claimed = await repository
                .ClaimBatchAsync(runId, "w1", 10, MaxAttempts, cancellationToken).ConfigureAwait(true);
            claimed.Count.ShouldBe(1, $"attempt {i + 1} of {MaxAttempts} should still be claimable");

            _ = await repository
                .FailItemAsync(claimed[0].ItemId, "PoisonLedgerPayloadException: malformed", "w1", cancellationToken)
                .ConfigureAwait(true);
        }

        // Quarantined by exclusion: the WHERE clause simply never matches it again.
        (await repository.ClaimBatchAsync(runId, "w1", 10, MaxAttempts, cancellationToken).ConfigureAwait(true))
            .ShouldBeEmpty("an item at the attempts ceiling must never be claimed");

        RunCounters counters = await repository.CountersAsync(runId, MaxAttempts, cancellationToken).ConfigureAwait(true);
        counters.FailedFinal.ShouldBe(1);
        counters.FailedRetryable.ShouldBe(0);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task BatchClaim_ReturnsUpToBatchSize()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (IStatementRunRepository repository, Guid runId) =
            await SeedRunAsync(7, cancellationToken).ConfigureAwait(true);

        (await repository.ClaimBatchAsync(runId, "w1", 5, MaxAttempts, cancellationToken).ConfigureAwait(true))
            .Count.ShouldBe(5, "a full queue yields exactly the batch size");

        (await repository.ClaimBatchAsync(runId, "w1", 5, MaxAttempts, cancellationToken).ConfigureAwait(true))
            .Count.ShouldBe(2, "a nearly drained queue yields what remains");

        (await repository.ClaimBatchAsync(runId, "w1", 5, MaxAttempts, cancellationToken).ConfigureAwait(true))
            .ShouldBeEmpty("a drained queue yields nothing");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task StaleClaim_IsReaped_AndAttemptsPreserved()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (IStatementRunRepository repository, Guid runId) =
            await SeedRunAsync(1, cancellationToken).ConfigureAwait(true);

        IReadOnlyList<ClaimedItem> claimed = await repository
            .ClaimBatchAsync(runId, "doomed-worker", 10, MaxAttempts, cancellationToken).ConfigureAwait(true);
        claimed.Count.ShouldBe(1);

        // Simulate the worker dying long ago: age the claim past the stale threshold. Test-only
        // clock control - production claims age by existing.
        _ = await ExecAsync(
            "UPDATE statement_run_item SET claimed_at = now() - INTERVAL '20 minutes' WHERE id = @id;",
            new { id = claimed[0].ItemId }, cancellationToken).ConfigureAwait(true);

        int reaped = await repository.ReapStaleClaimsAsync(TimeSpan.FromMinutes(15), cancellationToken)
            .ConfigureAwait(true);
        reaped.ShouldBe(1);

        // Claimable again - and the burned attempt SURVIVES, which is the entire mechanism that
        // stops a worker-killing item from cycling forever.
        IReadOnlyList<ClaimedItem> reclaimed = await repository
            .ClaimBatchAsync(runId, "next-worker", 10, MaxAttempts, cancellationToken).ConfigureAwait(true);
        reclaimed.Count.ShouldBe(1);
        reclaimed[0].Attempts.ShouldBe(2, "the reaper resets status, never attempts");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task ReleasedClaims_ReturnToQueue_ScopedToTheReleasingWorker()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (IStatementRunRepository repository, Guid runId) =
            await SeedRunAsync(2, cancellationToken).ConfigureAwait(true);

        IReadOnlyList<ClaimedItem> mine = await repository
            .ClaimBatchAsync(runId, "me", 1, MaxAttempts, cancellationToken).ConfigureAwait(true);
        IReadOnlyList<ClaimedItem> theirs = await repository
            .ClaimBatchAsync(runId, "them", 1, MaxAttempts, cancellationToken).ConfigureAwait(true);

        // Releasing BOTH ids as "me" must touch only my claim: a graceful shutdown that released
        // other workers' rows would double-queue live work.
        int released = await repository
            .ReleaseClaimsAsync([mine[0].ItemId, theirs[0].ItemId], "me", cancellationToken).ConfigureAwait(true);
        released.ShouldBe(1);

        (await ScalarAsync<string>(
            "SELECT status FROM statement_run_item WHERE id = @id;",
            new { id = theirs[0].ItemId }, cancellationToken).ConfigureAwait(true))
            .ShouldBe(RunItemStatus.Rendering, "another worker's claim must survive my shutdown");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Completion_IsScopedToTheClaimant()
    {
        // Part C of the RED remediation. A render that outlives the stale window races its
        // reaped-and-reclaimed successor; without claimed_by in the predicate, the original
        // worker could mark DONE a row the successor now owns. With it, the original's complete
        // and fail both match ZERO rows - a real, distinguishable outcome the caller reports as
        // generation_stale_completion_total.
        CancellationToken ct = TestContext.Current.CancellationToken;
        (IStatementRunRepository repository, Guid runId) =
            await SeedRunAsync(1, ct).ConfigureAwait(true);

        IReadOnlyList<ClaimedItem> original = await repository
            .ClaimBatchAsync(runId, "original-worker", 10, MaxAttempts, ct).ConfigureAwait(true);
        original.Count.ShouldBe(1);

        // Reap and reclaim: the successor now owns the item.
        _ = await ExecAsync(
            "UPDATE statement_run_item SET claimed_at = now() - INTERVAL '20 minutes' WHERE id = @id;",
            new { id = original[0].ItemId }, ct).ConfigureAwait(true);
        _ = await repository.ReapStaleClaimsAsync(TimeSpan.FromMinutes(15), ct).ConfigureAwait(true);
        IReadOnlyList<ClaimedItem> successor = await repository
            .ClaimBatchAsync(runId, "successor-worker", 10, MaxAttempts, ct).ConfigureAwait(true);
        successor.Count.ShouldBe(1);

        // The ORIGINAL worker's completion must touch nothing.
        NpgsqlConnectionFactory factory = _postgres.ConnectionFactoryFor("app_generation");
        await using (factory.ConfigureAwait(true))
        {
            var uow = new StatementDelivery.Persistence.Uow.NpgsqlUnitOfWork(factory);
            int completed = await uow.ExecuteAsync(
                (tx, token) => repository.CompleteItemAsync(
                    original[0].ItemId, Guid.CreateVersion7(), "original-worker", tx, token),
                ct).ConfigureAwait(true);

            completed.ShouldBe(0, "a reaped claim must be uncompletable by its former owner");
        }

        (await ScalarAsync<string>(
            "SELECT status FROM statement_run_item WHERE id = @id;",
            new { id = original[0].ItemId }, ct).ConfigureAwait(true))
            .ShouldBe(RunItemStatus.Rendering, "the successor's claim is untouched");

        // And the original's FAIL is equally impotent.
        (await repository.FailItemAsync(original[0].ItemId, "IOException: late failure", "original-worker", ct)
            .ConfigureAwait(true))
            .ShouldBe(0, "a reaped claim must be unfailable by its former owner");
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>Creates a RUNNING run with <paramref name="items"/> queued items over fresh accounts.</summary>
    private async Task<(IStatementRunRepository Repository, Guid RunId)> SeedRunAsync(
        int items, CancellationToken cancellationToken)
    {
        NpgsqlConnectionFactory factory = _postgres.ConnectionFactoryFor("app_generation", maxPoolSize: 60);
        var repository = new StatementRunRepository(factory);

        // A unique period per test, so the UNIQUE(period) constraint never collides across tests
        // sharing the container. Months in the far past are safe: nothing else touches them.
        int offset = Interlocked.Increment(ref s_periodOffset);
        DateOnly anchor = new DateOnly(2000, 1, 1).AddMonths(offset);
        StatementPeriod period = StatementPeriod.ForMonth(anchor.Year, anchor.Month);

        StatementRun run = await repository
            .CreateOrGetAsync(Guid.CreateVersion7(), period, deadlineAt: null, cancellationToken)
            .ConfigureAwait(false);

        Guid[] accounts = [.. Enumerable.Range(0, items).Select(static _ => Guid.CreateVersion7())];
        _ = await repository.EnqueueBatchAsync(run.Id, accounts, traceParent: null, cancellationToken)
            .ConfigureAwait(false);
        _ = await repository.MarkRunningAsync(run.Id, cancellationToken).ConfigureAwait(false);

        return (repository, run.Id);
    }

    private static int s_periodOffset;

    private async Task<T?> ScalarAsync<T>(string sql, object args, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<T>(new CommandDefinition(
            sql, args, commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private async Task<int> ExecAsync(string sql, object args, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(new CommandDefinition(
            sql, args, commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
