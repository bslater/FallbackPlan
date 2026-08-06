namespace FallbackPlan.Storage.Abstractions;

/// <summary>
/// The outcome of a conditional put (docs/architecture/05-storage-providers.md
/// §2.2). "Already exists" is the most common <em>expected</em> outcome — an
/// idempotent retry of a write that in fact succeeded — which is why it is a
/// result, not an exception (NFR-PORT-004).
/// </summary>
public enum PutOutcome
{
    /// <summary>The object was created.</summary>
    Created,

    /// <summary>An object already exists at the key; store objects are immutable (specification 01 §4).</summary>
    AlreadyExists,

    /// <summary>A stated precondition did not hold.</summary>
    PreconditionFailed,
}

/// <summary>The result of <see cref="IObjectStore.PutAsync"/>.</summary>
public sealed record PutResult(PutOutcome Outcome);

/// <summary>The outcome of a delete.</summary>
public enum DeleteOutcome
{
    /// <summary>The object was deleted.</summary>
    Deleted,

    /// <summary>No object existed at the key — an expected outcome, not a fault.</summary>
    NotFound,

    /// <summary>A stated precondition did not hold.</summary>
    PreconditionFailed,
}

/// <summary>The result of <see cref="IObjectStore.DeleteAsync"/>.</summary>
public sealed record DeleteResult(DeleteOutcome Outcome);

/// <summary>Metadata for one stored object.</summary>
public sealed record ObjectMetadata(long Length, DateTimeOffset? LastModified);

/// <summary>
/// The result of <see cref="IObjectStore.GetMetadataAsync"/>: found with
/// metadata, or not found — as a result, not an exception (NFR-PORT-004).
/// </summary>
public sealed record GetMetadataResult
{
    /// <summary>The shared not-found result.</summary>
    public static readonly GetMetadataResult NotFound = new();

    private GetMetadataResult() => Metadata = null;

    /// <summary>Creates a found result.</summary>
    public GetMetadataResult(ObjectMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        Metadata = metadata;
    }

    /// <summary>Whether the object exists.</summary>
    public bool Found => Metadata is not null;

    /// <summary>The object's metadata when found.</summary>
    public ObjectMetadata? Metadata { get; }
}

/// <summary>How an open-read request resolved.</summary>
public enum OpenReadOutcome
{
    /// <summary>The object was opened; <see cref="OpenReadResult.Content"/> carries the stream.</summary>
    Found,

    /// <summary>No object exists at the key.</summary>
    NotFound,

    /// <summary>The requested range does not fall within the object.</summary>
    RangeNotSatisfiable,
}

/// <summary>
/// The result of <see cref="IObjectStore.OpenReadAsync"/>. The caller owns and
/// disposes <see cref="Content"/> when the outcome is
/// <see cref="OpenReadOutcome.Found"/>.
/// </summary>
public sealed record OpenReadResult : IDisposable
{
    /// <summary>The shared not-found result.</summary>
    public static readonly OpenReadResult NotFound = new(OpenReadOutcome.NotFound);

    /// <summary>The shared range-not-satisfiable result.</summary>
    public static readonly OpenReadResult RangeNotSatisfiable = new(OpenReadOutcome.RangeNotSatisfiable);

    private OpenReadResult(OpenReadOutcome outcome) => Outcome = outcome;

    /// <summary>Creates a found result over an open content stream.</summary>
    public OpenReadResult(Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Outcome = OpenReadOutcome.Found;
        Content = content;
    }

    /// <summary>How the request resolved.</summary>
    public OpenReadOutcome Outcome { get; }

    /// <summary>The content stream when found.</summary>
    public Stream? Content { get; }

    /// <inheritdoc />
    public void Dispose() => Content?.Dispose();
}
