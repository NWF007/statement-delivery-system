using System.IO.Pipelines;
using StatementDelivery.Crypto.Framing;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Domain.Rendering;
using StatementDelivery.Domain.ValueObjects;
using StatementDelivery.ServiceDefaults.Storage;

namespace Generation.Worker;

/// <summary>
/// The render → encrypt → upload bridge: one bounded pipe, no full PDF anywhere.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <see cref="RenderPipeline"/> so its FAILURE semantics are testable without a
/// database: the Prompt 5 audit's HIGH 1 lived exactly here, and no test could reach it through
/// the full pipeline. The renderer writes into the pipe; the storage writer consumes the reader;
/// the pause threshold caps in-flight plaintext at <see cref="PipeBufferBytes"/>.
/// </para>
/// <para>
/// THE FAILURE CONTRACT (the part the audit found broken): when the WRITER faults, the renderer
/// may be parked at the pause threshold with nobody left to read. It must be unblocked - by
/// completing the reader with the writer's exception - and the writer's exception must be the one
/// that propagates. A drain that waits forever converts a storage outage into a permanently
/// wedged render slot per large document; capacity decays monotonically until pod restart.
/// </para>
/// </remarks>
public static class RenderStreamBridge
{
    /// <summary>Pipe pause threshold: the most PDF allowed in flight between render and encrypt.</summary>
    public const int PipeBufferBytes = 256 * 1024;

    /// <summary>
    /// How long the renderer may take to drain after the pipe is completed under it. Defence in
    /// depth: completing the reader should unblock it instantly, and a slot must never be
    /// permanent even if it does not.
    /// </summary>
    public static readonly TimeSpan RenderDrainTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Renders <paramref name="document"/> through the pipe into the encrypting writer.</summary>
    /// <param name="renderer">The PDF renderer.</param>
    /// <param name="contentWriter">The encrypting object-store writer.</param>
    /// <param name="document">The statement to render.</param>
    /// <param name="ctx">The identity bound into the ciphertext.</param>
    /// <param name="accountId">The owning account, for the object key.</param>
    /// <param name="period">The statement period, for the object key and retention.</param>
    /// <param name="kekId">The cohort KEK expected for this customer.</param>
    /// <param name="onRenderSeconds">Render-stage duration callback, for metrics. Optional.</param>
    /// <param name="time">Time source.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the store recorded.</returns>
    public static async Task<StoredObject> ExecuteAsync(
        IStatementRenderer renderer,
        IStatementContentWriter contentWriter,
        StatementDocument document,
        CryptoContext ctx,
        AccountId accountId,
        StatementPeriod period,
        string kekId,
        Action<double>? onRenderSeconds,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(contentWriter);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(time);

        var pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: PipeBufferBytes,
            resumeWriterThreshold: PipeBufferBytes / 2,
            useSynchronizationContext: false));

        // ⚠ Task.Run IS LOAD-BEARING, NOT A STYLE CHOICE. The renderer is synchronous (QuestPDF
        // draws on the calling thread), so without the explicit hop the render's synchronous
        // prefix runs INSIDE this method's invocation - and for any document larger than the pipe
        // buffer it blocks at the pause threshold BEFORE the writer below is ever started. That
        // is a success-path deadlock for every large statement, with a healthy storage backend,
        // discovered when the red-before-green test for the failure path hung the whole test host
        // instead of failing. The audit's HIGH 1 was understated: it described the failure path,
        // and the shipped code could not complete the success path either.
        Task renderTask = Task.Run(
            () => RenderIntoAsync(renderer, document, pipe.Writer, onRenderSeconds, time, cancellationToken),
            CancellationToken.None);

        StoredObject stored;
        try
        {
            stored = await contentWriter.WriteAsync(
                pipe.Reader.AsStream(),
                ctx,
                accountId,
                period,
                kekId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Unblock the renderer with the REAL cause before waiting on it. Without this, a
            // renderer paused at the 256KB threshold waits forever: no reader, no completion, and
            // the slot never returns. Completing the reader surfaces `ex` (or a completed-flush
            // result) to the renderer's next write, and it winds down promptly.
            await pipe.Reader.CompleteAsync(ex).ConfigureAwait(false);

            // Bounded, best-effort drain. The WRITER's exception is the diagnosis - a storage
            // outage must never be reported as a rendering bug - so anything the drain throws
            // (including the renderer re-surfacing `ex`) is deliberately swallowed here, and the
            // timeout is defence in depth for a renderer wedged for some novel reason.
            try
            {
                await renderTask.WaitAsync(RenderDrainTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The drain gave up on a wedged renderer, which means renderTask is being
                // ABANDONED still running. Observe its eventual fault explicitly: an abandoned
                // task's unobserved exception escalates at finalisation under test runners (it
                // crashed the CI unit job) and is noise-with-consequences anywhere else.
                _ = renderTask.ContinueWith(
                    static t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (Exception)
            {
                // Swallowed by design; see above.
            }

            throw;
        }

        // SUCCESS: the writer read to end-of-stream, so the renderer has effectively finished -
        // but its own fault (if any) lives on renderTask and MUST propagate; unlike the failure
        // path, nothing here outranks it. Bounded all the same: a slot must never be permanent.
        await renderTask.WaitAsync(RenderDrainTimeout, CancellationToken.None).ConfigureAwait(false);
        await pipe.Reader.CompleteAsync().ConfigureAwait(false);

        return stored;
    }

    private static async Task RenderIntoAsync(
        IStatementRenderer renderer,
        StatementDocument document,
        PipeWriter writer,
        Action<double>? onRenderSeconds,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        long start = time.GetTimestamp();
        try
        {
            Stream output = writer.AsStream();
            await using (output.ConfigureAwait(false))
            {
                await renderer.RenderAsync(document, output, cancellationToken).ConfigureAwait(false);
            }

            await writer.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Propagate into the READER: the encrypting writer's next read observes the failure
            // and aborts the upload, instead of seeing a truncated stream and storing half a PDF.
            await writer.CompleteAsync(ex).ConfigureAwait(false);
            throw;
        }
        finally
        {
            onRenderSeconds?.Invoke(time.GetElapsedTime(start).TotalSeconds);
        }
    }
}
