using System.Security.Cryptography;
using System.Text;

namespace MockLedger.Api;

/// <summary>One ledger transaction, as the wire contract.</summary>
/// <param name="PostedOn">Posting date, ISO 8601.</param>
/// <param name="Description">Merchant or transaction descriptor.</param>
/// <param name="AmountMinorUnits">Signed ZAR cents. Credits positive, debits negative. A long, never a float.</param>
public sealed record LedgerTransaction(DateOnly PostedOn, string Description, long AmountMinorUnits);

/// <summary>The transactions response.</summary>
/// <param name="AccountId">The account.</param>
/// <param name="OpeningBalanceMinorUnits">Balance at period start, ZAR cents.</param>
/// <param name="ClosingBalanceMinorUnits">Balance at period end, ZAR cents.</param>
/// <param name="Transactions">The period's transactions, in posting order.</param>
public sealed record LedgerResponse(
    Guid AccountId,
    long OpeningBalanceMinorUnits,
    long ClosingBalanceMinorUnits,
    IReadOnlyList<LedgerTransaction> Transactions);

/// <summary>
/// Deterministic transaction generation.
/// </summary>
/// <remarks>
/// <para>
/// THE PRNG IS SEEDED FROM SHA-256(accountId | from | to), so the same (account, period) always
/// produces the same data - byte-identical JSON on every call, from every replica, forever.
/// Without this the determinism chain collapses at its first link: rendering the same statement
/// twice would produce different content, and the byte-determinism test in the renderer would be
/// asserting nothing about the system.
/// </para>
/// <para>
/// The SHAPE is deliberately realistic, because the tails are where rendering bugs live:
/// most accounts get 5-80 transactions; roughly one in nineteen gets ZERO (the empty-statement
/// path); roughly one in ninety-seven gets several hundred (the pagination path). A
/// salary-shaped credit lands on the 25th, debit orders cluster at month start, and the rest are
/// plausible merchant lines.
/// </para>
/// </remarks>
public static class LedgerGenerator
{
    private static readonly string[] Merchants =
    [
        "PICK N PAY 1584 CLAREMONT", "WOOLWORTHS ONLINE", "SHELL WESTLAKE", "UBER TRIP HELP.UBER.COM",
        "TAKEALOT.COM", "CHECKERS SIXTY60", "NETFLIX.COM", "VIRGIN ACTIVE RSA", "CLICKS PHARMACY 0221",
        "ENGEN QUICKSHOP N1", "MR PRICE ONLINE", "SPOTIFY AB", "STEERS PLUMSTEAD", "EXCLUSIVE BOOKS VA",
    ];

    private static readonly string[] DebitOrders =
    [
        "DEBIT ORDER: OUTSURANCE", "DEBIT ORDER: DISCOVERY HEALTH", "DEBIT ORDER: VODACOM",
        "DEBIT ORDER: SANLAM LIFE", "DEBIT ORDER: CITY OF CT MUNICIPAL",
    ];

    /// <summary>Builds the deterministic response for one (account, period).</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="from">Period start, inclusive.</param>
    /// <param name="to">Period end, inclusive.</param>
    /// <returns>The response - identical for identical inputs, always.</returns>
    public static LedgerResponse Generate(Guid accountId, DateOnly from, DateOnly to)
    {
        // SHA-256 of the identifying tuple, folded into a 32-bit seed. Not a security boundary -
        // purely a stable, well-mixed function from (account, period) to a PRNG stream.
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{accountId:D}|{from:yyyy-MM-dd}|{to:yyyy-MM-dd}"));
        int seed = BitConverter.ToInt32(hash, 0);
        var rng = new Random(seed);

        // The tails, chosen from the hash rather than the RNG stream so the account's "personality"
        // (empty / huge / ordinary) is stable even if the shape logic below evolves.
        int personality = hash[4];
        int count = personality switch
        {
            < 13 => 0,                        // ~5%: dormant-looking, zero transactions
            < 16 => 300 + rng.Next(300),      // ~1%: several hundred - the pagination tail
            _ => 5 + rng.Next(76),            // the broad middle: 5-80
        };

        long opening = 10_000_00 + (long)(rng.NextDouble() * 90_000_00);
        var transactions = new List<LedgerTransaction>(count + 2);
        int totalDays = to.DayNumber - from.DayNumber + 1;

        // Debit orders first, clustered in the opening days of the period.
        int debitOrders = count == 0 ? 0 : Math.Min(2 + rng.Next(3), count);
        for (int i = 0; i < debitOrders; i++)
        {
            transactions.Add(new LedgerTransaction(
                from.AddDays(rng.Next(Math.Min(5, totalDays))),
                DebitOrders[rng.Next(DebitOrders.Length)],
                -(20_000 + rng.Next(180_000))));
        }

        // Ordinary merchant lines across the month.
        for (int i = debitOrders; i < count; i++)
        {
            transactions.Add(new LedgerTransaction(
                from.AddDays(rng.Next(totalDays)),
                Merchants[rng.Next(Merchants.Length)],
                -(1_500 + rng.Next(250_000))));
        }

        // The salary-shaped credit on the 25th (or the last day of a short period).
        if (count > 0)
        {
            DateOnly payday = from.AddDays(Math.Min(24, totalDays - 1));
            transactions.Add(new LedgerTransaction(
                payday,
                "SALARY: ACME HOLDINGS (PTY) LTD",
                2_500_000 + rng.Next(2_500_000)));
        }

        // Posting order, with a deterministic tie-break so equal dates never reorder between
        // calls: List.Sort is unstable, and an unstable sort under a stable comparator is the
        // kind of nondeterminism that surfaces once a month in production and never in a test.
        transactions.Sort(static (a, b) =>
        {
            int byDate = a.PostedOn.CompareTo(b.PostedOn);
            if (byDate != 0)
            {
                return byDate;
            }

            int byAmount = a.AmountMinorUnits.CompareTo(b.AmountMinorUnits);
            return byAmount != 0 ? byAmount : string.CompareOrdinal(a.Description, b.Description);
        });

        long closing = opening;
        foreach (LedgerTransaction transaction in transactions)
        {
            closing += transaction.AmountMinorUnits;
        }

        return new LedgerResponse(accountId, opening, closing, transactions);
    }

    /// <summary>
    /// Whether this account exists at all, derived from the account id alone.
    /// </summary>
    /// <remarks>
    /// Roughly 1 in 256 account ids are "unknown" and return 404 - enough to exercise the
    /// worker's not-found handling without configuring anything. Deterministic, so a test can
    /// construct a known-missing id when it needs one.
    /// </remarks>
    public static bool IsKnown(Guid accountId) =>
        SHA256.HashData(accountId.ToByteArray())[0] != 0;
}
