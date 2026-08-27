using Microsoft.Extensions.Logging;

namespace Db.Migrator;

/// <summary>
/// Source-generated log messages for the migrator.
/// </summary>
/// <remarks>
/// Separated from the top-level Program.cs because [LoggerMessage] needs a partial class to
/// generate into. Source generation also keeps argument evaluation behind the level check, which
/// matters less here than in a hot path but keeps one convention across the whole repository.
/// </remarks>
internal static partial class MigratorLog
{
    [LoggerMessage(
        EventId = 5000,
        Level = LogLevel.Critical,
        Message = "Migration configuration is invalid: {Failure}")]
    public static partial void ConfigurationInvalid(ILogger logger, string failure);

    [LoggerMessage(
        EventId = 5001,
        Level = LogLevel.Information,
        Message = "Schema is up to date. No migrations to apply.")]
    public static partial void UpToDate(ILogger logger);

    [LoggerMessage(
        EventId = 5002,
        Level = LogLevel.Information,
        Message = "Applying {PendingCount} migration(s) with lock_timeout={LockTimeoutSeconds}s and statement_timeout={StatementTimeoutSeconds}s: {Scripts}")]
    public static partial void ApplyingMigrations(
        ILogger logger,
        int pendingCount,
        int lockTimeoutSeconds,
        int statementTimeoutSeconds,
        string scripts);

    [LoggerMessage(
        EventId = 5003,
        Level = LogLevel.Critical,
        Message = "Migration failed on script {Script}. That script left the schema unchanged: DbUp runs each one in its own transaction.")]
    public static partial void MigrationFailed(ILogger logger, Exception exception, string script);

    [LoggerMessage(
        EventId = 5004,
        Level = LogLevel.Information,
        Message = "Applied {AppliedCount} migration(s) successfully.")]
    public static partial void MigrationsApplied(ILogger logger, int appliedCount);
}
