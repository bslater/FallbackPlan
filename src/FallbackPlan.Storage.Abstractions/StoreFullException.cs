namespace FallbackPlan.Storage.Abstractions;

/// <summary>
/// A store has no room for an object (ADR-0012 Amendment 5): what another
/// attempt will not change, and what room made at the store will. A put it
/// refuses leaves no object behind, partial or whole (FR-QUOTA-002).
/// </summary>
public sealed class StoreFullException : StoreUnavailableException
{
    /// <summary>Creates an empty exception.</summary>
    public StoreFullException()
    {
    }

    /// <summary>Creates the exception with its message.</summary>
    /// <param name="message">The object there was no room for, and what the store answered.</param>
    public StoreFullException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with its message and the underlying failure.</summary>
    /// <param name="message">The object there was no room for, and what the store answered.</param>
    /// <param name="innerException">The underlying failure.</param>
    public StoreFullException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
