using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Repository.Format.Manifests;

/// <summary>
/// One version of one file (specification 06 §4, object type <c>0x02</c>).
/// Segment references are logical-only — no blob identifier, no physical
/// offset, ever (06 §3.1; standing constraint 3; FR-ARCH-010) — and the
/// codec enforces that structurally: the reference array has exactly three
/// positions and unknown manifest keys are rejected, so physical location
/// has nowhere to hide.
/// </summary>
public sealed record FileVersionManifest
{
    /// <summary>The entry kind (key 1).</summary>
    public required EntryKind EntryKind { get; init; }

    /// <summary>The entry name as the source reported it, raw bytes (key 2).</summary>
    public required ReadOnlyMemory<byte> Name { get; init; }

    /// <summary>The name's normalisation (key 3).</summary>
    public required NameNormalisation NameNormalisation { get; init; }

    /// <summary>The plaintext file length (key 4).</summary>
    public required ulong LogicalLength { get; init; }

    /// <summary>The ordered logical segment references (key 5; 06 §3).</summary>
    public required IReadOnlyList<SegmentReference> SegmentReferences { get; init; }

    /// <summary>Sparse extents materialised as zeroes on restore (key 6); empty when none.</summary>
    public IReadOnlyList<SparseExtent> SparseExtents { get; init; } = [];

    /// <summary>SHA-256 over the reconstructed plaintext, sparse zeroes included (key 7; 06 §4.2; FR-RST-002).</summary>
    public required ReadOnlyMemory<byte> WholeFileHash { get; init; }

    /// <summary>The segmentation profile this version was captured under (key 8).</summary>
    public required ushort SegmentationProfile { get; init; }

    /// <summary>The previous version's object identifier (key 9); null for a first version.</summary>
    public ObjectId? ParentVersion { get; init; }

    /// <summary>The metadata map (key 10; 06 §4.1).</summary>
    public EntryMetadata Metadata { get; init; } = EntryMetadata.Empty;

    /// <summary>The symlink target, raw bytes (key 11); null unless the entry is a symlink.</summary>
    public ReadOnlyMemory<byte>? LinkTarget { get; init; }

    /// <summary>The hardlink group identifier (key 12); null unless the file shares an inode.</summary>
    public ReadOnlyMemory<byte>? HardlinkGroup { get; init; }

    /// <summary>Capture diagnostics (key 13); present only when non-empty.</summary>
    public IReadOnlyList<string> CaptureDiagnostics { get; init; } = [];
}
