namespace FallbackPlan.Storage.Abstractions;

/// <summary>
/// A store on the far side of a network could not be reached at all — no
/// connection, no answer — after every attempt its provider makes
/// (ADR-0091, ADR-0093). An <see cref="IOException"/>, so every caller that
/// survives a store fault survives this one; a caller that tells an outage
/// from a refusal catches it first and records the pair unavailable, which
/// closes itself, rather than failed (FR-DEST-003).
/// </summary>
/// <remarks>
/// One type for every provider, so the service tells an outage from a
/// refusal once rather than once per API it speaks. Each provider throws a
/// sealed type of its own derived from this one, which names it in a log.
/// </remarks>
public class StoreUnreachableException : IOException
{
    /// <summary>Creates an empty exception.</summary>
    public StoreUnreachableException()
    {
    }

    /// <summary>Creates the exception with its message.</summary>
    /// <param name="message">What could not be reached.</param>
    public StoreUnreachableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with its message and the transport's own failure.</summary>
    /// <param name="message">What could not be reached.</param>
    /// <param name="innerException">The transport's failure.</param>
    public StoreUnreachableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
