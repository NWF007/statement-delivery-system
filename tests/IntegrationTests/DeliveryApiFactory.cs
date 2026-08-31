using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace IntegrationTests;

/// <summary>
/// Hosts the REAL Delivery.Api against the Testcontainers PostgreSQL instance.
/// </summary>
/// <remarks>
/// <para>
/// The real application: its real bearer authentication, its real routing, its real dependency
/// graph, its real repositories. A hand-assembled pipeline would prove only that the hand-assembled
/// pipeline works - and the failure mode these tests exist to catch is precisely a mismatch between
/// what the endpoint does and what everyone assumed it does.
/// </para>
/// <para>
/// Only configuration is overridden, and only to point the service at the test container.
/// </para>
/// <para>
/// The generic parameter names a type in Delivery.Api rather than <c>Program</c>, because this test
/// project now references two services and both expose a <c>Program</c> in the global namespace.
/// WebApplicationFactory only uses the parameter to find the assembly, so any public type from the
/// right one does the job without an <c>extern alias</c>.
/// </para>
/// </remarks>
public sealed class DeliveryApiFactory : WebApplicationFactory<Delivery.Api.Configuration.JwtOptions>
{
    private const string Issuer = "https://localhost/statement-delivery-test";
    private const string Audience = "statement-delivery-api";
    private const string SigningKey = "integration-test-signing-key-at-least-32-chars";

    private readonly string _connectionString;

    /// <summary>Initialises a new instance of the <see cref="DeliveryApiFactory"/> class.</summary>
    /// <param name="connectionString">The app_delivery connection string for the test container.</param>
    public DeliveryApiFactory(string connectionString) => _connectionString = connectionString;

    /// <summary>
    /// Mints a bearer token whose <c>sub</c> is the given customer.
    /// </summary>
    /// <remarks>
    /// The subject IS the authorisation decision. Handing tests a way to mint a token for an
    /// arbitrary customer is what makes the IDOR cases expressible: token for A, request for B.
    /// </remarks>
    /// <param name="customerId">The subject.</param>
    /// <returns>A signed bearer token.</returns>
    public static string TokenFor(Guid customerId)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity([new Claim("sub", customerId.ToString("D"))]),
            Expires = DateTime.UtcNow.AddMinutes(30),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
                SecurityAlgorithms.HmacSha256),
        };

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = true }.CreateToken(descriptor);
    }

    /// <summary>
    /// Mints a bearer token carrying the operator scope as well as the subject.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="TokenFor"/> so that "an ordinary customer token cannot read the
    /// audit trail" is expressible - which is the whole point of the staff policy.
    /// </remarks>
    /// <param name="customerId">The subject.</param>
    /// <returns>A signed bearer token with the audit.verify scope.</returns>
    public static string StaffTokenFor(Guid customerId)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", customerId.ToString("D")),

                // Space-delimited, as a real authorisation server issues it. A single-value claim
                // would pass a naive RequireClaim and hide the parsing this policy actually needs.
                new Claim("scope", "statements.read audit.verify"),
            ]),
            Expires = DateTime.UtcNow.AddMinutes(30),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
                SecurityAlgorithms.HmacSha256),
        };

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = true }.CreateToken(descriptor);
    }

    /// <summary>Mints a bearer token carrying the data-protection-officer scope.</summary>
    /// <remarks>
    /// Separate from <see cref="StaffTokenFor"/> deliberately: erasure sits ABOVE staff, and
    /// "a staff token cannot schedule an erasure" is a test this separation makes expressible.
    /// </remarks>
    /// <param name="customerId">The subject.</param>
    /// <returns>A signed bearer token with the erasure.execute scope.</returns>
    public static string DpoTokenFor(Guid customerId)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", customerId.ToString("D")),
                new Claim("scope", "statements.read erasure.execute"),
            ]),
            Expires = DateTime.UtcNow.AddMinutes(30),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
                SecurityAlgorithms.HmacSha256),
        };

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = true }.CreateToken(descriptor);
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment(Environments.Development);

        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Postgres:PrimaryConnectionString"] = _connectionString,
                ["Postgres:MaxPoolSize"] = "10",

                ["Jwt:Issuer"] = Issuer,
                ["Jwt:Audience"] = Audience,
                ["Jwt:DevelopmentSigningKey"] = SigningKey,
                ["Jwt:RequireHttpsMetadata"] = "false",

                ["Audit:ChainCount"] = "16",

                // Prompt 6: the API registers AddObjectStorage for the legal-hold admin surface,
                // and ObjectStorageOptions validates on start. The client is lazy - nothing here
                // contacts this endpoint unless a test drives the legal-hold endpoints, and those
                // tests substitute IStatementObjectAdmin or point at the MinIO fixture.
                ["ObjectStorage:BucketName"] = "statements-test",
                ["ObjectStorage:ServiceUrl"] = "http://localhost:9",
                ["ObjectStorage:AccessKey"] = "unused",
                ["ObjectStorage:SecretKey"] = "unused",

                // The generation worker owns partition maintenance; the API only verifies it.
                ["Partitioning:MaintenanceEnabled"] = "false",

                // No Redis in this fixture: an empty connection string selects the in-memory
                // distributed cache rather than failing.
                ["Cache:ConnectionString"] = string.Empty,

                ["DownloadLinks:GatewayBaseUrl"] = "http://gateway.test",

                // No OTLP endpoint, so no exporter is registered and the test does not spend its
                // life retrying a connection to a collector that is not there.
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = string.Empty,
            }));
    }
}
