using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using StatementDelivery.Persistence.Leasing;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// Leader election against a real PostgreSQL instance.
/// </summary>
/// <remarks>
/// The property under test belongs to the database, not to the C# - it is the atomicity of
/// <c>INSERT ... ON CONFLICT DO UPDATE ... WHERE ... RETURNING</c>. A fake would only prove that
/// the fake agrees with itself.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class LeaseManagerTests
{
    private readonly PostgresFixture _postgres;

    /// <summary>Initialises a new instance of the <see cref="LeaseManagerTests"/> class.</summary>
    /// <param name="postgres">The shared PostgreSQL fixture.</param>
    public LeaseManagerTests(PostgresFixture postgres) => _postgres = postgres;

    private PostgresLeaseManager ManagerFor(string holderId, int timeToLiveSeconds = 30) =>
        new(
            _postgres.ConnectionFactoryFor("app_generation"),
            Options.Create(new LeaseOptions { TimeToLiveSeconds = timeToLiveSeconds, HolderId = holderId }),
            NullLoggerFactory.Instance);

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task ExactlyOneHolder_WinsTheLease()
    {
        // ACCEPTANCE CHECK 14, as a test rather than a log grep. Three contenders, one winner.
        // With a naive Timer instead of this, all three would run the job.
        string lease = $"election-{Guid.CreateVersion7():N}";
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        PostgresLeaseManager[] contenders =
        [
            ManagerFor("replica-a"),
            ManagerFor("replica-b"),
            ManagerFor("replica-c"),
        ];

        ILeaseHandle?[] handles = await Task.WhenAll(
            contenders.Select(manager => manager.TryAcquireAsync(lease, cancellationToken))).ConfigureAwait(true);

        try
        {
            handles.Count(handle => handle is not null).ShouldBe(1, "exactly one replica may hold the lease");
        }
        finally
        {
            foreach (ILeaseHandle? handle in handles)
            {
                if (handle is not null)
                {
                    await handle.DisposeAsync().ConfigureAwait(true);
                }
            }
        }
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Release_AllowsImmediateTakeover()
    {
        // Disposing a handle expires the row rather than waiting out the time to live. Without
        // this, a rolling deploy leaves the job unattended for a full lease period every time.
        string lease = $"takeover-{Guid.CreateVersion7():N}";
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        PostgresLeaseManager first = ManagerFor("replica-a");
        PostgresLeaseManager second = ManagerFor("replica-b");

        ILeaseHandle? held = await first.TryAcquireAsync(lease, cancellationToken).ConfigureAwait(true);
        held.ShouldNotBeNull();

        (await second.TryAcquireAsync(lease, cancellationToken).ConfigureAwait(true))
            .ShouldBeNull("a live lease must block a second holder");

        await held.DisposeAsync().ConfigureAwait(true);

        ILeaseHandle? takenOver = await second.TryAcquireAsync(lease, cancellationToken).ConfigureAwait(true);
        try
        {
            takenOver.ShouldNotBeNull("a released lease must be immediately available");
            takenOver.FenceToken.ShouldBeGreaterThan(held.FenceToken);
        }
        finally
        {
            if (takenOver is not null)
            {
                await takenOver.DisposeAsync().ConfigureAwait(true);
            }
        }
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task ExpiredLease_IsTakenOver_WithAHigherFenceToken()
    {
        // The crash case: a holder dies without releasing. Expiry is forced directly rather than
        // waited out, so the test asserts the takeover rule instead of asserting that Task.Delay
        // works.
        string lease = $"expiry-{Guid.CreateVersion7():N}";
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        PostgresLeaseManager dead = ManagerFor("replica-dead");
        PostgresLeaseManager alive = ManagerFor("replica-alive");

        ILeaseHandle? original = await dead.TryAcquireAsync(lease, cancellationToken).ConfigureAwait(true);
        original.ShouldNotBeNull();
        long originalToken = original.FenceToken;

        await using (NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true))
        {
            _ = await connection.ExecuteAsync(
                // Both columns move: ck_distributed_lease_expiry pins expires_at > acquired_at,
                // exactly as real elapsed time would leave them.
                "UPDATE distributed_lease SET acquired_at = now() - interval '11 seconds', expires_at = now() - interval '1 second' WHERE lease_name = @lease;",
                new { lease }).ConfigureAwait(true);
        }

        ILeaseHandle? successor = await alive.TryAcquireAsync(lease, cancellationToken).ConfigureAwait(true);

        try
        {
            successor.ShouldNotBeNull("an expired lease must be available for takeover");

            // THE POINT OF THE FENCE TOKEN. The former holder may still be alive, paused by a long
            // GC or a hypervisor stall, and about to resume believing it is the leader. It cannot
            // know it was paused. A strictly greater token is what lets everything downstream
            // reject its writes.
            successor.FenceToken.ShouldBeGreaterThan(originalToken);
        }
        finally
        {
            await original.DisposeAsync().ConfigureAwait(true);
            if (successor is not null)
            {
                await successor.DisposeAsync().ConfigureAwait(true);
            }
        }
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task FenceToken_IsStrictlyIncreasing_AcrossEveryAcquisition()
    {
        string lease = $"fencing-{Guid.CreateVersion7():N}";
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        var tokens = new List<long>();

        for (int i = 0; i < 5; i++)
        {
            PostgresLeaseManager manager = ManagerFor($"replica-{i}");
            ILeaseHandle? handle = await manager.TryAcquireAsync(lease, cancellationToken).ConfigureAwait(true);
            handle.ShouldNotBeNull();
            tokens.Add(handle.FenceToken);
            await handle.DisposeAsync().ConfigureAwait(true);
        }

        tokens.ShouldBe(tokens.Order().ToList());
        tokens.Distinct().Count().ShouldBe(tokens.Count);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task RenewalHeartbeat_KeepsTheLeaseAlive_PastItsOriginalExpiry()
    {
        // Five-second time to live means renewal every 1.67 seconds. Six seconds of waiting proves
        // the heartbeat is actually running rather than merely constructed.
        string lease = $"renewal-{Guid.CreateVersion7():N}";
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        PostgresLeaseManager manager = ManagerFor("replica-a", timeToLiveSeconds: 5);
        ILeaseHandle? handle = await manager.TryAcquireAsync(lease, cancellationToken).ConfigureAwait(true);
        handle.ShouldNotBeNull();

        try
        {
            long initialToken = handle.FenceToken;
            await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(true);

            handle.LeaseLost.IsCancellationRequested.ShouldBeFalse("a renewed lease must not be reported as lost");
            handle.FenceToken.ShouldBeGreaterThan(initialToken, "each renewal increments the fence token");

            await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true);
            DateTime expiresAt = await connection.ExecuteScalarAsync<DateTime>(
                "SELECT expires_at FROM distributed_lease WHERE lease_name = @lease;",
                new { lease }).ConfigureAwait(true);

            expiresAt.ShouldBeGreaterThan(DateTime.UtcNow, "the heartbeat must have pushed expiry into the future");
        }
        finally
        {
            await handle.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task AppDelivery_CannotTakeALease()
    {
        // The APIs hold SELECT on distributed_lease so an operator can see who leads, and nothing
        // more. An API replica that could take a lease could quietly become the thing that runs
        // destructive scheduled work.
        var manager = new PostgresLeaseManager(
            _postgres.ConnectionFactoryFor("app_delivery"),
            Options.Create(new LeaseOptions { HolderId = "api-replica" }),
            NullLoggerFactory.Instance);

        PostgresException error = await Should.ThrowAsync<PostgresException>(
            () => manager.TryAcquireAsync($"forbidden-{Guid.CreateVersion7():N}", TestContext.Current.CancellationToken))
            .ConfigureAwait(true);

        error.SqlState.ShouldBe("42501");
    }
}
