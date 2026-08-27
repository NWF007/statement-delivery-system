using Npgsql;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Domain.Statements;
using StatementDelivery.Domain.ValueObjects;

namespace StatementDelivery.Persistence.Repositories;

/// <summary>
/// Reads statements. Every method takes the OWNER, and the owner goes into the WHERE clause.
/// </summary>
/// <remarks>
/// <para>
/// OWNERSHIP IS A PREDICATE, NEVER A POST-CHECK. There is no method here that returns a statement
/// and leaves the caller to compare identifiers afterwards, and that omission is the single most
/// important design decision in this interface.
/// </para>
/// <para>
/// A post-check is one forgotten <c>if</c> away from a data breach, and the forgotten version looks
/// exactly like the correct version in review - the row loads, something is returned, tests that
/// only cover the happy path pass. A predicate cannot be forgotten: leave the owner out and the
/// code does not compile.
/// </para>
/// </remarks>
public interface IStatementReadRepository
{
    /// <summary>
    /// Finds one statement, if it exists AND belongs to <paramref name="owner"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE PARTITION KEY TRAVELS WITH THE ID, and it looks redundant until you know why.
    /// <c>statement</c> is RANGE-partitioned on <c>period_start</c> and its primary key is
    /// <c>(id, period_start)</c>. A point lookup that omits the date cannot prune, so it visits
    /// EVERY partition - eighty-four of them at full retention - to find one row.
    /// </para>
    /// <para>
    /// This is why the HTTP contract requires <c>?period=</c> on the metadata endpoint. The
    /// parameter is not bureaucracy; it is the difference between an index lookup and a fan-out
    /// across the whole table. See docs/adr/0013-mandatory-date-range-on-statement-queries.md.
    /// </para>
    /// <para>
    /// Returns null both when the statement does not exist and when it belongs to someone else.
    /// The caller cannot tell the two apart, which is deliberate - see
    /// docs/adr/0012-404-not-403-for-unowned-resources.md.
    /// </para>
    /// </remarks>
    /// <param name="id">The statement identifier.</param>
    /// <param name="periodStart">The partition key. Required; see the remarks.</param>
    /// <param name="owner">The authenticated subject. Goes into the WHERE clause.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The statement, or null when it does not exist or is not owned by the caller.</returns>
    Task<Statement?> FindAsync(
        StatementId id,
        DateOnly periodStart,
        CustomerId owner,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists a customer's available statements, newest first, one keyset page at a time.
    /// </summary>
    /// <remarks>
    /// The date range is MANDATORY, not a convenience. It is what lets the planner prune
    /// partitions; without it this query visits every month ever generated.
    /// </remarks>
    /// <param name="customer">The owner. Always the authenticated subject, never route input.</param>
    /// <param name="fromInclusive">Inclusive lower bound on the period.</param>
    /// <param name="toExclusive">Exclusive upper bound on the period.</param>
    /// <param name="cursor">Position of the previous page, or null for the first page.</param>
    /// <param name="limit">Page size. Capped server-side regardless of what was asked for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One page of results.</returns>
    Task<CursorPage<Statement>> ListForCustomerAsync(
        CustomerId customer,
        DateOnly fromInclusive,
        DateOnly toExclusive,
        Cursor? cursor,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>
/// Writes statements. Every method takes the caller's transaction.
/// </summary>
/// <remarks>
/// The transaction is a parameter rather than something this type opens for itself, because the
/// audit record for an operation must commit or roll back WITH that operation. A repository that
/// managed its own transaction would make that impossible to express.
/// </remarks>
public interface IStatementWriteRepository
{
    /// <summary>Inserts a new statement.</summary>
    /// <param name="statement">The statement to insert.</param>
    /// <param name="transaction">The caller's transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task InsertAsync(Statement statement, NpgsqlTransaction transaction, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a statement to a new lifecycle status.
    /// </summary>
    /// <remarks>
    /// Takes <paramref name="partitionKey"/> for the same reason
    /// <see cref="IStatementReadRepository.FindAsync"/> does: without it, the UPDATE has to find
    /// the row by scanning every partition.
    /// </remarks>
    /// <param name="id">The statement identifier.</param>
    /// <param name="partitionKey">The statement's <c>period_start</c>.</param>
    /// <param name="status">The new status.</param>
    /// <param name="transaction">The caller's transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows affected; zero means the row was not found.</returns>
    Task<int> UpdateStatusAsync(
        StatementId id,
        DateOnly partitionKey,
        StatementStatus status,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken);
}
