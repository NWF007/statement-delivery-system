using System.ComponentModel.DataAnnotations;

namespace StatementDelivery.Persistence.Connections;

/// <summary>
/// Connection configuration for one service's access to PostgreSQL.
/// </summary>
/// <remarks>
/// Bound with ValidateDataAnnotations().ValidateOnStart(), so a service with a missing or
/// malformed connection string fails at startup instead of starting half-configured and
/// failing on the first request.
/// </remarks>
public sealed class PostgresOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Postgres";

    /// <summary>
    /// Gets or sets the connection string for the primary. In every deployed environment this
    /// points at PgBouncer (port 6432), never at PostgreSQL (port 5432) directly.
    /// Only Db.Migrator connects to the server directly, because DDL needs a session it owns.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string PrimaryConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the connection string used for <see cref="ConnectionIntent.ReadEventual"/>.
    /// Null or empty means "no replica configured", in which case eventual reads fall back to
    /// the primary and a warning is logged at startup.
    /// </summary>
    public string? ReplicaConnectionString { get; set; }

    /// <summary>
    /// Gets or sets the maximum size of the client-side Npgsql pool, per process.
    /// </summary>
    /// <remarks>
    /// This is multiplied by the replica count, so it is the single number that decides whether
    /// the estate fits inside PgBouncer's pool budget. Six Delivery.Api replicas at 20 is 120
    /// client connections; four hundred Generation.Worker replicas at 20 would be eight thousand.
    /// See docs/adr/0008-pgbouncer-transaction-pooling.md before raising it.
    /// </remarks>
    [Range(1, 200)]
    public int MaxPoolSize { get; set; } = 20;

    /// <summary>Gets or sets the minimum pool size held open per process.</summary>
    [Range(0, 200)]
    public int MinPoolSize { get; set; }

    /// <summary>Gets or sets the TCP connect timeout, in seconds.</summary>
    [Range(1, 120)]
    public int ConnectTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Gets or sets the command timeout for <see cref="ConnectionIntent.Write"/>, in seconds.
    /// </summary>
    [Range(1, 3600)]
    public int WriteCommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets the command timeout for <see cref="ConnectionIntent.ReadStrong"/>, in seconds.
    /// </summary>
    /// <remarks>
    /// Deliberately short. This is the customer-facing delivery path: a query that has not
    /// answered in a few seconds has already failed the user, and holding the connection open
    /// only spreads the damage to everyone queued behind it.
    /// </remarks>
    [Range(1, 3600)]
    public int ReadStrongCommandTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Gets or sets the command timeout for <see cref="ConnectionIntent.ReadEventual"/>, in seconds.
    /// </summary>
    [Range(1, 3600)]
    public int ReadEventualCommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets the number of statements Npgsql may auto-prepare per connection.
    /// </summary>
    /// <remarks>
    /// Non-zero is only safe because PgBouncer runs with max_prepared_statements above zero.
    /// See the note in <see cref="NpgsqlConnectionFactory"/> before changing this.
    /// </remarks>
    [Range(0, 200)]
    public int MaxAutoPrepare { get; set; } = 20;

    /// <summary>
    /// Gets or sets the value reported as application_name. It appears in pg_stat_activity and
    /// in PgBouncer's SHOW POOLS output, which is how a runaway workload gets attributed to a
    /// service at 3am. Defaulted to the service name by AddPersistence.
    /// </summary>
    public string ApplicationName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether PostgreSQL error details (column values, constraint
    /// operands) are included in exception messages. Off outside Development: those details reach
    /// logs, and in this system a column value can be a customer identifier.
    /// </summary>
    public bool IncludeErrorDetail { get; set; }
}
