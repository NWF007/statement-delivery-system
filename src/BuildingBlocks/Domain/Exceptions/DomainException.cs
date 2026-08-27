namespace StatementDelivery.Domain.Exceptions;

/// <summary>
/// Base type for every rule the domain enforces.
/// </summary>
/// <remarks>
/// <para>
/// DOMAIN EXCEPTIONS CARRY NO HTTP STATUS CODE AND NO USER-FACING TEXT, on purpose. The domain does
/// not know HTTP exists. It does not know whether it is being driven by a web request, a batch
/// worker, or a test.
/// </para>
/// <para>
/// Mapping to an RFC 9457 problem document happens in the web adapter, which is the only layer
/// that knows what a status code is. Putting a 400 in here would mean the same rule violation
/// became a 400 in a background worker, where nobody is listening.
/// </para>
/// <para>
/// The messages are for engineers reading logs. They are never returned to a caller - see
/// GlobalExceptionHandler, which replaces the detail outside Development.
/// </para>
/// </remarks>
public abstract class DomainException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="DomainException"/> class.</summary>
    /// <param name="message">Diagnostic message, for engineers rather than callers.</param>
    protected DomainException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance of the <see cref="DomainException"/> class.</summary>
    /// <param name="message">Diagnostic message.</param>
    /// <param name="innerException">The underlying cause.</param>
    protected DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A state transition the aggregate does not permit was attempted.
/// </summary>
/// <remarks>
/// Distinct from <see cref="InvariantViolationException"/> because the two mean different things
/// operationally: an invariant violation is usually bad input, while an illegal transition is
/// usually a concurrency bug or a lost update, and the second is far more interesting at 3am.
/// </remarks>
public sealed class InvalidStateTransitionException : DomainException
{
    /// <summary>Initialises a new instance of the <see cref="InvalidStateTransitionException"/> class.</summary>
    /// <param name="entity">The aggregate type name.</param>
    /// <param name="from">The current state.</param>
    /// <param name="to">The attempted state.</param>
    public InvalidStateTransitionException(string entity, string from, string to)
        : base($"{entity} cannot transition from {from} to {to}.")
    {
        Entity = entity;
        From = from;
        To = to;
    }

    /// <summary>Gets the aggregate type name.</summary>
    public string Entity { get; }

    /// <summary>Gets the state the aggregate was in.</summary>
    public string From { get; }

    /// <summary>Gets the state that was attempted.</summary>
    public string To { get; }
}

/// <summary>
/// A value object or entity was constructed in a state its invariants forbid.
/// </summary>
/// <remarks>
/// Thrown from factories rather than checked by callers, so that an invalid instance cannot exist
/// to be passed around. Invalid states are unrepresentable, not merely discouraged.
/// </remarks>
public sealed class InvariantViolationException : DomainException
{
    /// <summary>Initialises a new instance of the <see cref="InvariantViolationException"/> class.</summary>
    /// <param name="message">Which invariant was violated, and how.</param>
    public InvariantViolationException(string message)
        : base(message)
    {
    }
}
