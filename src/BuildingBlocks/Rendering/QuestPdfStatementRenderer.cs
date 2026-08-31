using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using StatementDelivery.Domain.Rendering;

namespace StatementDelivery.Rendering;

/// <summary>
/// Renders statements with QuestPDF.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ EVERYTHING IN THIS CLASS IS SUBORDINATE TO BYTE DETERMINISM. The same
/// <see cref="StatementDocument"/> must produce IDENTICAL bytes on every render, in every process,
/// on every machine - because <c>content_sha256</c> is stored on the statement row and verified on
/// download, and a hash that drifts per render makes idempotent regeneration unverifiable.
/// PDF generation is non-deterministic by default; every source is pinned here:
/// </para>
/// <list type="bullet">
/// <item><description>
/// TIME. Creation and modification dates are set to the PERIOD END, never a clock. There is no
/// <c>DateTime.UtcNow</c> anywhere in a rendering path, and the input type has no field that could
/// smuggle one in.
/// </description></item>
/// <item><description>
/// PRODUCER STRINGS. Fixed literals with no library version, so a package bump cannot silently
/// change every hash. (A bump that changes the LAYOUT still changes hashes - that is real
/// regeneration and versioning handles it - but metadata must not.)
/// </description></item>
/// <item><description>
/// FONTS. Environment font discovery is OFF (<see cref="RenderingServiceCollectionExtensions"/>),
/// so rendering cannot depend on whatever the host happens to have installed - the dev machine
/// and the chiseled container resolve identically, from the library's own embedded Lato.
/// </description></item>
/// </list>
/// <para>
/// The proof is not this comment: <c>Render_SameDocument_ProducesIdenticalBytes</c> renders twice
/// in-process, and <c>Render_AcrossProcessRestart_ProducesIdenticalBytes</c> compares against a
/// child process. If a future library version breaks the property, those tests are the tripwire,
/// and ADR-0029 records the honest fallback.
/// </para>
/// </remarks>
public sealed class QuestPdfStatementRenderer : IStatementRenderer
{
    // Fixed, versionless, forever. See the determinism remarks above.
    private const string ProducerName = "SDP";

    /// <inheritdoc />
    public Task RenderAsync(StatementDocument document, Stream output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();

        // QuestPDF's API is synchronous; the pipeline calls this off the claim loop's hot path.
        // Task.Run would add nothing but a thread hop - the render IS the unit of work here.
        Compose(document).GeneratePdf(output);

        return Task.CompletedTask;
    }

    private static Document Compose(StatementDocument statement) =>
        Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(36f);
            page.DefaultTextStyle(static text => text.FontSize(9f).FontFamily("Lato"));

