using StatementDelivery.Contracts;

namespace StatementDelivery.Messaging;

/// <summary>
/// Publishes an integration event. The only supported implementation writes to the
/// transactional outbox; nothing publishes to a broker directly.
/// </summary>
/// <remarks>
/// The abstraction exists so that the call site cannot choose to bypass the outbox.
/// Publishing directly from a request handler means the event can be lost when the
/// transaction rolls back, or emitted for a change that never committed - the classic
/// dual-write failure. Writing to the outbox inside the same transaction as the state
/// change makes both survive or neither.
/// </remarks>
public interface IIntegrationEventPublisher
{
    /// <summary>
    /// Enqueues <paramref name="integrationEvent"/> for publication as part of the caller's
    /// current database transaction.
    /// </summary>
    /// <typeparam name="TEvent">The concrete event contract type.</typeparam>
    /// <param name="integrationEvent">The event to publish.</param>
    /// <param name="transaction">
    /// The ambient transaction the state change is being written in. Required, not optional:
    /// an outbox write outside the business transaction is just a slower dual write.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask PublishAsync<TEvent>(
        TEvent integrationEvent,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken)
        where TEvent : IIntegrationEvent;
}
