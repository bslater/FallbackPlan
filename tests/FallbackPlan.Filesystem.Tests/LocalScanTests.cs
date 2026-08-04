using System.Text;
using FallbackPlan.Domain;
using FallbackPlan.Filesystem;
using FallbackPlan.Filesystem.Local;

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

        if (!OperatingSystem.IsWindows())
        {
            Assert.NotNull(entry.Metadata.PosixMode);
            Assert.False(string.IsNullOrEmpty(entry.Metadata.OwnerName));
        }

        // The captured mtime agrees with the filesystem's own report to the
        // millisecond — the value NFR-PERF-003's short-circuit will compare.
        var expected = (ulong)new DateTimeOffset(File.GetLastWriteTimeUtc(full)).ToUnixTimeMilliseconds();
        Assert.Equal(expected, entry.Metadata.ModifiedAt!.Value);
    }

    [Fact]
    public async Task Symlinks_are_captured_as_links_and_never_followed()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // creating symlinks on Windows CI requires privilege; the POSIX path proves the contract
        }

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

    [Fact]
    public async Task Hardlinks_share_identity_and_report_their_link_count()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

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

    [Fact]
    public async Task Special_files_carry_their_kind_as_diagnostics_and_are_never_errors()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

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

    [Fact]
    public async Task Extended_attributes_round_trip_where_the_platform_supports_them()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

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

    [Fact]
    public async Task Sparse_holes_are_reported_as_extents_where_supported()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

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

    [Fact]
    public async Task An_unreadable_directory_is_a_failure_event_not_an_aborted_scan()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
        {
            return; // chmod-based denial is a POSIX shape, and root ignores permission bits
        }

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
        if (OperatingSystem.IsWindows())
        {
            Assert.True(info.ReservedNames);
            Assert.False(info.CaseSensitive);
        }
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
