using FallbackPlan.Domain;
using FallbackPlan.Repository.Format.Manifests;
using Microsoft.Win32.SafeHandles;

namespace FallbackPlan.Filesystem;

/// <summary>What a scanned entry is, from the scanner's viewpoint.</summary>
public enum ScanEntryKind
{
    /// <summary>A regular file.</summary>
    File = 1,

    /// <summary>A directory.</summary>
    Directory = 2,

    /// <summary>A symbolic link — never followed unless the backup set approves.</summary>
    Symlink = 3,

    /// <summary>A FIFO, socket, or device node (ADR-0026 §Decision 4).</summary>
    Special = 4,
}

/// <summary>
/// The source filesystem's stable identity for one entry (architecture
/// 06 §1): device + file id so a rename is a rename, plus the link count
/// that makes hardlink groups detectable.
/// </summary>
public readonly record struct ScanIdentity(ulong Device, ulong FileId, uint LinkCount);

/// <summary>
/// One entry the scanner produced. Paths appear twice, deliberately: the
/// rules-and-trees form (relative, <c>/</c>-separated) and the OS form used
/// to open the entry. Name bytes and normalisation are captured per
/// architecture 06 §2 — never normalised destructively.
/// </summary>
public sealed record ScanEntry
{
    /// <summary>The <c>/</c>-separated relative path within the scan root — the rules-v1 matching subject.</summary>
    public required string RelativePath { get; init; }

    /// <summary>The final component's raw bytes (UTF-8 of the reported name).</summary>
    public required ReadOnlyMemory<byte> NameBytes { get; init; }

    /// <summary>The observed normalisation form of the name.</summary>
    public required NameNormalisation NameNormalisation { get; init; }

    /// <summary>What the entry is.</summary>
    public required ScanEntryKind Kind { get; init; }

    /// <summary>The logical length in bytes; 0 for directories, symlinks, and specials.</summary>
    public required long Length { get; init; }

    /// <summary>The stable identity, when the platform provides one.</summary>
    public ScanIdentity? Identity { get; init; }

    /// <summary>Everything the metadata matrix captured (specification 06 §4.1).</summary>
    public required EntryMetadata Metadata { get; init; }

    /// <summary>The symlink target's raw bytes; present only for symlinks.</summary>
    public ReadOnlyMemory<byte>? LinkTarget { get; init; }

    /// <summary>Sparse extents detected on the entry, empty when none or unsupported.</summary>
    public IReadOnlyList<SparseExtent> SparseExtents { get; init; } = [];

    /// <summary>Named alternate streams (name, length) — content is opened separately.</summary>
    public IReadOnlyList<(string Name, long Length)> AlternateStreamNames { get; init; } = [];

    /// <summary>ADR-0026 §Decision 2 diagnostics collected during capture.</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    /// <summary>The OS path used to open the entry's content.</summary>
    public required string FullPath { get; init; }

    /// <summary>
    /// An open, no-follow handle on the entry's content, where the source
    /// could take one; otherwise <see langword="null"/> and the entry is
    /// opened by <see cref="FullPath"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A handle is what makes "the object I stat'd is the object I read" a
    /// fact rather than an assumption. It names an inode for as long as it
    /// is open, so nothing can substitute another object between
    /// classification and read — the time-of-check-to-time-of-use gap
    /// architecture 06 §4.1 records.
    /// </para>
    /// <para>
    /// It belongs to the scanner and is valid only while the consumer is
    /// handling this entry's event: the scan stream is pulled one event at a
    /// time, and the handle closes when the walk moves on.
    /// </para>
    /// </remarks>
    public SafeFileHandle? ContentHandle { get; init; }
}

/// <summary>A per-path capture failure — an error-manifest entry, never an aborted snapshot.</summary>
public sealed record ScanFailure(string RelativePath, CaptureFailureReason Reason, string Detail);

/// <summary>
/// One event in the scan stream. Directories bracket their children —
/// <see cref="EnterDirectory"/> … children … <see cref="LeaveDirectory"/> —
/// so a consumer can build trees bottom-up with children durable before
/// parents (architecture 04 §3 invariant I1).
/// </summary>
public abstract record ScanEvent
{
    private ScanEvent()
    {
    }

    /// <summary>A directory was entered; its children follow in byte-sorted order.</summary>
    public sealed record EnterDirectory(ScanEntry Entry) : ScanEvent;

