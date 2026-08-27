using System.ComponentModel.DataAnnotations;

namespace Db.Migrator;

/// <summary>
/// Configuration for the schema migrator.
/// </summary>
/// <remarks>
/// Every value here arrives from the environment. None of it is committed, and none of it is
/// logged: the role passwords below are written into the database by V001 and would otherwise be
/// one careless log line away from the log store.
/// </remarks>
public sealed class MigrationOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Migration";

    /// <summary>
    /// Gets or sets the connection string used to apply migrations.
    /// </summary>
    /// <remarks>
    /// This is the ONE connection in the system that goes DIRECTLY to PostgreSQL rather than
    /// through PgBouncer. DDL needs a session it owns for the duration - advisory-free locking,
    /// SET LOCAL semantics, and a transaction that spans several statements - and a transaction
    /// pooler cannot promise any of that. It also needs CREATEROLE, which no running service has.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Gets or sets the login password for the app_delivery role.</summary>
    [Required(AllowEmptyStrings = false)]
    [MinLength(12)]
    public string AppDeliveryPassword { get; set; } = string.Empty;

    /// <summary>Gets or sets the login password for the app_download role.</summary>
    [Required(AllowEmptyStrings = false)]
    [MinLength(12)]
    public string AppDownloadPassword { get; set; } = string.Empty;

    /// <summary>Gets or sets the login password for the app_generation role.</summary>
    [Required(AllowEmptyStrings = false)]
    [MinLength(12)]
    public string AppGenerationPassword { get; set; } = string.Empty;

    /// <summary>Gets or sets the login password for the app_retention role.</summary>
    [Required(AllowEmptyStrings = false)]
    [MinLength(12)]
    public string AppRetentionPassword { get; set; } = string.Empty;

    /// <summary>Gets or sets the login password for the app_migrator DDL-owner role.</summary>
    [Required(AllowEmptyStrings = false)]
    [MinLength(12)]
    public string AppMigratorPassword { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether to create the database when it does not exist.
    /// </summary>
    public bool EnsureDatabaseExists { get; set; } = true;

    /// <summary>
    /// Gets or sets the lock timeout applied to every migration session, in seconds.
    /// </summary>
    /// <remarks>
    /// FAIL FAST RATHER THAN BLOCK. A migration waiting indefinitely on a lock does not just delay
    /// itself: it sits at the head of the lock queue, and every ordinary query that wants the same
    /// table queues behind it. The migration that was "just waiting" has taken the application down.
    /// </remarks>
    [Range(1, 300)]
    public int LockTimeoutSeconds { get; set; } = 3;

    /// <summary>
    /// Gets or sets the statement timeout applied to every migration session, in seconds.
    /// </summary>
    [Range(1, 3600)]
    public int StatementTimeoutSeconds { get; set; } = 30;
}
