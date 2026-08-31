using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using StatementDelivery.Domain.Auditing;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Persistence.Auditing;
using StatementDelivery.Persistence.Bulk;
using StatementDelivery.Persistence.Connections;
using StatementDelivery.Persistence.Uow;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// ADR-0032's port-contract sweep: the awkward input shapes production can produce, exercised
/// against the real database. Convenient test inputs - a non-empty collection, one append per
/// transaction - systematically walk the branch production does not take.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PortShapeTests
{
    private readonly PostgresFixture _postgres;

    /// <summary>Initialises a new instance of the <see cref="PortShapeTests"/> class.</summary>
    /// <param name="postgres">The shared PostgreSQL fixture.</param>
    public PortShapeTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task BulkWriter_EmptySequence_WritesNothingAndSucceeds()
    {
        // The planner's re-plan pass can legitimately stream zero new accounts; an empty COPY
        // must be a cheap no-op, not an error.
        CancellationToken ct = TestContext.Current.CancellationToken;
        NpgsqlConnectionFactory factory = _postgres.ConnectionFactoryFor("app_generation");
        await using (factory.ConfigureAwait(true))
        {
            NpgsqlBinaryCopyWriter writer = CreateBulkWriter(factory);

            long written = await writer.WriteAsync("customer", Empty(), ct).ConfigureAwait(true);
            written.ShouldBe(0);
        }

        static async IAsyncEnumerable<TestCustomerRow> Empty()
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task BulkWriter_SourceFaultsMidStream_PersistsNothing()
    {
        // A COPY aborted mid-stream must leave ZERO rows - a half-written batch that partially
        // committed would make re-planning produce duplicates the ON CONFLICT clause cannot see
        // (they are real rows, not conflicts).
        CancellationToken ct = TestContext.Current.CancellationToken;
        var marker = Guid.CreateVersion7();

        NpgsqlConnectionFactory factory = _postgres.ConnectionFactoryFor("app_generation");
        await using (factory.ConfigureAwait(true))
        {
            NpgsqlBinaryCopyWriter writer = CreateBulkWriter(factory);

            _ = await Should.ThrowAsync<InvalidOperationException>(
                () => writer.WriteAsync("customer", FaultAfter(marker, 50), ct)).ConfigureAwait(true);
        }

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(ct).ConfigureAwait(true);
        (await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM customer WHERE external_ref LIKE @prefix;",
            new { prefix = marker.ToString("N") + "%" },
            commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(true))
            .ShouldBe(0, "an aborted COPY must not partially commit");

        static async IAsyncEnumerable<TestCustomerRow> FaultAfter(Guid marker, int rows)
        {
            for (int i = 0; i < rows; i++)
            {
                await Task.CompletedTask.ConfigureAwait(false);
                yield return new TestCustomerRow(
                    Guid.CreateVersion7(),
                    string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{marker:N}-{i:D4}"));
            }

            throw new InvalidOperationException("Simulated source failure mid-batch.");
        }
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task AuditWriter_TwoAppendsInOneTransaction_BothLandInOrder()
    {
        // The redeem path appends once per transaction by design, but nothing in IAuditWriter's
        // contract forbids two - and a future caller will try it. Both must land, consecutively,
        // and the chain must still verify: the head lock is held for the TRANSACTION, so the
        // second append must see the first's in-transaction head update.
        CancellationToken ct = TestContext.Current.CancellationToken;

        var customer = new CustomerId(Guid.CreateVersion7());
        var statementId = new StatementId(Guid.CreateVersion7());
        short chainId = AuditHashing.AssignChain(statementId.Value, customer.Value);

        NpgsqlConnectionFactory factory = _postgres.ConnectionFactoryFor("app_generation");
        await using (factory.ConfigureAwait(true))
        {
            var uow = new NpgsqlUnitOfWork(factory);
            var writer = new PostgresAuditWriter(Options.Create(new AuditOptions()), factory);

            (AuditReceipt first, AuditReceipt second) = await uow.ExecuteAsync(
                async (tx, token) =>
                {
                    AuditReceipt a = await writer.AppendAsync(
                        Entry(statementId, customer, AuditAction.StatementGenerated), tx, token).ConfigureAwait(false);
                    AuditReceipt b = await writer.AppendAsync(
                        Entry(statementId, customer, AuditAction.StatementStatusChanged), tx, token).ConfigureAwait(false);
                    return (a, b);
                },
                ct).ConfigureAwait(true);

            first.ChainId.ShouldBe(chainId);
            second.ChainId.ShouldBe(chainId);
            second.Seq.ShouldBe(first.Seq + 1, "the second append must build on the first, inside the same transaction");

            var verifier = new PostgresAuditVerifier(factory);
            ChainVerification verification = await verifier
                .VerifyChainAsync(chainId, 1, second.Seq, ct).ConfigureAwait(true);
            verification.Verified.ShouldBeTrue("two same-transaction appends must leave the chain intact");
        }
    }

    // ---------------------------------------------------------------------------------------------

    private static NpgsqlBinaryCopyWriter CreateBulkWriter(NpgsqlConnectionFactory factory)
    {
        // The production ctor resolves IBulkRowMapper<T> per row type from DI; mirror that
        // wiring so the test exercises the writer exactly as the planner receives it.
        ServiceProvider services = new ServiceCollection()
            .AddSingleton<IBulkRowMapper<TestCustomerRow>, CustomerRowMapperForTests>()
            .BuildServiceProvider();
        return new NpgsqlBinaryCopyWriter(factory, services, NullLogger<NpgsqlBinaryCopyWriter>.Instance);
    }

    private static AuditEntry Entry(StatementId statementId, CustomerId customer, string action) => new(
        new AuditEventId(Guid.CreateVersion7()),
        statementId,
        customer,
        ActorType.System,
        "port-shape-test",
        action,
        AuditOutcome.Success,
        DenialReasonCode: null,
        SourceIp: null,
        UserAgentHash: null,
        new Dictionary<string, object?>(StringComparer.Ordinal),
        DateTimeOffset.UtcNow);

    private sealed record TestCustomerRow(Guid Id, string ExternalRef);

    private sealed class CustomerRowMapperForTests : IBulkRowMapper<TestCustomerRow>
    {
        public IReadOnlyList<string> Columns => ["id", "external_ref", "status"];

        public async ValueTask WriteRowAsync(
            NpgsqlBinaryImporter importer, TestCustomerRow row, CancellationToken cancellationToken)
        {
            await importer.WriteAsync(row.Id, NpgsqlTypes.NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
            await importer.WriteAsync(row.ExternalRef, NpgsqlTypes.NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            await importer.WriteAsync("ACTIVE", NpgsqlTypes.NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
        }
    }
}
