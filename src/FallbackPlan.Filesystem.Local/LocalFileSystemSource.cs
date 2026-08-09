using Bodu;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Format.Manifests;
using Microsoft.Win32.SafeHandles;
using FallbackPlan.Filesystem.Local.Resources;

namespace FallbackPlan.Filesystem.Local;

/// <summary>
/// The cross-platform local filesystem source (architecture 06 §1–§3;
/// phase-1 wave S): streaming depth-first traversal in ascending raw-name
/// byte order, stable identity where the platform provides one, the
/// metadata matrix captured with degrade-and-report semantics, rules-v1
/// pruning, mount-boundary detection, and per-path failures collected as
/// error-manifest material — an unreadable file is an entry in the error
/// manifest, never a failed backup.
/// </summary>
/// <remarks>
/// Memory is bounded by tree depth and the widest single directory (its
/// sorted child list), never by file count. Symlinks are never followed.
/// Windows-specific capture (alternate streams, security descriptors) is
/// implemented here and exercised by the CI matrix.
/// </remarks>
public sealed class LocalFileSystemSource : IFileSystemSource
{
    /// <inheritdoc />
    public SourceFilesystemInfo Probe(string rootPath)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(rootPath);
        var full = Path.GetFullPath(rootPath);

        if (OperatingSystem.IsWindows())
        {
            string name;
            try
            {
                name = new DriveInfo(Path.GetPathRoot(full)!).DriveFormat.ToLowerInvariant();
            }
            catch (IOException)
            {
                name = "unknown";
            }

            return new SourceFilesystemInfo(
                CaseSensitive: false, SupportsSparse: name == "ntfs", Name: name,
                MaxPathBytes: 32_767 * 2, MaxComponentBytes: 255 * 2, ReservedNames: true);
        }

        var fsName = OperatingSystem.IsMacOS() ? "apfs" : LinuxFilesystemName(full);
        var sparse = fsName is "ext4" or "xfs" or "btrfs" or "zfs" or "apfs" or "tmpfs" or "overlay";

