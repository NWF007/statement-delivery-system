using Generation.Worker;
using Shouldly;
using StatementDelivery.Crypto.Framing;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Domain.Rendering;
using StatementDelivery.Domain.Statements;
using StatementDelivery.Domain.ValueObjects;
using StatementDelivery.Rendering;
using StatementDelivery.ServiceDefaults.Storage;
using UnitTests.Rendering;
using Xunit;

namespace UnitTests.Generation;

/// <summary>
/// The bridge's FAILURE contract - the site of a high-severity defect.
/// </summary>
/// <remarks>
/// When the storage writer faults, a renderer parked at the pipe's 256&#160;KB pause threshold has
/// nobody left to read it. Before the fix, the bridge's drain awaited it forever: every large
/// document rendered during a storage outage wedged one render slot permanently, and fleet
/// capacity decayed monotonically until pod restart. Under the threshold the failure was clean -
/// which is why no small-document test could ever see it.
/// </remarks>
public sealed class RenderStreamBridgeTests
{
    static RenderStreamBridgeTests() =>
        RenderingServiceCollectionExtensions.ApplyGlobalSettings("Community");

    /// <summary>
    /// MEASURED, not guessed: 1,500 lines renders to ~251KB - just UNDER the 256KB pause
    /// threshold, and a first draft of this test passed against the broken code because the
    /// whole document drained into the pipe buffer. 3,000 lines is ~460KB: decisively past the
    /// threshold, so the renderer genuinely blocks when nobody reads.
    /// </summary>
    private const int LargeDocumentLines = 3000;

