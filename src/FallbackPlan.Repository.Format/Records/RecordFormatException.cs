namespace FallbackPlan.Repository.Format.Records;

/// <summary>
/// Thrown when record bytes violate the framing rules of specification 04 —
/// refused and reported, never repaired (specification 00 §10). A record with
/// <c>logical_length</c> zero is a damage finding: a conforming writer cannot
/// have produced it (04 §2.1).
/// </summary>
public sealed class RecordFormatException : FormatException
{
    /// <summary>Creates the exception with a default message.</summary>
    public RecordFormatException()
        : base("Record bytes violate the framing rules of specification 04.")
    {
    }

    /// <summary>Creates the exception with a message naming the violation.</summary>
    public RecordFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and cause.</summary>
    public RecordFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
