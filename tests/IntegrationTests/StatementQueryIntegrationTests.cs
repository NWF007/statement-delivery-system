using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using StatementDelivery.Domain.Auditing;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Domain.Statements;
using StatementDelivery.Domain.ValueObjects;
using StatementDelivery.Persistence.Auditing;
using StatementDelivery.Persistence.Connections;
using StatementDelivery.Persistence.Repositories;
using StatementDelivery.Persistence.Uow;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// The statement read path against real PostgreSQL: pruning, keyset stability, ownership, and the
/// audit-or-nothing rule.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed partial class StatementQueryIntegrationTests
{
    private readonly PostgresFixture _postgres;

    /// <summary>Initialises a new instance of the <see cref="StatementQueryIntegrationTests"/> class.</summary>
    /// <param name="postgres">The shared PostgreSQL fixture.</param>
    public StatementQueryIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

    /// <summary>Creates a customer with one account and returns both identifiers.</summary>
    private async Task<(CustomerId Customer, AccountId Account)> SeedCustomerAsync(CancellationToken cancellationToken)
    {
        var customer = new CustomerId(Guid.CreateVersion7());
        var account = new AccountId(Guid.CreateVersion7());

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(false);

        _ = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO customer (id, external_ref, status) VALUES (@customer, @ref, 'ACTIVE');
            INSERT INTO account (id, customer_id, account_number_masked, product_type, status, opened_at)
            VALUES (@account, @customer, '****1234', 'CURRENT', 'ACTIVE', now());
            """,
            new { customer = customer.Value, account = account.Value, @ref = customer.Value.ToString("N") },
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return (customer, account);
    }

    /// <summary>Inserts one AVAILABLE statement for a month offset from today.</summary>
    private async Task<StatementId> SeedStatementAsync(
        CustomerId customer,
        AccountId account,
        int monthOffset,
        CancellationToken cancellationToken,
        int version = 1)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        StatementPeriod period = StatementPeriod.ForMonth(
            today.AddMonths(monthOffset).Year,
            today.AddMonths(monthOffset).Month);

        var id = new StatementId(Guid.CreateVersion7());

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(false);

        _ = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO statement (
                id, account_id, customer_id, period_start, period_end, version, status,
                storage_key, size_bytes, content_sha256,
                wrapped_dek, dek_algorithm, kek_id, retain_until, generated_at)
            VALUES (
                @id, @account, @customer, @start, @end, @version, 'AVAILABLE',
                @key, 42000,

                -- An AVAILABLE row must now be both READABLE (V013: key material) and VERIFIABLE
                -- (V015: a 32-byte digest). This seeder predates both and would fail at the INSERT.
                decode(repeat('ab', 32), 'hex'),
                decode(repeat('cd', 61), 'hex'), 'AES-256-GCM', 'kek-test', @retain, now());
            """,
            new
            {
                id = id.Value,
                account = account.Value,
                customer = customer.Value,
                start = period.Start,
                end = period.End,
                version,
                key = StatementDelivery.Domain.Statements.StorageKeyScheme.KeyFor(
                    id, account, period, 1),
                retain = RetentionPolicy.Default.RetainUntil(period),
            },
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return id;
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task WithDateRange_PrunesPartitions()
    {
        // THE TEST THAT CATCHES THE DAY SOMEBODY MAKES THE DATE RANGE OPTIONAL.
        //
        // Partition pruning only happens when the query filters on the partition key. The hottest
        // query is keyed on customer_id while the partition key is time, so the range in the API
        // contract is the ONLY thing that makes this query prune. Remove it and the build goes red
        // here rather than the p99 going red in production.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (CustomerId customer, AccountId account) = await SeedCustomerAsync(cancellationToken).ConfigureAwait(true);

        // V006 creates partitions for the current month plus three either side.
        for (int offset = -3; offset <= 3; offset++)
        {
            _ = await SeedStatementAsync(customer, account, offset, cancellationToken).ConfigureAwait(true);
        }

        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true);
        _ = await connection.ExecuteAsync(new CommandDefinition(
            "ANALYZE statement;", commandTimeout: 120, cancellationToken: cancellationToken)).ConfigureAwait(true);

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        DateOnly thisMonth = new(today.Year, today.Month, 1);

        string bounded = await ExplainAsync(
            connection,
            """
            EXPLAIN (ANALYZE, BUFFERS)
            SELECT id, period_start FROM statement
             WHERE customer_id = @customer
               AND status = 'AVAILABLE'
               AND period_start >= @from
               AND period_start <  @to
             ORDER BY period_start DESC, id DESC
             LIMIT 50;
            """,
            new { customer = customer.Value, from = thisMonth, to = thisMonth.AddMonths(1) },
            cancellationToken).ConfigureAwait(true);

        string unbounded = await ExplainAsync(
            connection,
            """
            EXPLAIN (ANALYZE, BUFFERS)
            SELECT id, period_start FROM statement
             WHERE customer_id = @customer
               AND status = 'AVAILABLE'
             ORDER BY period_start DESC, id DESC
             LIMIT 50;
            """,
            new { customer = customer.Value },
            cancellationToken).ConfigureAwait(true);

        int boundedPartitions = CountPartitions(bounded);
        int unboundedPartitions = CountPartitions(unbounded);

        boundedPartitions.ShouldBeGreaterThan(0, "the bounded query must still scan the partition it needs");
        boundedPartitions.ShouldBeLessThan(
            unboundedPartitions,
            $"a bounded range must prune.\n--- BOUNDED ---\n{bounded}\n--- UNBOUNDED ---\n{unbounded}");

        // Recorded so the plan can be pasted into docs/SCALE.md rather than retyped.
        System.Diagnostics.Trace.WriteLine(bounded);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task CursorPagination_IsStableUnderConcurrentInserts()
    {
        // The reason keyset exists. With OFFSET, a row inserted while the client is paging shifts
        // every subsequent page by one - so a row is either shown twice or skipped entirely, and
        // neither is visible to the client. A keyset cursor is anchored to a value, not a position.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (CustomerId customer, AccountId account) = await SeedCustomerAsync(cancellationToken).ConfigureAwait(true);

        for (int offset = -3; offset <= 3; offset++)
        {
            _ = await SeedStatementAsync(customer, account, offset, cancellationToken).ConfigureAwait(true);
        }

        var repository = new StatementReadRepository(_postgres.ConnectionFactoryFor("app_delivery"));
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        DateOnly from = new DateOnly(today.Year, today.Month, 1).AddMonths(-6);
        DateOnly to = new DateOnly(today.Year, today.Month, 1).AddMonths(6);

        var seen = new List<Guid>();
        Cursor? cursor = null;
        int pages = 0;

        do
        {
            CursorPage<Statement> page = await repository
                .ListForCustomerAsync(customer, from, to, cursor, 2, cancellationToken)
                .ConfigureAwait(true);

            seen.AddRange(page.Items.Select(s => s.Id.Value));

            // Insert a NEW statement between pages, in a version slot that does not collide.
            if (page.HasMore)
            {
                _ = await SeedStatementAsync(customer, account, -3 + (pages % 7), cancellationToken, version: pages + 2)
                    .ConfigureAwait(true);
            }

            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 20);

        seen.Distinct().Count().ShouldBe(seen.Count, "keyset pagination must never return a row twice");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Ownership_IsAPredicate_NotAPostCheck()
    {
        // The repository puts customer_id in the WHERE clause, so a statement belonging to somebody
        // else is not "found and rejected" - it is not found at all. There is no code path where a
        // row is loaded and then compared, which is the path a missing `if` turns into a breach.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        (CustomerId owner, AccountId account) = await SeedCustomerAsync(cancellationToken).ConfigureAwait(true);
        (CustomerId stranger, _) = await SeedCustomerAsync(cancellationToken).ConfigureAwait(true);

        StatementId id = await SeedStatementAsync(owner, account, 0, cancellationToken).ConfigureAwait(true);
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        DateOnly period = new(today.Year, today.Month, 1);

        var repository = new StatementReadRepository(_postgres.ConnectionFactoryFor("app_delivery"));

        (await repository.FindAsync(id, period, owner, cancellationToken).ConfigureAwait(true))
            .ShouldNotBeNull("the owner can read their own statement");

        (await repository.FindAsync(id, period, stranger, cancellationToken).ConfigureAwait(true))
            .ShouldBeNull("another customer gets nothing back at all");

        CursorPage<Statement> strangerPage = await repository
            .ListForCustomerAsync(stranger, period, period.AddMonths(1), null, 50, cancellationToken)
            .ConfigureAwait(true);

        strangerPage.Items.ShouldBeEmpty();
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task AppDeliveryRole_CannotDeleteFromStatement()
    {
        // ACCEPTANCE CHECK 22. The customer-facing API can read statements and nothing else, so a
        // compromised delivery credential can leak data but cannot destroy it.
        await using NpgsqlConnection connection =
            await _postgres.OpenAsAsync("app_delivery", TestContext.Current.CancellationToken).ConfigureAwait(true);

        PostgresException delete = await Should.ThrowAsync<PostgresException>(
            () => connection.ExecuteAsync("DELETE FROM statement;")).ConfigureAwait(true);
        delete.SqlState.ShouldBe("42501");

        PostgresException update = await Should.ThrowAsync<PostgresException>(
            () => connection.ExecuteAsync("UPDATE statement SET status = 'PURGED';")).ConfigureAwait(true);
        update.SqlState.ShouldBe("42501");

        PostgresException insert = await Should.ThrowAsync<PostgresException>(
            () => connection.ExecuteAsync(
                "INSERT INTO statement (id, account_id, customer_id, period_start, period_end, retain_until) "
                + "VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), '2026-08-01', '2026-08-31', '2033-08-31');"))
            .ConfigureAwait(true);
        insert.SqlState.ShouldBe("42501");
    }

    // =============================================================================================
    //  UnitOfWork_AuditFailure_RollsBackBusinessOperation LIVED HERE AND WAS VACUOUS.
    //
    //  It opened its own transaction and called IAuditWriter.AppendAsync directly - a shape no
    //  production code used, because every write path committed its business change in one
    //  transaction and appended the audit record in another. So it proved that NpgsqlUnitOfWork
    //  rolls back when a delegate throws, which is trivially true, and it did NOT prove the rule
    //  stated in its own first line: AN OPERATION WITH NO AUDIT RECORD MUST BE IMPOSSIBLE.
    //
    //  It passed for three prompts while that rule was false of every shipped endpoint.
    //
    //  REPLACED BY RedemptionInvariantTests.Redemption_WhenAuditWriteFails_DoesNotConsumeToken,
    //  which drives the real HTTP redemption path with a deliberately broken audit writer and
    //  asserts the token survives. That test fails against the code as it was shipped; this one
    //  could not. Deleted rather than kept alongside it, because a test that cannot fail for the
    //  reason it names is worse than no test - it answers the question and answers it wrongly.
    // =============================================================================================

    private static async Task<string> ExplainAsync(
        NpgsqlConnection connection,
        string sql,
        object parameters,
        CancellationToken cancellationToken)
    {
        IEnumerable<string> lines = await connection.QueryAsync<string>(new CommandDefinition(
            sql, parameters, commandTimeout: 60, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return string.Join('\n', lines);
    }

    private static int CountPartitions(string plan) =>
        PartitionName().Matches(plan).Select(m => m.Value).Distinct(StringComparer.Ordinal).Count();

    [GeneratedRegex(@"statement_\d{4}_\d{2}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PartitionName();
}
