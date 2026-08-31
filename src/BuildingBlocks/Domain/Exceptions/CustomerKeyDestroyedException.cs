namespace StatementDelivery.Domain.Exceptions;

/// <summary>
/// A write was attempted for a customer whose key is destroyed or scheduled for destruction.
/// </summary>
/// <remarks>
/// The warm-cache write guard's signal (remediation Part G): a generation worker holding a
/// cached CEK can outlive the key's destruction by the cache's MaxAge, and without the
/// database-side guard it would publish a statement that becomes permanently unreadable the
/// moment the cache expires. SCHEDULED_DESTRUCTION counts too — writing new statements into a
/// cooling-off window creates data that is about to become unreadable, which is worse than
/// refusing. The run item that hits this must fail TERMINALLY: the condition is deterministic
/// and no retry changes it.
/// </remarks>
public sealed class CustomerKeyDestroyedException : DomainException
{
    /// <summary>Initialises a new instance of the <see cref="CustomerKeyDestroyedException"/> class.</summary>
    public CustomerKeyDestroyedException()
        : base("The customer's key is destroyed or scheduled for destruction; no new statement may be published for them.")
    {
    }

    /// <summary>Initialises a new instance of the <see cref="CustomerKeyDestroyedException"/> class.</summary>
    /// <param name="message">The message.</param>
    public CustomerKeyDestroyedException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance of the <see cref="CustomerKeyDestroyedException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public CustomerKeyDestroyedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
