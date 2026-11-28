namespace FallbackPlan.Repository.Format.Manifests;

/// <summary>
/// A manifest that would encode to more than a metadata object may be
/// (specification 00 §8). Raised on encode only, and not a caller bug as other
/// violations are: a file of enough segment references and holes needs a
/// manifest this large, and the writer records the file as too large rather
/// than ending its run.
/// </summary>
public sealed class ManifestTooLargeException : ManifestValidationException
{
    /// <summary>Creates the exception for a manifest that would encode to <paramref name="encodedBytes"/> bytes.</summary>
    public ManifestTooLargeException(string message, long encodedBytes)
        : base(message)
    {
        EncodedBytes = encodedBytes;
    }

    /// <summary>Creates the exception; the message names the violated rule.</summary>
    public ManifestTooLargeException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with no detail.</summary>
    public ManifestTooLargeException()
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    public ManifestTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>What the manifest would have encoded to, in bytes.</summary>
    public long EncodedBytes { get; }
}
