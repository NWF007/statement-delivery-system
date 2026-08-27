using System.Data;
using System.Globalization;
using Dapper;

namespace StatementDelivery.Persistence.Dapper;

/// <summary>
/// Teaches Dapper to bind <see cref="DateOnly"/> as a PostgreSQL <c>date</c>.
/// </summary>
/// <remarks>
/// <para>
/// WITHOUT THIS, EVERY PARTITION-KEY LOOKUP IN THE SYSTEM THROWS. Dapper 2.1.79 has no built-in
/// mapping for <see cref="DateOnly"/>, so passing one as a parameter fails in
/// <c>SqlMapper.LookupDbType</c> with "The member periodStart of type System.DateOnly cannot be used
/// as a parameter value" - before a single byte reaches PostgreSQL.
/// </para>
/// <para>
/// That is not a corner case here. <c>period_start</c> is the RANGE partition key on
/// <c>statement</c>, so it is a parameter on the statement read path, on link issue, and on the
/// pruned lookup that follows every token redemption. The failure is total: reading a statement and
/// downloading one both throw.
/// </para>
/// <para>
/// HOW IT WAS MISSED, WHICH IS THE PART WORTH REMEMBERING. Everything that exercises it needs a real
/// database, every one of those tests is Docker-gated, and the development host has no container
/// runtime - so the whole class of defect stayed invisible until CI ran the suite on Linux. A clean
/// compile, 264 passing tests and a full architecture suite all went green over code that could not
/// execute.
/// </para>
/// <para>
/// Only the PARAMETER direction needs help. Npgsql already returns <see cref="DateOnly"/> when it
/// reads a <c>date</c> column, so result mapping was never broken - which is exactly why the gap was
/// easy to write and hard to see.
/// </para>
/// </remarks>
public sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
{
    /// <inheritdoc />
    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
        ArgumentNullException.ThrowIfNull(parameter);

        // DbType.Date, not DateTime. A timestamp parameter against a `date` column forces a cast,
        // and a cast on a partition key is how a pruned lookup silently becomes a full scan.
        parameter.DbType = DbType.Date;
        parameter.Value = value;
    }

    /// <inheritdoc />
    public override DateOnly Parse(object value) => DateOnlyConversion.FromDatabase(value);
}

/// <summary>
/// The nullable counterpart of <see cref="DateOnlyTypeHandler"/>.
/// </summary>
/// <remarks>
/// A separate handler rather than the same instance registered twice: Dapper looks a handler up by
/// the exact declared type, and the generic registration overload needs a
/// <c>TypeHandler&lt;DateOnly?&gt;</c> to express this one.
/// </remarks>
public sealed class NullableDateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly?>
{
    /// <inheritdoc />
    public override void SetValue(IDbDataParameter parameter, DateOnly? value)
    {
        ArgumentNullException.ThrowIfNull(parameter);

        parameter.DbType = DbType.Date;

        // DBNull, not null. A CLR null on a parameter is not the same thing to ADO.NET, and the
        // difference shows up as a silently omitted predicate rather than an error.
        parameter.Value = value.HasValue ? value.Value : DBNull.Value;
    }

    /// <inheritdoc />
    public override DateOnly? Parse(object value) =>
        value is null or DBNull ? null : DateOnlyConversion.FromDatabase(value);
}

/// <summary>Shared conversion for the two handlers.</summary>
internal static class DateOnlyConversion
{
    /// <summary>Converts whatever the provider returned into a <see cref="DateOnly"/>.</summary>
    /// <param name="value">The raw value from the reader.</param>
    /// <returns>The date.</returns>
    internal static DateOnly FromDatabase(object value) => value switch
    {
        DateOnly date => date,
        DateTime timestamp => DateOnly.FromDateTime(timestamp),
        string text => DateOnly.Parse(text, CultureInfo.InvariantCulture),
        _ => throw new DataException(
            $"Cannot convert {value?.GetType().FullName ?? "null"} to DateOnly."),
    };
}

/// <summary>
/// Registers the Dapper type handlers this platform needs.
/// </summary>
public static class DapperConfiguration
{
    /// <summary>
    /// Runs exactly once per process, on first touch.
    /// </summary>
    /// <remarks>
    /// Dapper's handler table is process-wide static state, and a static field initialiser is the
    /// simplest thing that registers into it exactly once - no lock, no double-check, and correct
    /// even when two hosts start in the same process, which is what WebApplicationFactory does when
    /// a test spins up both services.
    /// </remarks>
    private static readonly bool Registered = Register();

    /// <summary>Ensures the handlers are installed. Idempotent.</summary>
    public static void EnsureConfigured() => _ = Registered;

    private static bool Register()
    {
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
        SqlMapper.AddTypeHandler(new NullableDateOnlyTypeHandler());
        return true;
    }
}
