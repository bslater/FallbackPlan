using FallbackPlan.Domain;

namespace FallbackPlan.Repository.Format.Manifests;

/// <summary>
/// One contiguous stretch of a large file's segment references and holes,
/// in an object of its own (object type <c>0x12</c>; ADR-0202). A file version
/// manifest whose file has more than <see cref="ReferenceRunCodec.MaxPieces"/>
/// pieces lists its runs in order rather than holding its references, so each
/// run can be written as the file is read and the manifest last.
/// </summary>
public sealed record ReferenceRun
{
    /// <summary>Where in the file the stretch begins (key 1).</summary>
    public required ulong Start { get; init; }

    /// <summary>How much of the file the stretch covers (key 2).</summary>
    public required ulong Length { get; init; }

    /// <summary>The stretch's segment references, offsets in the file (key 3; 06 §3).</summary>
    public required IReadOnlyList<SegmentReference> SegmentReferences { get; init; }

    /// <summary>The stretch's holes, offsets in the file (key 4); empty when none.</summary>
    public IReadOnlyList<SparseExtent> SparseExtents { get; init; } = [];
}
