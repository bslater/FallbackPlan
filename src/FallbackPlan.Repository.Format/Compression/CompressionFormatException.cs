namespace FallbackPlan.Repository.Format.Compression;

/// <summary>
/// Thrown when stored compressed bytes violate the compression rules of
/// specification 10 — a frame exceeding the record's logical length, a short
/// result, trailing bytes, or a malformed frame. Refused and reported, never
/// repaired (specification 00 §10).
/// </summary>
public sealed class CompressionFormatException : FormatException
{
    /// <summary>Creates the exception with a default message.</summary>
    public CompressionFormatException()
        : base("Stored bytes violate the compression rules of specification 10.")
    {
    }

    /// <summary>Creates the exception with a message naming the violation.</summary>
    public CompressionFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and cause.</summary>
    public CompressionFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
