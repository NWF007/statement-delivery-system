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

    [LoggerMessage(
        EventId = 5005,
        Level = LogLevel.Warning,
        Message = "PostgreSQL is not accepting connections yet (attempt {Attempt} of {MaxAttempts}): {Reason}. Retrying in {DelaySeconds}s.")]
    public static partial void WaitingForDatabase(ILogger logger, int attempt, int maxAttempts, string reason, int delaySeconds);

    [LoggerMessage(
        EventId = 5006,
        Level = LogLevel.Critical,
        Message = "PostgreSQL did not accept a connection within {MaxAttempts} attempts. Giving up.")]
    public static partial void DatabaseUnreachable(ILogger logger, Exception exception, int maxAttempts);

    [LoggerMessage(
        EventId = 5007,
        Level = LogLevel.Critical,
        Message = "The migrator stopped on an unexpected error. The schema is unchanged beyond any script already reported as applied.")]
    public static partial void UnexpectedFailure(ILogger logger, Exception exception);
}
