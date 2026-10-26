using System.Globalization;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Format.Manifests;

namespace FallbackPlan.Restore;

/// <summary>
/// What the names of a hard-link group must agree on before a restore makes
/// them one file again (FR-RST-004; ADR-0102).
/// </summary>
/// <remarks>
/// A group is named by manifest key 12, which keys on the device's identity
/// and the file's, not on the filesystem's (ADR-0026 §1). Two files on two
/// filesystems can share one, so the group alone does not prove two names
/// were one file. They were where they captured the same content and length
/// and every attribute but the access time, which reading the first name
/// moves and which the metadata digest already holds back. A name that does
/// not agree is a file of its own.
/// </remarks>
internal static class RestoreHardLinks
{
    /// <summary>The group <paramref name="manifest"/> names, as text, or null for a file that is one of no group.</summary>
    public static string? GroupOf(FileVersionManifest manifest) =>
        manifest is { EntryKind: EntryKind.File, HardlinkGroup: { } group } ? Convert.ToHexString(group.Span) : null;

    /// <summary>
    /// What a name must agree on with the first name of its group to share
    /// that name's file, as text, or null for a file that is one of no group.
    /// </summary>
    public static string? KeyOf(FileVersionManifest manifest) =>
        GroupOf(manifest) is { } group
            ? string.Join(
                ':',
                group,
                Convert.ToHexString(manifest.WholeFileHash.Span),
                manifest.LogicalLength.ToString(CultureInfo.InvariantCulture),
                Convert.ToHexString(FileVersionManifestCodec.MetadataDigest(manifest.Metadata)))
            : null;
}
