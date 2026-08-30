using Dapper;
using Npgsql;
using Shouldly;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Domain.Statements;
using StatementDelivery.Domain.ValueObjects;
using StatementDelivery.Persistence.Connections;
using StatementDelivery.Persistence.Repositories;
using StatementDelivery.Persistence.Uow;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// Publishing a rendered statement: the write path Prompt 5 will build generation on.
/// </summary>
/// <remarks>
/// <para>
/// Three constraints govern an AVAILABLE row - <c>ck_statement_available_has_storage</c> (V006),
/// <c>ck_statement_available_has_key_material</c> (V013) and
/// <c>ck_statement_available_has_digest</c> (V015). Between Prompt 4 and this change there was
/// no method in the repository that could satisfy all three, and the one method that set
/// <c>status</c> could satisfy none of them.
/// </para>
/// <para>
/// It had zero callers, so nothing failed. Prompt 5's first write would have been the first caller,
/// and a check-constraint violation arriving during statement generation looks like a crypto defect.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class StatementPublishTests
{
    private readonly PostgresFixture _postgres;

    /// <summary>Initialises a new instance of the <see cref="StatementPublishTests"/> class.</summary>
    /// <param name="postgres">The shared PostgreSQL fixture.</param>
    public StatementPublishTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task MarkAvailable_WithFullEnvelope_SatisfiesAllCheckConstraints()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (CustomerId customer, AccountId account, StatementId statement, DateOnly period) =
            await SeedPendingAsync(cancellationToken).ConfigureAwait(true);

        NpgsqlConnectionFactory factory = _postgres.ConnectionFactoryFor("app_generation");
        await using (factory.ConfigureAwait(true))
        {
            var unitOfWork = new NpgsqlUnitOfWork(factory);
            var repository = new StatementWriteRepository(factory);

            var envelope = new CryptoEnvelope(
                WrappedDek: RandomBytes(61),
                KekId: "kek-integration-test",
                Algorithm: "AES-256-GCM",
                ContentSha256: RandomBytes(32),
                Binding: new ContentBinding(statement.Value, customer.Value, 1));

            var location = new StorageLocation(
                Key: $"statements/{period:yyyy/MM}/{statement.Value:N}.pdf",
                Tier: "STANDARD",
                SizeBytes: 91_234,
                Envelope: envelope);

            DateTimeOffset generatedAt = DateTimeOffset.UtcNow;

            int affected = await unitOfWork.ExecuteAsync(
                (NpgsqlTransaction transaction, CancellationToken token) =>
                    repository.MarkAvailableAsync(statement, period, location, generatedAt, transaction, token),
                cancellationToken).ConfigureAwait(true);

            affected.ShouldBe(1);
        }

        await using NpgsqlConnection connection =
            await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true);

        PublishedRow row = await connection.QuerySingleAsync<PublishedRow>(new CommandDefinition(
            """
            SELECT status                       AS Status,
                   storage_key                  AS StorageKey,
                   storage_tier                 AS StorageTier,
                   size_bytes                   AS SizeBytes,
                   octet_length(content_sha256) AS ContentShaLength,
                   octet_length(wrapped_dek)    AS WrappedDekLength,
                   dek_algorithm                AS DekAlgorithm,
                   kek_id                       AS KekId,
                   iv                           AS Iv,
                   auth_tag                     AS AuthTag,
                   generated_at IS NOT NULL     AS HasGeneratedAt
              FROM statement
             WHERE id = @id AND period_start = @period;
            """,
            new { id = statement.Value, period },
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(true);

        row.Status.ShouldBe("AVAILABLE");
        row.StorageKey.ShouldNotBeNullOrWhiteSpace();
        row.StorageTier.ShouldBe("STANDARD");
        row.SizeBytes.ShouldBe(91_234);
        row.ContentShaLength.ShouldBe(32, "V015 requires a 32-byte digest on an AVAILABLE row");
        row.WrappedDekLength.ShouldBeGreaterThanOrEqualTo(40, "V013 floors a wrapped key at 40 bytes");
        row.DekAlgorithm.ShouldBe("AES-256-GCM");
        row.KekId.ShouldBe("kek-integration-test");
        row.HasGeneratedAt.ShouldBeTrue();

        // The framed format gives every frame its own nonce and tag, so these two columns have
        // nothing truthful to hold. Asserted rather than ignored: a future writer that starts
        // populating them has changed the format, and this should stop them.
        row.Iv.ShouldBeNull("the framed format has no single IV - see ADR-0019");
        row.AuthTag.ShouldBeNull("the framed format has no single auth tag - see ADR-0019");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task MarkAvailable_WithoutCryptoColumns_IsRejectedBy23514()
    {
        // THE TEST THAT WOULD HAVE CAUGHT IT.
        //
        // This issues the EXACT UPDATE the deleted UpdateStatusAsync issued - status and nothing
        // else - and asserts the database refuses it. That method had zero callers, so no test ever
        // executed this statement against a schema carrying V013 and V015, and the trap sat there
        // waiting for Prompt 5.
        //
        // It deliberately bypasses the repository. Going through MarkAvailableAsync would fail in
        // C# on the null-envelope guard and prove only that the guard exists; the claim being made
        // here is about the DATABASE, and only the database can support it.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (_, _, StatementId statement, DateOnly period) =
            await SeedPendingAsync(cancellationToken).ConfigureAwait(true);

        await using NpgsqlConnection connection =
            await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true);

        PostgresException rejected = await Should.ThrowAsync<PostgresException>(
            () => connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE statement
                   SET status = 'AVAILABLE'
                 WHERE id = @id AND period_start = @period;
                """,
                new { id = statement.Value, period },
                commandTimeout: 30,
                cancellationToken: cancellationToken))).ConfigureAwait(true);

        // On the SQLSTATE, not the message. Message text is localised and version-dependent; the
        // class of error is neither.
        rejected.SqlState.ShouldBe(
            "23514",
            "an AVAILABLE row without storage, key material and a digest must be refused by a CHECK constraint");

        // And the row did not move.
        (await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM statement WHERE id = @id AND period_start = @period;",
            new { id = statement.Value, period },
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(true))
            .ShouldBe("PENDING");
    }

    [Fact(SkipUnless = nameof(DockerAvailability.IsAvailable), SkipType = typeof(DockerAvailability), Skip = DockerAvailability.SkipReason)]
    public async Task MarkFailed_LeavesTheStatementRetryable()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (_, _, StatementId statement, DateOnly period) =
            await SeedPendingAsync(cancellationToken).ConfigureAwait(true);

        NpgsqlConnectionFactory factory = _postgres.ConnectionFactoryFor("app_generation");
        await using (factory.ConfigureAwait(true))
        {
            var unitOfWork = new NpgsqlUnitOfWork(factory);
            var repository = new StatementWriteRepository(factory);

            int affected = await unitOfWork.ExecuteAsync(
                (NpgsqlTransaction transaction, CancellationToken token) =>
                    repository.MarkFailedAsync(statement, period, transaction, token),
                cancellationToken).ConfigureAwait(true);

            affected.ShouldBe(1);
        }

        await using NpgsqlConnection connection =
            await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true);

        // FAILED, and no crypto columns invented on the way. A failed render produced no bytes.
        (await connection.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT status FROM statement WHERE id = @id AND period_start = @period;",
            new { id = statement.Value, period },
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(true))
            .ShouldBe("FAILED");
    }

    private static byte[] RandomBytes(int length) =>
        System.Security.Cryptography.RandomNumberGenerator.GetBytes(length);

    /// <summary>Seeds a customer, an account, and one PENDING statement ready to be published.</summary>
    private async Task<(CustomerId Customer, AccountId Account, StatementId Statement, DateOnly Period)>
        SeedPendingAsync(CancellationToken cancellationToken)
    {
        var customer = new CustomerId(Guid.CreateVersion7());
        var account = new AccountId(Guid.CreateVersion7());
        var statement = new StatementId(Guid.CreateVersion7());

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        StatementPeriod period = StatementPeriod.ForMonth(today.Year, today.Month);

        await using NpgsqlConnection connection =
            await _postgres.OpenAdminAsync(cancellationToken).ConfigureAwait(true);

        _ = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO customer (id, external_ref, status) VALUES (@customer, @ref, 'ACTIVE');
            INSERT INTO account (id, customer_id, account_number_masked, product_type, status, opened_at)
            VALUES (@account, @customer, '****1234', 'CURRENT', 'ACTIVE', now());
            INSERT INTO statement (id, account_id, customer_id, period_start, period_end,
                                   version, status, retain_until)
            VALUES (@statement, @account, @customer, @start, @end, 1, 'PENDING', @retain);
            """,
            new
            {
                customer = customer.Value,
                account = account.Value,
                statement = statement.Value,
                @ref = customer.Value.ToString("N"),
                start = period.Start,
                end = period.End,
                retain = period.Start.AddYears(7),
            },
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(true);

        return (customer, account, statement, period.Start);
    }

    private sealed record PublishedRow
    {
        public string Status { get; init; } = string.Empty;

        public string? StorageKey { get; init; }

        public string? StorageTier { get; init; }

        public long SizeBytes { get; init; }

        public int ContentShaLength { get; init; }

        public int WrappedDekLength { get; init; }

        public string? DekAlgorithm { get; init; }

        public string? KekId { get; init; }

        public byte[]? Iv { get; init; }

        public byte[]? AuthTag { get; init; }

        public bool HasGeneratedAt { get; init; }
    }
}
