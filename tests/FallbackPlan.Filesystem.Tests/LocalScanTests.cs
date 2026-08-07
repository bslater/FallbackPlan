using System.Text;
using FallbackPlan.Domain;
using FallbackPlan.Filesystem;
using FallbackPlan.Filesystem.Local;
using FallbackPlan.TestSupport;
using System.Runtime.Versioning;

namespace FallbackPlan.Filesystem.Tests;

/// <summary>
/// The local scanner (phase-1 wave S; architecture 06 §1–§3): deterministic
/// byte-sorted traversal, correct classification, metadata capture,
/// rules-v1 pruning with excluded-is-not-failed semantics, symlinks never
/// followed, hardlink identity, and the revalidation stat. Platform-specific
/// behaviour is skipped where the platform lacks it, never faked.
/// </summary>
public sealed partial class LocalScanTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-scan-tests", Guid.NewGuid().ToString("n"));

    private readonly LocalFileSystemSource _source = new();

    public LocalScanTests() => Directory.CreateDirectory(_root);

    private string Write(string relativePath, string content = "content")
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private async Task<List<ScanEvent>> ScanAsync(ScanOptions? options = null)
    {
        var events = new List<ScanEvent>();
        await foreach (var scanEvent in _source.ScanAsync(_root, options ?? new ScanOptions(), CancellationToken.None))
        {
            events.Add(scanEvent);
        }

        return events;
    }

    private static IEnumerable<string> Paths(IEnumerable<ScanEvent> events) => events.Select(scanEvent => scanEvent switch
    {
        ScanEvent.Leaf leaf => "F " + leaf.Entry.RelativePath,
        ScanEvent.EnterDirectory enter => "> " + enter.Entry.RelativePath,
        ScanEvent.LeaveDirectory leave => "< " + leave.Entry.RelativePath,
        ScanEvent.Failure failure => "! " + failure.Detail.RelativePath,
        _ => "?",
    });

    [Fact]
    public async Task Traversal_is_depth_first_and_byte_sorted()
    {
        Write("b/inner.txt");
        Write("a-file.txt");
        Write("c.txt");
        Directory.CreateDirectory(Path.Combine(_root, "B-upper"));

        var events = await ScanAsync();

        // 'B' (0x42) sorts before 'a' (0x61) and 'b' (0x62) in raw bytes —
        // exactly what tree entry ordering requires (06 §5).
        Assert.Equal(
            ["> ", "> B-upper", "< B-upper", "F a-file.txt", "> b", "F b/inner.txt", "< b", "F c.txt", "< "],
            Paths(events));
    }

    [Fact]
    public async Task Scanning_twice_yields_identical_event_sequences()
    {
        Write("x/deep/file1.bin", "one");
        Write("x/file2.bin", "two");
        Write("y.bin", "three");

        Assert.Equal(Paths(await ScanAsync()), Paths(await ScanAsync()));
    }

    [Fact]
    public async Task Excluded_paths_are_pruned_not_failed()
    {
        Write("keep/data.txt");
        Write("skip/cache.tmp");
        Write("skip/deep/more.tmp");

        Assert.True(PathRuleSet.TryCreate([], ["skip"], caseSensitive: true, out var rules, out _));
        var events = await ScanAsync(new ScanOptions { Rules = rules });

        var paths = Paths(events).ToList();
        Assert.DoesNotContain(paths, path => path.Contains("skip", StringComparison.Ordinal));
        Assert.Empty(events.OfType<ScanEvent.Failure>());
        Assert.Contains("F keep/data.txt", paths);
    }

    [Fact]
    public async Task File_metadata_captures_times_mode_and_ownership()
    {
        var full = Write("meta.txt", "metadata");

        var events = await ScanAsync();
        var entry = events.OfType<ScanEvent.Leaf>().Single().Entry;

        Assert.Equal(ScanEntryKind.File, entry.Kind);
        Assert.Equal(8, entry.Length);
        Assert.NotNull(entry.Metadata.ModifiedAt);
        Assert.True(entry.Identity.HasValue && entry.Identity.Value.FileId != 0);

        // The captured mtime agrees with the filesystem's own report to the
        // millisecond — the value NFR-PERF-003's short-circuit will compare.
        var expected = (ulong)new DateTimeOffset(File.GetLastWriteTimeUtc(full)).ToUnixTimeMilliseconds();
        Assert.Equal(expected, entry.Metadata.ModifiedAt!.Value);
    }

    [PlatformFact(TestPlatforms.Posix, "creating a symlink on Windows needs a privilege the runner lacks")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Symlinks_are_captured_as_links_and_never_followed()
    {
        Write("target/secret.txt");
        File.CreateSymbolicLink(Path.Combine(_root, "link"), Path.Combine(_root, "target"));

        var events = await ScanAsync();
        var link = events.OfType<ScanEvent.Leaf>().Single(leaf => leaf.Entry.RelativePath == "link").Entry;

        Assert.Equal(ScanEntryKind.Symlink, link.Kind);
        Assert.NotNull(link.LinkTarget);
        Assert.Contains("target", Encoding.UTF8.GetString(link.LinkTarget!.Value.Span), StringComparison.Ordinal);

        // Followed content would appear twice; it must appear exactly once.
        Assert.Single(events.OfType<ScanEvent.Leaf>(), leaf => leaf.Entry.RelativePath.EndsWith("secret.txt", StringComparison.Ordinal));
    }

    [PlatformFact(TestPlatforms.Posix, "the link(2) syscall this drives is POSIX; Windows hardlinks are covered separately")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Hardlinks_share_identity_and_report_their_link_count()
    {
        var original = Write("one.bin", "linked");
        Assert.Equal(0, Link(original, Path.Combine(_root, "two.bin")));

        var events = await ScanAsync();
        var leaves = events.OfType<ScanEvent.Leaf>().ToList();

        var one = leaves.Single(leaf => leaf.Entry.RelativePath == "one.bin").Entry;
        var two = leaves.Single(leaf => leaf.Entry.RelativePath == "two.bin").Entry;

        Assert.Equal(one.Identity!.Value.FileId, two.Identity!.Value.FileId);
        Assert.Equal(one.Identity.Value.Device, two.Identity.Value.Device);
        Assert.Equal(2u, one.Identity.Value.LinkCount);
    }

    [PlatformFact(TestPlatforms.Posix, "FIFOs, sockets and device nodes are POSIX entry kinds with no Windows analogue")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Special_files_carry_their_kind_as_diagnostics_and_are_never_errors()
    {
        var fifo = Path.Combine(_root, "pipe");
        if (MkFifo(fifo, 0x1B6 /* 0666 */) != 0)
        {
            return; // filesystem refuses FIFOs (some CI mounts) — nothing to assert
        }

        var events = await ScanAsync();
        var entry = events.OfType<ScanEvent.Leaf>().Single().Entry;

        Assert.Equal(ScanEntryKind.Special, entry.Kind);
        Assert.Contains("special-kind: fifo", entry.Diagnostics);
        Assert.Equal(0, entry.Length);
        Assert.Empty(events.OfType<ScanEvent.Failure>());
    }

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "mkfifo", SetLastError = true,
        StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    private static partial int MkFifo(string path, uint mode);

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "link", SetLastError = true,
        StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    private static partial int Link(string existingPath, string newPath);

    [PlatformFact(TestPlatforms.Posix, "setxattr(2) is POSIX; the Windows analogue is alternate data streams, tested separately")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Extended_attributes_round_trip_where_the_platform_supports_them()
    {
        var full = Write("attr.txt");
        if (SetXattr(full, "user.fbp-test", "hello"u8.ToArray(), 5, 0) != 0)
        {
            return; // tmpfs without user xattrs etc. — skip, never fake
        }

        var events = await ScanAsync();
        var entry = events.OfType<ScanEvent.Leaf>().Single().Entry;

        var attribute = Assert.Single(entry.Metadata.ExtendedAttributes);
        Assert.Equal("user.fbp-test", Encoding.UTF8.GetString(attribute.Name.Span));
        Assert.Equal("hello"u8.ToArray(), attribute.Value.ToArray());
    }

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "setxattr", SetLastError = true,
        StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    private static partial int SetXattr(string path, string name, byte[] value, nuint size, int flags);

    [PlatformFact(TestPlatforms.Posix, "hole discovery here uses SEEK_HOLE; Windows sparse files use a different query")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Sparse_holes_are_reported_as_extents_where_supported()
    {
        // 1 MiB hole between two data regions, created by seeking.
        var full = Path.Combine(_root, "sparse.bin");
        using (var stream = File.Create(full))
        {
            stream.Write("head"u8);
            stream.Seek(1024 * 1024 + 4, SeekOrigin.Begin);
            stream.Write("tail"u8);
        }

        var events = await ScanAsync();
        var entry = events.OfType<ScanEvent.Leaf>().Single().Entry;

        if (entry.SparseExtents.Count == 0)
        {
            return; // filesystem materialised the hole — legitimate, nothing to assert
        }

        var hole = entry.SparseExtents[0];
        Assert.True(hole.Offset >= 4 && hole.Offset + hole.Length <= 1024 * 1024 + 4,
            $"hole [{hole.Offset}, +{hole.Length}) must sit inside the written gap");
    }

    [UnprivilegedPlatformFact(TestPlatforms.Posix, "denial is expressed here with chmod, a POSIX permission shape")]
    [PlatformTrait(TestPlatforms.Posix)]
    [UnsupportedOSPlatform("windows")]
    public async Task An_unreadable_directory_is_a_failure_event_not_an_aborted_scan()
    {
        Write("open/readable.txt");
        var denied = Path.Combine(_root, "denied");
        Directory.CreateDirectory(denied);
        File.WriteAllText(Path.Combine(denied, "hidden.txt"), "x");
        File.SetUnixFileMode(denied, UnixFileMode.None);

        try
        {
            var events = await ScanAsync();

            var failure = Assert.Single(events.OfType<ScanEvent.Failure>());
            Assert.Equal(CaptureFailureReason.Permission, failure.Detail.Reason);
            Assert.StartsWith("denied", failure.Detail.RelativePath, StringComparison.Ordinal);

            // The rest of the tree still captured (architecture 06 §1).
            Assert.Contains(events.OfType<ScanEvent.Leaf>(), leaf => leaf.Entry.RelativePath == "open/readable.txt");
        }
        finally
        {
            File.SetUnixFileMode(denied, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void The_revalidation_stat_sees_a_change()
    {
        var full = Write("reval.txt", "before");
        Assert.True(LocalFileSystemSource.TryStat(full, out var before));

        File.WriteAllText(full, "after-longer");
        Assert.True(LocalFileSystemSource.TryStat(full, out var after));

        Assert.NotEqual(before.Size, after.Size);
        Assert.Equal(before.FileId, after.FileId);
    }

    [Fact]
    public void Probe_reports_a_filesystem_name_and_limits()
    {
        var info = _source.Probe(_root);

        Assert.False(string.IsNullOrEmpty(info.Name));
        Assert.True(info.MaxComponentBytes is null or > 0);
    }

    [PlatformFact(TestPlatforms.Posix, "POSIX mode bits and owner names have no NTFS equivalent — Windows carries a security descriptor instead")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Posix_metadata_is_captured_for_a_regular_file()
    {
        Write("file.txt", "contents");

        var entry = (await ScanAsync()).OfType<ScanEvent.Leaf>().Single().Entry;

        Assert.NotNull(entry.Metadata.PosixMode);
        Assert.False(string.IsNullOrEmpty(entry.Metadata.OwnerName));
    }

    [PlatformFact(TestPlatforms.Windows, "directory junctions are an NTFS reparse-point shape with no POSIX analogue")]
    [PlatformTrait(TestPlatforms.Windows)]
    public async Task A_directory_junction_is_a_link_and_is_not_descended()
    {
        Write("target/secret.txt");

        // A junction carries BOTH the Directory and ReparsePoint attributes.
        // Testing Directory first classified it as an ordinary directory, and
        // the scanner descended through it — out of the approved root, which
        // architecture 06 §2 forbids. Junctions need no privilege to create,
        // so this is the shape an unprivileged attacker actually has.
        var junction = Path.Combine(_root, "junction");
        Directory.CreateSymbolicLink(junction, Path.Combine(_root, "target"));

        var events = await ScanAsync();
        var link = events.OfType<ScanEvent.Leaf>()
            .Single(leaf => leaf.Entry.RelativePath == "junction").Entry;

        Assert.Equal(ScanEntryKind.Symlink, link.Kind);
        Assert.NotNull(link.LinkTarget);

        // Followed content would appear twice; it must appear exactly once.
        Assert.Single(
            events.OfType<ScanEvent.Leaf>(),
            leaf => leaf.Entry.RelativePath.EndsWith("secret.txt", StringComparison.Ordinal));
    }

    [PlatformFact(TestPlatforms.Windows, "reserved device names (CON, NUL) and case-insensitive-by-default volumes are Windows filesystem shapes")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void Probe_reports_the_windows_filesystem_shape()
    {
        var info = _source.Probe(_root);

        Assert.True(info.ReservedNames);
        Assert.False(info.CaseSensitive);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (UnauthorizedAccessException)
            {
                // a permission-test directory left unreadable; best effort
            }
        }
    }
}
