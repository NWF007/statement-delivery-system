using System.Diagnostics;
using System.Globalization;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using SeedTool;
using StatementDelivery.Domain.Abstractions;
using StatementDelivery.Domain.ValueObjects;
using StatementDelivery.Persistence.Bulk;
using StatementDelivery.Persistence.Connections;
using StatementDelivery.Persistence.Ids;

// =============================================================================================
//  Volume seeding for docs/SCALE.md.
//
//  Binary COPY throughout, never row-by-row INSERT. Two and a half million statements one
//  statement at a time is a parse, plan, execute and round trip per row - roughly two orders of
//  magnitude slower, and it would take hours rather than minutes.
//
//  Deterministic from --seed, so two runs produce byte-identical data and their timings can
//  actually be compared.
// =============================================================================================

SeedOptions? options = SeedOptions.Parse(args);
if (options is null)
{
    return 1;
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddSimpleConsole(console => console.SingleLine = true);
builder.Services.Configure<PostgresOptions>(postgres =>
{
    postgres.PrimaryConnectionString = options.ConnectionString;
    postgres.ApplicationName = "seed-tool";
    postgres.MaxPoolSize = 4;

    // Generous: one COPY of fifty thousand rows is a single statement, and the delivery path's
    // five-second budget would abort it for being exactly as slow as it is supposed to be.
    postgres.WriteCommandTimeoutSeconds = 1800;
});
builder.Services.AddSingleton<NpgsqlConnectionFactory>();
builder.Services.AddSingleton<IDbConnectionFactory>(sp => sp.GetRequiredService<NpgsqlConnectionFactory>());
builder.Services.AddSingleton<IIdGenerator, UuidV7Generator>();
builder.Services.AddSingleton<IBulkWriter, NpgsqlBinaryCopyWriter>();
builder.Services.AddSingleton<IBulkRowMapper<CustomerRow>, CustomerRowMapper>();
builder.Services.AddSingleton<IBulkRowMapper<AccountRow>, AccountRowMapper>();
builder.Services.AddSingleton<IBulkRowMapper<StatementRow>, StatementRowMapper>();

using IHost host = builder.Build();
ILogger logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SeedTool");
var connections = host.Services.GetRequiredService<IDbConnectionFactory>();
var ids = host.Services.GetRequiredService<IIdGenerator>();
var bulk = host.Services.GetRequiredService<IBulkWriter>();

var random = new Random(options.Seed);
DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
StatementPeriod newest = StatementPeriod.ForMonth(today.Year, today.Month).Previous();

SeedLog.Starting(logger, options.Customers, options.Months, options.ApproximateStatements, options.Seed);

// ---------------------------------------------------------------------------------------------
// Partitions first. An INSERT into a range-partitioned table with no matching partition FAILS, so
// every month in the window must exist before the first statement row is written.
// ---------------------------------------------------------------------------------------------
var partitionStopwatch = Stopwatch.StartNew();
int partitionsCreated;

await using (NpgsqlConnection connection = await connections.OpenAsync(ConnectionIntent.Write).ConfigureAwait(false))
{
    DateTimeOffset from = new(
        newest.Start.AddMonths(-(options.Months + 1)).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    partitionsCreated = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
        "SELECT ensure_range_partitions('statement'::regclass, 'month', @periods, @from);",
        new { periods = options.Months + 3, from },
        commandTimeout: 1800)).ConfigureAwait(false);
}

partitionStopwatch.Stop();
SeedLog.PartitionsReady(logger, partitionsCreated, (long)partitionStopwatch.Elapsed.TotalMilliseconds);

// ---------------------------------------------------------------------------------------------
// Customers and accounts.
//
// REALISTIC DISTRIBUTION, not a uniform one. A uniform dataset makes every query look equally
// cheap, which is exactly the illusion a benchmark exists to dispel: most customers hold one
// account, a minority hold several, and a slice are dormant with gaps in their history.
// ---------------------------------------------------------------------------------------------
var customers = new List<CustomerRow>(options.Customers);
var accounts = new List<AccountRow>((int)(options.Customers * 1.2));

for (int i = 0; i < options.Customers; i++)
{
    Guid customerId = ids.NewId();

    // ~8% dormant. Their statements stop partway through the window, which is what produces the
    // gaps a real dataset has and a generated one usually does not.
    bool dormant = random.Next(100) < 8;

    customers.Add(new CustomerRow(
        customerId,
        string.Create(CultureInfo.InvariantCulture, $"EXT-{options.Seed}-{i:D8}"),
        dormant ? "DORMANT" : "ACTIVE"));

    // ~85% one account, ~13% two, ~2% three. Averages to about 1.17 accounts per customer, which
    // is where the "100k customers, ~115k accounts" shape comes from.
    int roll = random.Next(100);
    int accountCount = roll < 85 ? 1 : roll < 98 ? 2 : 3;

    for (int a = 0; a < accountCount; a++)
    {
        accounts.Add(new AccountRow(
            ids.NewId(),
            customerId,
            string.Create(CultureInfo.InvariantCulture, $"****{random.Next(1000, 9999)}"),
            random.Next(3) switch { 0 => "CURRENT", 1 => "SAVINGS", _ => "CREDIT" },
            dormant ? "DORMANT" : "ACTIVE",
            new DateTimeOffset(newest.Start.AddMonths(-random.Next(options.Months, options.Months + 60)).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)));
    }
}

