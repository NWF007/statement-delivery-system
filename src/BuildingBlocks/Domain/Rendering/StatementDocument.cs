using StatementDelivery.Domain.Exceptions;
using StatementDelivery.Domain.ValueObjects;

namespace StatementDelivery.Domain.Rendering;

/// <summary>
/// One transaction line on a rendered statement.
/// </summary>
/// <remarks>
/// MONEY IS <see langword="long"/> MINOR UNITS, HERE AND EVERYWHERE UPSTREAM OF THE RENDERER.
/// Formatting into rands-and-cents happens at the moment of drawing and nowhere else. No
/// <see langword="double"/>, no <see langword="float"/>, no <see cref="decimal"/> - the first is
/// wrong (0.1 is not representable), the last is merely unnecessary once amounts are integers of
/// the smallest unit. An architecture test walks the compiled IL of the money-bearing types and
/// fails the build if a floating-point member ever appears.
/// </remarks>
/// <param name="PostedOn">The posting date.</param>
/// <param name="Description">Merchant or transaction descriptor, as the ledger supplied it.</param>
/// <param name="AmountMinorUnits">Signed amount in minor units. Credits positive, debits negative.</param>
public sealed record StatementLine(DateOnly PostedOn, string Description, long AmountMinorUnits);

/// <summary>
/// Everything the renderer needs to draw one statement. Nothing more.
/// </summary>
/// <remarks>
/// <para>
/// A PURE VALUE, BUILT ENTIRELY FROM INPUTS THE PIPELINE ALREADY HOLDS. There is deliberately no
/// clock anywhere near this type: <see cref="GeneratedFor"/> is a DATE DERIVED FROM THE PERIOD,
/// not <c>DateTime.UtcNow</c>, because the rendered bytes must be identical however many times
/// and on whatever day the same document is rendered. <c>content_sha256</c> is stored on the
/// statement row and verified on every download; a timestamp that changes per render would change
/// the hash and make idempotent regeneration unverifiable. See ADR-0029.
/// </para>
/// <para>
/// The closing balance is CARRIED, NOT COMPUTED, so the renderer can never disagree with the
/// ledger about what the customer owes - it draws what it was told. The constructor still checks
/// the arithmetic and refuses a document whose lines do not sum, because a statement that renders
/// beautifully and lies about the balance is worse than no statement.
/// </para>
/// </remarks>
public sealed record StatementDocument
{
    /// <summary>Initialises a new instance of the <see cref="StatementDocument"/> record.</summary>
    /// <param name="accountNumberMasked">Masked account number, exactly as stored.</param>
    /// <param name="customerDisplayName">Display name for the header.</param>
    /// <param name="period">The statement period.</param>
    /// <param name="openingBalanceMinor">Opening balance, minor units.</param>
    /// <param name="closingBalanceMinor">Closing balance, minor units.</param>
    /// <param name="currency">ISO 4217 code, for example ZAR.</param>
    /// <param name="lines">Transaction lines, in posting order.</param>
    /// <param name="generatedFor">The as-of date shown on the document. Period-derived, never now.</param>
    /// <exception cref="InvariantViolationException">The balances and lines disagree.</exception>
    public StatementDocument(
        string accountNumberMasked,
        string customerDisplayName,
        StatementPeriod period,
        long openingBalanceMinor,
        long closingBalanceMinor,
        string currency,
        IReadOnlyList<StatementLine> lines,
        DateOnly generatedFor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountNumberMasked);
        ArgumentException.ThrowIfNullOrWhiteSpace(customerDisplayName);
        ArgumentNullException.ThrowIfNull(period);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentNullException.ThrowIfNull(lines);

        long sum = 0;
        foreach (StatementLine line in lines)
        {
            sum += line.AmountMinorUnits;
        }

        if (openingBalanceMinor + sum != closingBalanceMinor)
        {
            throw new InvariantViolationException(
                "Statement balances do not reconcile: opening + lines != closing. "
                + "Rendering a statement that lies about its own arithmetic is not an option.");
        }

        AccountNumberMasked = accountNumberMasked;
        CustomerDisplayName = customerDisplayName;
        Period = period;
        OpeningBalanceMinor = openingBalanceMinor;
        ClosingBalanceMinor = closingBalanceMinor;
        Currency = currency;
        Lines = lines;
        GeneratedFor = generatedFor;
    }

    /// <summary>Gets the masked account number.</summary>
    public string AccountNumberMasked { get; }

    /// <summary>Gets the customer display name.</summary>
    public string CustomerDisplayName { get; }

    /// <summary>Gets the statement period.</summary>
    public StatementPeriod Period { get; }

    /// <summary>Gets the opening balance in minor units.</summary>
    public long OpeningBalanceMinor { get; }

    /// <summary>Gets the closing balance in minor units.</summary>
    public long ClosingBalanceMinor { get; }

    /// <summary>Gets the ISO 4217 currency code.</summary>
    public string Currency { get; }

    /// <summary>Gets the transaction lines, in posting order.</summary>
    public IReadOnlyList<StatementLine> Lines { get; }

    /// <summary>Gets the as-of date the document displays. Derived from the period, never a clock.</summary>
    public DateOnly GeneratedFor { get; }
}

/// <summary>
/// Renders one statement document to a stream.
/// </summary>
/// <remarks>
/// <para>
/// A STREAM, NEVER A <c>byte[]</c>. An 800-transaction statement must not materialise as an
/// array: 360 concurrent renderers each holding a multi-megabyte buffer is how the fleet OOMs,
/// and the constant-memory property already proved on the read path dies quietly on the
/// write path. The caller pipes this output straight into the encrypting writer.
/// </para>
/// <para>
/// The port lives in the domain; the QuestPDF implementation lives in BuildingBlocks/Rendering.
/// That separation is also the licensing exit: if procurement rejects QuestPDF's revenue-gated
/// Community licence, PDFsharp slots in behind this interface as a single-class change. See
/// docs/LICENSING.md.
/// </para>
/// </remarks>
public interface IStatementRenderer
{
    /// <summary>Renders <paramref name="document"/> as a PDF into <paramref name="output"/>.</summary>
    /// <param name="document">The statement to draw.</param>
    /// <param name="output">The destination stream. Written forward-only; never rewound.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RenderAsync(StatementDocument document, Stream output, CancellationToken cancellationToken);
}
