using System.Diagnostics.CodeAnalysis;
using StatementDelivery.Domain.Abstractions;

namespace StatementDelivery.Domain.Identifiers;

/// <summary>
/// Contract shared by every strongly-typed identifier, so that one generic JSON converter can
/// serve all of them. Dapper parameters are still unwrapped by hand (<c>id.Value</c>) at each call
/// site; there is no generic type handler, deliberately, so a query's parameter shape stays visible.
/// </summary>
/// <typeparam name="TSelf">The concrete identifier type.</typeparam>
public interface IStronglyTypedId<TSelf>
    where TSelf : struct, IStronglyTypedId<TSelf>
{
    /// <summary>Gets the underlying value.</summary>
    Guid Value { get; }

    /// <summary>Wraps a raw <see cref="Guid"/>.</summary>
    /// <param name="value">The raw value.</param>
    /// <returns>The wrapped identifier.</returns>
    static abstract TSelf FromGuid(Guid value);
}

// =============================================================================================
//  WHY THESE EXIST AT ALL.
//
//  A raw Guid is assignable to every parameter that takes a Guid. Passing a CustomerId where an
//  AccountId was expected compiles, runs, and returns somebody else's data - and in this system
//  the query that takes a customer identifier IS the authorisation check. That class of bug does
//  not surface in testing, because both values are well-formed identifiers of the right shape.
//
//  Wrapping each one makes the mistake a compile error. The wrapper is a readonly record struct,
//  so it is the same 16 bytes as the Guid it wraps and costs nothing at runtime.
// =============================================================================================

