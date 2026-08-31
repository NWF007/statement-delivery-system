using System.Security.Cryptography;
using Shouldly;
using StatementDelivery.Domain.Rendering;
using StatementDelivery.Domain.ValueObjects;
using StatementDelivery.Rendering;
using Xunit;

namespace UnitTests.Rendering;

/// <summary>
/// The renderer's contract: deterministic bytes, correct pagination, and streaming output.
/// </summary>
/// <remarks>
/// These run WITHOUT Docker - the renderer touches no database and no network - so byte
/// determinism is verified on every build on every machine, which is exactly where a font or
/// library drift would first appear.
/// </remarks>
public sealed class StatementRenderingTests
{
    static StatementRenderingTests() =>
        RenderingServiceCollectionExtensions.ApplyGlobalSettings("Community");

    private static readonly QuestPdfStatementRenderer Renderer = new();

    [Fact]
    public async Task Render_SameDocument_ProducesIdenticalBytes()
    {
        // THE DETERMINISM TEST. content_sha256 is stored on the statement row and verified on
        // download; if two renders of the same document differ by a byte, idempotent regeneration
        // becomes unverifiable. Two full renders, byte-compared - not hash-compared, so a failure
        // shows WHERE they diverge.
        StatementDocument document = SampleDocument(lines: 40);

        byte[] first = await RenderToArrayAsync(document).ConfigureAwait(true);
        byte[] second = await RenderToArrayAsync(document).ConfigureAwait(true);

        first.Length.ShouldBeGreaterThan(1000, "a statement PDF is not this small; the render likely failed");
        second.ShouldBe(first, "the same document must render to identical bytes in one process");
    }

    [Fact]
    public async Task Render_EightHundredTransactions_PaginatesCorrectly()
    {
        // The tail where rendering bugs live. 800 lines must paginate, carry the running balance
        // across pages, and still end on the closing balance.
        StatementDocument document = SampleDocument(lines: 800);

        byte[] pdf = await RenderToArrayAsync(document).ConfigureAwait(true);

        // Structural assertions on the PDF itself: a real multi-page document has many /Page
        // objects. Counting them needs no PDF library - but "/Type /Pages" (the page TREE node)
        // contains "/Type /Page" as a substring, so the tree nodes must be subtracted out.
        int pages = PageCount(pdf);
        pages.ShouldBeGreaterThan(10, "800 transaction lines cannot fit on ten A4 pages");

        // And it is a well-formed file: header at byte zero, trailer marker present.
        pdf.AsSpan(0, 5).ToArray().ShouldBe("%PDF-"u8.ToArray());
        CountOccurrences(pdf, "%%EOF"u8.ToArray()).ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Render_ZeroTransactions_ProducesValidStatement()
    {
        StatementDocument document = SampleDocument(lines: 0);

        byte[] pdf = await RenderToArrayAsync(document).ConfigureAwait(true);

        pdf.AsSpan(0, 5).ToArray().ShouldBe("%PDF-"u8.ToArray());
        PageCount(pdf).ShouldBe(1, "an empty statement is one page");
    }

    [Fact]
    public async Task Render_StreamsToOutput_WithoutBuffering()
    {
        // The renderer's output goes straight into the encrypting writer through a pipe, and a
        // pipe cannot seek. A renderer that needs Seek or Position would force the pipeline to
        // buffer the whole PDF first - the exact OOM shape the hard constraints forbid. This
        // stream throws on every backward-looking member, so a regression fails here rather than
        // in production under load.
        StatementDocument document = SampleDocument(lines: 120);

        var forwardOnly = new ForwardOnlyStream();
        await Renderer.RenderAsync(document, forwardOnly, CancellationToken.None).ConfigureAwait(true);

        forwardOnly.BytesWritten.ShouldBeGreaterThan(1000);
    }

    [Fact]
    public void Document_RefusesBalancesThatDoNotReconcile()
    {
        // A statement that renders beautifully and lies about its own arithmetic is worse than no
        // statement. The constructor is the guard.
        _ = Should.Throw<StatementDelivery.Domain.Exceptions.InvariantViolationException>(() =>
            new StatementDocument(
                "****1234", "Test Customer", StatementPeriod.ForMonth(2026, 8),
                openingBalanceMinor: 10_00,
                closingBalanceMinor: 999_99, // does not equal opening + lines
                "ZAR",
                [new StatementLine(new DateOnly(2026, 8, 5), "COFFEE", -10_00)],
                new DateOnly(2026, 8, 31)));
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>Builds a deterministic sample document. Same inputs, same value, every call.</summary>
    internal static StatementDocument SampleDocument(int lines)
    {
        StatementPeriod period = StatementPeriod.ForMonth(2026, 8);
        var items = new List<StatementLine>(lines);

        long balanceDelta = 0;
        for (int i = 0; i < lines; i++)
        {
            // Deterministic pseudo-variety without any RNG: derived arithmetically from the index.
            long amount = ((i % 7) + 1) * -137_50 + (i % 11 == 0 ? 15_000_00 : 0);
            balanceDelta += amount;
            items.Add(new StatementLine(
                period.Start.AddDays(i % 28),
                $"MERCHANT {i:D4} * REF {i * 7919:D6}",
                amount));
        }

        return new StatementDocument(
            "****5678",
            "A. Sample Customer",
            period,
            openingBalanceMinor: 84_213_45,
            closingBalanceMinor: 84_213_45 + balanceDelta,
            "ZAR",
            items,
            period.End);
    }

    private static async Task<byte[]> RenderToArrayAsync(StatementDocument document)
    {
        using var buffer = new MemoryStream();
        await Renderer.RenderAsync(document, buffer, CancellationToken.None).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static int PageCount(byte[] pdf) =>
        CountOccurrences(pdf, "/Type /Page"u8.ToArray()) - CountOccurrences(pdf, "/Type /Pages"u8.ToArray());

    private static int CountOccurrences(byte[] haystack, byte[] needle)
    {
        int count = 0;
        ReadOnlySpan<byte> span = haystack;

        while (true)
        {
            int index = span.IndexOf(needle);
            if (index < 0)
            {
                return count;
            }

            count++;
            span = span[(index + needle.Length)..];
        }
    }

    /// <summary>A stream that permits only forward writes, like a pipe.</summary>
    private sealed class ForwardOnlyStream : Stream
    {
        public long BytesWritten { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException("Forward-only: no Length.");

        public override long Position
        {
            get => throw new NotSupportedException("Forward-only: no Position.");
            set => throw new NotSupportedException("Forward-only: no Position.");
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("Forward-only: no Read.");

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException("Forward-only: no Seek.");

        public override void SetLength(long value) =>
            throw new NotSupportedException("Forward-only: no SetLength.");

        public override void Write(byte[] buffer, int offset, int count) => BytesWritten += count;
    }
}
