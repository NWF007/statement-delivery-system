using Microsoft.Extensions.Logging;

namespace SeedTool;

/// <summary>
/// Source-generated log messages for the seed tool.
/// </summary>
internal static partial class SeedLog
{
    [LoggerMessage(
        EventId = 6000,
        Level = LogLevel.Information,
        Message = "Seeding {Customers} customers x {Months} months (~{ApproximateStatements} statements), seed={Seed}.")]
    public static partial void Starting(ILogger logger, int customers, int months, long approximateStatements, int seed);

    [LoggerMessage(
        EventId = 6001,
        Level = LogLevel.Information,
        Message = "Created {PartitionsCreated} monthly partition(s) in {ElapsedMs} ms. Every row now has somewhere to land.")]
    public static partial void PartitionsReady(ILogger logger, int partitionsCreated, long elapsedMs);

    [LoggerMessage(
        EventId = 6002,
        Level = LogLevel.Information,
        Message = "Written {Written} statement row(s) so far.")]
    public static partial void Progress(ILogger logger, long written);

    [LoggerMessage(
        EventId = 6003,
        Level = LogLevel.Information,
        Message = "{Table}: {Rows} row(s) in {ElapsedMs} ms via binary COPY.")]
    public static partial void TableWritten(ILogger logger, string table, long rows, long elapsedMs);

    [LoggerMessage(
        EventId = 6004,
        Level = LogLevel.Information,
        Message = "Seed complete: {TotalRows} row(s) in {ElapsedSeconds}s; statements at {RowsPerSecond} rows/sec.")]
    public static partial void Finished(ILogger logger, long totalRows, long elapsedSeconds, long rowsPerSecond);

    [LoggerMessage(
        EventId = 6005,
        Level = LogLevel.Information,
        Message = "ANALYZE complete. Planner statistics are fresh, so EXPLAIN output is now meaningful.")]
    public static partial void Analyzed(ILogger logger);
}
