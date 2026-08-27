using Npgsql;

namespace StatementDelivery.Persistence.Bulk;

/// <summary>
/// Streams rows into a table using the PostgreSQL binary COPY protocol.
/// </summary>
/// <remarks>
/// <para>
/// Thirty million rows a month cannot go in one INSERT at a time. Binary COPY is roughly two
/// orders of magnitude faster than row-by-row inserts: one protocol message stream instead of a
/// parse, plan, execute and round trip per row.
/// </para>
/// <para>
/// Nothing uses this yet, and that is deliberate. The shape of the persistence layer depends on
/// it - a bulk path that has to be retrofitted tends to arrive as a second, parallel data access
/// stack - so the abstraction and its benchmark exist before the first caller does.
/// </para>
/// </remarks>
public interface IBulkWriter
{
    /// <summary>
    /// Copies <paramref name="rows"/> into <paramref name="table"/>.
    /// </summary>
    /// <typeparam name="T">
    /// The row type. An <see cref="IBulkRowMapper{T}"/> for it must be registered in dependency
    /// injection; resolution failure is a startup-shaped error rather than a silent no-op.
    /// </typeparam>
    /// <param name="table">
    /// Unqualified table name. Interpolated into the COPY statement, so it is validated against a
    /// strict identifier pattern first - COPY takes no parameters, which makes this the one place
    /// in the data layer where an identifier reaches SQL as text.
    /// </param>
    /// <param name="rows">The rows to write, consumed lazily.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows written.</returns>
    Task<long> WriteAsync<T>(string table, IAsyncEnumerable<T> rows, CancellationToken cancellationToken);
}

/// <summary>
/// Describes how one row type is written to the binary COPY stream.
/// </summary>
/// <remarks>
/// Hand-written rather than reflected. Binary COPY requires the exact PostgreSQL type of every
/// column in the exact declared order; getting that from reflection means guessing at type
/// mappings, and a wrong guess is a corrupt stream rather than a compile error.
/// </remarks>
/// <typeparam name="T">The row type.</typeparam>
public interface IBulkRowMapper<in T>
{
    /// <summary>
    /// Gets the target column names, in the order <see cref="WriteRowAsync"/> writes them.
    /// </summary>
    IReadOnlyList<string> Columns { get; }

    /// <summary>
    /// Writes exactly one value per entry in <see cref="Columns"/>, in that order.
    /// </summary>
    /// <param name="importer">The open binary importer. A row has already been started.</param>
    /// <param name="row">The row to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask WriteRowAsync(NpgsqlBinaryImporter importer, T row, CancellationToken cancellationToken);
}
