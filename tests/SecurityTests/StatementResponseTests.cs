using System.Text.Json;
using Delivery.Api.Statements;
using Shouldly;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Domain.Statements;
using StatementDelivery.Domain.ValueObjects;
using Xunit;

namespace SecurityTests;

/// <summary>
/// The wire shape of a statement must never carry storage or crypto material.
/// </summary>
/// <remarks>
/// Runs without Docker, unlike the end-to-end version, so this rule is checked on every machine and
/// every build rather than only where a container runtime happens to exist. A leak here would hand
/// an attacker the object-storage layout and the envelope-encryption structure - the map to
/// everything - so it is worth checking twice.
/// </remarks>
public sealed class StatementResponseTests
{
    private static readonly string[] ForbiddenFragments =
    [
        "storageKey", "storage_key", "StorageKey",
        "storageTier", "storage_tier",
        "wrappedDek", "wrapped_dek",
        "dekAlgorithm", "dek_algorithm",
        "kekId", "kek_id",
        "authTag", "auth_tag",
        "contentSha256", "content_sha256",
        "retainUntil", "retain_until",
        "purgedAt", "purged_at",
        "failureReason",
    ];

    /// <summary>
    /// The SAME options minimal APIs use. Serialising with the library default would assert
    /// PascalCase while the wire actually carries camelCase - a test passing against a payload
    /// nobody ever sends.
    /// </summary>
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static Statement FullyPopulatedStatement() =>
        Statement.Create(
                new StatementId(Guid.CreateVersion7()),
                new AccountId(Guid.CreateVersion7()),
                new CustomerId(Guid.CreateVersion7()),
                StatementPeriod.ForMonth(2026, 8),
                RetentionPolicy.Default)
            .MarkAvailable(
                new StorageLocation(
                    "statements/2026/08/secret-object-key.pdf",
                    "GLACIER",
                    91_234,

                    // POPULATED SINCE CRYPTO LANDED, and the fixture is worth less without it. A
                    // "fully populated" statement whose Envelope was null meant the forbidden-name
                    // list below covered wrappedDek, kekId and contentSha256 while no such value
                    // was ever serialised - the assertion passed over an empty set. These values are
                    // distinctive so a leak is unmistakable in a diff.
                    new CryptoEnvelope(
                        Convert.FromHexString("01DEADBEEFCAFEBABE0102030405060708090A0B0C0D0E0F1011121314151617181920212223"),
                        "alias/statement-cek-0007",
                        "AES-256-GCM",
                        Convert.FromHexString("A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F90"),
                        new ContentBinding(
                            Guid.Parse("0199a1f0-1111-7000-8000-000000000001"),
                            Guid.Parse("0199a1f0-2222-7000-8000-000000000002"),
                            3))),
                new DateTimeOffset(2026, 9, 1, 2, 14, 33, TimeSpan.Zero));

    [Fact]
    public void SerialisedResponse_ContainsNoStorageOrCryptoFieldNames()
    {
        string json = JsonSerializer.Serialize(StatementResponse.From(FullyPopulatedStatement()), WebOptions);

        foreach (string forbidden in ForbiddenFragments)
        {
            json.ShouldNotContain(forbidden, Case.Insensitive, $"'{forbidden}' must never reach the wire");
        }
    }

    [Fact]
    public void SerialisedResponse_ContainsNoStorageKeyVALUE()
    {
        // Names are only half of it. A field renamed to something innocuous would still leak the
        // key, so the VALUE is checked too.
        Statement statement = FullyPopulatedStatement();

        string json = JsonSerializer.Serialize(StatementResponse.From(statement), WebOptions);

        json.ShouldNotContain("secret-object-key", Case.Insensitive);
        json.ShouldNotContain("GLACIER", Case.Insensitive, "the storage tier reveals where the bytes live");

        // AND THE CRYPTO ENVELOPE, by value. Every form it could plausibly be serialised in: hex
        // (what ToString on a byte[] never gives, but a converter might), base64 (what
        // System.Text.Json DOES give for a byte[] by default), and the plain KEK alias.
        //
        // The base64 check is the one that matters. If someone adds the envelope to the response
        // type, .NET will happily emit the wrapped DEK as base64 with no ceremony at all - and a
        // test looking only for the string "wrappedDek" would pass if the property were renamed.
        byte[] wrappedDek = statement.Storage!.Envelope!.WrappedDek;
        byte[] digest = statement.Storage.Envelope.ContentSha256!;

        json.ShouldNotContain(Convert.ToBase64String(wrappedDek), Case.Insensitive, "the wrapped data key must never reach the wire");
        json.ShouldNotContain(Convert.ToHexString(wrappedDek), Case.Insensitive);
        json.ShouldNotContain(Convert.ToBase64String(digest), Case.Insensitive);
        json.ShouldNotContain("alias/statement-cek-0007", Case.Insensitive, "the KEK alias names the cohort this customer is in");
    }

    [Fact]
    public void SerialisedResponse_CarriesExactlyTheAgreedFields()
    {
        // An allow-list assertion. A field ADDED to the response type - however innocent - fails
        // here, which forces the addition to be a deliberate act rather than a side effect of
        // someone extending the domain model.
        using JsonDocument document = JsonDocument.Parse(
            JsonSerializer.Serialize(StatementResponse.From(FullyPopulatedStatement()), WebOptions));

        string[] properties = [.. document.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];

        properties.ShouldBe(["accountId", "generatedAt", "id", "period", "sizeBytes", "status", "version"]);
    }

    [Fact]
    public void SizeBytes_IsNullBeforeTheStatementIsRendered()
    {
        Statement pending = Statement.Create(
            new StatementId(Guid.CreateVersion7()),
            new AccountId(Guid.CreateVersion7()),
            new CustomerId(Guid.CreateVersion7()),
            StatementPeriod.ForMonth(2026, 8),
            RetentionPolicy.Default);

        StatementResponse.From(pending).SizeBytes.ShouldBeNull();
    }

    [Fact]
    public void DevelopmentTokenEndpoint_IsMappedOnlyInsideTheDevelopmentGuard()
    {
        // An endpoint that mints bearer tokens is exactly the thing that must not escape into a
        // deployed environment. It is guarded twice - by this mapping and by JwtOptionsValidator
        // refusing to start with a symmetric signing key outside Development - and this test
        // asserts the first of those, because it is the one a refactor can quietly move.
        string program = RepositoryFiles.Read("src/Services/Delivery.Api/Program.cs");

        int guardIndex = program.IndexOf("if (app.Environment.IsDevelopment())", StringComparison.Ordinal);
        int mapIndex = program.IndexOf("MapDevTokenEndpoint", StringComparison.Ordinal);

        guardIndex.ShouldBeGreaterThan(-1, "the Development guard must exist");
        mapIndex.ShouldBeGreaterThan(guardIndex, "the token endpoint must be mapped inside the Development guard");
    }
}
