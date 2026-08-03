namespace FallbackPlan.Repository.Format.Manifests;

/// <summary>
/// A manifest violates specification 06 — on decode this is a damage
/// finding (a conforming writer cannot produce one, 00 §8); on encode it is
/// a caller bug caught before any byte leaves the process.
/// </summary>
public sealed class ManifestValidationException : FormatException
{
    /// <summary>Creates the exception; the message names the violated rule.</summary>
    public ManifestValidationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with no detail.</summary>
    public ManifestValidationException()
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    public ManifestValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
