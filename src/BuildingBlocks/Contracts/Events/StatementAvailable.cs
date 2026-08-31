namespace StatementDelivery.Contracts.Events;

/// <summary>
/// A statement was rendered, encrypted, stored, and is available for download.
/// </summary>
/// <remarks>
/// <para>
/// THE FIRST REAL INTEGRATION EVENT, and deliberately spartan: identifiers and period only.
/// No balances, no line counts, no storage keys, no crypto material - a notification channel is
/// the easiest place for statement data to leak into systems that were never threat-modelled to
/// hold it. A consumer that needs more calls the API with the identifiers, under the API's
/// authorisation.
/// </para>
/// <para>
/// Written to the transactional outbox in the SAME transaction as the statement row, so "the
/// statement exists" and "the event will be published" commit or fail together. The relay
/// publishes at least once; <see cref="EventId"/> is the consumer's dedup key.
/// </para>
/// </remarks>
/// <param name="EventId">Unique identity of this occurrence. UUIDv7.</param>
/// <param name="OccurredAt">When the statement became available. UTC.</param>
/// <param name="StatementId">The statement.</param>
/// <param name="AccountId">The account it belongs to.</param>
/// <param name="CustomerId">The owning customer.</param>
/// <param name="PeriodStart">Statement period start.</param>
/// <param name="PeriodEnd">Statement period end.</param>
/// <param name="Version">The statement version. A regeneration publishes a new event.</param>
public sealed record StatementAvailable(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid StatementId,
    Guid AccountId,
    Guid CustomerId,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    int Version) : IIntegrationEvent;
