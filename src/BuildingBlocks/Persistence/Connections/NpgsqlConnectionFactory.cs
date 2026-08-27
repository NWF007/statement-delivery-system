using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using StatementDelivery.Persistence.Repositories;

namespace StatementDelivery.Persistence.Connections;

// =============================================================================================
//  READ THIS BEFORE WRITING ANY SQL IN THIS REPOSITORY.
//
//  Every service in this system reaches PostgreSQL through PgBouncer in TRANSACTION POOLING
//  mode. That is not a tuning preference, it is a hard requirement: the generation fleet alone
//  wants 400 replicas x 5 connections = 2,000 backends, and PostgreSQL forks a process per
//  connection with a practical ceiling in the low hundreds.
//  See docs/adr/0008-pgbouncer-transaction-pooling.md.
//
//  Transaction pooling means a connection is handed back to the pool at COMMIT, and the next
//  statement you issue may land on a completely different PostgreSQL backend. The following
//  features silently do the wrong thing as a result. None of them throw a clear error. All of
//  them pass in development, where there is only one backend, and fail intermittently under
//  production load, which is the worst possible failure mode.
//
//  ---------------------------------------------------------------------------------------
//  FEATURE                       WHY IT BREAKS                          USE INSTEAD
//  ---------------------------------------------------------------------------------------
//  pg_advisory_lock              Acquired on one backend, released on   The distributed_lease
//  (session advisory locks)      another. Leaks until that backend      table (ILeaseManager),
//                                dies, and nothing reports it.          or pg_advisory_xact_lock,
//                                                                       which is transaction-scoped.
//
//  SET at connect time           Applies to whichever backend happened  SET LOCAL, inside the
//                                to serve the connect. Later statements transaction that needs it.
//                                run on a backend that never saw it.
//
//  LISTEN / NOTIFY               The subscription lives on a backend    Polling, or a real broker.
//                                you no longer own. Notifications are   The outbox exists for this.
//                                delivered to nobody.
//
//  WITH HOLD cursors             Do not survive the transaction that    Keyset pagination
//                                created them.                          (see Paging/Cursor.cs).
//
//  Temp tables across            The second statement may execute on a  CTEs, or a real table.
//  statements                    backend where the temp table does not
//                                exist.
//  ---------------------------------------------------------------------------------------
//
//  PREPARED STATEMENTS - which of the two options is in force here:
//  PgBouncer 1.21+ supports protocol-level prepared statements in transaction mode by tracking
//  them per client and re-preparing on whichever backend it routes to. deploy/pgbouncer/pgbouncer.ini
//  therefore sets max_prepared_statements above zero, and Npgsql auto-prepare is left ENABLED
//  (PostgresOptions.MaxAutoPrepare, default 20). That is the chosen option, because the delivery
//  hot path is a small number of statements executed constantly and re-planning each one is pure
//  waste. If you ever deploy against a pooler that cannot do this - an older PgBouncer, or a
//  managed pooler with the feature disabled - the fallback is to set MaxAutoPrepare to 0 rather
//  than to stop using the pooler. Setting exactly one of the two is mandatory; setting neither
//  produces "prepared statement S_1 already exists" errors under load and nowhere else.
// =============================================================================================

/// <summary>
/// Builds and owns one <see cref="NpgsqlDataSource"/> per <see cref="ConnectionIntent"/>.
/// </summary>
/// <remarks>
/// <para>
/// One data source per intent rather than one per server, because the intents differ by more
/// than their destination: each carries its own command-timeout budget, baked into the
/// connection string so that a command created from the connection inherits it without the
/// call site having to remember.
/// </para>
/// <para>
/// Registered as a singleton. <see cref="NpgsqlDataSource"/> owns the client-side pool, so
/// constructing one per request would defeat pooling entirely.
/// </para>
/// </remarks>
public sealed partial class NpgsqlConnectionFactory : IDbConnectionFactory, IDbConnectionFactoryTimeouts, IAsyncDisposable
{
    private readonly NpgsqlDataSource _write;
    private readonly NpgsqlDataSource _readStrong;
    private readonly NpgsqlDataSource _readEventual;
    private readonly PostgresOptions _options;

