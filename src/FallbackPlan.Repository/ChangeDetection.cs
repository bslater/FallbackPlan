using FallbackPlan.Domain;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Catalogue;

namespace FallbackPlan.Repository;

/// <summary>
/// The unchanged predicates shared by tree publication and the source
/// comparer, extracted so the preview a client shows and the decision the
/// next backup takes are one judgement, not two (FR-SVC-009).
/// </summary>
internal static class ChangeDetection
{
    /// <summary>
    /// Whether <paramref name="entry"/>'s content is provably the same as
    /// <paramref name="prior"/>'s — identity, size, and modification time
    /// all present and all equal.
    /// </summary>
    /// <remarks>
    /// All three must be present, not merely non-contradictory. A rebuilt
    /// catalogue holds no identities, so it disables both short-circuits
    /// rather than weakening either: without identity, size and time alone
    /// cannot tell an unchanged file from a different file at the same
    /// path.
    /// </remarks>
    internal static bool IsContentUnchanged(ScanEntry entry, CatalogueTreeEntry? prior) =>
        entry.Kind == ScanEntryKind.File &&
        prior is { EntryKind: EntryKind.File } &&
        prior.ModifiedAt is { } priorModified && entry.Metadata.ModifiedAt == priorModified &&
        prior.LogicalLength is { } priorLength && (ulong)entry.Length == priorLength &&
        prior.IdentityDevice is { } priorDevice && prior.IdentityFileId is { } priorFileId &&
        entry.Identity is { } identity &&
        identity.Device == priorDevice && identity.FileId == priorFileId;

    /// <summary>
    /// Whether the prior version states the same metadata this entry now
    /// carries.
    /// </summary>
    /// <remarks>
    /// A prior row with no digest is treated as changed. That is the
    /// conservative direction and it is cheap: the consequence is one
    /// manifest rewrite with no content read, whereas the other default
    /// would silently drop a metadata change to save it.
    /// </remarks>
    internal static bool IsMetadataUnchanged(ReadOnlyMemory<byte> digest, CatalogueTreeEntry prior) =>
        prior.MetadataDigest is { } recorded && recorded.Span.SequenceEqual(digest.Span);
}
