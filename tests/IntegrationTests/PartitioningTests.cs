using System.Diagnostics;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using StatementDelivery.Persistence.Partitioning;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// Range partitioning, its maintenance, and the proof that pruning actually happens.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed partial class PartitioningTests
{
    private readonly PostgresFixture _postgres;

    /// <summary>Initialises a new instance of the <see cref="PartitioningTests"/> class.</summary>
    /// <param name="postgres">The shared PostgreSQL fixture.</param>
    public PartitioningTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task V004_CreatesTheFirstWeekOfPartitions()
    {
        // A partitioned parent with no children accepts no writes at all: the first INSERT fails
        // with "no partition of relation outbox found for row". The migration therefore has to
        // create partitions, not merely the parent.
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        int partitions = await connection.ExecuteScalarAsync<int>(
            """
            SELECT count(*)::int
              FROM pg_inherits i
              JOIN pg_class c ON c.oid = i.inhrelid
             WHERE i.inhparent = 'outbox'::regclass;
            """).ConfigureAwait(true);

        partitions.ShouldBeGreaterThanOrEqualTo(8, "V004 creates today plus seven days ahead");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task EnsureRangePartitions_IsIdempotent()
    {
        // Maintenance runs on a schedule, forever. If a repeat run created duplicates or threw,
        // every interval after the first would be an incident.
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        int firstRun = await connection.ExecuteScalarAsync<int>(
            "SELECT ensure_range_partitions('outbox'::regclass, 'day', 7);").ConfigureAwait(true);
        int secondRun = await connection.ExecuteScalarAsync<int>(
            "SELECT ensure_range_partitions('outbox'::regclass, 'day', 7);").ConfigureAwait(true);

        secondRun.ShouldBe(0, "a second run over the same window must create nothing");
        firstRun.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task EnsureRangePartitions_RejectsAnUnsupportedGranularity()
    {
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        PostgresException error = await Should.ThrowAsync<PostgresException>(
            () => connection.ExecuteScalarAsync<int>(
                "SELECT ensure_range_partitions('outbox'::regclass, 'fortnight', 1);")).ConfigureAwait(true);

        error.MessageText.ShouldContain("granularity");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task RangePartitionExists_AnswersFromCatalogueBounds()
    {
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        bool tomorrow = await connection.ExecuteScalarAsync<bool>(
            "SELECT range_partition_exists('outbox'::regclass, 'day', @at);",
            new { at = DateTimeOffset.UtcNow.AddDays(1) }).ConfigureAwait(true);

        bool farFuture = await connection.ExecuteScalarAsync<bool>(
            "SELECT range_partition_exists('outbox'::regclass, 'day', @at);",
            new { at = DateTimeOffset.UtcNow.AddDays(400) }).ConfigureAwait(true);

        tomorrow.ShouldBeTrue("V004 pre-created a week of partitions");
        farFuture.ShouldBeFalse("nothing has created a partition four hundred days out");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task PartitionHealthCheck_IsHealthy_WhenTheNextPeriodIsCovered()
    {
        var options = new PartitionOptions { MaintenanceEnabled = false };
        options.Tables.Add(new PartitionedTableOptions { Table = "outbox", Granularity = "day", PeriodsAhead = 7 });

        var check = new PartitionHealthCheck(
            _postgres.ConnectionFactoryFor("app_delivery"),
            Options.Create(options),
            new PartitionMetrics(new DummyMeterFactory()),
            TimeProvider.System);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken).ConfigureAwait(true);

        result.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task PartitionHealthCheck_FailsReadiness_WhenThePartitionIsMissing()
    {
        // The check must FAIL, loudly, before the write does. A missing partition is an outage in
        // waiting: the insert that finds no home does not degrade, it errors.
        //
        // Probing a month ahead against a table with only a week of daily partitions is the
        // fastest honest way to produce that state without dropping anything.
        var options = new PartitionOptions { MaintenanceEnabled = false };
        options.Tables.Add(new PartitionedTableOptions { Table = "outbox", Granularity = "month", PeriodsAhead = 1 });

        var check = new PartitionHealthCheck(
            _postgres.ConnectionFactoryFor("app_delivery"),
            Options.Create(options),
            new PartitionMetrics(new DummyMeterFactory()),
            TimeProvider.System);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken).ConfigureAwait(true);

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description.ShouldNotBeNull().ShouldContain("outbox");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task BoundedQuery_PrunesPartitions_AndUnboundedQueryDoesNot()
    {
        // THE TEST THAT CATCHES THE DAY SOMEBODY REMOVES THE DATE CONSTRAINT.
        //
        // Partition pruning only works when the query filters on the partition key. The hottest
        // real query is "this customer's statements", keyed on customer_id, while the partition key
        // is time - a genuine tension, resolved in ADR-0007 by making a bounded date range part of
        // the API contract. This asserts that resolution actually pays off in the plan.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true);

        DateTimeOffset today = DateTimeOffset.UtcNow;

        for (int day = 0; day < 6; day++)
        {
            _ = await connection.ExecuteAsync(
                """
                INSERT INTO outbox (id, created_at, event_type, payload)
                VALUES (@id, @createdAt, 'test.pruning.v1', '{"k":1}'::jsonb);
                """,
                new { id = Guid.CreateVersion7(), createdAt = today.AddDays(day).AddHours(1) }).ConfigureAwait(true);
        }

        _ = await connection.ExecuteAsync("ANALYZE outbox;").ConfigureAwait(true);

        string boundedPlan = await ExplainAsync(
            connection,
            """
            EXPLAIN (ANALYZE, BUFFERS)
            SELECT id, created_at, event_type
              FROM outbox
             WHERE created_at >= @from
               AND created_at <  @to
             ORDER BY created_at, id
             LIMIT 100;
            """,
            new { from = today.AddHours(-1), to = today.AddDays(1) },
            cancellationToken).ConfigureAwait(true);

        string unboundedPlan = await ExplainAsync(
            connection,
            """
            EXPLAIN (ANALYZE, BUFFERS)
            SELECT id, created_at, event_type
              FROM outbox
             ORDER BY created_at, id
             LIMIT 100;
            """,
            parameters: null,
            cancellationToken).ConfigureAwait(true);

        int boundedPartitions = CountScannedPartitions(boundedPlan);
        int unboundedPartitions = CountScannedPartitions(unboundedPlan);

        boundedPartitions.ShouldBeGreaterThan(0, "the bounded query must still scan the partitions it needs");
        boundedPartitions.ShouldBeLessThan(
            unboundedPartitions,
            $"a bounded range must prune partitions.\n--- BOUNDED ---\n{boundedPlan}\n--- UNBOUNDED ---\n{unboundedPlan}");

        // Recorded so the plan can be pasted into docs/SCALE.md rather than retyped from memory.
        Trace.WriteLine(boundedPlan);
    }

    private static async Task<string> ExplainAsync(
        NpgsqlConnection connection,
        string sql,
        object? parameters,
        CancellationToken cancellationToken)
    {
        // EXPLAIN (ANALYZE) actually EXECUTES the statement, so it needs a timeout like any other
        // query. Generous, because it is a diagnostic rather than a request-path query.
        IEnumerable<string> lines = await connection.QueryAsync<string>(
            new CommandDefinition(sql, parameters, commandTimeout: 60, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Counts the distinct <c>outbox_YYYY_MM_DD</c> relations named in an EXPLAIN plan.
    /// </summary>
    private static int CountScannedPartitions(string plan) =>
        PartitionName().Matches(plan)
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .Count();

    [GeneratedRegex(@"outbox_\d{4}_\d{2}_\d{2}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PartitionName();
}

/// <summary>
/// Minimal <see cref="System.Diagnostics.Metrics.IMeterFactory"/> for tests that need metrics
/// wired but do not assert on them.
/// </summary>
internal sealed class DummyMeterFactory : System.Diagnostics.Metrics.IMeterFactory
{
    private readonly List<System.Diagnostics.Metrics.Meter> _meters = [];

    public System.Diagnostics.Metrics.Meter Create(System.Diagnostics.Metrics.MeterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var meter = new System.Diagnostics.Metrics.Meter(options);
        _meters.Add(meter);
        return meter;
    }

    public void Dispose()
    {
        foreach (System.Diagnostics.Metrics.Meter meter in _meters)
        {
            meter.Dispose();
        }

        _meters.Clear();
    }
}