    /// <summary>All of the directory's surviving children have been emitted.</summary>
    public sealed record LeaveDirectory(ScanEntry Entry) : ScanEvent;

    /// <summary>A non-directory entry.</summary>
    public sealed record Leaf(ScanEntry Entry) : ScanEvent;

    /// <summary>A path that could not be captured (error-manifest material).</summary>
    public sealed record Failure(ScanFailure Detail) : ScanEvent;
}

/// <summary>Scanner behaviour switches. Defaults are the architecture's rules.</summary>
public sealed record ScanOptions
{
    /// <summary>The compiled include/exclude rules; null captures everything.</summary>
    public PathRuleSet? Rules { get; init; }

    /// <summary>
    /// Prepended (with a joining <c>/</c>) to the NFC relative path when
    /// consulting <see cref="Rules"/> — and to nothing else: emitted
    /// <see cref="ScanEntry.RelativePath"/>s are unchanged. A multi-root
    /// publication compiles its rules against label-prefixed subjects
    /// (ADR-0040), and this is how the walk's own pruning judges the same
    /// spelling the publisher will.
    /// </summary>
    public string? SubjectPrefix { get; init; }

    /// <summary>Whether to descend across mount points (default: stop and record, architecture 06 §1).</summary>
    public bool CrossMountBoundaries { get; init; }

    /// <summary>Total read attempts for a file that changes mid-read (ADR-0026 §Decision 2).</summary>
    public int ReadAttempts { get; init; } = 2;

    /// <summary>Whether to capture extended attributes.</summary>
    public bool CaptureExtendedAttributes { get; init; } = true;

    /// <summary>Whether to enumerate alternate data streams (Windows).</summary>
    public bool CaptureAlternateStreams { get; init; } = true;

    /// <summary>Whether to capture the Windows security descriptor.</summary>
    public bool CaptureSecurityDescriptors { get; init; } = true;
}

/// <summary>
/// The probed source filesystem: what the snapshot records (ADR-0022
/// §Decision 6 keys 1–3; ADR-0026 §Decision 7 keys 4–6) and what the
/// scanner itself needs to know.
/// </summary>
public sealed record SourceFilesystemInfo(
    bool CaseSensitive,
    bool SupportsSparse,
    string Name,
    uint? MaxPathBytes,
    uint? MaxComponentBytes,
    bool? ReservedNames);

/// <summary>
/// What a post-read revalidation observed (architecture 06 §1: stat before
/// and after reading; a difference means the content may be inconsistent
/// and the read is retried, then diagnosed — ADR-0026 §Decision 2).
/// </summary>
/// <param name="Length">The object's length after the read.</param>
/// <param name="ModifiedAtMs">Its modification time after the read.</param>
/// <param name="Identity">
/// The identity of the object that was actually read, where the source took
/// a handle. Size and modification time detect an ordinary edit; they do
/// not detect a substitution, which is what this catches — a mismatch means
/// the bytes just read did not come from the object the scanner classified.
/// </param>
public sealed record RevalidationProbe(long Length, ulong? ModifiedAtMs, ScanIdentity? Identity = null);

/// <summary>
/// A filesystem the engine can capture from (architecture 11 §6). One
/// implementation per platform family; the contracts know no platform.
/// </summary>
public interface IFileSystemSource
{
    /// <summary>Probes the filesystem holding <paramref name="rootPath"/>.</summary>
    SourceFilesystemInfo Probe(string rootPath);

    /// <summary>
    /// Re-stats an entry after its content was read; null when the entry no
    /// longer exists. The publisher compares against the scan-time values to
    /// detect a file that changed mid-read.
    /// </summary>
    RevalidationProbe? Revalidate(ScanEntry entry);

    /// <summary>
    /// Streams the tree under <paramref name="rootPath"/> depth-first,
    /// children in ascending raw-name-byte order, rules applied, memory
    /// bounded by tree depth and directory width — never by file count.
    /// </summary>
    IAsyncEnumerable<ScanEvent> ScanAsync(string rootPath, ScanOptions options, CancellationToken cancellationToken);

    /// <summary>Opens an entry's content for reading.</summary>
    Stream OpenRead(ScanEntry entry);

    /// <summary>Opens a named alternate stream's content (Windows; throws elsewhere).</summary>
    Stream OpenAlternateStream(ScanEntry entry, string streamName);
}
