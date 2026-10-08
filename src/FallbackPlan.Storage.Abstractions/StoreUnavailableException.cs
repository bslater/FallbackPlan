namespace FallbackPlan.Storage.Abstractions;

/// <summary>
/// A store did not serve a request, and nothing about the request was wrong:
/// it could not be reached, it was too busy to serve, or it had no room. A gap
/// that closes itself when the store recovers, so a caller that catches this
/// records the pair unavailable rather than failed (FR-DEST-003), and tells
/// the three apart by type where what a person can do differs (FR-QUOTA-001).
/// </summary>
/// <remarks>
/// An <see cref="IOException"/>, so every caller that survives a store fault
/// survives this one. A refusal of the request itself, such as a credential
/// the store will not take or a quota it holds, is not this: it lasts until a
/// person changes something.
/// </remarks>
public class StoreUnavailableException : IOException
{
    /// <summary>Creates an empty exception.</summary>
    public StoreUnavailableException()
    {
    }

    /// <summary>Creates the exception with its message.</summary>
    /// <param name="message">What the store did not serve, and why.</param>
    public StoreUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with its message and the underlying failure.</summary>
    /// <param name="message">What the store did not serve, and why.</param>
    /// <param name="innerException">The underlying failure.</param>
    public StoreUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
