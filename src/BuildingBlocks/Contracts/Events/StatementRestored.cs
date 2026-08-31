namespace StatementDelivery.Contracts.Events;

/// <summary>
/// An archive-restore completed: the statement can be downloaded again until the restored copy
/// expires.
/// </summary>
/// <remarks>
/// Published through the transactional outbox in the SAME transaction that marks the restore
/// request AVAILABLE, so "the restore completed" and "the caller will be told" commit or fail
/// together. Spartan on principle, like <see cref="StatementAvailable"/>: identifiers and dates
/// only — a notification channel must never become a data channel.
/// </remarks>
/// <param name="EventId">Unique identity of this occurrence. UUIDv7.</param>
/// <param name="OccurredAt">When the restore completed. UTC.</param>
/// <param name="RestoreId">The restore request this completes.</param>
/// <param name="StatementId">The statement.</param>
/// <param name="CustomerId">The owning customer.</param>
/// <param name="AvailableUntil">When the restored copy expires and the statement is cold again. Null when it does not expire.</param>
public sealed record StatementRestored(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid RestoreId,
    Guid StatementId,
    Guid CustomerId,
    DateTimeOffset? AvailableUntil) : IIntegrationEvent;
