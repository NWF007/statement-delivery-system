using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using StatementDelivery.Domain.Abstractions;
using StatementDelivery.Persistence.Auditing;
using StatementDelivery.Persistence.Bulk;
using StatementDelivery.Persistence.Connections;
using StatementDelivery.Persistence.Ids;
using StatementDelivery.Persistence.Leasing;
using StatementDelivery.Persistence.Partitioning;
using StatementDelivery.Persistence.Repositories;
using StatementDelivery.Persistence.Tokens;
using StatementDelivery.Persistence.Uow;

namespace StatementDelivery.Persistence;

/// <summary>
/// Registers the persistence building block.
/// </summary>
public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Adds connection routing, leasing, identifier generation, bulk writing and partition
    /// maintenance.
    /// </summary>
    /// <remarks>
    /// Every options type is bound with ValidateDataAnnotations().ValidateOnStart(), so a service
    /// with a missing connection string or an out-of-range pool size fails during startup instead
    /// of starting and failing on its first request. A half-configured service that passes its
    /// liveness probe is worse than one that never starts.
    /// </remarks>
    /// <param name="builder">The host application builder.</param>
    /// <param name="serviceName">
    /// Service name, reported as application_name. It shows up in pg_stat_activity and in
    /// PgBouncer's SHOW POOLS, which is how a runaway workload gets attributed to a service.
    /// </param>
    /// <returns>The builder, for chaining.</returns>
    public static IHostApplicationBuilder AddPersistence(this IHostApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        // FIRST, before any repository is registered. Dapper cannot bind DateOnly on its own, and
        // DateOnly is the RANGE partition key on `statement` - so without this every statement
        // lookup and every token redemption throws before reaching the database. See
        // Dapper/DateOnlyTypeHandlers.cs.
        Dapper.DapperConfiguration.EnsureConfigured();

        builder.Services
            .AddOptions<PostgresOptions>()
            .Bind(builder.Configuration.GetSection(PostgresOptions.SectionName))
            .Configure(options =>
            {
                if (string.IsNullOrWhiteSpace(options.ApplicationName))
                {
                    options.ApplicationName = serviceName;
                }

                // PostgreSQL error detail can contain column values, and in this system a column
                // value can identify a customer. Development only, never by accident.
                options.IncludeErrorDetail = builder.Environment.IsDevelopment();
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services
            .AddOptions<LeaseOptions>()
            .Bind(builder.Configuration.GetSection(LeaseOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services
            .AddOptions<AuditOptions>()
            .Bind(builder.Configuration.GetSection(AuditOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services
            .AddOptions<PartitionOptions>()
            .Bind(builder.Configuration.GetSection(PartitionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.TryAddSingletonTimeProvider();

        builder.Services.AddSingleton<NpgsqlConnectionFactory>();
        builder.Services.AddSingleton<IDbConnectionFactory>(sp => sp.GetRequiredService<NpgsqlConnectionFactory>());
        builder.Services.AddSingleton<ILeaseManager, PostgresLeaseManager>();
        builder.Services.AddSingleton<IIdGenerator, UuidV7Generator>();
        builder.Services.AddSingleton<IBulkWriter, NpgsqlBinaryCopyWriter>();
        builder.Services.AddSingleton<IDbConnectionFactoryTimeouts>(sp => sp.GetRequiredService<NpgsqlConnectionFactory>());
        builder.Services.AddSingleton<PartitionMetrics>();
        builder.Services.AddSingleton<PartitionHealthCheck>();
        builder.Services.AddSingleton<AuditChainHealthCheck>();

        // Repositories and the unit of work. Scoped would be equally correct; singleton is chosen
        // because none of them hold per-request state - they are stateless wrappers over SQL, and
        // the connection factory they share is already a singleton.
        builder.Services.AddSingleton<IStatementReadRepository, StatementReadRepository>();
        builder.Services.AddSingleton<IStatementWriteRepository, StatementWriteRepository>();
        builder.Services.AddSingleton<IUnitOfWork, NpgsqlUnitOfWork>();
        builder.Services.AddSingleton<IDownloadTokenRepository, DownloadTokenRepository>();

        builder.Services.AddSingleton<IAuditWriter, PostgresAuditWriter>();
        builder.Services.AddSingleton<IAuditVerifier, PostgresAuditVerifier>();

        // TODO(security): the no-op anchor does NOT close the gap it stands in for. Chain heads
        // live in the same database as the events, so a sufficiently privileged attacker can
        // rewrite both and produce a self-consistent forgery. Replace with an Object Lock-backed
        // implementation. See docs/adr/0010-sharded-audit-hash-chains.md.
        builder.Services.AddSingleton<IChainAnchor, NoOpChainAnchor>();

        builder.Services.AddHostedService<PartitionMaintenanceService>();

        // Readiness, never liveness. A missing partition means this instance should stop taking
        // traffic; restarting the process would not create the partition and would only make the
        // outage noisier. See ServiceDefaults for the live/ready split.
        builder.Services
            .AddHealthChecks()
            .AddCheck<PartitionHealthCheck>(
                PartitionHealthCheck.Name,
                HealthStatus.Unhealthy,
                tags: ["ready", "db"])
            .AddCheck<AuditChainHealthCheck>(
                AuditChainHealthCheck.Name,
                HealthStatus.Unhealthy,
                tags: ["ready", "db", "audit"]);

        return builder;
    }

    private static void TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }
}
