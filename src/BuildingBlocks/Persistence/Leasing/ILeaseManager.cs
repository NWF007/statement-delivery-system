namespace StatementDelivery.Persistence.Leasing;

/// <summary>
/// Distributed leader election backed by the <c>distributed_lease</c> table.
/// </summary>
/// <remarks>
/// <para>
/// WHY NOT ADVISORY LOCKS. The obvious recipe for this is <c>pg_advisory_lock</c>. It is
/// unusable here: every service connects through PgBouncer in transaction pooling mode, so a
/// session-scoped advisory lock is acquired on one backend and the release is sent to a
/// different one. The lock then leaks until that backend dies, and nothing reports it.
/// See docs/adr/0008-pgbouncer-transaction-pooling.md.
/// </para>
/// <para>
/// The lease table has three properties advisory locks do not. It works through any pooler,
/// because acquire-and-renew is a single atomic statement rather than session state. It is
/// observable: SELECT the table and you can see who holds what and until when, which matters at
/// 3am. And the monotonic fence token lets a downstream resource reject a holder that was paused
/// - by a long GC, a hypervisor stall, a network partition - and resumed after its lease had
/// already been taken over.
/// </para>
/// </remarks>
public interface ILeaseManager
{
    /// <summary>
    /// Attempts to acquire, or renew, the named lease.
    /// </summary>
    /// <param name="leaseName">
    /// Stable name of the work being guarded, for example <c>retention-sweep</c>. The name is the
    /// primary key of the lease row.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A handle whose renewal heartbeat is already running, or <see langword="null"/> if another
    /// holder has a live lease. Null is the ordinary case for every replica but one, and is not
    /// an error.
    /// </returns>
    Task<ILeaseHandle?> TryAcquireAsync(string leaseName, CancellationToken cancellationToken);
}

/// <summary>
/// A held lease. Disposing releases it, which lets a standby take over immediately rather than
/// waiting out the remaining time to live.
/// </summary>
public interface ILeaseHandle : IAsyncDisposable
{
    /// <summary>Gets the name of the lease held.</summary>
    string LeaseName { get; }

    /// <summary>Gets the identity of this holder, as written to the lease row.</summary>
    string HolderId { get; }

    /// <summary>
    /// Gets the fence token issued when the lease was acquired. Strictly increasing per lease
    /// name across every acquisition by every holder, for all time.
    /// </summary>
    /// <remarks>
    /// Pass this to anything the leased work writes to, and have that resource reject a token
    /// lower than the highest it has already seen. That is what stops a stalled holder from
    /// waking up and acting on a lease that has since moved on - the failure mode a time-to-live
    /// alone cannot prevent, because the stalled process has no way to know it was stalled.
    /// </remarks>
    long FenceToken { get; }

    /// <summary>
    /// Gets a token that is cancelled the moment renewal fails, meaning the lease has been lost.
    /// </summary>
    /// <remarks>
    /// Leased work must observe this, not just the host's stopping token. Losing a lease is not
    /// shutdown: the process is healthy and another replica is now the leader, so continuing to
    /// work would mean two leaders running the same job.
    /// </remarks>
    CancellationToken LeaseLost { get; }
}