var totalStopwatch = Stopwatch.StartNew();

long customerCount = await bulk.WriteAsync("customer", ToAsync(customers), CancellationToken.None).ConfigureAwait(false);
SeedLog.TableWritten(logger, "customer", customerCount, (long)totalStopwatch.Elapsed.TotalMilliseconds);

var accountStopwatch = Stopwatch.StartNew();
long accountCountWritten = await bulk.WriteAsync("account", ToAsync(accounts), CancellationToken.None).ConfigureAwait(false);
accountStopwatch.Stop();
SeedLog.TableWritten(logger, "account", accountCountWritten, (long)accountStopwatch.Elapsed.TotalMilliseconds);

// ---------------------------------------------------------------------------------------------
// Statements, streamed in batches so the whole set is never in memory at once.
// ---------------------------------------------------------------------------------------------
var statementStopwatch = Stopwatch.StartNew();
long statementsWritten = 0;

for (int offset = 0; offset < accounts.Count; offset += 500)
{
    List<AccountRow> slice = accounts.GetRange(offset, Math.Min(500, accounts.Count - offset));
    statementsWritten += await bulk
        .WriteAsync("statement", GenerateStatements(slice), CancellationToken.None)
        .ConfigureAwait(false);

    if (offset % 20_000 == 0)
    {
        SeedLog.Progress(logger, statementsWritten);
    }
}

statementStopwatch.Stop();

double rowsPerSecond = statementStopwatch.Elapsed.TotalSeconds > 0
    ? statementsWritten / statementStopwatch.Elapsed.TotalSeconds
    : statementsWritten;

SeedLog.TableWritten(logger, "statement", statementsWritten, (long)statementStopwatch.Elapsed.TotalMilliseconds);

totalStopwatch.Stop();
SeedLog.Finished(
    logger,
    customerCount + accountCountWritten + statementsWritten,
    (long)totalStopwatch.Elapsed.TotalSeconds,
    (long)rowsPerSecond);

// ---------------------------------------------------------------------------------------------
// ANALYZE. Without fresh statistics the planner has no row estimates, and the EXPLAIN output that
// goes into docs/SCALE.md would be measuring the planner's ignorance rather than the schema.
// ---------------------------------------------------------------------------------------------
await using (NpgsqlConnection connection = await connections.OpenAsync(ConnectionIntent.Write).ConfigureAwait(false))
{
    _ = await connection.ExecuteAsync(new CommandDefinition(
        "ANALYZE customer; ANALYZE account; ANALYZE statement;", commandTimeout: 3600)).ConfigureAwait(false);
}

SeedLog.Analyzed(logger);
return 0;

async IAsyncEnumerable<T> ToAsync<T>(IEnumerable<T> source)
{
    foreach (T item in source)
    {
        yield return item;
        await Task.CompletedTask.ConfigureAwait(false);
    }
}

async IAsyncEnumerable<StatementRow> GenerateStatements(IEnumerable<AccountRow> forAccounts)
{
    foreach (AccountRow account in forAccounts)
    {
        // A dormant account stops producing statements partway through the window.
        int months = account.Status == "DORMANT"
            ? random.Next(1, Math.Max(2, options.Months / 2))
            : options.Months;

        StatementPeriod period = newest;

        for (int m = 0; m < months; m++)
        {
            Guid statementId = ids.NewId();

            yield return BuildStatement(statementId, account, period, version: 1);

            // ~1.5% of statements were regenerated. Version 2 is a NEW ROW alongside version 1, not
            // an update - which is what makes "what did the statement say before it was corrected?"
            // an answerable question.
            if (random.Next(1000) < 15)
            {
                yield return BuildStatement(ids.NewId(), account, period, version: 2);
            }

            period = period.Previous();
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}

StatementRow BuildStatement(Guid statementId, AccountRow account, StatementPeriod period, int version) =>
    new(
        statementId,
        account.Id,
        account.CustomerId,
        period.Start,
        period.End,
        version,
        "AVAILABLE",

        // The key is COMPUTED from the row, never discovered by listing a bucket. At 2.5 billion
        // objects a listing is not slow, it is unusable.
        string.Create(CultureInfo.InvariantCulture, $"statements/{period.Start:yyyy/MM}/{statementId:N}-v{version}.pdf"),
        random.Next(38_000, 420_000),
        RetentionPolicy.Default.RetainUntil(period),
        new DateTimeOffset(period.End.AddDays(1).ToDateTime(new TimeOnly(2, 14, 33)), TimeSpan.Zero));