/// <summary>Identifies a customer. This is the subject of every authorisation decision.</summary>
/// <param name="Value">The underlying value.</param>
public readonly record struct CustomerId(Guid Value) : IStronglyTypedId<CustomerId>
{
    /// <summary>Mints a new identifier.</summary>
    /// <param name="generator">The identifier generator.</param>
    /// <returns>A new identifier.</returns>
    public static CustomerId New(IIdGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return new CustomerId(generator.NewId());
    }

    /// <inheritdoc />
    public static CustomerId FromGuid(Guid value) => new(value);

    /// <summary>Parses the canonical textual form.</summary>
    /// <param name="value">The text to parse.</param>
    /// <returns>The parsed identifier.</returns>
    /// <exception cref="FormatException">The value is not a well-formed identifier.</exception>
    public static CustomerId Parse(string? value) =>
        TryParse(value, out CustomerId parsed)
            ? parsed
            : throw new FormatException("Value is not a well-formed CustomerId.");

    /// <summary>Attempts to parse the canonical textual form.</summary>
    /// <param name="value">The text to parse.</param>
    /// <param name="result">The parsed identifier when parsing succeeds.</param>
    /// <returns><see langword="true"/> when parsing succeeded.</returns>
    public static bool TryParse([NotNullWhen(true)] string? value, out CustomerId result)
    {
        if (Guid.TryParse(value, out Guid parsed))
        {
            result = new CustomerId(parsed);
            return true;
        }

        result = default;
        return false;
    }

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>Identifies an account.</summary>
/// <param name="Value">The underlying value.</param>
public readonly record struct AccountId(Guid Value) : IStronglyTypedId<AccountId>
{
    /// <summary>Mints a new identifier.</summary>
    /// <param name="generator">The identifier generator.</param>
    /// <returns>A new identifier.</returns>
    public static AccountId New(IIdGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return new AccountId(generator.NewId());
    }

    /// <inheritdoc />
    public static AccountId FromGuid(Guid value) => new(value);

    /// <summary>Attempts to parse the canonical textual form.</summary>
    /// <param name="value">The text to parse.</param>
    /// <param name="result">The parsed identifier when parsing succeeds.</param>
    /// <returns><see langword="true"/> when parsing succeeded.</returns>
    public static bool TryParse([NotNullWhen(true)] string? value, out AccountId result)
    {
        if (Guid.TryParse(value, out Guid parsed))
        {
            result = new AccountId(parsed);
            return true;
        }

        result = default;
        return false;
    }

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>Identifies a statement.</summary>
/// <param name="Value">The underlying value.</param>
public readonly record struct StatementId(Guid Value) : IStronglyTypedId<StatementId>
{
    /// <summary>Mints a new identifier.</summary>
    /// <param name="generator">The identifier generator.</param>
    /// <returns>A new identifier.</returns>
    public static StatementId New(IIdGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return new StatementId(generator.NewId());
    }

    /// <inheritdoc />
    public static StatementId FromGuid(Guid value) => new(value);

    /// <summary>Attempts to parse the canonical textual form.</summary>
    /// <param name="value">The text to parse.</param>
    /// <param name="result">The parsed identifier when parsing succeeds.</param>
    /// <returns><see langword="true"/> when parsing succeeded.</returns>
    public static bool TryParse([NotNullWhen(true)] string? value, out StatementId result)
    {
        if (Guid.TryParse(value, out Guid parsed))
        {
            result = new StatementId(parsed);
            return true;
        }

        result = default;
        return false;
    }

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>Identifies a generation run.</summary>
/// <param name="Value">The underlying value.</param>
public readonly record struct RunId(Guid Value) : IStronglyTypedId<RunId>
{
    /// <summary>Mints a new identifier.</summary>
    /// <param name="generator">The identifier generator.</param>
    /// <returns>A new identifier.</returns>
    public static RunId New(IIdGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return new RunId(generator.NewId());
    }

    /// <inheritdoc />
    public static RunId FromGuid(Guid value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>Identifies an audit event.</summary>
/// <param name="Value">The underlying value.</param>
public readonly record struct AuditEventId(Guid Value) : IStronglyTypedId<AuditEventId>
{
    /// <summary>Mints a new identifier.</summary>
    /// <param name="generator">The identifier generator.</param>
    /// <returns>A new identifier.</returns>
    public static AuditEventId New(IIdGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return new AuditEventId(generator.NewId());
    }

    /// <inheritdoc />
    public static AuditEventId FromGuid(Guid value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// Identifies a download link.
/// </summary>
/// <remarks>
/// SAFE TO RETURN TO THE CUSTOMER. This is not the credential - the credential is the 32-byte
/// TokenSecret that never appears in a database column. This identifier exists so a link can be
/// revoked by name without anybody having to hold the secret to do it.
/// </remarks>
/// <param name="Value">The underlying value.</param>
public readonly record struct DownloadTokenId(Guid Value) : IStronglyTypedId<DownloadTokenId>
{
    /// <summary>Mints a new identifier.</summary>
    /// <param name="generator">The identifier generator.</param>
    /// <returns>A new identifier.</returns>
    public static DownloadTokenId New(IIdGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return new DownloadTokenId(generator.NewId());
    }

    /// <inheritdoc />
    public static DownloadTokenId FromGuid(Guid value) => new(value);

    /// <summary>Attempts to parse the canonical textual form.</summary>
    /// <param name="value">The text to parse.</param>
    /// <param name="result">The parsed identifier when parsing succeeds.</param>
    /// <returns><see langword="true"/> when parsing succeeded.</returns>
    public static bool TryParse([NotNullWhen(true)] string? value, out DownloadTokenId result)
    {
        if (Guid.TryParse(value, out Guid parsed))
        {
            result = new DownloadTokenId(parsed);
            return true;
        }

        result = default;
        return false;
    }

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>Identifies a legal hold.</summary>
/// <param name="Value">The underlying value.</param>
public readonly record struct LegalHoldId(Guid Value) : IStronglyTypedId<LegalHoldId>
{
    /// <summary>Mints a new identifier.</summary>
    /// <param name="generator">The identifier generator.</param>
    /// <returns>A new identifier.</returns>
    public static LegalHoldId New(IIdGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return new LegalHoldId(generator.NewId());
    }

    /// <inheritdoc />
    public static LegalHoldId FromGuid(Guid value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}
