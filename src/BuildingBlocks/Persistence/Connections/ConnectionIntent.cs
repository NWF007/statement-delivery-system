namespace StatementDelivery.Persistence.Connections;

/// <summary>
/// Declares what a caller intends to do with a connection, and therefore which server it
/// may be routed to.
/// </summary>
/// <remarks>
/// <para>
/// Read/write splitting in this system is a CORRECTNESS concern, not only a performance one.
/// Routing the single-use download-token consume to a replica is not slow - it is wrong,
/// because replica lag would let the same token be redeemed twice.
/// </para>
/// <para>
/// <see cref="IDbConnectionFactory.OpenAsync"/> takes this with no default value on purpose.
/// Forcing every call site to state its intent is the whole point: a default would make
/// "I did not think about it" indistinguishable from "I decided".
/// </para>
/// </remarks>
public enum ConnectionIntent
{
    /// <summary>
    /// The caller will modify data. Always routed to the primary.
    /// </summary>
    Write = 0,

    /// <summary>
    /// The caller reads data it must see the latest committed version of - typically
    /// read-modify-write logic, or a read whose result gates a security decision.
    /// Always routed to the primary.
    /// </summary>
    ReadStrong = 1,

    /// <summary>
    /// The caller can tolerate replication lag: reporting, catalogue browsing, backfills.
    /// Routed to a replica when one is configured, and to the primary when one is not.
    /// The fallback is logged as a warning at startup so it is visible rather than silent.
    /// </summary>
    ReadEventual = 2,
}
