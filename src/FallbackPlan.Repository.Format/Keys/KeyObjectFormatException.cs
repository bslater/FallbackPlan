namespace FallbackPlan.Repository.Format.Keys;

/// <summary>
/// Thrown when key-object bytes violate the framing rules of specification
/// 03 §3 — refused and reported, never repaired (specification 00 §10).
/// </summary>
public sealed class KeyObjectFormatException : FormatException
{
    /// <summary>Creates the exception with a default message.</summary>
    public KeyObjectFormatException()
        : base("Key-object bytes violate the framing rules of specification 03 §3.")
    {
    }

    /// <summary>Creates the exception with a message naming the violation.</summary>
    public KeyObjectFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and cause.</summary>
    public KeyObjectFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
