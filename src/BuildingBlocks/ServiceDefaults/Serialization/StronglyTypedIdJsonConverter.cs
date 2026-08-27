using System.Text.Json;
using System.Text.Json.Serialization;
using StatementDelivery.Domain.Identifiers;

namespace StatementDelivery.ServiceDefaults.Serialization;

/// <summary>
/// Serialises a strongly-typed identifier as a bare JSON string.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS IS NEEDED AT ALL. A <c>readonly record struct CustomerId(Guid Value)</c> is, to
/// System.Text.Json, an object with one property. Without a converter it serialises as
/// <c>{"value":"01a03ee4-…"}</c> rather than <c>"01a03ee4-…"</c>, and deserialising the bare string
/// a client actually sends THROWS. The wrapper exists to stop identifiers being mixed up in C#; it
/// must not leak that shape onto the wire.
/// </para>
/// <para>
/// WHY IT LIVES HERE AND NOT IN THE DOMAIN. The domain is serialisation-agnostic and references
/// nothing - see docs/adr and tests/ArchitectureTests/DomainPurityTests.cs. How an identifier is
/// represented in JSON is a decision belonging to the transport, so it belongs to an adapter. If
/// this file were in the domain, the model would carry an opinion about HTTP.
/// </para>
/// <para>
/// One generic converter serves all six identifiers, driven by
/// <see cref="IStronglyTypedId{TSelf}"/> and its static abstract <c>FromGuid</c>. Six hand-written
/// converters would be six places for the seventh identifier to be forgotten.
/// </para>
/// </remarks>
/// <typeparam name="TId">The identifier type.</typeparam>
public sealed class StronglyTypedIdJsonConverter<TId> : JsonConverter<TId>
    where TId : struct, IStronglyTypedId<TId>
{
    /// <inheritdoc />
    public override TId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // A JsonException here becomes a 400, which is correct: a malformed identifier in a request
        // body is client error. Throwing anything else would make it a 500.
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected a string identifier for {typeof(TId).Name}, found {reader.TokenType}.");
        }

        if (!Guid.TryParse(reader.GetString(), out Guid value))
        {
            throw new JsonException($"Value is not a well-formed {typeof(TId).Name}.");
        }

        return TId.FromGuid(value);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.Value);
    }

    /// <summary>
    /// Handles an identifier used as a dictionary KEY.
    /// </summary>
    /// <remarks>
    /// Without this override, a <c>Dictionary&lt;CustomerId, T&gt;</c> serialises its keys through
    /// <c>ToString()</c> on the boxed struct and fails to round-trip. Rare, but silent when wrong.
    /// </remarks>
    public override void WriteAsPropertyName(Utf8JsonWriter writer, TId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePropertyName(value.Value.ToString("D"));
    }

    /// <inheritdoc />
    public override TId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (!Guid.TryParse(reader.GetString(), out Guid value))
        {
            throw new JsonException($"Property name is not a well-formed {typeof(TId).Name}.");
        }

        return TId.FromGuid(value);
    }
}

/// <summary>
/// Supplies <see cref="StronglyTypedIdJsonConverter{TId}"/> for every type implementing
/// <see cref="IStronglyTypedId{TSelf}"/>.
/// </summary>
/// <remarks>
/// A factory rather than six registrations, so an identifier added later is covered the moment it
/// implements the interface. The failure this prevents is quiet: a new identifier serialising as
/// <c>{"value":…}</c> in one endpoint while every other identifier is a string.
/// </remarks>
public sealed class StronglyTypedIdJsonConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);

        return typeToConvert.IsValueType
            && Array.Exists(
                typeToConvert.GetInterfaces(),
                i => i.IsGenericType
                     && i.GetGenericTypeDefinition() == typeof(IStronglyTypedId<>)
                     && i.GetGenericArguments()[0] == typeToConvert);
    }

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(StronglyTypedIdJsonConverter<>).MakeGenericType(typeToConvert))!;
}
