namespace FallbackPlan.Storage.S3;

/// <summary>
/// The store could not be reached at all — no connection, no answer — after
/// every attempt (ADR-0091). An <see cref="IOException"/>, so every caller
/// that survives a store fault survives this one; a caller that tells an
/// outage from a refusal catches it first and records the pair unavailable,
/// which closes itself, rather than failed (FR-DEST-003).
/// </summary>
public sealed class S3StoreUnreachableException : IOException
{
    /// <summary>Creates an empty exception.</summary>
    public S3StoreUnreachableException()
    {
    }

    /// <summary>Creates the exception with its message.</summary>
    /// <param name="message">What could not be reached.</param>
    public S3StoreUnreachableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with its message and the transport's own failure.</summary>
    /// <param name="message">What could not be reached.</param>
    /// <param name="innerException">The transport's failure.</param>
    public S3StoreUnreachableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
