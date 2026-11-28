using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Repository.Format.Manifests;

/// <summary>
/// A reference run a file version lists did not read (ADR-0202 decision 4):
/// damage to the version, as a missing continuation is to its tree, and never
/// a shorter file. A reader that records where damage is names the run.
/// </summary>
public sealed class ReferenceRunMissingException : ManifestValidationException
{
    /// <summary>Creates the exception for the run at <paramref name="index"/> of its version's, <paramref name="runId"/>.</summary>
    public ReferenceRunMissingException(string message, ObjectId runId, int index)
        : base(message)
    {
        RunId = runId;
        Index = index;
    }

    /// <summary>Creates the exception; the message names the run.</summary>
    public ReferenceRunMissingException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with no detail.</summary>
    public ReferenceRunMissingException()
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    public ReferenceRunMissingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The run that did not read.</summary>
    public ObjectId RunId { get; }

    /// <summary>Where the run stands among its version's, from 0.</summary>
    public int Index { get; }
}
