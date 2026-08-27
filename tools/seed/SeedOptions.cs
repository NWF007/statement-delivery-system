namespace SeedTool;

/// <summary>
/// Parsed command line for the seed tool.
/// </summary>
/// <param name="Customers">Number of synthetic customers.</param>
/// <param name="Months">Months of statement history to generate.</param>
/// <param name="BatchSize">Rows per binary COPY batch.</param>
/// <param name="Seed">Random seed. Fixed by default so runs are comparable.</param>
/// <param name="ConnectionString">Connection string, taken from the environment.</param>
public sealed record SeedOptions(int Customers, int Months, int BatchSize, int Seed, string ConnectionString)
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

        int customers = 100_000;
        int months = 24;
        int batchSize = 50_000;

        // A FIXED DEFAULT, not Random.Shared. A volume benchmark whose input differs between runs
        // produces numbers that cannot be compared to the previous run - which is the only thing
        // anybody ever wants to do with them.
        int seed = 42;

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
                case "--help" or "-h":
                    PrintUsage();
                    return null;
                default:
                    Console.Error.WriteLine($"Unrecognised argument: {args[i]}");
                    PrintUsage();
                    return null;
            }
        }

        if (customers < 1 || months < 1 || batchSize < 1)
        {
            Console.Error.WriteLine("--customers, --months and --batch-size must all be at least 1.");
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

        return new SeedOptions(customers, months, batchSize, seed, connectionString);
    }

    private static void PrintUsage() =>
        Console.WriteLine(
            """
            Usage: dotnet run --project tools/seed -- [options]

              --customers <n>    Synthetic customers.                  Default 100000
              --months <n>       Months of statement history.          Default 24
              --batch-size <n>   Rows per binary COPY batch.           Default 50000
              --seed <n>         Random seed; fixed for reproducibility. Default 42

            Produces roughly: 100k customers, ~115k accounts, ~2.4M statements.

            The connection string comes from Postgres__PrimaryConnectionString and must belong to a
            role holding INSERT on customer, account and statement, plus EXECUTE on
            ensure_range_partitions - app_generation, in the compose stack.
            """);
}
