using Npgsql;
using NpgsqlTypes;
using StatementDelivery.Persistence.Bulk;

namespace SeedTool;

/// <summary>A synthetic customer row.</summary>
/// <param name="Id">UUIDv7 identifier.</param>
/// <param name="ExternalRef">Upstream reference. Unique.</param>
/// <param name="Status">ACTIVE, DORMANT or CLOSED.</param>
public sealed record CustomerRow(Guid Id, string ExternalRef, string Status);

/// <summary>A synthetic account row.</summary>
/// <param name="Id">UUIDv7 identifier.</param>
/// <param name="CustomerId">Owning customer.</param>
/// <param name="MaskedNumber">Masked display number. Never a real account number.</param>
/// <param name="ProductType">Product type.</param>
/// <param name="Status">Account status.</param>
/// <param name="OpenedAt">When the account opened.</param>
public sealed record AccountRow(
    Guid Id,
    Guid CustomerId,
    string MaskedNumber,
    string ProductType,
    string Status,
    DateTimeOffset OpenedAt);

/// <summary>A synthetic statement row.</summary>
/// <param name="Id">UUIDv7 identifier.</param>
/// <param name="AccountId">Owning account.</param>
/// <param name="CustomerId">Denormalised owner, for hot-path authorisation.</param>
/// <param name="PeriodStart">Partition key.</param>
/// <param name="PeriodEnd">Last day of the covered month.</param>
/// <param name="Version">Generation version; 2 for a regenerated statement.</param>
/// <param name="Status">Lifecycle status.</param>
/// <param name="StorageKey">Object key. Computed, never discovered by listing.</param>
/// <param name="SizeBytes">Stored size.</param>
/// <param name="RetainUntil">Retention deadline, derived from the period end.</param>
/// <param name="GeneratedAt">When the bytes were produced.</param>
/// <param name="WrappedDek">The wrapped data key. Structurally real; see StatementRowMapper.</param>
/// <param name="KekId">The cohort KEK alias for this customer.</param>
/// <param name="ContentSha256">Digest of the plaintext. Required for AVAILABLE rows since V015.</param>
public sealed record StatementRow(
    Guid Id,
    Guid AccountId,
    Guid CustomerId,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    int Version,
    string Status,
    string StorageKey,
    long SizeBytes,
    DateOnly RetainUntil,
    DateTimeOffset GeneratedAt,
    byte[] WrappedDek,
    string KekId,
    byte[] ContentSha256);

/// <summary>Binary COPY mapping for <see cref="CustomerRow"/>.</summary>
public sealed class CustomerRowMapper : IBulkRowMapper<CustomerRow>
{
    /// <inheritdoc />
    public IReadOnlyList<string> Columns { get; } = ["id", "external_ref", "status"];

    /// <inheritdoc />
    public async ValueTask WriteRowAsync(NpgsqlBinaryImporter importer, CustomerRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentNullException.ThrowIfNull(row);

        await importer.WriteAsync(row.Id, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.ExternalRef, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.Status, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Binary COPY mapping for <see cref="AccountRow"/>.</summary>
public sealed class AccountRowMapper : IBulkRowMapper<AccountRow>
{
    /// <inheritdoc />
    public IReadOnlyList<string> Columns { get; } =
        ["id", "customer_id", "account_number_masked", "product_type", "status", "opened_at"];

    /// <inheritdoc />
    public async ValueTask WriteRowAsync(NpgsqlBinaryImporter importer, AccountRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentNullException.ThrowIfNull(row);

        await importer.WriteAsync(row.Id, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.CustomerId, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.MaskedNumber, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.ProductType, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.Status, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.OpenedAt, NpgsqlDbType.TimestampTz, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Binary COPY mapping for <see cref="StatementRow"/>.
/// </summary>
/// <remarks>
/// <para>
/// THE CRYPTO COLUMNS ARE MANDATORY FOR AN AVAILABLE STATEMENT. V013 adds
/// <c>ck_statement_available_has_key_material</c>, so a seeded AVAILABLE row without a wrapped DEK
/// and a KEK id is now rejected by the database - which is the constraint working, not the seed
/// tool being awkward.
/// </para>
/// <para>
/// So the tool writes a REAL wrapped key: a fresh 32-byte DEK, wrapped by a real cohort KEK derived
/// from a seed-tool-only secret, in the real envelope format. Sixty-one bytes per row, which at 2.5
/// billion rows is about 150 GB - a number worth having in the volume data rather than discovering
/// during a capacity review.
/// </para>
/// <para>
/// THESE ROWS ARE NOT DECRYPTABLE, and neither were they before: the objects they name have never
/// existed. This is not "pretending to encrypt" - nothing here claims a readable statement - it is
/// generating a row of the right SHAPE, which is the entire purpose of a volume seeder. The bytes
/// are as fictional as the storage key that points at them.
/// </para>
/// </remarks>
public sealed class StatementRowMapper : IBulkRowMapper<StatementRow>
{
    /// <inheritdoc />
    public IReadOnlyList<string> Columns { get; } =
    [
        "id", "account_id", "customer_id", "period_start", "period_end",
        "version", "status", "storage_key", "size_bytes", "retain_until", "generated_at",
        "wrapped_dek", "dek_algorithm", "kek_id", "content_sha256",
    ];

    /// <inheritdoc />
    public async ValueTask WriteRowAsync(NpgsqlBinaryImporter importer, StatementRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentNullException.ThrowIfNull(row);

        await importer.WriteAsync(row.Id, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.AccountId, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.CustomerId, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.PeriodStart, NpgsqlDbType.Date, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.PeriodEnd, NpgsqlDbType.Date, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.Version, NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.Status, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.StorageKey, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.SizeBytes, NpgsqlDbType.Bigint, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.RetainUntil, NpgsqlDbType.Date, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.GeneratedAt, NpgsqlDbType.TimestampTz, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.WrappedDek, NpgsqlDbType.Bytea, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(StatementDelivery.Crypto.Keys.KeyWrap.AlgorithmName, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.KekId, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
        await importer.WriteAsync(row.ContentSha256, NpgsqlDbType.Bytea, cancellationToken).ConfigureAwait(false);
    }
}
