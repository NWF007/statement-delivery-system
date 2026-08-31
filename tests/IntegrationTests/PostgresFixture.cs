using System.Reflection;
using Db.Migrator;
using DbUp;
using DbUp.Builder;
using DbUp.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using StatementDelivery.Persistence.Connections;
using Testcontainers.PostgreSql;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// A real PostgreSQL 17 container with the repository's own migrations applied to it.
/// </summary>
/// <remarks>
/// The migrations run through the actual <c>Db.Migrator</c> assembly, its actual embedded scripts,
/// and its actual <see cref="SessionGuardPreprocessor"/>. A fixture that applied a hand-written
/// schema would test a schema nothing ships.
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>Password used for every application role in the container.</summary>
    /// <remarks>Throwaway, and it lives for the duration of one test run in one container.</remarks>
    public const string RolePassword = "integration-test-password";

    private PostgreSqlContainer? _container;

    /// <summary>Gets the superuser connection string.</summary>
    public string AdminConnectionString { get; private set; } = string.Empty;

    /// <summary>Gets a value indicating whether the container started.</summary>
    public bool Started { get; private set; }

    /// <summary>Builds a connection string for one of the least-privilege application roles.</summary>
    /// <param name="role">The role name, for example <c>app_delivery</c>.</param>
    /// <returns>A connection string authenticating as that role.</returns>
    public string ConnectionStringFor(string role)
    {
        var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString)
        {
            Username = role,
            Password = RolePassword,
        };

        return builder.ConnectionString;
    }

    /// <summary>Opens a connection as the given role.</summary>
    /// <param name="role">The role name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An open connection.</returns>
    public async Task<NpgsqlConnection> OpenAsAsync(string role, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(ConnectionStringFor(role));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>Opens a superuser connection.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An open connection.</returns>
    public async Task<NpgsqlConnection> OpenAdminAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>Creates a connection factory bound to one application role.</summary>
    /// <param name="role">The role name.</param>
    /// <returns>A connection factory.</returns>
    /// <param name="maxPoolSize">
    /// Client pool size. Raised for the concurrency tests, which run more writers than the default
    /// pool would admit - and a connection-pool timeout would look exactly like the lock contention
    /// those tests exist to measure.
    /// </param>
    public NpgsqlConnectionFactory ConnectionFactoryFor(
        string role, int maxPoolSize = 5, int writeTimeoutSeconds = 30, int connectTimeoutSeconds = 5)
    {
        var options = new PostgresOptions
        {
            PrimaryConnectionString = ConnectionStringFor(role),
            ApplicationName = $"integration-tests:{role}",
            MaxPoolSize = maxPoolSize,
            MinPoolSize = 0,
            WriteCommandTimeoutSeconds = writeTimeoutSeconds,
            ConnectTimeoutSeconds = connectTimeoutSeconds,

            // Auto-prepare off in tests. These connect directly rather than through PgBouncer, and
            // several tests deliberately reuse the same statement text against different roles.
            MaxAutoPrepare = 0,
        };

        return new NpgsqlConnectionFactory(
            Options.Create(options),
            NullLoggerFactory.Instance,
            NullLogger<NpgsqlConnectionFactory>.Instance);
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        // The DateOnly handlers normally register when a host wires Persistence into DI - but a
        // FILTERED run (the CI concurrency loop runs one test twenty times in its own process)
        // can reach direct-Dapper seeding before any host exists, and the first DateOnly
        // parameter throws. The fixture is every integration test's chokepoint, so it registers
        // them unconditionally.
        StatementDelivery.Persistence.Dapper.DapperConfiguration.EnsureConfigured();

        _container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("statements")
            .WithUsername("postgres")
            .WithPassword("postgres")

            // Matches the compose stack. Slow queries have to be visible somewhere, and finding out
            // in production that nothing was logging them is the wrong time.
            .WithCommand("-c", "log_min_duration_statement=200")

            // The container has no PgBouncer and InitializeAsync lifts the per-role caps, so the
            // global ceiling is the only limit left - and the default 100 is not enough for the
            // claim-contention tests' fifty direct connections running beside other parallel
            // collections. Production never sees this shape; the pooler holds the fleet to a few
            // dozen backends (ADR-0008).
            .WithCommand("-c", "max_connections=300")
            .Build();

        await _container.StartAsync().ConfigureAwait(false);
        AdminConnectionString = _container.GetConnectionString();

        Migrate(AdminConnectionString);

        // A migrated but unanalysed database gives the planner no row estimates, and the partition
        // pruning assertions would be measuring the planner's ignorance rather than the schema.
        await using NpgsqlConnection connection = await OpenAdminAsync(CancellationToken.None).ConfigureAwait(false);
        await using NpgsqlCommand analyze = connection.CreateCommand();
        analyze.CommandText = "ANALYZE;";
        _ = await analyze.ExecuteNonQueryAsync().ConfigureAwait(false);

        Started = true;
    }

    /// <summary>
    /// Builds a second, SEPARATELY MIGRATED database that stands in for a lagging read replica.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A real streaming replica cannot be paused mid-test, so lag is simulated the only way that is
    /// both deterministic and honest: a database with the IDENTICAL SCHEMA and none of the rows.
    /// Every read routed to it returns nothing, which is exactly what a replica arbitrarily far
    /// behind the primary returns, and it is the worst case the routing has to survive.
    /// </para>
    /// <para>
    /// Migrated through <see cref="Migrate"/>, the same routine the primary uses, so the stand-in
    /// cannot drift into a shape the production migrator would never produce.
    /// </para>
    /// </remarks>
    /// <param name="databaseName">Name for the new database. Must be a plain identifier.</param>
    /// <param name="role">The application role the caller will connect as.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A connection string for <paramref name="role"/> against the lagging database.</returns>
    public async Task<string> CreateLaggingReplicaAsync(
        string databaseName,
        string role,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        if (!databaseName.All(static c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            throw new ArgumentException("Database name must be a plain identifier.", nameof(databaseName));
        }

        await using (NpgsqlConnection admin = await OpenAdminAsync(cancellationToken).ConfigureAwait(false))
        {
            await using NpgsqlCommand create = admin.CreateCommand();

            // CREATE DATABASE cannot be parameterised, hence the identifier check above.
            create.CommandText = $"CREATE DATABASE {databaseName};";
            _ = await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var adminToReplica = new NpgsqlConnectionStringBuilder(AdminConnectionString)
        {
            Database = databaseName,
        };

        Migrate(adminToReplica.ConnectionString);

        return new NpgsqlConnectionStringBuilder(adminToReplica.ConnectionString)
        {
            Username = role,
            Password = RolePassword,
        }.ConnectionString;
    }

    /// <summary>
    /// Applies every migration to one database, in two passes.
    /// </summary>
    /// <remarks>
    /// TWO PASSES, MIRRORING Db.Migrator/Program.cs. Scripts named <c>*.notx.sql</c> run outside a
    /// transaction, because <c>CREATE INDEX CONCURRENTLY</c> cannot run inside one.
    /// <para>
    /// This fixture MUST match the real runner. A test database built by a different procedure from
    /// the production one is a test database that proves nothing about production - and this
    /// particular divergence would not be subtle: running V014 inside a transaction fails outright,
    /// so every integration test would go red at once with an error about CONCURRENTLY that points
    /// nowhere near the fixture.
    /// </para>
    /// </remarks>
    /// <param name="connectionString">An administrative connection string for the target database.</param>
    private static void Migrate(string connectionString)
    {
        MigrateCore(connectionString);

        // V001 caps connections per role (app_generation at 40) so that a service bypassing
        // PgBouncer fails loudly instead of exhausting backends. These tests ARE that bypass, on
        // purpose: no pooler in the container, xUnit collections in parallel, and the
        // claim-contention tests alone open fifty direct connections as one role. The caps stay
        // in the schema the compose stack and production run under. The uncap lives HERE, after
        // EVERY migration run, because roles are CLUSTER-wide: building the replica database
        // re-runs V001 and silently re-capped them mid-suite the moment the replica-lag test
        // got far enough to build its replica.
        using var uncapConnection = new NpgsqlConnection(connectionString);
        uncapConnection.Open();
        using NpgsqlCommand uncap = uncapConnection.CreateCommand();
        uncap.CommandText = """
            ALTER ROLE app_delivery   CONNECTION LIMIT -1;
            ALTER ROLE app_download   CONNECTION LIMIT -1;
            ALTER ROLE app_generation CONNECTION LIMIT -1;
            ALTER ROLE app_retention  CONNECTION LIMIT -1;
            """;
        _ = uncap.ExecuteNonQuery();
    }

    private static void MigrateCore(string connectionString)
    {
        foreach (bool nonTransactional in (bool[])[false, true])
        {
            UpgradeEngineBuilder engine = DeployChanges.To
                .PostgresqlDatabase(connectionString)
                .WithScriptsEmbeddedInAssembly(
                    typeof(MigrationOptions).Assembly,
                    name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(".notx.sql", StringComparison.OrdinalIgnoreCase) == nonTransactional)
                .WithVariables(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["appDeliveryPassword"] = RolePassword,
                    ["appDownloadPassword"] = RolePassword,
                    ["appGenerationPassword"] = RolePassword,
                    ["appRetentionPassword"] = RolePassword,
                    ["appMigratorPassword"] = RolePassword,
                })
                .WithPreprocessor(new SessionGuardPreprocessor(3, nonTransactional ? 0 : 30))
                .LogToNowhere();

            DatabaseUpgradeResult result =
                (nonTransactional ? engine.WithoutTransaction() : engine.WithTransactionPerScript())
                .Build()
                .PerformUpgrade();

            if (!result.Successful)
            {
                throw new InvalidOperationException(
                    $"Migrations failed on {result.ErrorScript?.Name ?? "(unknown)"}.",
                    result.Error);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();

        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Shares one migrated PostgreSQL container across every test in the collection.
/// </summary>
/// <remarks>
/// One container, not one per class. Starting PostgreSQL and applying migrations costs several
/// seconds; paying that per test class is how an integration suite becomes something people skip.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<MinioFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "postgres";
}
