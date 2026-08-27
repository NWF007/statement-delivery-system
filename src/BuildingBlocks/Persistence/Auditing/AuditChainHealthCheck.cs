using System.Globalization;
using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using StatementDelivery.Persistence.Connections;

namespace StatementDelivery.Persistence.Auditing;

/// <summary>
/// Readiness check asserting that every configured audit chain has a seeded head.
/// </summary>
/// <remarks>
/// <para>
/// Registered with the <c>ready</c> tag, never <c>live</c>. A missing chain head means audit
/// appends will fail - and because the audit write shares the business transaction, a failed append
/// fails the operation it was recording. This instance must stop taking traffic; restarting it
/// would not seed the head.
/// </para>
/// <para>
/// This is the check that catches the specific mistake of raising Audit:ChainCount without adding
/// the migration that seeds the extra heads. Without it, the symptom is customer-facing request
/// failures on roughly (new - old) / new of all traffic, with nothing pointing at configuration.
/// </para>
/// </remarks>
public sealed class AuditChainHealthCheck : IHealthCheck
{
    /// <summary>The registered name of this check.</summary>
    public const string Name = "audit-chains-ready";

    private const string CountHeadsSql = """
        SELECT count(*)::int
          FROM audit_chain_head
         WHERE chain_id >= 0
           AND chain_id < @chainCount;
        """;

    private readonly IDbConnectionFactory _connections;
    private readonly AuditOptions _options;

    /// <summary>Initialises a new instance of the <see cref="AuditChainHealthCheck"/> class.</summary>
    /// <param name="connections">Connection factory.</param>
    /// <param name="options">Audit options.</param>
    public AuditChainHealthCheck(IDbConnectionFactory connections, IOptions<AuditOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connections = connections;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using NpgsqlConnection connection =
                await _connections.OpenAsync(ConnectionIntent.ReadStrong, cancellationToken).ConfigureAwait(false);

            int seeded = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                CountHeadsSql,
                new { chainCount = _options.ChainCount },
                commandTimeout: _connections.CommandTimeoutSeconds(ConnectionIntent.ReadStrong),
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (seeded == _options.ChainCount)
            {
                return HealthCheckResult.Healthy(string.Create(
                    CultureInfo.InvariantCulture,
                    $"All {_options.ChainCount} audit chain head(s) are seeded."));
            }

            return HealthCheckResult.Unhealthy(string.Create(
                CultureInfo.InvariantCulture,
                $"Audit:ChainCount is {_options.ChainCount} but only {seeded} chain head(s) are seeded. Audit appends to the unseeded chains will fail, and a failed audit append rolls back the operation it was recording. Add a migration seeding the missing heads."));
        }
        catch (NpgsqlException ex)
        {
            return HealthCheckResult.Unhealthy("Could not read audit_chain_head.", ex);
        }
    }
}
