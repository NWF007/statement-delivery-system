using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Npgsql;
using StatementDelivery.Contracts;

namespace StatementDelivery.Messaging.Outbox;

/// <summary>
/// The only <see cref="IIntegrationEventPublisher"/>: an INSERT into the transactional outbox,
/// on the caller's transaction.
/// </summary>
/// <remarks>
/// <para>
/// This is the write half of the pattern V004 built the table for. The relay
/// (Generation.Worker's <c>OutboxRelayService</c>) is the read half. Between them, "state
/// changed" and "event published" become one atomic fact plus one at-least-once delivery -
/// never a dual write.
/// </para>
/// <para>
/// The traceparent is captured HERE, at write time, from the ambient activity. It is what lets
/// the eventual consumer's span join the trace that rendered the statement, across minutes of
/// queue time and a process boundary.
/// </para>
/// </remarks>
public sealed class OutboxEventPublisher : IIntegrationEventPublisher
{
    /// <summary>
    /// Serialisation is deliberately unconfigured default System.Text.Json: property-name-preserving,
    /// no indentation. The payload is a CONTRACT - changing these options changes every payload,
    /// which is a wire-format version bump in disguise.
    /// </summary>
    private static readonly JsonSerializerOptions PayloadOptions = JsonSerializerOptions.Default;

    private const string InsertSql = """
        INSERT INTO outbox (id, event_type, payload, trace_parent)
        VALUES (@id, @eventType, @payload::jsonb, @traceParent);
        """;

    /// <summary>Command timeout for the single-row insert. Generous; the row is small.</summary>
    private const int CommandTimeoutSeconds = 30;

    /// <inheritdoc />
    public async ValueTask PublishAsync<TEvent>(
        TEvent integrationEvent,
        DbTransaction transaction,
        CancellationToken cancellationToken)
        where TEvent : IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        ArgumentNullException.ThrowIfNull(transaction);

        if (transaction is not NpgsqlTransaction npgsqlTransaction)
        {
            throw new ArgumentException(
                "The outbox lives in PostgreSQL; the transaction must be an NpgsqlTransaction.",
                nameof(transaction));
        }

        // Serialised as the CONCRETE type, so every declared property lands in the payload.
        // Serialising as TEvent would slice to whatever the generic constraint exposes.
        string payload = JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), PayloadOptions);

        _ = await npgsqlTransaction.Connection!.ExecuteAsync(new CommandDefinition(
            InsertSql,
            new
            {
                // The event's own id, not a fresh one: the outbox row and the event share an
                // identity, which is what makes consumer-side dedup line up with producer intent.
                id = integrationEvent.EventId,
                eventType = integrationEvent.GetType().Name,
                payload,
                traceParent = Activity.Current?.Id,
            },
            transaction: npgsqlTransaction,
            commandTimeout: CommandTimeoutSeconds,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
