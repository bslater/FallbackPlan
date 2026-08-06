namespace FallbackPlan.Repository.Format.Cbor;

/// <summary>
/// Thrown when CBOR input violates the deterministic encoding profile
/// (specification 00 §4.1–§4.2) or a caller-stated limit (specification 00
/// §8). Non-canonical input is refused, never accepted leniently: object
/// identifiers derive from encoded bytes, so a lenient decoder would silently
/// permit two encodings of the same object (NFR-PORT-003, NFR-COMP-004).
/// </summary>
public sealed class CborFormatException : FormatException
{
    /// <summary>Creates the exception with a default message.</summary>
    public CborFormatException()
        : base("CBOR input violates the deterministic encoding profile.")
    {
    }

    /// <summary>Creates the exception with a message naming the violation.</summary>
    public CborFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception wrapping the underlying decoder error.</summary>
    public CborFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
