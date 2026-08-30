using System.Data;
using Npgsql;
using StatementDelivery.Persistence.Connections;

namespace StatementDelivery.Persistence.Uow;

/// <summary>
/// Runs a block of work inside one database transaction.
/// </summary>
/// <remarks>
/// <para>
/// THE RULE: AN OPERATION WITH NO AUDIT RECORD MUST BE IMPOSSIBLE. The audit append happens inside
/// the same transaction as the operation it records, so if the audit insert fails the business
/// operation rolls back with it. There is no path that writes the change and loses the record of
/// it - which is the only way "tamper-evident" means anything, because an attacker who can make
/// the audit write fail silently has defeated the whole subsystem.
/// </para>
/// <para>
/// ⚠ THIS PARAGRAPH WAS FALSE FROM PROMPT 2 UNTIL 2026-08-30, AND NOTHING CAUGHT IT.
///
/// A unit of work cannot make it true on its own: it opens a transaction and runs a delegate, and
/// whether the audit append is inside that delegate is entirely up to the caller. Every caller put
/// it outside. The rule now lives where it can be stated properly - ADR-0025 - and is carried by
/// <c>RequestAudit.RecordAsync</c>'s transaction-accepting overload, which the three write paths
/// use. If you are adding a fourth, use that overload, and call it LAST.
/// </para>
/// <para>
/// THE COUNTER-RULE, BECAUSE IT BITES: THE AUDIT APPEND MUST BE THE LAST STATEMENT IN THE
/// TRANSACTION. Appending takes <c>FOR UPDATE</c> on the chain head, and that row is a
/// serialisation point for every other writer on the same chain. Hold it across a long operation
/// and you have throttled a sixteenth of the system's write throughput behind whatever slow thing
/// you did next. Keep business transactions short, and append last.
/// </para>
/// </remarks>
public interface IUnitOfWork
{
    /// <summary>Runs <paramref name="work"/> in a transaction, committing on success.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="work">The work to perform. Receives the open transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whatever the work returned.</returns>
    Task<T> ExecuteAsync<T>(
        Func<NpgsqlTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken);

    /// <summary>Runs <paramref name="work"/> in a transaction, committing on success.</summary>
    /// <param name="work">The work to perform.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ExecuteAsync(
        Func<NpgsqlTransaction, CancellationToken, Task> work,
        CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IUnitOfWork"/> over a single Npgsql connection.
/// </summary>
public sealed class NpgsqlUnitOfWork : IUnitOfWork
{
    private readonly IDbConnectionFactory _connections;

    /// <summary>Initialises a new instance of the <see cref="NpgsqlUnitOfWork"/> class.</summary>
    /// <param name="connections">Connection factory.</param>
    public NpgsqlUnitOfWork(IDbConnectionFactory connections) => _connections = connections;

    /// <inheritdoc />
    public async Task<T> ExecuteAsync<T>(
        Func<NpgsqlTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        // Write intent, always. A transaction that writes must never be routed to a replica, and
        // making that a routing decision rather than a convention removes the chance of getting it
        // wrong somewhere.
        await using NpgsqlConnection connection =
            await _connections.OpenAsync(ConnectionIntent.Write, cancellationToken).ConfigureAwait(false);

        // ReadCommitted, PostgreSQL's default. The audit append does not rely on the isolation
        // level for its ordering - it takes an explicit row lock on the chain head instead - so
        // raising the level here would cost serialisation failures without buying anything.
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        try
        {
            T result = await work(transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            // Rolled back on ANY exception, including one thrown by the audit append. That is the
            // mechanism behind "an operation with no audit record is impossible": the audit failure
            // takes the business change down with it rather than being swallowed and logged.
            //
            // Not passing the caller's token: if the operation was cancelled, that token is already
            // cancelled and the rollback would be skipped, leaving the transaction open until the
            // connection is reclaimed.
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public Task ExecuteAsync(Func<NpgsqlTransaction, CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        return ExecuteAsync<object?>(
            async (transaction, token) =>
            {
                await work(transaction, token).ConfigureAwait(false);
                return null;
            },
            cancellationToken);
    }
}
