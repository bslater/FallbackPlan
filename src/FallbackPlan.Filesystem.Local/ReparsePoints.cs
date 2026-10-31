namespace FallbackPlan.Filesystem.Local;

/// <summary>
/// Which Windows reparse points capture reads as links (ADR-0183). A
/// symbolic link and a mount point, a junction among them, name another
/// path, and capture keeps that path and never goes through it. Every other
/// reparse point is how a filter keeps a file's or a folder's content, as
/// deduplication does, and is read as the file or the folder it is, through
/// the filter.
/// </summary>
internal static class ReparsePoints
{
    /// <summary><c>IO_REPARSE_TAG_SYMLINK</c>: a symbolic link, to a file or a folder.</summary>
    public const uint SymbolicLinkTag = 0xA000_000C;

    /// <summary><c>IO_REPARSE_TAG_MOUNT_POINT</c>: a junction, or a volume mounted on a folder.</summary>
    public const uint MountPointTag = 0xA000_0003;

    private const uint LinkMode = 0xA000;
    private const uint FolderMode = 0x4000;
    private const uint FileMode = 0x8000;

    /// <summary>
    /// The stat mode an entry is captured as: a link where it is a reparse
    /// point that names another path, or one whose tag could not be read;
    /// otherwise a folder or a file, as its directory bit says.
    /// </summary>
    /// <param name="attributes">The entry's attribute bits.</param>
    /// <param name="tag">Its reparse tag, or null where none was read.</param>
    /// <remarks>
    /// The reparse bit is tested before the directory bit, because a folder's
    /// link carries both, and a folder's link read as a folder would be
    /// descended into, out of the approved root (architecture 06 §2). A
    /// reparse point whose tag could not be read is held to the same, since
    /// what it is cannot be said.
    /// </remarks>
    public static uint ModeOf(FileAttributes attributes, uint? tag) =>
        (attributes & FileAttributes.ReparsePoint) != 0 && tag is null or SymbolicLinkTag or MountPointTag
            ? LinkMode
            : (attributes & FileAttributes.Directory) != 0 ? FolderMode : FileMode;
}
