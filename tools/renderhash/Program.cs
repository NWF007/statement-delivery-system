using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using StatementDelivery.Crypto.Framing;
using StatementDelivery.Domain.Rendering;
using StatementDelivery.Domain.ValueObjects;
using StatementDelivery.Rendering;

// =============================================================================================
//  renderhash - determinism probe and local render bench. See the csproj for why this exists.
//
//  hash [lines]        render the canonical document once, print SHA-256 of the bytes
//  bench <count> [lines]  render+encrypt <count> statements to a null sink, print stage timings
// =============================================================================================

RenderingServiceCollectionExtensions.ApplyGlobalSettings("Community");
var renderer = new QuestPdfStatementRenderer();

string mode = args.Length > 0 ? args[0] : "hash";

switch (mode)
{
    case "hash":
    {
        int lines = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 100;

        using var buffer = new MemoryStream();
        await renderer.RenderAsync(CanonicalDocument(lines), buffer, CancellationToken.None).ConfigureAwait(false);

        Console.WriteLine(Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray())));
        return 0;
    }

    case "bench":
    {
        int count = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 200;
        int lines = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 80;

        var cipher = new FramedAeadCipher();
        byte[] dek = RandomNumberGenerator.GetBytes(32);

        // Warm-up: JIT, font load, Skia init - one render outside the measured window.
        using (var warm = new MemoryStream())
        {
            await renderer.RenderAsync(CanonicalDocument(lines), warm, CancellationToken.None).ConfigureAwait(false);
        }

        long renderTicks = 0, encryptTicks = 0, totalPlain = 0, totalCipher = 0;
        var wall = Stopwatch.StartNew();

        for (int i = 0; i < count; i++)
        {
            // Vary the document per iteration the way a real run does - each account differs.
            StatementDocument document = CanonicalDocument(lines, salt: i);

            // The bench renders into a buffer, then encrypts from it, so the two stages can be
            // TIMED separately. Production pipes the two together and never holds the whole PDF;
            // the peak-memory number below is therefore an UPPER bound on the streaming pipeline.
            var sw = Stopwatch.StartNew();
            using var pdf = new MemoryStream();
            await renderer.RenderAsync(document, pdf, CancellationToken.None).ConfigureAwait(false);
            renderTicks += sw.ElapsedTicks;

            pdf.Position = 0;
            sw.Restart();
            CipherResult result = await cipher.EncryptAsync(
                pdf,
                Stream.Null,
                dek,
                new CryptoContext(Guid.NewGuid(), Guid.NewGuid(), 1),
                CancellationToken.None).ConfigureAwait(false);
            encryptTicks += sw.ElapsedTicks;

            totalPlain += result.PlaintextLength;
            totalCipher += result.CiphertextLength;
        }

        wall.Stop();
        using Process me = Process.GetCurrentProcess();

        double seconds = wall.Elapsed.TotalSeconds;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"items                {count}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"lines_per_item       {lines}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"wall_seconds         {seconds:F2}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"items_per_second     {count / seconds:F1}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"render_ms_avg        {new TimeSpan(renderTicks).TotalMilliseconds / count:F1}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"encrypt_ms_avg       {new TimeSpan(encryptTicks).TotalMilliseconds / count:F2}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"avg_pdf_bytes        {totalPlain / count}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"cipher_overhead_pct  {(totalCipher - totalPlain) * 100.0 / totalPlain:F2}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"peak_working_set_mb  {me.PeakWorkingSet64 / (1024.0 * 1024.0):F0}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"gc_heap_mb           {GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0):F0}"));
        return 0;
    }

    default:
        Console.Error.WriteLine("usage: renderhash [hash [lines] | bench <count> [lines]]");
        return 1;
}

// The canonical probe document. Deterministic from (lines, salt) - no clock, no RNG - so two
// processes given the same arguments build the same value.
static StatementDocument CanonicalDocument(int lines, int salt = 0)
{
    StatementPeriod period = StatementPeriod.ForMonth(2026, 8);
    var items = new List<StatementLine>(lines);

    long delta = 0;
    for (int i = 0; i < lines; i++)
    {
        long amount = ((i % 7) + 1) * -137_50 + (i % 11 == 0 ? 15_000_00 : 0) + (salt % 5);
        delta += amount;
        items.Add(new StatementLine(
            period.Start.AddDays(i % 28),
            string.Create(CultureInfo.InvariantCulture, $"MERCHANT {i:D4} * REF {(i + salt) * 7919:D6}"),
            amount));
    }

    return new StatementDocument(
        "****5678",
        "A. Sample Customer",
        period,
        openingBalanceMinor: 84_213_45,
        closingBalanceMinor: 84_213_45 + delta,
        "ZAR",
        items,
        period.End);
}
