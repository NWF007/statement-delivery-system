using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Shouldly;
using StatementDelivery.Persistence.Bulk;
using StatementDelivery.Persistence.Connections;
using StatementDelivery.Persistence.Ids;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// The binary COPY bulk-insert path.
/// </summary>
/// <remarks>
/// Thirty million rows a month cannot go in one INSERT at a time. This is the benchmark the brief
/// calls for: it exists before the first caller does, because the shape of the persistence layer
/// depends on it and a bulk path retrofitted later tends to arrive as a second, parallel data
/// access stack.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class BulkWriterTests
{
    private readonly PostgresFixture _postgres;

    /// <summary>Initialises a new instance of the <see cref="BulkWriterTests"/> class.</summary>
    /// <param name="postgres">The shared PostgreSQL fixture.</param>
    public BulkWriterTests(PostgresFixture postgres) => _postgres = postgres;

    private NpgsqlBinaryCopyWriter CreateWriter()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IBulkRowMapper<TestOutboxRow>, TestOutboxRowMapper>();
        ServiceProvider provider = services.BuildServiceProvider();

        return new NpgsqlBinaryCopyWriter(
            _postgres.ConnectionFactoryFor("app_generation"),
            provider,
            NullLogger<NpgsqlBinaryCopyWriter>.Instance);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task WriteAsync_StreamsRowsThroughBinaryCopy()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        NpgsqlBinaryCopyWriter writer = CreateWriter();
        var ids = new UuidV7Generator();
        const int RowCount = 25_000;
        string marker = $"bulk.{Guid.CreateVersion7():N}";

        var stopwatch = Stopwatch.StartNew();
        long written = await writer.WriteAsync("outbox", GenerateAsync(RowCount, ids, marker), cancellationToken).ConfigureAwait(true);
        stopwatch.Stop();

        written.ShouldBe(RowCount);

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true);
        int stored = await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM outbox WHERE event_type = @marker;",
            new { marker }).ConfigureAwait(true);

        stored.ShouldBe(RowCount);

        double rowsPerSecond = RowCount / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        Trace.WriteLine($"binary COPY: {RowCount} rows in {stopwatch.ElapsedMilliseconds} ms ({rowsPerSecond:F0} rows/sec)");

        // Not a performance assertion - CI hardware varies far too much for that to be anything but
        // a flaky test. It is a smoke test that the COPY path is genuinely streaming rather than
        // degenerating into row-by-row inserts, which would be orders of magnitude slower than this.
        rowsPerSecond.ShouldBeGreaterThan(1_000);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task WriteAsync_RoutesRowsToTheCorrectPartitions()
    {
        // COPY into a partitioned parent routes per row. Worth asserting once: a silent failure
        // here would pile every row into one partition and quietly undo the whole design.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        NpgsqlBinaryCopyWriter writer = CreateWriter();
        var ids = new UuidV7Generator();
        string marker = $"routing.{Guid.CreateVersion7():N}";

        _ = await writer.WriteAsync("outbox", GenerateAcrossDaysAsync(ids, marker), cancellationToken).ConfigureAwait(true);

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true);
        int distinctPartitions = await connection.ExecuteScalarAsync<int>(
            "SELECT count(DISTINCT tableoid)::int FROM outbox WHERE event_type = @marker;",
            new { marker }).ConfigureAwait(true);

        distinctPartitions.ShouldBe(5, "one partition per distinct day written");
    }

    [Theory(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    [InlineData("outbox; DROP TABLE outbox")]
    [InlineData("Outbox")]
    [InlineData("public.outbox")]
    [InlineData("out box")]
    public async Task WriteAsync_RejectsAnythingThatIsNotAPlainIdentifier(string table)
    {
        // COPY takes no parameters, so the table name is interpolated into the statement. That
        // makes this the one place in the data layer where an identifier reaches SQL as text, and
        // the validation is the only thing standing in for a parameter.
        NpgsqlBinaryCopyWriter writer = CreateWriter();

        await Should.ThrowAsync<ArgumentException>(
            () => writer.WriteAsync(table, GenerateAsync(1, new UuidV7Generator(), "x"), TestContext.Current.CancellationToken))
            .ConfigureAwait(true);
    }

    private static async IAsyncEnumerable<TestOutboxRow> GenerateAsync(int count, UuidV7Generator ids, string marker)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        for (int i = 0; i < count; i++)
        {
            yield return new TestOutboxRow(ids.NewId(), now, marker);
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    private static async IAsyncEnumerable<TestOutboxRow> GenerateAcrossDaysAsync(UuidV7Generator ids, string marker)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        for (int day = 0; day < 5; day++)
        {
            for (int i = 0; i < 10; i++)
            {
                yield return new TestOutboxRow(ids.NewId(), now.AddDays(day).AddHours(2), marker);
                await Task.CompletedTask.ConfigureAwait(false);
            }
        }
    }
}

/// <summary>A minimal outbox row for the bulk-write benchmark.</summary>
/// <param name="Id">UUIDv7 identifier.</param>
/// <param name="CreatedAt">Partition key.</param>
/// <param name="EventType">Marker used to isolate one test run's rows from another's.</param>
public sealed record TestOutboxRow(Guid Id, DateTimeOffset CreatedAt, string EventType);

/// <summary>Binary COPY mapping for <see cref="TestOutboxRow"/>.</summary>
public sealed class TestOutboxRowMapper : IBulkRowMapper<TestOutboxRow>
{
    /// <inheritdoc />
    public IReadOnlyList<string> Columns { get; } = ["id", "created_at", "event_type", "payload"];

    /// <inheritdoc />
    public async ValueTask WriteRowAsync(NpgsqlBinaryImporter importer, TestOutboxRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentNullException.ThrowIfNull(row);

        await importer.WriteAsync(row.Id, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.CreatedAt, NpgsqlDbType.TimestampTz, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.EventType, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync("{}", NpgsqlDbType.Jsonb, cancellationToken).ConfigureAwait(false);
    }
}
