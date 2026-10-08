namespace FallbackPlan.Storage.Abstractions;

/// <summary>
/// A store answered every attempt its provider made at a request by asking to
/// be asked later: a throttle, or a server that erred or timed out
/// (ADR-0012 Amendment 5). The next pass may find it serving.
/// </summary>
public sealed class StoreBusyException : StoreUnavailableException
{
    /// <summary>Creates an empty exception.</summary>
    public StoreBusyException()
    {
    }

    /// <summary>Creates the exception with its message.</summary>
    /// <param name="message">The request, and what the store answered it with.</param>
    public StoreBusyException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with its message and the underlying failure.</summary>
    /// <param name="message">The request, and what the store answered it with.</param>
    /// <param name="innerException">The underlying failure.</param>
    public StoreBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
