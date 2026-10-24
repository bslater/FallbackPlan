using FallbackPlan.Domain;
using FallbackPlan.Repository.Resources;

namespace FallbackPlan.Repository;

/// <summary>
/// A write refused because the repository is at a format newer than this
/// build writes (specification 11 §5.3; ADR-0066 Amendment 1). The repository
/// opened, and reads of it go on; what an object written at that format must
/// carry is the newer build's to say, so this build writes none.
/// </summary>
public sealed class NewerFormatException : Exception
{
    /// <summary>Creates the refusal for a repository at <paramref name="effectiveFormatVersion"/>, naming both versions.</summary>
    /// <param name="effectiveFormatVersion">The repository's effective format version.</param>
    public NewerFormatException(ushort effectiveFormatVersion)
        : base(Strings.FormatNewerFormat_NotWritten(effectiveFormatVersion, FormatLimits.LatestFormatVersion))
    {
    }

    /// <summary>Creates the exception.</summary>
    public NewerFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with no detail.</summary>
    public NewerFormatException()
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    public NewerFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Refuses a write into a repository whose effective format this build
    /// does not implement. Every act that authors an object, or reclaims one
    /// on this build's reading of the index, asks this first.
    /// </summary>
    /// <param name="effectiveFormatVersion">The repository's effective format version.</param>
    /// <exception cref="NewerFormatException">The version is newer than this build writes.</exception>
    public static void ThrowIfNewer(ushort effectiveFormatVersion)
    {
        if (effectiveFormatVersion > FormatLimits.LatestFormatVersion)
        {
            throw new NewerFormatException(effectiveFormatVersion);
        }
    }
}
