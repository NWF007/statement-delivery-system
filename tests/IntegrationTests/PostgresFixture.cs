using System.Reflection;
using Db.Migrator;
using DbUp;
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
    public NpgsqlConnectionFactory ConnectionFactoryFor(string role, int maxPoolSize = 5)
    {
        var options = new PostgresOptions
        {
            PrimaryConnectionString = ConnectionStringFor(role),
            ApplicationName = $"integration-tests:{role}",
            MaxPoolSize = maxPoolSize,
            MinPoolSize = 0,

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

        _container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("statements")
            .WithUsername("postgres")
            .WithPassword("postgres")

            // Matches the compose stack. Slow queries have to be visible somewhere, and finding out
            // in production that nothing was logging them is the wrong time.
            .WithCommand("-c", "log_min_duration_statement=200")
            .Build();

        await _container.StartAsync().ConfigureAwait(false);
        AdminConnectionString = _container.GetConnectionString();

        DatabaseUpgradeResult result = DeployChanges.To
            .PostgresqlDatabase(AdminConnectionString)
            .WithScriptsEmbeddedInAssembly(
                typeof(MigrationOptions).Assembly,
                name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .WithVariables(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["appDeliveryPassword"] = RolePassword,
                ["appDownloadPassword"] = RolePassword,
                ["appGenerationPassword"] = RolePassword,
                ["appRetentionPassword"] = RolePassword,
                ["appMigratorPassword"] = RolePassword,
            })
            .WithPreprocessor(new SessionGuardPreprocessor(3, 30))
            .WithTransactionPerScript()
            .LogToNowhere()
            .Build()
            .PerformUpgrade();

        if (!result.Successful)
        {
            throw new InvalidOperationException(
                $"Migrations failed on {result.ErrorScript?.Name ?? "(unknown)"}.",
                result.Error);
        }

        // A migrated but unanalysed database gives the planner no row estimates, and the partition
        // pruning assertions would be measuring the planner's ignorance rather than the schema.
        await using NpgsqlConnection connection = await OpenAdminAsync(CancellationToken.None).ConfigureAwait(false);
        await using NpgsqlCommand analyze = connection.CreateCommand();
        analyze.CommandText = "ANALYZE;";
        _ = await analyze.ExecuteNonQueryAsync().ConfigureAwait(false);

        Started = true;
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
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "postgres";
}
