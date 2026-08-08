using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Format.Manifests;

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
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
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
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(options);

        var full = Path.GetFullPath(rootPath);
        if (!TryStat(full, out var rootStat) || !rootStat.IsDirectory)
        {
            throw new DirectoryNotFoundException($"'{rootPath}' is not a scannable directory.");
        }

        var rootEntry = BuildEntry(full, relativePath: "", "/"u8.ToArray(), rootStat, options);

        yield return new ScanEvent.EnterDirectory(rootEntry);
        await foreach (var scanEvent in WalkAsync(full, "", rootStat.Device, options, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return scanEvent;
        }

        yield return new ScanEvent.LeaveDirectory(rootEntry);
    }

    private async IAsyncEnumerable<ScanEvent> WalkAsync(
        string directory,
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
            children = ListChildren(directory);
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
            if (!RoundTrips(nameBytes))
            {
                yield return new ScanEvent.Failure(new ScanFailure(
                    relativePath,
                    CaptureFailureReason.NameNotRepresentable,
                    "The entry's name is not valid UTF-8 and cannot be represented by this host's string form."));
                continue;
            }

            var rulesSubject = relativePath.Normalize(NormalizationForm.FormC);

            if (options.Rules?.IsExcluded(rulesSubject) == true)
            {
                continue; // pruned by policy — not a failure, not an error-manifest entry (06 §8)
            }

            if (!TryStat(fullPath, out var stat))
            {
                yield return new ScanEvent.Failure(new ScanFailure(
                    relativePath, CaptureFailureReason.NotFound, "The entry vanished between listing and stat."));
                continue;
            }

            ScanEntry? entry = null;
            ScanFailure? entryFailure = null;
            try
            {
                entry = BuildEntry(fullPath, relativePath, nameBytes, stat, options);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                entryFailure = new ScanFailure(
                    relativePath,
                    exception is UnauthorizedAccessException ? CaptureFailureReason.Permission : CaptureFailureReason.IoError,
                    exception.Message);
            }

            if (entryFailure is not null || entry is null)
            {
                yield return new ScanEvent.Failure(entryFailure ?? new ScanFailure(
                    relativePath, CaptureFailureReason.IoError, "The entry could not be captured."));
                continue;
            }

            if (entry.Kind == ScanEntryKind.Directory)
            {
                if (!options.CrossMountBoundaries && stat.Device != rootDevice)
                {
                    // Stop at the boundary and say so (architecture 06 §1) —
                    // the directory appears, its contents do not.
                    var boundary = entry with
                    {
                        Diagnostics = [.. entry.Diagnostics, "mount-boundary: " + Probe(fullPath).Name],
                    };
                    yield return new ScanEvent.EnterDirectory(boundary);
                    yield return new ScanEvent.LeaveDirectory(boundary);
                    continue;
                }

                if (options.Rules?.MayDescend(rulesSubject) == false)
                {
                    continue;
                }

                yield return new ScanEvent.EnterDirectory(entry);
                await foreach (var scanEvent in WalkAsync(fullPath, relativePath, rootDevice, options, cancellationToken)
                    .ConfigureAwait(false))
                {
                    yield return scanEvent;
                }

                yield return new ScanEvent.LeaveDirectory(entry);
            }
            else
            {
                yield return new ScanEvent.Leaf(entry);
            }
        }
    }

    /// <summary>
    /// Lists one directory's children, taking each name's bytes from the
    /// filesystem rather than from a decoded string (06 §4.3).
    /// </summary>
    /// <remarks>
    /// On POSIX this is <c>readdir</c>. The managed enumeration hands back
    /// <see cref="string"/>, and a name that is not valid UTF-8 has already
    /// been destroyed by the time it arrives — decoded to U+FFFD, and
    /// re-encoding gives bytes that are not the ones on disk and do not open
    /// the file. On Windows a name is UTF-16 and its UTF-8 encoding is exact,
    /// so the managed enumeration is the source there.
    /// </remarks>
    private static List<(string Name, byte[] NameBytes, string FullPath)> ListChildren(string directory)
    {
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

    /// <summary>Whether a name survives the host's string form unchanged.</summary>
    /// <param name="nameBytes">The name as the filesystem reported it.</param>
    /// <returns><see langword="true"/> when decoding and re-encoding is lossless.</returns>
    private static bool RoundTrips(ReadOnlySpan<byte> nameBytes) =>
        Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(nameBytes)).AsSpan().SequenceEqual(nameBytes);

    private static ScanEntry BuildEntry(
        string fullPath, string relativePath, byte[] nameBytes, StatResult stat, ScanOptions options)
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

        var metadata = CaptureMetadata(fullPath, stat, options);

        ReadOnlyMemory<byte>? linkTarget = null;
        if (kind == ScanEntryKind.Symlink)
        {
            // A directory junction is a link too, and its target lives on
            // DirectoryInfo rather than FileInfo. Reading it through the wrong
            // one returns null, which would record a link with no target.
            FileSystemInfo info = Directory.Exists(fullPath)
                ? new DirectoryInfo(fullPath)
                : new FileInfo(fullPath);

            if (info.LinkTarget is { } target)
            {
                linkTarget = Encoding.UTF8.GetBytes(target);
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
        };
    }

    private static EntryMetadata CaptureMetadata(string fullPath, StatResult stat, ScanOptions options)
    {
        var xattrs = new List<ExtendedAttributeEntry>();
        if (options.CaptureExtendedAttributes && !OperatingSystem.IsWindows())
        {
            var names = OperatingSystem.IsMacOS()
                ? DarwinInterop.ListXattrNames(fullPath)
                : LinuxInterop.ListXattrNames(fullPath);
            foreach (var name in names)
            {
                var value = OperatingSystem.IsMacOS()
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
    public RevalidationProbe? Revalidate(ScanEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return TryStat(entry.FullPath, out var stat)
            ? new RevalidationProbe(stat.Size, stat.ModifiedAtMs)
            : null;
    }

    /// <inheritdoc />
    public Stream OpenRead(ScanEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
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
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrEmpty(streamName);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Alternate data streams exist on Windows only.");
        }

        return File.Open(entry.FullPath + ":" + streamName, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
        });
    }
}