        return new SourceFilesystemInfo(
            CaseSensitive: !OperatingSystem.IsMacOS(),
            SupportsSparse: sparse,
            Name: fsName,
            MaxPathBytes: 4096,
            MaxComponentBytes: 255,
            ReservedNames: false);
    }

    private static string LinuxFilesystemName(string path)
    {
        // Longest mount-point prefix wins; /proc/mounts is authoritative and
        // cheap. Failure degrades to "unknown" — the probe is informational.
        try
        {
            var best = ("", "unknown");
            foreach (var line in File.ReadLines("/proc/mounts"))
            {
                var fields = line.Split(' ');
                if (fields.Length >= 3 && path.StartsWith(fields[1], StringComparison.Ordinal)
                    && fields[1].Length > best.Item1.Length)
                {
                    best = (fields[1], fields[2]);
                }
            }

            return best.Item2;
        }
        catch (IOException)
        {
            return "unknown";
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ScanEvent> ScanAsync(
        string rootPath, ScanOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(rootPath);
        ThrowHelper.ThrowIfNull(options);

        var full = Path.GetFullPath(rootPath);

        // The root's own descriptor, from which every descent below is taken.
        // Null on Windows, and on a POSIX root that cannot be opened as a
        // directory without following a link — both fall back to the
        // path-based walk, which is what this platform has always done.
        using var root = PosixDirectoryScope.TryOpenRoot(full);

        if (!(root is not null && root.TryStatSelf(out var rootStat)) &&
            (!TryStat(full, out rootStat) || !rootStat.IsDirectory))
        {
            throw new DirectoryNotFoundException(Strings.FormatLocalFileSystemSource_NotScannableDirectory(rootPath));
        }

        var rootEntry = BuildEntry(full, relativePath: "", "/"u8.ToArray(), rootStat, options, root);

        yield return new ScanEvent.EnterDirectory(rootEntry);
        await foreach (var scanEvent in WalkAsync(full, root, "", rootStat.Device, options, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return scanEvent;
        }

        yield return new ScanEvent.LeaveDirectory(rootEntry);
    }

    private async IAsyncEnumerable<ScanEvent> WalkAsync(
        string directory,
        PosixDirectoryScope? scope,
        string relativePrefix,
        ulong rootDevice,
        ScanOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<(string Name, byte[] NameBytes, string FullPath)> children = [];
        ScanFailure? listingFailure = null;
        try
        {
            children = ListChildren(directory, scope);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            listingFailure = new ScanFailure(
                relativePrefix,
                exception is UnauthorizedAccessException ? CaptureFailureReason.Permission : CaptureFailureReason.IoError,
                exception.Message);
        }

        if (listingFailure is not null)
        {
            yield return new ScanEvent.Failure(listingFailure);
            yield break;
        }

        // Ascending raw-name-byte order (06 §5): the tree writer needs it,
        // and doing it here makes the whole scan deterministic.
        children.Sort((left, right) => left.NameBytes.AsSpan().SequenceCompareTo(right.NameBytes));

        foreach (var (name, nameBytes, fullPath) in children)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = relativePrefix.Length == 0 ? name : relativePrefix + "/" + name;

            // The name the host gave us back is not the name on disk. Storing
            // it would put a file in the repository under a name it does not
            // have — and on POSIX the substituted path does not open the file
            // either, so the entry would look captured while its content was
            // never read. Reported, not guessed at (06 §4.3).
            if (!RoundTrips(name, nameBytes))
            {
                yield return new ScanEvent.Failure(new ScanFailure(
                    relativePath,
                    CaptureFailureReason.NameNotRepresentable,
                    "The entry's name has no faithful representation in both the host's string form "
                    + "and UTF-8, so capturing it would store a name the file does not have."));
                continue;
            }

            var rulesSubject = relativePath.Normalize(NormalizationForm.FormC);

            if (options.Rules?.IsExcluded(rulesSubject) == true)
            {
                continue; // pruned by policy — not a failure, not an error-manifest entry (06 §8)
            }

            // Handle-relative where the platform allows it: the name never
            // becomes a path, so nothing can be substituted underneath it.
            var stated = scope is not null
                ? scope.TryStat(nameBytes, out var stat)
                : TryStat(fullPath, out stat);

            if (!stated)
            {
                yield return new ScanEvent.Failure(new ScanFailure(
                    relativePath, CaptureFailureReason.NotFound, "The entry vanished between listing and stat."));
                continue;
            }

            if (stat.IsDirectory)
            {
                using var childScope = scope?.TryOpenChild(nameBytes);

                // The stat is re-taken from the descriptor once it is open.
                // Between the listing stat and the open, the name could have
                // been repointed; the descriptor is what the walk descends
                // into, so the descriptor is what must be described.
                if (childScope is not null && childScope.TryStatSelf(out var opened))
                {
                    stat = opened;
                }
                else if (scope is not null)
                {
                    yield return new ScanEvent.Failure(new ScanFailure(
                        relativePath,
                        CaptureFailureReason.Permission,
                        "The directory could not be opened without following a link."));
                    continue;
                }

                ScanEntry? directoryEntry = null;
                ScanFailure? directoryFailure = null;
                try
                {
                    directoryEntry = BuildEntry(
                        fullPath, relativePath, nameBytes, stat, options, childScope);
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                {
                    directoryFailure = ToFailure(relativePath, exception);
                }

                if (directoryEntry is null)
                {
                    yield return new ScanEvent.Failure(directoryFailure ?? new ScanFailure(
                        relativePath, CaptureFailureReason.IoError, "The entry could not be captured."));
                    continue;
                }

                if (!options.CrossMountBoundaries && stat.Device != rootDevice)
                {
                    // Stop at the boundary and say so (architecture 06 §1) —
                    // the directory appears, its contents do not.
                    var boundary = directoryEntry with
                    {
                        Diagnostics = [.. directoryEntry.Diagnostics, "mount-boundary: " + Probe(fullPath).Name],
                    };
                    yield return new ScanEvent.EnterDirectory(boundary);
                    yield return new ScanEvent.LeaveDirectory(boundary);
                    continue;
                }

                if (options.Rules?.MayDescend(rulesSubject) == false)
                {
                    continue;
                }

                yield return new ScanEvent.EnterDirectory(directoryEntry);
                await foreach (var scanEvent in WalkAsync(
                    fullPath, childScope, relativePath, rootDevice, options, cancellationToken).ConfigureAwait(false))
                {
                    yield return scanEvent;
                }

                yield return new ScanEvent.LeaveDirectory(directoryEntry);
                continue;
            }

            // A regular file is opened here and read from the handle. Anything
            // else — a link, a FIFO, a device — is described from its stat and
            // never opened: opening it would follow, block, or touch hardware,
            // and none of them carries content this format stores.
            using var content = scope is not null && stat.IsRegularFile
                ? scope.TryOpenContent(nameBytes)
                : null;

            if (scope is not null && stat.IsRegularFile)
            {
                if (content is null || content.IsInvalid)
                {
                    yield return new ScanEvent.Failure(new ScanFailure(
                        relativePath,
                        CaptureFailureReason.Permission,
                        "The file could not be opened without following a link."));
                    continue;
                }

                // Same reasoning as the directory case, and it matters more:
                // this stat describes the bytes that will actually be read.
                if (PosixDirectoryScope.StatHandle((int)content.DangerousGetHandle(), out var openedFile))
                {
                    stat = openedFile;
                }
            }

            ScanEntry? entry = null;
            ScanFailure? entryFailure = null;
            try
            {
                entry = BuildEntry(fullPath, relativePath, nameBytes, stat, options, scope, nameBytes, content);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                entryFailure = ToFailure(relativePath, exception);
            }

            yield return entry is null
                ? new ScanEvent.Failure(entryFailure ?? new ScanFailure(
                    relativePath, CaptureFailureReason.IoError, "The entry could not be captured."))
                : new ScanEvent.Leaf(entry);
        }
    }

    private static ScanFailure ToFailure(string relativePath, Exception exception) => new(
        relativePath,
        exception is UnauthorizedAccessException ? CaptureFailureReason.Permission : CaptureFailureReason.IoError,
        exception.Message);

    /// <summary>
    /// Lists one directory's children, taking each name's bytes from the
    /// filesystem rather than from a decoded string (06 §4.3).
    /// </summary>
    /// <remarks>
    /// On POSIX this is <c>readdir</c>. The managed enumeration hands back
    /// <see cref="string"/>, and a name that is not valid UTF-8 has already
    /// been destroyed by the time it arrives — decoded to U+FFFD, and
    /// re-encoding gives bytes that are not the ones on disk and do not open
    /// the file.
    /// </remarks>
    /// <remarks>
    /// On Windows a name is UTF-16, so the managed enumeration is the source
    /// there. Its UTF-8 encoding is exact for every name **except** one
    /// containing an unpaired surrogate, which has no UTF-8 encoding at all —
    /// <see cref="RoundTrips"/> is what catches those, and it has to look at
    /// the string rather than the bytes, because the substitution happens
    /// during the encode.
    /// </remarks>
    private static List<(string Name, byte[] NameBytes, string FullPath)> ListChildren(
        string directory, PosixDirectoryScope? scope)
    {
        // From the descriptor when the walk holds one: the directory being
        // listed is then the directory that was descended into, not whatever
        // the path names now.
        if (scope is not null && scope.TryListNames(out var fromHandle))
        {
            return [.. fromHandle.Select(bytes => (
                Name: Encoding.UTF8.GetString(bytes),
                NameBytes: bytes,
                FullPath: Path.Combine(directory, Encoding.UTF8.GetString(bytes))))];
        }

        if (PosixDirectory.IsSupported && PosixDirectory.TryReadNames(directory, out var rawNames))
        {
            return rawNames
                .Select(bytes => (
                    Name: Encoding.UTF8.GetString(bytes),
                    NameBytes: bytes,
                    FullPath: Path.Combine(directory, Encoding.UTF8.GetString(bytes))))
                .ToList();
        }

        return Directory.EnumerateFileSystemEntries(directory)
            .Select(path => (Name: Path.GetFileName(path), Path: path))
            .Select(child => (child.Name, Encoding.UTF8.GetBytes(child.Name), child.Path))
            .ToList();
    }

    /// <summary>Whether a name survives conversion between the two forms unchanged.</summary>
    /// <param name="name">The name as a host string.</param>
    /// <param name="nameBytes">The name as bytes.</param>
    /// <returns><see langword="true"/> when neither conversion loses anything.</returns>
    /// <remarks>
    /// <b>Both directions are checked, and each catches a different platform.</b>
    /// On POSIX the bytes are authoritative and the string is derived, so the
    /// bytes-first check is the one that bites: an invalid UTF-8 sequence
    /// decodes to U+FFFD and re-encodes to something else. On Windows the
    /// string is authoritative and the bytes are derived, so that check is
    /// trivially true and would have passed an unpaired surrogate straight
    /// through — the substitution has already happened by the time the bytes
    /// exist. The string-first check is what refuses it (06 §4.3).
    /// </remarks>
    private static bool RoundTrips(string name, ReadOnlySpan<byte> nameBytes) =>
        Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(nameBytes)).AsSpan().SequenceEqual(nameBytes)
        && string.Equals(
            Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(name)), name, StringComparison.Ordinal);

    /// <summary>
    /// Builds one entry from what is already open.
    /// </summary>
    /// <param name="fullPath">The reconstructed path, for the calls that still need one.</param>
    /// <param name="relativePath">The rules-and-trees path.</param>
    /// <param name="nameBytes">The final component's raw bytes.</param>
    /// <param name="stat">The stat taken from the handle where there is one.</param>
    /// <param name="options">Scanner switches.</param>
    /// <param name="scope">
    /// For a directory, its own open scope; for a leaf, its parent's — which
    /// is what <c>readlinkat</c> resolves the link name against.
    /// </param>
    /// <param name="nameInScope">The leaf's name within <paramref name="scope"/>; null for a directory.</param>
    /// <param name="content">An open handle on a regular file's content.</param>
    private static ScanEntry BuildEntry(
        string fullPath,
        string relativePath,
        byte[] nameBytes,
        StatResult stat,
        ScanOptions options,
        PosixDirectoryScope? scope = null,
        byte[]? nameInScope = null,
        SafeFileHandle? content = null)
    {
        var kind = stat switch
        {
            { IsDirectory: true } => ScanEntryKind.Directory,
            { IsRegularFile: true } => ScanEntryKind.File,
            { IsSymlink: true } => ScanEntryKind.Symlink,
            _ => ScanEntryKind.Special,
        };

        var diagnostics = new List<string>();
        if (kind == ScanEntryKind.Special)
        {
            diagnostics.Add("special-kind: " + stat switch
            {
                { IsFifo: true } => "fifo",
                { IsSocket: true } => "socket",
                { IsCharDevice: true } => "chardev",
                { IsBlockDevice: true } => "blockdev",
                _ => "unknown",
            });

            if (stat.IsCharDevice || stat.IsBlockDevice)
            {
                diagnostics.Add(string.Create(
                    CultureInfo.InvariantCulture, $"device: {stat.RdevMajor},{stat.RdevMinor}"));
            }
        }

        // Extended attributes come off the descriptor when there is one — the
        // same object the content came from, rather than whatever the path
        // resolves to by the time they are read.
        var metadataDescriptor = content is { IsInvalid: false }
            ? (int)content.DangerousGetHandle()
            : nameInScope is null && scope is not null ? scope.Descriptor : -1;

        var metadata = CaptureMetadata(fullPath, stat, options, metadataDescriptor);

        ReadOnlyMemory<byte>? linkTarget = null;
        if (kind == ScanEntryKind.Symlink)
        {
            // A link target is a byte string under the same rules as a name,
            // so it is read as bytes where the platform allows: one that is
            // not valid UTF-8 would otherwise be recorded as a target that
            // does not point where the link points.
            if (scope is not null && nameInScope is not null && scope.ReadLink(nameInScope) is { } target)
            {
                linkTarget = target;
            }
            else
            {
                // A directory junction is a link too, and its target lives on
                // DirectoryInfo rather than FileInfo. Reading it through the
                // wrong one returns null, which would record a link with no
                // target.
                FileSystemInfo info = Directory.Exists(fullPath)
                    ? new DirectoryInfo(fullPath)
                    : new FileInfo(fullPath);

                if (info.LinkTarget is { } managed)
                {
                    linkTarget = Encoding.UTF8.GetBytes(managed);
                }
            }
        }

        var name = Encoding.UTF8.GetString(nameBytes);
        var normalisation = name.IsNormalized(NormalizationForm.FormC)
            ? NameNormalisation.Nfc
            : name.IsNormalized(NormalizationForm.FormD) ? NameNormalisation.Nfd : NameNormalisation.Unknown;

        return new ScanEntry
        {
            RelativePath = relativePath,
            NameBytes = nameBytes,
            NameNormalisation = normalisation,
            Kind = kind,
            Length = kind == ScanEntryKind.File ? stat.Size : 0,
            Identity = new ScanIdentity(stat.Device, stat.FileId, stat.LinkCount),
            Metadata = metadata,
            LinkTarget = linkTarget,
            SparseExtents = kind == ScanEntryKind.File
                ? SparseProbe.FindHoles(fullPath, stat.Size)
                : [],
            AlternateStreamNames = kind == ScanEntryKind.File && options.CaptureAlternateStreams && OperatingSystem.IsWindows()
                ? WindowsInterop.ListAlternateStreams(fullPath)
                : [],
            Diagnostics = diagnostics,
            FullPath = fullPath,
            ContentHandle = content,
        };
    }

    private static EntryMetadata CaptureMetadata(
        string fullPath, StatResult stat, ScanOptions options, int descriptor)
    {
        var xattrs = new List<ExtendedAttributeEntry>();
        if (options.CaptureExtendedAttributes && !OperatingSystem.IsWindows())
        {
            var names = descriptor >= 0
                ? OperatingSystem.IsMacOS()
                    ? DarwinInterop.ListXattrNamesAt(descriptor)
                    : LinuxInterop.ListXattrNamesAt(descriptor)
                : OperatingSystem.IsMacOS()
                    ? DarwinInterop.ListXattrNames(fullPath)
                    : LinuxInterop.ListXattrNames(fullPath);

            foreach (var name in names)
            {
                var value = descriptor >= 0
                    ? OperatingSystem.IsMacOS()
                        ? DarwinInterop.GetXattrAt(descriptor, name)
                        : LinuxInterop.GetXattrAt(descriptor, name)
                    : OperatingSystem.IsMacOS()
                        ? DarwinInterop.GetXattr(fullPath, name)
                        : LinuxInterop.GetXattr(fullPath, name);

                if (value is not null)
                {
                    xattrs.Add(new ExtendedAttributeEntry(Encoding.UTF8.GetBytes(name), value));
                }
            }
        }

        return new EntryMetadata
        {
            ModifiedAt = stat.ModifiedAtMs,
            CreatedAt = stat.CreatedAtMs,
            AccessedAt = stat.AccessedAtMs,
            PosixMode = OperatingSystem.IsWindows() ? null : stat.PermissionBits,
            OwnerName = OperatingSystem.IsWindows() ? null : PosixNames.UserName(stat.Uid),
            GroupName = OperatingSystem.IsWindows() ? null : PosixNames.GroupName(stat.Gid),
            WindowsSecurityDescriptor =
                OperatingSystem.IsWindows() && options.CaptureSecurityDescriptors
                    ? WindowsInterop.GetSecurityDescriptor(fullPath)
                    : null,
            ExtendedAttributes = xattrs,
            FileAttributes = OperatingSystem.IsWindows()
                ? (uint)File.GetAttributes(fullPath)
                : null,
        };
    }

    /// <summary>
    /// The revalidation stat (architecture 06 §1): size, mtime, identity —
    /// compared before and after a read to detect mid-read change.
    /// </summary>
    public static bool TryStat(string path, out StatResult result)
    {
        if (OperatingSystem.IsWindows())
        {
            return TryStatWindows(path, out result);
        }

        return OperatingSystem.IsMacOS()
            ? DarwinInterop.TryStat(path, out result)
            : LinuxInterop.TryStat(path, out result);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool TryStatWindows(string path, out StatResult result)
    {
        result = default;
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists)
        {
            return false;
        }

        if (!WindowsInterop.TryGetIdentity(path, out var device, out var fileId, out var links))
        {
            device = 0;
            fileId = 0;
            links = 1;
        }

        var attributes = info.Attributes;

        // The reparse bit is tested FIRST, and the order is the whole point.
        // A directory junction or a directory symlink carries both bits, so
        // testing Directory first classified it as an ordinary directory and
        // the scanner would descend through it — out of the approved root,
        // which architecture 06 §2 forbids. A link is a link whatever it
        // points at.
        var mode = (attributes & FileAttributes.ReparsePoint) != 0
            ? 0xA000u
            : (attributes & FileAttributes.Directory) != 0 ? 0x4000u : 0x8000u;

        result = new StatResult(
            Device: device,
            FileId: fileId,
            LinkCount: links,
            Mode: mode,
            Uid: 0,
            Gid: 0,
            Size: info is FileInfo file && (attributes & FileAttributes.Directory) == 0 ? file.Length : 0,
            ModifiedAtMs: (ulong)new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
            CreatedAtMs: (ulong)new DateTimeOffset(info.CreationTimeUtc).ToUnixTimeMilliseconds(),
            AccessedAtMs: (ulong)new DateTimeOffset(info.LastAccessTimeUtc).ToUnixTimeMilliseconds(),
            RdevMajor: 0,
            RdevMinor: 0);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Against the handle when the scanner took one, and that is what makes
    /// this a revalidation rather than a second lookup: a descriptor names
    /// one inode for as long as it is open, so this stat describes exactly
    /// the object whose bytes were just read. Stat'ing the path again
    /// describes whatever is at the path now, which is not the same claim.
    /// </remarks>
    public RevalidationProbe? Revalidate(ScanEntry entry)
    {
        ThrowHelper.ThrowIfNull(entry);

        if (entry.ContentHandle is { IsInvalid: false, IsClosed: false } handle &&
            PosixDirectoryScope.StatHandle((int)handle.DangerousGetHandle(), out var fromHandle))
        {
            return new RevalidationProbe(
                fromHandle.Size,
                fromHandle.ModifiedAtMs,
                new ScanIdentity(fromHandle.Device, fromHandle.FileId, fromHandle.LinkCount));
        }

        // No handle — Windows, or a fallback walk. The identity still travels,
        // and it is worth more here than it is above: this stat describes
        // whatever the path names now, so a change means the name was
        // repointed while the file was being read.
        return TryStat(entry.FullPath, out var stat)
            ? new RevalidationProbe(
                stat.Size, stat.ModifiedAtMs, new ScanIdentity(stat.Device, stat.FileId, stat.LinkCount))
            : null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The handle is duplicated rather than handed over, because a file that
    /// changes mid-read is read again (ADR-0026 §Decision 2) and the stream
    /// from the first attempt has been disposed by then. Both attempts read
    /// the same inode; neither goes back through the name.
    /// </remarks>
    public Stream OpenRead(ScanEntry entry)
    {
        ThrowHelper.ThrowIfNull(entry);

        if (entry.ContentHandle is { IsInvalid: false, IsClosed: false } handle)
        {
            var duplicate = PosixHandleInterop.Duplicate((int)handle.DangerousGetHandle());
            if (duplicate >= 0)
            {
                return new FileStream(PosixHandleInterop.ToHandle(duplicate), FileAccess.Read);
            }
        }

        return File.Open(entry.FullPath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.SequentialScan,
        });
    }

    /// <inheritdoc />
    public Stream OpenAlternateStream(ScanEntry entry, string streamName)
    {
        ThrowHelper.ThrowIfNull(entry);
        ThrowHelper.ThrowIfNullOrEmpty(streamName);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(Strings.LocalFileSystemSource_AlternateDataStreamsExistWindows);
        }

        return File.Open(entry.FullPath + ":" + streamName, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
        });
    }
}
