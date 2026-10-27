namespace FallbackPlan.Storage.Abstractions;

/// <summary>
/// A store refused to read an object it holds only in an archival tier, from
/// which it serves nothing until the object is rehydrated (ADR-0103). Not a
/// fault of the credential, and not a gap that closes on its own: a
/// rehydration is asked for by a person at the store and takes hours, so it
/// is not a <see cref="StoreUnavailableException"/>. A reader that holds
/// another copy reads around it.
/// </summary>
public sealed class StoreObjectArchivedException : IOException
{
    /// <summary>Creates an empty exception.</summary>
    public StoreObjectArchivedException()
    {
    }

    /// <summary>Creates the exception with its message.</summary>
    /// <param name="message">The object, the tier it is in, and what the store answered.</param>
    public StoreObjectArchivedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with its message and the underlying failure.</summary>
    /// <param name="message">The object, the tier it is in, and what the store answered.</param>
    /// <param name="innerException">The underlying failure.</param>
    public StoreObjectArchivedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
