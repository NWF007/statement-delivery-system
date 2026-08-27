using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using StatementDelivery.Persistence.Connections;

namespace StatementDelivery.Persistence.Bulk;

/// <summary>
/// <see cref="IBulkWriter"/> implemented with <c>BeginBinaryImportAsync</c>.
/// </summary>
public sealed partial class NpgsqlBinaryCopyWriter : IBulkWriter
{
    private readonly IDbConnectionFactory _connections;
    private readonly IServiceProvider _services;
    private readonly ILogger<NpgsqlBinaryCopyWriter> _logger;

    /// <summary>Initialises a new instance of the <see cref="NpgsqlBinaryCopyWriter"/> class.</summary>
    /// <param name="connections">Connection factory.</param>
    /// <param name="services">Service provider used to resolve the row mapper.</param>
    /// <param name="logger">Logger.</param>
    public NpgsqlBinaryCopyWriter(
        IDbConnectionFactory connections,
        IServiceProvider services,
        ILogger<NpgsqlBinaryCopyWriter> logger)
    {
        _connections = connections;
        _services = services;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<long> WriteAsync<T>(string table, IAsyncEnumerable<T> rows, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(rows);

        if (!SafeIdentifier().IsMatch(table))
        {
            throw new ArgumentException(
                "Table must be a lower-case unquoted PostgreSQL identifier. COPY does not accept parameters, so the name is interpolated and cannot be sanitised later.",
                nameof(table));
        }

        IBulkRowMapper<T> mapper = _services.GetRequiredService<IBulkRowMapper<T>>();

        foreach (string column in mapper.Columns)
        {
            if (!SafeIdentifier().IsMatch(column))
            {
                throw new InvalidOperationException(
                    $"Column '{column}' declared by {mapper.GetType().Name} is not a lower-case unquoted PostgreSQL identifier.");
            }
        }

        string copyStatement = string.Create(
            CultureInfo.InvariantCulture,
            $"COPY {table} ({string.Join(", ", mapper.Columns)}) FROM STDIN (FORMAT BINARY)");

        long written = 0;
        long startedAt = TimeProvider.System.GetTimestamp();

        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.Write, cancellationToken).ConfigureAwait(false);

        await using (NpgsqlBinaryImporter importer =
            await connection.BeginBinaryImportAsync(copyStatement, cancellationToken).ConfigureAwait(false))
        {
            await foreach (T row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                await mapper.WriteRowAsync(importer, row, cancellationToken).ConfigureAwait(false);
                written++;
            }

            // Nothing is durable until Complete. Disposing without it rolls the whole copy back,
            // which is the behaviour we want on cancellation: a half-written batch is worse than
            // no batch, because the resumable job would have no way to tell where it stopped.
            _ = await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        TimeSpan elapsed = TimeProvider.System.GetElapsedTime(startedAt);
        LogCopyCompleted(
            _logger,
            written,
            table,
            (long)elapsed.TotalMilliseconds,
            elapsed.TotalSeconds > 0 ? written / elapsed.TotalSeconds : written);

        return written;
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifier();

    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Information,
        Message = "Binary COPY wrote {RowCount} row(s) into {Table} in {ElapsedMs} ms ({RowsPerSecond} rows/sec).")]
    private static partial void LogCopyCompleted(
        ILogger logger,
        long rowCount,
        string table,
        long elapsedMs,
        double rowsPerSecond);
}
