using System.Text.Json.Serialization;
using StatementDelivery.Domain.Statements;

namespace Delivery.Api.Statements;

/// <summary>
/// The wire shape of a statement.
/// </summary>
/// <remarks>
/// <para>
/// AN ALLOW-LIST, NOT A PROJECTION OF THE ENTITY. This record names every field a customer may
/// see, and adding a field is a deliberate act. Serialising the aggregate directly would mean that
/// the day <c>storage_key</c>, <c>wrapped_dek</c>, <c>kek_id</c>, <c>iv</c> or <c>auth_tag</c> get
/// populated, they would start appearing in responses with nobody having decided that.
/// </para>
/// <para>
/// Those five fields would hand an attacker the storage layout and the envelope-encryption
/// structure - the map to everything. StatementResponseTests asserts the serialised payload
/// contains none of their names.
/// </para>
/// </remarks>
/// <param name="Id">The statement identifier.</param>
/// <param name="AccountId">The owning account.</param>
/// <param name="Period">The covered month.</param>
/// <param name="Version">The generation version.</param>
/// <param name="Status">The lifecycle status.</param>
/// <param name="SizeBytes">The stored size, or null until the statement is rendered.</param>
/// <param name="GeneratedAt">When the bytes were produced, or null.</param>
public sealed record StatementResponse(
    string Id,
    string AccountId,
    PeriodResponse Period,
    int Version,
    string Status,
    long? SizeBytes,
    DateTimeOffset? GeneratedAt)
{
    /// <summary>Projects an aggregate onto the wire shape.</summary>
    /// <param name="statement">The statement.</param>
    /// <returns>The response.</returns>
    public static StatementResponse From(Statement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

        return new StatementResponse(
            statement.Id.ToString(),
            statement.AccountId.ToString(),
            new PeriodResponse(statement.Period.Start, statement.Period.End),
            statement.Version,
            statement.Status.ToString().ToUpperInvariant(),

            // Size is safe to expose - the customer is about to download the file - but the KEY is
            // not. Note that only SizeBytes is taken from StorageLocation, never Key.
            statement.Storage?.SizeBytes,
            statement.GeneratedAt);
    }
}

/// <summary>The covered month.</summary>
/// <param name="Start">First day of the month. Also the partition key.</param>
/// <param name="End">Last day of the month.</param>
public sealed record PeriodResponse(DateOnly Start, DateOnly End);

/// <summary>One page of statements.</summary>
/// <param name="Items">The page contents, newest first.</param>
/// <param name="NextCursor">Opaque cursor for the following page, or null at the end.</param>
/// <param name="HasMore">Whether a following page exists.</param>
public sealed record StatementPageResponse(
    IReadOnlyList<StatementResponse> Items,
    [property: JsonPropertyName("nextCursor")] string? NextCursor,
    bool HasMore);
