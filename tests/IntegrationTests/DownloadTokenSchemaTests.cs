using System.Globalization;
using Dapper;
using Npgsql;
using Shouldly;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// What the DATABASE enforces about download tokens, independently of the application.
/// </summary>
/// <remarks>
/// Every rule here is also enforced in C#. That duplication is the point: a bug in the domain layer
/// that minted a twelve-hour token would be caught by <c>ck_token_ttl</c> before it became a
/// long-lived bearer credential, and a compromised service that tried to mint tokens from the
/// gateway would be stopped by a missing INSERT grant. Controls that live in only one place fail in
/// only one place.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class DownloadTokenSchemaTests
{
    private readonly PostgresFixture _postgres;

    /// <summary>Initialises a new instance of the <see cref="DownloadTokenSchemaTests"/> class.</summary>
    /// <param name="postgres">The shared PostgreSQL fixture.</param>
    public DownloadTokenSchemaTests(PostgresFixture postgres) => _postgres = postgres;

    private const string InsertTemplate = """
        INSERT INTO download_token (
            id, statement_id, statement_period, customer_id, token_sha256,
            issued_at, expires_at, single_use)
        VALUES (
            gen_random_uuid(), gen_random_uuid(), date_trunc('month', now())::date, gen_random_uuid(),
            decode(md5(random()::text) || md5(random()::text), 'hex'),
            now(), now() + @ttl::interval, true);
        """;

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task DbCheckConstraint_RejectsTtlOverOneHour()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true);

        // An hour exactly is fine. Two is not. The ceiling is a real boundary, not a suggestion,
        // and it holds even against a superuser writing raw SQL.
        _ = await connection.ExecuteAsync(new CommandDefinition(
            InsertTemplate, new { ttl = "1 hour" }, commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(true);

        PostgresException tooLong = await Should.ThrowAsync<PostgresException>(
            connection.ExecuteAsync(new CommandDefinition(
                InsertTemplate, new { ttl = "2 hours" }, commandTimeout: 30, cancellationToken: cancellationToken))).ConfigureAwait(true);

        tooLong.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        tooLong.ConstraintName.ShouldBe("ck_token_ttl");

        // And an expiry that precedes issue is refused by the same constraint - a token that was
        // never valid is as much a bug as one that is valid for too long.
        PostgresException backwards = await Should.ThrowAsync<PostgresException>(
            connection.ExecuteAsync(new CommandDefinition(
                InsertTemplate, new { ttl = "-5 minutes" }, commandTimeout: 30, cancellationToken: cancellationToken))).ConfigureAwait(true);

        backwards.ConstraintName.ShouldBe("ck_token_ttl");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task AppDownloadRole_CannotInsertIntoDownloadToken()
    {
        // THE GATEWAY REDEEMS; IT DOES NOT MINT. If the internet-facing, unauthenticated service
        // could create tokens, a remote code execution there would be a token factory. It needs
        // exactly SELECT and UPDATE, and the database is what makes that true rather than a
        // convention nobody re-checks.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using NpgsqlConnection connection = await _postgres.OpenAsAsync("app_download", cancellationToken).ConfigureAwait(true);

        PostgresException refused = await Should.ThrowAsync<PostgresException>(
            connection.ExecuteAsync(new CommandDefinition(
                InsertTemplate, new { ttl = "10 minutes" }, commandTimeout: 30, cancellationToken: cancellationToken))).ConfigureAwait(true);

        refused.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        // It cannot delete the evidence either.
        PostgresException noDelete = await Should.ThrowAsync<PostgresException>(
            connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM download_token WHERE false;", commandTimeout: 30, cancellationToken: cancellationToken))).ConfigureAwait(true);

        noDelete.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        // But it CAN do the two things redemption needs, or the service would not work at all.
        _ = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM download_token;", commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(true);

        _ = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE download_token SET consumed_at = now() WHERE false;", commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(true);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task AppGenerationRole_HasNoAccessToDownloadToken()
    {
        // The statement generator has no business reading download links at all.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using NpgsqlConnection connection = await _postgres.OpenAsAsync("app_generation", cancellationToken).ConfigureAwait(true);

        PostgresException refused = await Should.ThrowAsync<PostgresException>(
            connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT count(*) FROM download_token;", commandTimeout: 30, cancellationToken: cancellationToken))).ConfigureAwait(true);

        refused.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task AppRetentionRole_CanReadButNotWriteDownloadToken()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using NpgsqlConnection connection = await _postgres.OpenAsAsync("app_retention", cancellationToken).ConfigureAwait(true);

        _ = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM download_token;", commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(true);

        PostgresException refused = await Should.ThrowAsync<PostgresException>(
            connection.ExecuteAsync(new CommandDefinition(
                "UPDATE download_token SET consumed_at = now() WHERE false;",
                commandTimeout: 30, cancellationToken: cancellationToken))).ConfigureAwait(true);

        refused.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task AuditTrail_IsReadableByDeliveryOnly_AndStillAppendOnly()
    {
        // V012 widened one grant so /v1/audit/verify could exist. This asserts the widening is
        // exactly as narrow as it was meant to be: the authenticated API can read, the
        // internet-facing gateway still cannot, and NOBODY can rewrite.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await using (NpgsqlConnection delivery = await _postgres.OpenAsAsync("app_delivery", cancellationToken).ConfigureAwait(true))
        {
            _ = await delivery.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT count(*) FROM audit_event;", commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(true);

            // Reading is the only thing that changed. The append-only property is what protects the
            // trail, and it must survive the grant that made verification possible.
            PostgresException noUpdate = await Should.ThrowAsync<PostgresException>(
                delivery.ExecuteAsync(new CommandDefinition(
                    "UPDATE audit_event SET action = 'TAMPERED' WHERE false;",
                    commandTimeout: 30, cancellationToken: cancellationToken))).ConfigureAwait(true);

            noUpdate.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        await using NpgsqlConnection download = await _postgres.OpenAsAsync("app_download", cancellationToken).ConfigureAwait(true);

        PostgresException refused = await Should.ThrowAsync<PostgresException>(
            download.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT count(*) FROM audit_event;",
                commandTimeout: 30, cancellationToken: cancellationToken))).ConfigureAwait(true);

        refused.SqlState.ShouldBe(
            PostgresErrorCodes.InsufficientPrivilege,
            "the unauthenticated, internet-facing service must remain write-only on the audit trail");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task DownloadToken_DailyPartitions_ExistSevenDaysAhead()
    {
        // Daily, not monthly. Tokens live at most an hour, so a day's partition is dead an hour
        // after it closes - and DROP of a whole partition is what makes cleanup free rather than a
        // DELETE that has to walk billions of rows.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true);

        var partitions = (await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT c.relname
              FROM pg_inherits i
              JOIN pg_class c      ON c.oid = i.inhrelid
              JOIN pg_class parent ON parent.oid = i.inhparent
             WHERE parent.relname = 'download_token'
             ORDER BY c.relname;
            """,
            commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(true)).ToList();

        partitions.Count.ShouldBeGreaterThanOrEqualTo(8, "V010 asks for eight daily partitions from yesterday");

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        for (int day = 0; day <= 6; day++)
        {
            string expected = "download_token_" + today.AddDays(day).ToString("yyyy_MM_dd", CultureInfo.InvariantCulture);
            partitions.ShouldContain(expected, $"the partition for {expected} must exist ahead of time, not on demand");
        }
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task TokenHash_IsUniqueWithinAPartition()
    {
        // Two rows with the same hash would make the atomic consume ambiguous: the UPDATE would
        // match both and RETURNING would hand back two statements for one credential.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true);

        const string Sql = """
            INSERT INTO download_token (
                id, statement_id, statement_period, customer_id, token_sha256,
                issued_at, expires_at, single_use)
            VALUES (
                gen_random_uuid(), gen_random_uuid(), date_trunc('month', now())::date, gen_random_uuid(),
                decode(@hash, 'hex'), @issued, @expires, true);
            """;

        string hash = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        DateTimeOffset issued = DateTimeOffset.UtcNow;
        var parameters = new { hash, issued, expires = issued.AddMinutes(10) };

        _ = await connection.ExecuteAsync(new CommandDefinition(Sql, parameters, commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(true);

        PostgresException duplicate = await Should.ThrowAsync<PostgresException>(
            connection.ExecuteAsync(new CommandDefinition(Sql, parameters, commandTimeout: 30, cancellationToken: cancellationToken))).ConfigureAwait(true);

        duplicate.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task TokenHash_MustBeExactly32Bytes()
    {
        // A short value would mean something other than SHA-256 produced it, which is the shape of
        // a truncation bug - and a truncated hash is a smaller search space.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using NpgsqlConnection connection = await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true);

        PostgresException refused = await Should.ThrowAsync<PostgresException>(
            connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO download_token (
                    id, statement_id, statement_period, customer_id, token_sha256,
                    issued_at, expires_at, single_use)
                VALUES (
                    gen_random_uuid(), gen_random_uuid(), date_trunc('month', now())::date, gen_random_uuid(),
                    '\x0102030405'::bytea, now(), now() + interval '10 minutes', true);
                """,
                commandTimeout: 30, cancellationToken: cancellationToken))).ConfigureAwait(true);

        refused.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        refused.ConstraintName.ShouldBe("ck_token_hash_length");
    }
}
