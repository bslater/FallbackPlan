namespace FallbackPlan.Repository.Packing;

/// <summary>
/// Thrown when blob bytes violate the container rules of specification 05 —
/// envelope, footer, record table, or locator. Refused and reported as a
/// damage finding, never repaired (specification 00 §10).
/// </summary>
public sealed class BlobFormatException : FormatException
{
    /// <summary>Creates the exception with a default message.</summary>
    public BlobFormatException()
        : base("Blob bytes violate the container rules of specification 05.")
    {
    }

    /// <summary>Creates the exception with a message naming the violation.</summary>
    public BlobFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and cause.</summary>
    public BlobFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
