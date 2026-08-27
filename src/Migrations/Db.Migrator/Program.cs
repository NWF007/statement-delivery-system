using System.Reflection;
using Db.Migrator;
using DbUp;
using DbUp.Builder;
using DbUp.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
});

builder.Services.AddOptions<MigrationOptions>()
    .Bind(builder.Configuration.GetSection(MigrationOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

using IHost host = builder.Build();
ILogger logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Db.Migrator");

MigrationOptions options;
try
{
    options = host.Services.GetRequiredService<IOptions<MigrationOptions>>().Value;
}
catch (OptionsValidationException ex)
{
    // Configuration failures are the most common way a migrator run goes wrong, and the least
    // informative if it just throws. Name every failure, then stop.
    foreach (string failure in ex.Failures)
    {
        MigratorLog.ConfigurationInvalid(logger, failure);
    }

    return 2;
}

if (options.EnsureDatabaseExists)
{
    EnsureDatabase.For.PostgresqlDatabase(options.ConnectionString);
}

// Role passwords are substituted into V001 as $variables$. They are never logged: DbUp logs the
// script it executes, so a password reaching the log store is one careless setting away.
// ScriptPreprocessor order is the order they are registered; the session guards go on last so
// they are the first statements the server sees.
var variables = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["appDeliveryPassword"] = options.AppDeliveryPassword,
    ["appDownloadPassword"] = options.AppDownloadPassword,
    ["appGenerationPassword"] = options.AppGenerationPassword,
    ["appRetentionPassword"] = options.AppRetentionPassword,
    ["appMigratorPassword"] = options.AppMigratorPassword,
};

// ---------------------------------------------------------------------------------------------
// TWO PASSES, BECAUSE ONE TRANSACTION SETTING CANNOT SERVE BOTH KINDS OF SCRIPT.
//
// Rule 1 of Scripts/README.md: an index added to a table that already holds rows must be built
// CONCURRENTLY, or the build holds a lock that blocks writes for its whole duration. And
// CREATE INDEX CONCURRENTLY CANNOT RUN INSIDE A TRANSACTION - PostgreSQL rejects it outright - so
// such a script cannot go through WithTransactionPerScript().
//
// The convention: a script named `*.notx.sql` runs in the second pass, without a transaction.
//
// WHAT THAT COSTS, STATED PLAINLY. Non-transactional scripts run AFTER every transactional one,
// whatever their version numbers say, so version order is guaranteed only WITHIN a pass. That is
// acceptable for the one thing this pass exists to do - adding an index to a table that already
// exists is order-independent - and it is NOT acceptable for anything else. A `.notx.sql` script
// that alters data or depends on a later migration would break silently on a fresh database and
// work on an upgraded one, which is the worst kind of migration bug.
//
// The other consequence, from the same rule: a CONCURRENTLY build that fails leaves an INVALID
// index behind that nothing drops automatically. Check for one before re-running.
// ---------------------------------------------------------------------------------------------
const string NonTransactionalSuffix = ".notx.sql";

UpgradeEngine BuildUpgrader(bool nonTransactional)
{
    UpgradeEngineBuilder engine = DeployChanges.To
        .PostgresqlDatabase(options.ConnectionString)
        .WithScriptsEmbeddedInAssembly(
            Assembly.GetExecutingAssembly(),
            name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(NonTransactionalSuffix, StringComparison.OrdinalIgnoreCase) == nonTransactional)
        .WithVariables(variables)

        // statement_timeout is DISABLED for the concurrent pass. A concurrent index build over a
        // large table legitimately runs for minutes, and the 30-second guard that protects ordinary
        // DDL would kill it part way through - leaving exactly the INVALID index described above.
        // lock_timeout stays: CONCURRENTLY still takes brief locks, and waiting forever on one is
        // still how a migration takes the application down.
        .WithPreprocessor(new SessionGuardPreprocessor(
            options.LockTimeoutSeconds,
            nonTransactional ? 0 : options.StatementTimeoutSeconds))
        .LogTo(logger);

    return (nonTransactional ? engine.WithoutTransaction() : engine.WithTransactionPerScript()).Build();
}

UpgradeEngine transactional = BuildUpgrader(nonTransactional: false);
UpgradeEngine concurrent = BuildUpgrader(nonTransactional: true);

List<string> pending =
[
    .. transactional.GetScriptsToExecute().Select(script => script.Name),
    .. concurrent.GetScriptsToExecute().Select(script => script.Name),
];

if (pending.Count == 0)
{
    MigratorLog.UpToDate(logger);
    return 0;
}

// Materialised before the call: the log arguments must be cheap identifiers, or they are
// evaluated whether or not the level is enabled.
string pendingList = string.Join(", ", pending);

MigratorLog.ApplyingMigrations(
    logger,
    pending.Count,
    options.LockTimeoutSeconds,
    options.StatementTimeoutSeconds,
    pendingList);

int appliedCount = 0;

foreach (UpgradeEngine upgrader in (UpgradeEngine[])[transactional, concurrent])
{
    DatabaseUpgradeResult result = upgrader.PerformUpgrade();

    if (!result.Successful)
    {
        MigratorLog.MigrationFailed(logger, result.Error, result.ErrorScript?.Name ?? "(unknown)");

        // Non-zero, so `depends_on: condition: service_completed_successfully` holds every service
        // back. A service that starts against a half-migrated schema fails in a far more confusing
        // way than one that never starts at all.
        return 1;
    }

    appliedCount += result.Scripts.Count();
}

MigratorLog.MigrationsApplied(logger, appliedCount);
return 0;
