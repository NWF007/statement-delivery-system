using Npgsql;

namespace StatementDelivery.Persistence.Connections;

/// <summary>
/// Opens a PostgreSQL connection appropriate to a declared <see cref="ConnectionIntent"/>.
/// </summary>
/// <remarks>
/// Returns the concrete <see cref="NpgsqlConnection"/> rather than <c>IDbConnection</c> on
/// purpose: the bulk-insert path needs binary COPY (<c>BeginBinaryImportAsync</c>) and the
/// token path needs PostgreSQL-specific SQL. Hiding the driver behind a generic interface
/// would buy portability this system has explicitly decided it does not want, and would cost
/// it the two features it most depends on.
/// </remarks>
public interface IDbConnectionFactory
{
    /// <summary>
    /// Opens a connection routed according to <paramref name="intent"/>.
    /// </summary>
    /// <param name="intent">
    /// What the caller intends to do. There is deliberately no default value.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An open connection. The caller owns it and must dispose it.</returns>
    ValueTask<NpgsqlConnection> OpenAsync(
        ConnectionIntent intent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the command timeout, in seconds, configured for <paramref name="intent"/>.
    /// </summary>
    /// <remarks>
    /// The value is also baked into each data source's connection string, so a command created
    /// from the returned connection already carries it. This accessor exists for the cases where
    /// a caller builds a command out of band and needs to apply the same budget explicitly.
    /// </remarks>
    /// <param name="intent">The intent whose timeout budget is wanted.</param>
    int CommandTimeoutSeconds(ConnectionIntent intent);
}
