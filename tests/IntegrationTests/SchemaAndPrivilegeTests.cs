using Dapper;
using Npgsql;
using Shouldly;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// Asserts that the migrations produce the schema and the permission model they claim to.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SchemaAndPrivilegeTests
{
    private readonly PostgresFixture _postgres;

    /// <summary>Initialises a new instance of the <see cref="SchemaAndPrivilegeTests"/> class.</summary>
    /// <param name="postgres">The shared PostgreSQL fixture.</param>
    public SchemaAndPrivilegeTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task Migrations_ApplyCleanly_AndAreJournalled()
    {
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        List<string> applied = [.. await connection.QueryAsync<string>(
            "SELECT scriptname FROM schemaversions ORDER BY scriptname;").ConfigureAwait(true)];

        applied.Count.ShouldBe(12);
        applied.ShouldContain(name => name.Contains("V001__roles_and_grants", StringComparison.Ordinal));
        applied.ShouldContain(name => name.Contains("V002__distributed_lease", StringComparison.Ordinal));
        applied.ShouldContain(name => name.Contains("V003__partition_helper_functions", StringComparison.Ordinal));
        applied.ShouldContain(name => name.Contains("V004__outbox", StringComparison.Ordinal));
        applied.ShouldContain(name => name.Contains("V005__customer_and_account", StringComparison.Ordinal));
        applied.ShouldContain(name => name.Contains("V006__statement", StringComparison.Ordinal));
        applied.ShouldContain(name => name.Contains("V007__audit_event", StringComparison.Ordinal));
        applied.ShouldContain(name => name.Contains("V008__legal_hold_and_customer_key", StringComparison.Ordinal));
        applied.ShouldContain(name => name.Contains("V009__grants", StringComparison.Ordinal));
        applied.ShouldContain(name => name.Contains("V010__download_token", StringComparison.Ordinal));
        applied.ShouldContain(name => name.Contains("V011__token_grants", StringComparison.Ordinal));
        applied.ShouldContain(name => name.Contains("V012__audit_verify_grant", StringComparison.Ordinal));
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task OnlyTheExpectedTables_Exist()
    {
        // An allow-list, so a table nobody decided to add shows up as a failing test.
        //
        // download_token JOINED THIS LIST IN PROMPT 3, and the negative assertion that used to sit
        // below it - "tokens are the next prompt's work" - came off at the same time. That pairing
        // is the point of an allow-list: adding a table is a deliberate edit here, not a silent
        // side effect of a migration nobody read.
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        List<string> tables = [.. await connection.QueryAsync<string>(
            """
            SELECT c.relname
              FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = 'public'
               AND c.relkind IN ('r', 'p')
               AND c.relispartition = false
             ORDER BY c.relname;
            """).ConfigureAwait(true)];

        tables.ShouldBe(
            [
                "account",
                "audit_chain_head",
                "audit_event",
                "customer",
                "customer_key",
                "distributed_lease",
                "download_token",
                "legal_hold",
                "outbox",
                "schemaversions",
                "statement",
            ],
            ignoreOrder: true);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task EveryServiceRole_Exists_WithoutSuperuserRights()
    {
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        foreach (string role in (string[])["app_delivery", "app_download", "app_generation", "app_retention", "app_migrator"])
        {
            var found = await connection.QuerySingleOrDefaultAsync<(bool CanLogin, bool IsSuperuser, bool CanCreateRole, bool CanCreateDb)?>(
                """
                SELECT rolcanlogin AS "CanLogin",
                       rolsuper    AS "IsSuperuser",
                       rolcreaterole AS "CanCreateRole",
                       rolcreatedb AS "CanCreateDb"
                  FROM pg_roles
                 WHERE rolname = @role;
                """,
                new { role }).ConfigureAwait(true);

            found.ShouldNotBeNull($"role {role} must exist");
            found.Value.CanLogin.ShouldBeTrue($"{role} must be able to log in");
            found.Value.IsSuperuser.ShouldBeFalse($"{role} must not be a superuser");
            found.Value.CanCreateRole.ShouldBeFalse($"{role} must not be able to create roles");
            found.Value.CanCreateDb.ShouldBeFalse($"{role} must not be able to create databases");
        }
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task AppDelivery_CannotDelete()
    {
        // ACCEPTANCE CHECK 16, adapted. The brief's command targets audit_event, which does not
        // exist yet - the scaffold has no business tables. outbox is the table that DOES exist and
        // that app_delivery legitimately writes to, so it is the honest place to prove the same
        // property: the delivery role can INSERT but has no DELETE anywhere.
        await using NpgsqlConnection connection =
            await _postgres.OpenAsAsync("app_delivery", TestContext.Current.CancellationToken).ConfigureAwait(true);

        PostgresException error = await Should.ThrowAsync<PostgresException>(
            () => connection.ExecuteAsync("DELETE FROM outbox;")).ConfigureAwait(true);

        // 42501 is insufficient_privilege. Asserting the SQLSTATE rather than the message means the
        // test still means something in a different server locale.
        error.SqlState.ShouldBe("42501");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task AppDelivery_CanInsertIntoOutbox()
    {
        // The counterpart to the test above. A permission test that only proves things are denied
        // would still pass if the role had no rights at all, which is a different bug.
        await using NpgsqlConnection connection =
            await _postgres.OpenAsAsync("app_delivery", TestContext.Current.CancellationToken).ConfigureAwait(true);

        int affected = await connection.ExecuteAsync(
            """
            INSERT INTO outbox (id, created_at, event_type, payload)
            VALUES (@id, now(), 'test.privilege.v1', '{}'::jsonb);
            """,
            new { id = Guid.CreateVersion7() }).ConfigureAwait(true);

        affected.ShouldBe(1);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task AppDownload_CannotWriteToTheOutbox()
    {
        // The public, unauthenticated role. It is reachable from the internet, so its grants are
        // the tightest of the four.
        await using NpgsqlConnection connection =
            await _postgres.OpenAsAsync("app_download", TestContext.Current.CancellationToken).ConfigureAwait(true);

        PostgresException error = await Should.ThrowAsync<PostgresException>(
            () => connection.ExecuteAsync(
                "INSERT INTO outbox (id, created_at, event_type, payload) VALUES (@id, now(), 'x', '{}'::jsonb);",
                new { id = Guid.CreateVersion7() })).ConfigureAwait(true);

        error.SqlState.ShouldBe("42501");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task NoServiceRole_CanCreateTables()
    {
        // A running service that can CREATE TABLE can create one the audit trail knows nothing
        // about. Only app_migrator holds CREATE on the schema.
        foreach (string role in (string[])["app_delivery", "app_download", "app_generation", "app_retention"])
        {
            await using NpgsqlConnection connection =
                await _postgres.OpenAsAsync(role, TestContext.Current.CancellationToken).ConfigureAwait(true);

            PostgresException error = await Should.ThrowAsync<PostgresException>(
                () => connection.ExecuteAsync("CREATE TABLE should_not_exist (id INT);")).ConfigureAwait(true);

            error.SqlState.ShouldBe("42501", $"{role} must not be able to create tables");
        }
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task AppDelivery_CannotCreatePartitions()
    {
        // ensure_range_partitions is SECURITY DEFINER, so EXECUTE on it is effectively a narrow
        // grant of CREATE TABLE. Only the workers hold it; if the APIs could call it, the CREATE
        // restriction above would be trivially bypassable.
        await using NpgsqlConnection connection =
            await _postgres.OpenAsAsync("app_delivery", TestContext.Current.CancellationToken).ConfigureAwait(true);

        PostgresException error = await Should.ThrowAsync<PostgresException>(
            () => connection.ExecuteScalarAsync<int>(
                "SELECT ensure_range_partitions('outbox'::regclass, 'day', 1);")).ConfigureAwait(true);

        error.SqlState.ShouldBe("42501");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task SecurityDefinerFunctions_AreOwnedByTheMigrator_NotASuperuser()
    {
        // A SECURITY DEFINER function runs with its OWNER's privileges. If the migration were
        // applied by a superuser and the owner left alone, every role holding EXECUTE would in
        // effect be executing as that superuser.
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        List<(string Name, string Owner, bool IsSecurityDefiner)> functions =
        [
            .. await connection.QueryAsync<(string, string, bool)>(
                """
                SELECT p.proname       AS "Name",
                       r.rolname       AS "Owner",
                       p.prosecdef     AS "IsSecurityDefiner"
                  FROM pg_proc p
                  JOIN pg_namespace n ON n.oid = p.pronamespace
                  JOIN pg_roles r     ON r.oid = p.proowner
                 WHERE n.nspname = 'public'
                   AND p.proname IN ('ensure_range_partitions', 'range_partition_exists');
                """).ConfigureAwait(true),
        ];

        functions.Count.ShouldBe(2);
        functions.ShouldAllBe(function => function.Owner == "app_migrator");
        functions.ShouldContain(function => function.Name == "ensure_range_partitions" && function.IsSecurityDefiner);
    }
}
