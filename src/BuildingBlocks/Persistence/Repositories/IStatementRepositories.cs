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
    /// Finds one statement on the CALLER'S CONNECTION, inside the CALLER'S TRANSACTION.
    /// </summary>
    /// <remarks>
    /// <para>
    /// USE THIS OVERLOAD FOR ANY READ WHOSE RESULT GATES A SECURITY OR ACCESS DECISION. The
    /// overload above is the catalogue path: it opens its own connection at
    /// <see cref="Connections.ConnectionIntent.ReadEventual"/>, which is correct for browsing and
    /// wrong for anything that decides whether to serve.
    /// </para>
    /// <para>
    /// Three things go wrong when a gating read runs on its own eventual connection, and the
    /// download redemption path hit all three:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// ISOLATION. The read cannot see the transaction that is deciding, and the transaction cannot
    /// see the read. They are two sessions that happen to run next to each other.
    /// </description></item>
    /// <item><description>
    /// CORRECTNESS. Under replication lag the replica returns null for a row that exists on the
    /// primary. The caller treats that as a denial and commits anyway, so a single-use token is
    /// spent on a 404. Worse, stale crypto columns decrypt against the wrong key material and
    /// raise a ciphertext-integrity error - an integrity alert for what is replication lag.
    /// </description></item>
    /// <item><description>
    /// LIVENESS. Acquiring a second connection while holding a write transaction deadlocks a
    /// bounded pool: at concurrency equal to the pool size, every holder waits for a slot only
    /// another holder can release.
    /// </description></item>
    /// </list>
    /// <para>
    /// Same SQL, same ownership predicate, same partition key as the overload above. Only the
    /// connection changes. See docs/adr/0024-security-gating-reads-run-in-the-callers-transaction.md.
    /// </para>
    /// </remarks>
    /// <param name="id">The statement identifier.</param>
    /// <param name="periodStart">The partition key. Required; see the remarks above.</param>
    /// <param name="owner">The authenticated subject. Goes into the WHERE clause.</param>
    /// <param name="transaction">The caller's transaction. The command runs on its connection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The statement, or null when it does not exist or is not owned by the caller.</returns>
    Task<Statement?> FindAsync(
        StatementId id,
        DateOnly periodStart,
        CustomerId owner,
        NpgsqlTransaction transaction,
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
    /// Publishes a rendered statement: AVAILABLE, with everything needed to find and open its bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THIS REPLACED A GENERAL <c>UpdateStatusAsync(id, partitionKey, status, ...)</c>, WHICH COULD
    /// NOT EXPRESS A LEGAL TRANSITION TO AVAILABLE AND WAS THEREFORE A TRAP.
    /// </para>
    /// <para>
    /// Three constraints make an AVAILABLE row inseparable from its content:
    /// <c>ck_statement_available_has_storage</c> (V006) wants a storage key,
    /// <c>ck_statement_available_has_key_material</c> (V013) wants a wrapped DEK and a KEK id, and
    /// <c>ck_statement_available_has_digest</c> (V015) wants a digest. A method that set only
    /// <c>status</c> could satisfy none of them, so every call would have failed with SQLSTATE
    /// 23514 - and a check-constraint violation surfacing during statement generation reads as a
    /// crypto defect, which is a day spent in the wrong subsystem.
    /// </para>
    /// <para>
    /// The signature makes that unreachable instead of merely unlikely: there is no way to call
    /// this without the envelope, and no other method that reaches AVAILABLE at all. Same reasoning
    /// as the domain state machine - illegal states should be unconstructable, not merely rejected.
    /// </para>
    /// <para>
    /// SIZE AND DIGEST COME FROM <paramref name="location"/>, not from separate parameters.
    /// <see cref="StorageLocation.SizeBytes"/> and <c>Envelope.ContentSha256</c> already hold them,
    /// and a second parameter for a value the object carries is a second source that can disagree
    /// with the first.
    /// </para>
    /// </remarks>
    /// <param name="id">The statement identifier.</param>
    /// <param name="partitionKey">The statement's <c>period_start</c>. Required, so the UPDATE prunes.</param>
    /// <param name="location">
    /// Where the bytes are and how to open them. Its <see cref="StorageLocation.Envelope"/> must not
    /// be null: an AVAILABLE statement without key material cannot be stored, and cannot be read.
    /// </param>
    /// <param name="generatedAt">When the bytes were rendered.</param>
    /// <param name="transaction">The caller's transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Rows affected; zero means no such statement in that partition.</returns>
    Task<int> MarkAvailableAsync(
        StatementId id,
        DateOnly partitionKey,
        StorageLocation location,
        DateTimeOffset generatedAt,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marks a statement FAILED so it can be retried.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NO REASON PARAMETER, AND THE OMISSION IS DELIBERATE. There is no <c>failure_reason</c> column
    /// on <c>statement</c>, and adding one would put the explanation somewhere mutable, unversioned
    /// and overwritten by the next attempt.
    /// </para>
    /// <para>
    /// The reason belongs in the audit record the caller appends in this same transaction, where it
    /// is hash-chained, append-only, and keeps the history of every attempt rather than only the
    /// last. A parameter this method could not honestly persist would be worse than none.
    /// </para>
    /// </remarks>
    /// <param name="id">The statement identifier.</param>
    /// <param name="partitionKey">The statement's <c>period_start</c>.</param>
    /// <param name="transaction">The caller's transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Rows affected; zero means no such statement in that partition.</returns>
    Task<int> MarkFailedAsync(
        StatementId id,
        DateOnly partitionKey,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken);
}
