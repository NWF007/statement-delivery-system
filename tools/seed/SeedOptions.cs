namespace SeedTool;

/// <summary>
/// Parsed command line for the seed tool.
/// </summary>
/// <param name="Customers">Number of synthetic customers.</param>
/// <param name="Months">Months of statement history to generate; 0 writes customers and accounts only.</param>
/// <param name="BatchSize">Rows per binary COPY batch.</param>
/// <param name="Seed">Random seed. Fixed by default so runs are comparable.</param>
/// <param name="ConnectionString">Connection string, taken from the environment.</param>
/// <param name="Demo">Demo mode: a small known dataset plus a real generation run through the API.</param>
/// <param name="ApiUrl">The Delivery.Api base URL demo mode drives; from --api or DEMO_API_URL.</param>
public sealed record SeedOptions(
    int Customers, int Months, int BatchSize, int Seed, string ConnectionString, bool Demo, string ApiUrl)
{
    /// <summary>Approximate statement count: one per account per month, plus regenerations.</summary>
    public long ApproximateStatements => (long)Customers * Months;

    /// <summary>
    /// Parses arguments, returning null and printing usage when they are unusable.
    /// </summary>
    /// <param name="args">Raw command line.</param>
    /// <returns>Parsed options, or null.</returns>
    public static SeedOptions? Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        int? customers = null;
        int? months = null;
        int batchSize = 50_000;
        bool demo = false;
        string apiUrl = Environment.GetEnvironmentVariable("DEMO_API_URL") ?? "http://localhost:8081";

        // A FIXED DEFAULT, not Random.Shared. A volume benchmark whose input differs between runs
        // produces numbers that cannot be compared to the previous run - which is the only thing
        // anybody ever wants to do with them.
        int? seed = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--customers" when i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedCustomers):
                    customers = parsedCustomers;
                    i++;
                    break;
                case "--months" when i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedMonths):
                    months = parsedMonths;
                    i++;
                    break;
                case "--batch-size" when i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedBatch):
                    batchSize = parsedBatch;
                    i++;
                    break;
                case "--seed" when i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedSeed):
                    seed = parsedSeed;
                    i++;
                    break;
                case "--demo":
                    demo = true;
                    break;
                case "--api" when i + 1 < args.Length:
                    apiUrl = args[i + 1];
                    i++;
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return null;
                default:
                    Console.Error.WriteLine($"Unrecognised argument: {args[i]}");
                    PrintUsage();
                    return null;
            }
        }

        // Demo mode seeds a small, deterministic set and NO statement rows: the generation run it
        // requests produces the statements, with real objects behind them.
        int resolvedCustomers = customers ?? (demo ? 25 : 100_000);
        int resolvedMonths = months ?? (demo ? 0 : 24);
        int resolvedSeed = seed ?? (demo ? 7 : 42);

        if (resolvedCustomers < 1 || resolvedMonths < 0 || batchSize < 1)
        {
            Console.Error.WriteLine(
                "--customers and --batch-size must be at least 1; --months at least 0 (0 writes customers and accounts only).");
            return null;
        }

        if (!Uri.TryCreate(apiUrl.TrimEnd('/') + "/", UriKind.Absolute, out Uri? parsedApi)
            || parsedApi.Scheme is not ("http" or "https"))
        {
            Console.Error.WriteLine($"--api / DEMO_API_URL is not an absolute http(s) URL: {apiUrl}");
            return null;
        }

        string? connectionString = Environment.GetEnvironmentVariable("Postgres__PrimaryConnectionString");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine(
                "Postgres__PrimaryConnectionString is not set. The seed tool takes its connection from the environment, "
                + "so a command that writes millions of rows cannot be pointed at the wrong database by a stale flag in shell history.");
            return null;
        }

        return new SeedOptions(
            resolvedCustomers, resolvedMonths, batchSize, resolvedSeed, connectionString, demo, parsedApi.ToString());
    }

    private static void PrintUsage() =>
        Console.WriteLine(
            """
            Usage: dotnet run --project tools/seed -- [options]

              --customers <n>    Synthetic customers.                  Default 100000 (demo: 25)
              --months <n>       Months of statement history; 0 writes Default 24 (demo: 0)
                                 customers and accounts only.
              --batch-size <n>   Rows per binary COPY batch.           Default 50000
              --seed <n>         Random seed; fixed for reproducibility. Default 42 (demo: 7)
              --demo             Demo mode: the small seed above, the ten documented demo customers
                                 (11111111-1111-1111-1111-111111111101 .. 110), then a REAL
                                 generation run for last month requested through the API.
                                 Idempotent; what `docker compose up` runs as the seed-demo service.
              --api <url>        Delivery.Api base URL for demo mode.  Default DEMO_API_URL or
                                 http://localhost:8081

            Without --demo, produces roughly: 100k customers, ~115k accounts, ~2.4M statements.

            The connection string comes from Postgres__PrimaryConnectionString and must belong to a
            role holding INSERT on customer, account and statement, plus EXECUTE on
            ensure_range_partitions - app_generation, in the compose stack.
            """);
}
