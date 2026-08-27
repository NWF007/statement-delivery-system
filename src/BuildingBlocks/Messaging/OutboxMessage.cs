namespace StatementDelivery.Messaging;

/// <summary>
/// The row contract for the transactional outbox. Mirrors V004__outbox.sql exactly.
/// </summary>
/// <param name="Id">
/// UUIDv7 primary key. Time-ordered, so the relay reads the backlog in insertion order
/// with an index range scan rather than a sort.
/// </param>
/// <param name="CreatedAt">
/// Creation instant. This is the RANGE partition key: the outbox is partitioned daily so
/// that a published, past-grace-period day can be removed with DROP TABLE rather than a
/// DELETE that bloats the heap and churns WAL.
/// </param>
/// <param name="EventType">
/// Assembly-qualified-free contract name used to resolve the deserialisation target.
/// Renaming a published event type is a breaking change; add a new type instead.
/// </param>
/// <param name="Payload">The serialised event body (JSON, stored as jsonb).</param>
/// <param name="TraceParent">
/// W3C traceparent captured at write time. Carrying it through the outbox is what keeps a
/// trace connected across the asynchronous boundary; without it the consumer starts a new,
/// unrelated trace and the causal chain is lost exactly where it is most needed.
/// </param>
/// <param name="PublishedAt">
/// When the relay confirmed publication, or null while pending. The partial index
/// backing the relay query is defined WHERE published_at IS NULL, so it stays small
/// no matter how large the partition grows.
/// </param>
/// <param name="AttemptCount">Delivery attempts so far. Drives the relay backoff.</param>
public sealed record OutboxMessage(
    Guid Id,
    DateTimeOffset CreatedAt,
    string EventType,
    string Payload,
    string? TraceParent,
    DateTimeOffset? PublishedAt,
    int AttemptCount);