    /// <summary>
    /// Initialises the factory and eagerly builds every data source, so a malformed connection
    /// string surfaces at startup rather than on the first request that needs it.
    /// </summary>
    /// <param name="options">Validated connection options.</param>
    /// <param name="loggerFactory">Logger factory handed to Npgsql for command logging.</param>
    /// <param name="logger">Logger for the startup routing summary.</param>
    public NpgsqlConnectionFactory(
        IOptions<PostgresOptions> options,
        ILoggerFactory loggerFactory,
        ILogger<NpgsqlConnectionFactory> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;

        _write = Build(_options.PrimaryConnectionString, _options, _options.WriteCommandTimeoutSeconds, "write", loggerFactory);
        _readStrong = Build(_options.PrimaryConnectionString, _options, _options.ReadStrongCommandTimeoutSeconds, "read-strong", loggerFactory);

        ReadEventualUsesPrimary = string.IsNullOrWhiteSpace(_options.ReplicaConnectionString);
        string eventualConnectionString = ReadEventualUsesPrimary
            ? _options.PrimaryConnectionString
            : _options.ReplicaConnectionString!;
        _readEventual = Build(eventualConnectionString, _options, _options.ReadEventualCommandTimeoutSeconds, "read-eventual", loggerFactory);

        if (ReadEventualUsesPrimary)
        {
            // Deliberately a warning, not a debug line. The fallback is correct but it means the
            // primary is absorbing read traffic it was not sized for, and a silent fallback is
            // how that gets discovered during an incident instead of at deploy time.
            LogReadEventualFallsBackToPrimary(logger);
        }
        else
        {
            LogReadEventualUsesReplica(logger);
        }
    }

    /// <summary>
    /// Gets a value indicating whether eventual reads are currently served by the primary
    /// because no replica is configured. Surfaced so a readiness check or a diagnostic endpoint
    /// can report the degraded routing rather than hiding it.
    /// </summary>
    public bool ReadEventualUsesPrimary { get; }

    /// <inheritdoc />
    public ValueTask<NpgsqlConnection> OpenAsync(
        ConnectionIntent intent,
        CancellationToken cancellationToken = default)
    {
        NpgsqlDataSource source = intent switch
        {
            ConnectionIntent.Write => _write,
            ConnectionIntent.ReadStrong => _readStrong,
            ConnectionIntent.ReadEventual => _readEventual,
            _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, "Unknown connection intent."),
        };

        return source.OpenConnectionAsync(cancellationToken);
    }

    /// <summary>
    /// Gets the write command timeout, for components handed a transaction rather than opening
    /// their own connection and so unable to inherit it from a connection string.
    /// </summary>
    public int WriteCommandTimeoutSeconds => _options.WriteCommandTimeoutSeconds;

    /// <inheritdoc />
    public int CommandTimeoutSeconds(ConnectionIntent intent) => intent switch
    {
        ConnectionIntent.Write => _options.WriteCommandTimeoutSeconds,
        ConnectionIntent.ReadStrong => _options.ReadStrongCommandTimeoutSeconds,
        ConnectionIntent.ReadEventual => _options.ReadEventualCommandTimeoutSeconds,
        _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, "Unknown connection intent."),
    };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _write.DisposeAsync().ConfigureAwait(false);
        await _readStrong.DisposeAsync().ConfigureAwait(false);
        await _readEventual.DisposeAsync().ConfigureAwait(false);
    }

    private static NpgsqlDataSource Build(
        string connectionString,
        PostgresOptions options,
        int commandTimeoutSeconds,
        string intentTag,
        ILoggerFactory loggerFactory)
    {
        var csb = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = options.MaxPoolSize,
            MinPoolSize = options.MinPoolSize,
            Timeout = options.ConnectTimeoutSeconds,
            CommandTimeout = commandTimeoutSeconds,
            MaxAutoPrepare = options.MaxAutoPrepare,
            IncludeErrorDetail = options.IncludeErrorDetail,
            Pooling = true,

            // Npgsql issues DISCARD ALL when returning a connection to its own pool. Behind a
            // transaction-mode pooler the server-side session is already reset at COMMIT, so the
            // reset is a wasted round trip on every single checkout.
            NoResetOnClose = true,

            // System.Transactions auto-enlistment assumes a session it owns for the life of the
            // ambient scope. Transaction pooling cannot honour that. Explicit transactions only.
            Enlist = false,

            ApplicationName = string.IsNullOrWhiteSpace(options.ApplicationName)
                ? intentTag
                : options.ApplicationName + ":" + intentTag,
        };

        var builder = new NpgsqlDataSourceBuilder(csb.ConnectionString);
        builder.UseLoggerFactory(loggerFactory);
        return builder.Build();
    }

    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Warning,
        Message = "ConnectionIntent.ReadEventual has no replica configured and is falling back to the primary. " +
                  "Eventual reads are being served by the write node. Set Postgres:ReplicaConnectionString to fix this.")]
    private static partial void LogReadEventualFallsBackToPrimary(ILogger logger);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "ConnectionIntent.ReadEventual is routed to a configured read replica.")]
    private static partial void LogReadEventualUsesReplica(ILogger logger);
}