            page.Header().Element(header => ComposeHeader(header, statement));
            page.Content().Element(content => ComposeContent(content, statement));
            page.Footer().Element(ComposeFooter);
        }))
        .WithMetadata(new DocumentMetadata
        {
            Title = string.Create(CultureInfo.InvariantCulture, $"Statement {statement.Period}"),
            Author = "Statement Delivery Platform",
            Subject = statement.AccountNumberMasked,
            Creator = ProducerName,
            Producer = ProducerName,

            // THE DETERMINISM PIN. Period end, not now. Both dates, because a PDF reader shows
            // either and Skia hashes both into the document identifier.
            CreationDate = statement.Period.End.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            ModifiedDate = statement.Period.End.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        });

    private static void ComposeHeader(IContainer container, StatementDocument statement) =>
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                row.RelativeItem().Column(left =>
                {
                    left.Item().Text("Account statement").FontSize(16f).Bold();
                    left.Item().Text(statement.CustomerDisplayName).FontSize(10f);
                });

                row.RelativeItem().AlignRight().Column(right =>
                {
                    right.Item().AlignRight().Text(text =>
                    {
                        text.Span("Account ").FontColor(Colors.Grey.Darken1);
                        text.Span(statement.AccountNumberMasked).Bold();
                    });
                    right.Item().AlignRight().Text(text =>
                    {
                        text.Span("Period ").FontColor(Colors.Grey.Darken1);
                        text.Span(string.Create(
                            CultureInfo.InvariantCulture,
                            $"{statement.Period.Start:yyyy-MM-dd} to {statement.Period.End:yyyy-MM-dd}"));
                    });
                    right.Item().AlignRight().Text(text =>
                    {
                        text.Span("As of ").FontColor(Colors.Grey.Darken1);
                        text.Span(statement.GeneratedFor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    });
                });
            });

            column.Item().PaddingTop(8f).LineHorizontal(0.75f).LineColor(Colors.Grey.Medium);
        });

    private static void ComposeContent(IContainer container, StatementDocument statement) =>
        container.PaddingVertical(10f).Column(column =>
        {
            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.ConstantColumn(64f);   // date
                    columns.RelativeColumn(4f);    // description
                    columns.ConstantColumn(80f);   // debit
                    columns.ConstantColumn(80f);   // credit
                    columns.ConstantColumn(90f);   // running balance
                });

                table.Header(header =>
                {
                    header.Cell().Element(HeaderCell).Text("Date");
                    header.Cell().Element(HeaderCell).Text("Description");
                    header.Cell().Element(HeaderCell).AlignRight().Text("Debit");
                    header.Cell().Element(HeaderCell).AlignRight().Text("Credit");
                    header.Cell().Element(HeaderCell).AlignRight().Text("Balance");

                    static IContainer HeaderCell(IContainer cell) => cell
                        .BorderBottom(0.75f).BorderColor(Colors.Grey.Medium)
                        .PaddingVertical(4f)
                        .DefaultTextStyle(static t => t.SemiBold().FontSize(8.5f));
                });

                // Opening balance row, then every line with a RUNNING BALANCE - integer arithmetic
                // in minor units the whole way down; formatting happens only inside Amount().
                table.Cell().Element(BodyCell).Text(statement.Period.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                table.Cell().Element(BodyCell).Text("Opening balance").Italic();
                table.Cell().Element(BodyCell).Text(string.Empty);
                table.Cell().Element(BodyCell).Text(string.Empty);
                table.Cell().Element(BodyCell).AlignRight()
                    .Text(Amount(statement.OpeningBalanceMinor, statement.Currency));

                long running = statement.OpeningBalanceMinor;

                foreach (StatementLine line in statement.Lines)
                {
                    running += line.AmountMinorUnits;

                    table.Cell().Element(BodyCell)
                        .Text(line.PostedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    table.Cell().Element(BodyCell).Text(line.Description);
                    table.Cell().Element(BodyCell).AlignRight()
                        .Text(line.AmountMinorUnits < 0 ? Amount(-line.AmountMinorUnits, statement.Currency) : string.Empty);
                    table.Cell().Element(BodyCell).AlignRight()
                        .Text(line.AmountMinorUnits >= 0 ? Amount(line.AmountMinorUnits, statement.Currency) : string.Empty);
                    table.Cell().Element(BodyCell).AlignRight().Text(Amount(running, statement.Currency));
                }

                table.Cell().Element(ClosingCell)
                    .Text(statement.Period.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                table.Cell().Element(ClosingCell).Text("Closing balance").Bold();
                table.Cell().Element(ClosingCell).Text(string.Empty);
                table.Cell().Element(ClosingCell).Text(string.Empty);
                table.Cell().Element(ClosingCell).AlignRight()
                    .Text(Amount(statement.ClosingBalanceMinor, statement.Currency)).Bold();

                static IContainer BodyCell(IContainer cell) => cell
                    .BorderBottom(0.25f).BorderColor(Colors.Grey.Lighten2)
                    .PaddingVertical(2.5f);

                static IContainer ClosingCell(IContainer cell) => cell
                    .BorderTop(0.75f).BorderColor(Colors.Grey.Medium)
                    .PaddingVertical(4f);
            });

            if (statement.Lines.Count == 0)
            {
                column.Item().PaddingTop(6f)
                    .Text("No transactions were posted to this account during the period.")
                    .Italic().FontColor(Colors.Grey.Darken1);
            }
        });

    private static void ComposeFooter(IContainer container) =>
        container.Column(column =>
        {
            column.Item().LineHorizontal(0.25f).LineColor(Colors.Grey.Lighten1);
            column.Item().PaddingTop(4f).Row(row =>
            {
                row.RelativeItem()
                    .Text("This statement is system-generated and requires no signature.")
                    .FontSize(7.5f).FontColor(Colors.Grey.Darken1);

                row.RelativeItem().AlignRight().Text(text =>
                {
                    text.DefaultTextStyle(static t => t.FontSize(7.5f).FontColor(Colors.Grey.Darken1));
                    text.Span("Page ");
                    text.CurrentPageNumber();
                    text.Span(" of ");
                    text.TotalPages();
                });
            });
        });

    /// <summary>
    /// Formats minor units for display. THE ONLY PLACE MONEY BECOMES TEXT.
    /// </summary>
    /// <remarks>
    /// Integer division and modulus - the value is never converted through any floating-point or
    /// decimal representation on its way to the page. Invariant culture with explicit grouping,
    /// so the same document does not hash differently under a different OS locale.
    /// </remarks>
    private static string Amount(long minorUnits, string currency)
    {
        long abs = Math.Abs(minorUnits);
        string sign = minorUnits < 0 ? "-" : string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{sign}{currency} {(abs / 100).ToString("N0", CultureInfo.InvariantCulture)}.{abs % 100:D2}");
    }
}
