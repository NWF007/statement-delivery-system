using System.Data;
using System.Diagnostics.CodeAnalysis;
using Shouldly;
using StatementDelivery.Persistence.Dapper;
using Xunit;

namespace UnitTests.Persistence;

/// <summary>
/// The Dapper handlers that make <see cref="DateOnly"/> bindable as a parameter.
/// </summary>
/// <remarks>
/// <para>
/// WHAT THESE COVER AND WHAT THEY CANNOT. They pin the conversion contract - a <c>date</c> parameter
/// rather than a timestamp, DBNull rather than a CLR null, and every shape a provider might return
/// on the way back.
/// </para>
/// <para>
/// They do NOT prove Dapper is actually consulting them, because that lookup happens inside
/// <c>SqlMapper</c> while it builds a command against a live connection. That half is proven by the
/// Docker-gated integration tests - and the original defect lived in exactly that gap, which is why
/// it survived a clean build and a full green unit suite.
/// </para>
/// </remarks>
public sealed class DateOnlyTypeHandlerTests
{
    /// <summary>A minimal parameter, so the handler can be exercised with no provider present.</summary>
    private sealed class FakeParameter : IDbDataParameter
    {
        public byte Precision { get; set; }

        public byte Scale { get; set; }

        public int Size { get; set; }

        public DbType DbType { get; set; }

        public ParameterDirection Direction { get; set; }

        public bool IsNullable => true;

        [AllowNull]
        public string ParameterName { get; set; } = string.Empty;

        [AllowNull]
        public string SourceColumn { get; set; } = string.Empty;

        public DataRowVersion SourceVersion { get; set; }

        public object? Value { get; set; }
    }

    [Fact]
    public void SetValue_BindsAsDate_NotTimestamp()
    {
        var parameter = new FakeParameter();

        new DateOnlyTypeHandler().SetValue(parameter, new DateOnly(2026, 8, 1));

        // DbType.Date matters beyond tidiness: a timestamp parameter against a `date` column forces
        // a cast, and a cast on the partition key turns a pruned lookup into a full scan across
        // every monthly partition.
        parameter.DbType.ShouldBe(DbType.Date);
        parameter.Value.ShouldBe(new DateOnly(2026, 8, 1));
    }

    [Fact]
    public void NullableSetValue_UsesDbNull_NotClrNull()
    {
        var parameter = new FakeParameter();

        new NullableDateOnlyTypeHandler().SetValue(parameter, null);

        // A CLR null is not the same thing to ADO.NET as DBNull, and the difference surfaces as a
        // predicate that quietly does nothing rather than as an error.
        parameter.Value.ShouldBe(DBNull.Value);
        parameter.DbType.ShouldBe(DbType.Date);
    }

    [Fact]
    public void NullableSetValue_UnwrapsAValue()
    {
        var parameter = new FakeParameter();

        new NullableDateOnlyTypeHandler().SetValue(parameter, new DateOnly(2026, 1, 31));

        parameter.Value.ShouldBe(new DateOnly(2026, 1, 31));
    }

    [Theory]
    [MemberData(nameof(ProviderValues))]
    public void Parse_AcceptsEveryShapeAProviderMightReturn(object value, DateOnly expected)
    {
        new DateOnlyTypeHandler().Parse(value).ShouldBe(expected);
        new NullableDateOnlyTypeHandler().Parse(value).ShouldBe(expected);
    }

    public static TheoryData<object, DateOnly> ProviderValues() => new()
    {
        { new DateOnly(2026, 8, 27), new DateOnly(2026, 8, 27) },
        { new DateTime(2026, 8, 27, 13, 45, 0, DateTimeKind.Utc), new DateOnly(2026, 8, 27) },
        { "2026-08-27", new DateOnly(2026, 8, 27) },
    };

    [Fact]
    public void NullableParse_MapsDbNullToNull() =>
        new NullableDateOnlyTypeHandler().Parse(DBNull.Value).ShouldBeNull();

    [Fact]
    public void Parse_RejectsSomethingItCannotConvert() =>
        Should.Throw<DataException>(() => new DateOnlyTypeHandler().Parse(42));

    [Fact]
    public void EnsureConfigured_IsIdempotent()
    {
        // Dapper's handler table is process-wide static state, and two hosts can start in one
        // process - WebApplicationFactory does exactly that when a test drives both services.
        DapperConfiguration.EnsureConfigured();
        DapperConfiguration.EnsureConfigured();
        Should.NotThrow(DapperConfiguration.EnsureConfigured);
    }
}
