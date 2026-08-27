using System.Reflection;
using Db.Migrator;
using DbUp;
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

UpgradeEngine upgrader = DeployChanges.To
    .PostgresqlDatabase(options.ConnectionString)
    .WithScriptsEmbeddedInAssembly(
        Assembly.GetExecutingAssembly(),
        name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
    .WithVariables(variables)
    .WithPreprocessor(new SessionGuardPreprocessor(options.LockTimeoutSeconds, options.StatementTimeoutSeconds))

    // One transaction per script, so a script that fails halfway leaves nothing behind and the
    // journal is never ahead of the schema. The exception is CREATE INDEX CONCURRENTLY, which
    // cannot run inside a transaction - see Scripts/README.md before writing one.
    .WithTransactionPerScript()
    .LogTo(logger)
    .Build();

List<string> pending = [.. upgrader.GetScriptsToExecute().Select(script => script.Name)];

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

DatabaseUpgradeResult result = upgrader.PerformUpgrade();

if (!result.Successful)
{
    MigratorLog.MigrationFailed(logger, result.Error, result.ErrorScript?.Name ?? "(unknown)");

    // Non-zero, so `depends_on: condition: service_completed_successfully` holds every service back.
    // A service that starts against a half-migrated schema fails in a far more confusing way than
    // one that never starts at all.
    return 1;
}

int appliedCount = result.Scripts.Count();
MigratorLog.MigrationsApplied(logger, appliedCount);
return 0;