    /// <summary>The test's own patience. Far above the bridge's drain timeout, far below a hang.</summary>
    private static readonly TimeSpan TestPatience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The patience guard, made abandonment-safe. The first version was bare
    /// <c>Task.WhenAny(call, Task.Delay(...))</c>: when the DELAY won on a slow CI runner, the
    /// still-running call was abandoned, later faulted with the test's own
    /// StorageOutageException, and the unobserved exception crashed the whole test process at
    /// shutdown - the unit job's first real failure on Linux. If the timeout wins, the loser is
    /// explicitly observed before the test fails.
    /// </summary>
    private static async Task<Task> WithPatienceAsync(Task call)
    {
        Task winner = await Task.WhenAny(
            call, Task.Delay(TestPatience, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        if (!ReferenceEquals(winner, call))
        {
            _ = call.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return winner;
    }

    [Fact]
    public async Task RenderPipeline_WriterThrowsOnLargeDocument_DoesNotWedgeSlot()
    {
        // The writer dies instantly - an S3 outage, an auth failure - while the renderer is still
        // producing a document too large for the pipe buffer. The call must COMPLETE (with the
        // writer's exception), not hang: a wedged awaited-forever slot is capacity lost until pod
        // restart, and the reaper cannot give it back.
        Task<StoredObject> call = RenderStreamBridge.ExecuteAsync(
            new QuestPdfStatementRenderer(),
            new ThrowingWriter(new StorageOutageException("simulated S3 outage")),
            StatementRenderingTests.SampleDocument(LargeDocumentLines),
            new CryptoContext(Guid.NewGuid(), Guid.NewGuid(), 1),
            new AccountId(Guid.NewGuid()),
            StatementPeriod.ForMonth(2026, 8),
            "kek-test",
            onRenderSeconds: null,
            TimeProvider.System,
            CancellationToken.None);

        Task winner = await WithPatienceAsync(call).ConfigureAwait(true);

        winner.ShouldBe((Task)call,
            "the bridge must complete when the writer faults - a render slot wedged behind a paused "
            + "pipe is permanent capacity loss");

        _ = await Should.ThrowAsync<StorageOutageException>(() => call).ConfigureAwait(true);
    }

    [Fact]
    public async Task RenderPipeline_WriterThrows_WriterExceptionSurvives()
    {
        // Both sides fault - the writer first, then the renderer when the pipe is completed under
        // it. The STORAGE exception is the diagnosis; a drain that let the renderer's secondary
        // fault replace it would point every incident at the wrong subsystem.
        Task<StoredObject> call = RenderStreamBridge.ExecuteAsync(
            new QuestPdfStatementRenderer(),
            new ThrowingWriter(new StorageOutageException("the real cause")),
            StatementRenderingTests.SampleDocument(LargeDocumentLines),
            new CryptoContext(Guid.NewGuid(), Guid.NewGuid(), 1),
            new AccountId(Guid.NewGuid()),
            StatementPeriod.ForMonth(2026, 8),
            "kek-test",
            onRenderSeconds: null,
            TimeProvider.System,
            CancellationToken.None);

        Task winner = await WithPatienceAsync(call).ConfigureAwait(true);
        winner.ShouldBe((Task)call, "see the wedge test");

        StorageOutageException thrown =
            await Should.ThrowAsync<StorageOutageException>(() => call).ConfigureAwait(true);
        thrown.Message.ShouldBe("the real cause");
    }

    [Fact]
    public async Task RenderPipeline_RepeatedWriterFailures_DoNotDegradeParallelism()
    {
        // The real symptom: capacity decaying across a storage outage. Eight concurrent failures
        // at full "parallelism", then the writer heals - and the same slots must immediately do
        // real work at full speed. Before the fix, each large-document failure parked one call
        // forever, so the failure round would still be running when the success round started.
        const int Parallelism = 8;

        Task[] failures =
        [
            .. Enumerable.Range(0, Parallelism).Select(_ => (Task)RenderStreamBridge.ExecuteAsync(
                new QuestPdfStatementRenderer(),
                new ThrowingWriter(new StorageOutageException("outage")),
                StatementRenderingTests.SampleDocument(LargeDocumentLines),
                new CryptoContext(Guid.NewGuid(), Guid.NewGuid(), 1),
                new AccountId(Guid.NewGuid()),
                StatementPeriod.ForMonth(2026, 8),
                "kek-test",
                onRenderSeconds: null,
                TimeProvider.System,
                CancellationToken.None)),
        ];

        Task allFailed = Task.WhenAll(failures);
        Task winner = await WithPatienceAsync(allFailed).ConfigureAwait(true);
        winner.ShouldBe(allFailed, "every failing call must complete; none may wedge");
        _ = await Should.ThrowAsync<StorageOutageException>(() => allFailed).ConfigureAwait(true);

        // The outage ends; the slots must be alive and productive.
        var healed = new DrainingWriter();
        StoredObject stored = await RenderStreamBridge.ExecuteAsync(
            new QuestPdfStatementRenderer(),
            healed,
            StatementRenderingTests.SampleDocument(120),
            new CryptoContext(Guid.NewGuid(), Guid.NewGuid(), 1),
            new AccountId(Guid.NewGuid()),
            StatementPeriod.ForMonth(2026, 8),
            "kek-test",
            onRenderSeconds: null,
            TimeProvider.System,
            CancellationToken.None).ConfigureAwait(true);

        stored.PlaintextLength.ShouldBeGreaterThan(1000, "after recovery, renders flow again");
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>A distinct exception type, so the tests can assert WHOSE failure propagated.</summary>
    private sealed class StorageOutageException : Exception
    {
        public StorageOutageException(string message)
            : base(message)
        {
        }
    }

    /// <summary>A writer that fails immediately, without reading a byte - the worst case.</summary>
    private sealed class ThrowingWriter : IStatementContentWriter
    {
        private readonly Exception _failure;

        public ThrowingWriter(Exception failure) => _failure = failure;

        public Task<StoredObject> WriteAsync(
            Stream plaintext, CryptoContext ctx, AccountId accountId,
            StatementPeriod period, string kekId, CancellationToken ct) =>
            Task.FromException<StoredObject>(_failure);
    }

    /// <summary>A healthy writer: drains the stream and reports what it read.</summary>
    private sealed class DrainingWriter : IStatementContentWriter
    {
        public async Task<StoredObject> WriteAsync(
            Stream plaintext, CryptoContext ctx, AccountId accountId,
            StatementPeriod period, string kekId, CancellationToken ct)
        {
            long total = 0;
            byte[] buffer = new byte[64 * 1024];
            int read;
            while ((read = await plaintext.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
            }

            return new StoredObject(
                "test/key.enc", "STANDARD", total, total + 100,
                new CryptoEnvelope(new byte[61], kekId, "AES-256-GCM", new byte[32],
                    new ContentBinding(ctx.StatementId, ctx.CustomerId, ctx.Version)),
                DateTimeOffset.UtcNow.AddYears(7));
        }
    }
}
