using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Storage.AzureBlob;

/// <summary>
/// The Azure Blob store could not be reached at all — no connection, no
/// answer — after every attempt (ADR-0093). The service catches the
/// <see cref="StoreUnreachableException"/> it derives from, as it does every
/// provider's, and records the pair unavailable rather than failed
/// (FR-DEST-003).
/// </summary>
public sealed class AzureBlobStoreUnreachableException : StoreUnreachableException
{
    /// <summary>Creates an empty exception.</summary>
    public AzureBlobStoreUnreachableException()
    {
    }

    /// <summary>Creates the exception with its message.</summary>
    /// <param name="message">What could not be reached.</param>
    public AzureBlobStoreUnreachableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with its message and the transport's own failure.</summary>
    /// <param name="message">What could not be reached.</param>
    /// <param name="innerException">The transport's failure.</param>
    public AzureBlobStoreUnreachableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
