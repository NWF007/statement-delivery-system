using System.Text.Json;
using Shouldly;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.ServiceDefaults.Serialization;
using Xunit;

namespace UnitTests.ServiceDefaults;

/// <summary>
/// Strongly-typed identifiers must appear on the wire as bare strings.
/// </summary>
/// <remarks>
/// The wrapper exists to stop a <c>CustomerId</c> being passed where an <c>AccountId</c> was
/// expected. It must not leak that shape into JSON: without a converter an identifier serialises as
/// <c>{"value":"…"}</c> and deserialising the bare string a client actually sends throws.
/// </remarks>
public sealed class StronglyTypedIdJsonConverterTests
{
    private static readonly Guid Value = Guid.Parse("01a03ee4-d9c8-7534-800d-b5b2c5ce17e8");

    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        // The same shape ASP.NET Core uses, plus the factory ServiceDefaults registers.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new StronglyTypedIdJsonConverterFactory());
        return options;
    }

    [Fact]
    public void SerialisesAsABareString() =>
        JsonSerializer.Serialize(new CustomerId(Value), Options)
            .ShouldBe("\"01a03ee4-d9c8-7534-800d-b5b2c5ce17e8\"");

    [Fact]
    public void SerialisesAsABareStringInsideAnObject() =>
        JsonSerializer.Serialize(new { customerId = new CustomerId(Value) }, Options)
            .ShouldBe("{\"customerId\":\"01a03ee4-d9c8-7534-800d-b5b2c5ce17e8\"}");

    [Fact]
    public void RoundTripsFromTheStringAClientSends() =>
        JsonSerializer.Deserialize<CustomerId>("\"01a03ee4-d9c8-7534-800d-b5b2c5ce17e8\"", Options)
            .ShouldBe(new CustomerId(Value));

    [Fact]
    public void CoversEveryIdentifierType()
    {
        // A factory, not six registrations, so an identifier added later is covered the moment it
        // implements the interface. This asserts the factory actually claims all six.
        var factory = new StronglyTypedIdJsonConverterFactory();

        foreach (Type type in (Type[])
                 [
                     typeof(CustomerId), typeof(AccountId), typeof(StatementId),
                     typeof(RunId), typeof(AuditEventId), typeof(LegalHoldId),
                 ])
        {
            factory.CanConvert(type).ShouldBeTrue($"{type.Name} must be handled");
        }

        factory.CanConvert(typeof(Guid)).ShouldBeFalse("a raw Guid is not a strongly-typed id");
        factory.CanConvert(typeof(string)).ShouldBeFalse();
    }

    [Fact]
    public void EveryIdentifierRoundTrips()
    {
        string json = JsonSerializer.Serialize(
            new
            {
                customer = new CustomerId(Value),
                account = new AccountId(Value),
                statement = new StatementId(Value),
                run = new RunId(Value),
                auditEvent = new AuditEventId(Value),
                legalHold = new LegalHoldId(Value),
            },
            Options);

        json.ShouldNotContain("value", Case.Insensitive, "no identifier may leak its wrapper shape");

        using JsonDocument document = JsonDocument.Parse(json);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            property.Value.ValueKind.ShouldBe(JsonValueKind.String, $"{property.Name} must be a bare string");
        }
    }

    [Theory]
    [InlineData("\"not-a-guid\"")]
    [InlineData("\"\"")]
    [InlineData("123")]
    [InlineData("{\"value\":\"01a03ee4-d9c8-7534-800d-b5b2c5ce17e8\"}")]
    public void MalformedInput_ThrowsJsonException_WhichBecomesA400(string json)
    {
        // JsonException specifically, not some other exception type: minimal APIs turn a
        // JsonException during model binding into a 400. Anything else becomes a 500, which would
        // report client error as server error.
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<CustomerId>(json, Options));
    }

    [Fact]
    public void NullableIdentifiers_RoundTrip()
    {
        CustomerId? present = new CustomerId(Value);
        CustomerId? absent = null;

        JsonSerializer.Serialize(present, Options).ShouldBe("\"01a03ee4-d9c8-7534-800d-b5b2c5ce17e8\"");
        JsonSerializer.Serialize(absent, Options).ShouldBe("null");

        JsonSerializer.Deserialize<CustomerId?>("null", Options).ShouldBeNull();
        JsonSerializer.Deserialize<CustomerId?>("\"01a03ee4-d9c8-7534-800d-b5b2c5ce17e8\"", Options)
            .ShouldBe(present);
    }

    [Fact]
    public void IdentifiersWorkAsDictionaryKeys()
    {
        // Without WriteAsPropertyName/ReadAsPropertyName this silently fails to round-trip.
        var source = new Dictionary<CustomerId, int> { [new CustomerId(Value)] = 7 };

        string json = JsonSerializer.Serialize(source, Options);
        json.ShouldBe("{\"01a03ee4-d9c8-7534-800d-b5b2c5ce17e8\":7}");

        JsonSerializer.Deserialize<Dictionary<CustomerId, int>>(json, Options)
            .ShouldNotBeNull()[new CustomerId(Value)].ShouldBe(7);
    }
}
