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

    [PlatformFact(TestPlatforms.Posix, "the readdir ABI and non-UTF-8 filenames are POSIX shapes")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Names_come_from_the_filesystem_not_from_a_decoded_string()
    {
        // Names chosen to exercise the decode: ASCII, multi-byte UTF-8, and a
        // combining sequence that is a different byte string from its composed
        // form. All three are valid UTF-8, so the managed enumeration agrees —
        // which is the point. If the dirent name offset were wrong, this fails
        // loudly rather than silently mangling every filename in the tree.
        Write("plain.txt");
        Write("naïve-café.txt");
        Write("résumé.txt");

        var events = await ScanAsync();
        var scanned = events.OfType<ScanEvent.Leaf>()
            .Select(leaf => Encoding.UTF8.GetString(leaf.Entry.NameBytes.Span))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var expected = Directory.EnumerateFileSystemEntries(_root)
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, scanned);
    }

    [PlatformFact(TestPlatforms.Posix, "a filename that is not valid UTF-8 is only expressible on POSIX")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task A_name_that_is_not_valid_utf8_is_reported_rather_than_mangled()
    {
        // 0xFF 0xFE is not a valid UTF-8 sequence and is a perfectly legal
        // POSIX filename. Created through the syscall directly, because the
        // managed API cannot express it either.
        byte[] raw = [.. "bad"u8, (byte)0xFF, (byte)0xFE, .. "name.txt"u8];

        var path = RawPath(_root, raw);
        Assert.True(CreateFileWithRawName(path), "could not create a file with a non-UTF-8 name");

        try
        {
            var events = await ScanAsync();

        // Before this, the name decoded to U+FFFD, re-encoded to different
        // bytes, and the path built from it did not open the file — so the
        // entry was captured under a name it did not have, with content that
        // had never been read.
        var failure = Assert.Single(
            events.OfType<ScanEvent.Failure>(),
            f => f.Detail.Reason == CaptureFailureReason.NameNotRepresentable);
        Assert.Contains("a name the file does not have", failure.Detail.Detail, StringComparison.Ordinal);

            // And it is not also captured under a substituted name.
            Assert.DoesNotContain(
                events.OfType<ScanEvent.Leaf>(),
                leaf => leaf.Entry.NameBytes.Span.StartsWith("bad"u8));
        }
        finally
        {
            // Directory.Delete cannot remove it either — the same defect, from
            // the other end. The harness would otherwise fail on teardown.
            NativeUnlink(path);
        }
    }

    /// <summary>Builds a NUL-terminated path from a directory and raw name bytes.</summary>
    private static byte[] RawPath(string directory, byte[] nameBytes)
    {
        var path = new byte[Encoding.UTF8.GetByteCount(directory) + 1 + nameBytes.Length + 1];
        var written = Encoding.UTF8.GetBytes(directory, path);
        path[written++] = (byte)'/';
        nameBytes.CopyTo(path, written);
        path[written + nameBytes.Length] = 0;
        return path;
    }

    /// <summary>Creates a file whose name the managed API cannot express.</summary>
    private static bool CreateFileWithRawName(byte[] path)
    {
        var handle = NativeOpen(path, 0x40 | 0x1 /* O_CREAT | O_WRONLY */, 0x1A4 /* 0644 */);
        if (handle < 0)
        {
            return false;
        }

        NativeClose(handle);
        return true;
    }

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "unlink", SetLastError = true)]
    private static partial int NativeUnlink(byte[] pathname);

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "open", SetLastError = true)]
    private static partial int NativeOpen(byte[] pathname, int flags, uint mode);

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int NativeClose(int fd);

    [PlatformFact(TestPlatforms.Windows, "an unpaired surrogate is a UTF-16 filename shape; POSIX names are bytes")]
    [PlatformTrait(TestPlatforms.Windows)]
    public async Task A_name_with_an_unpaired_surrogate_is_reported_rather_than_substituted()
    {
        // U+D800 with nothing after it. Legal in an NTFS name, and it has no
        // UTF-8 encoding at all — Encoding.UTF8.GetBytes replaces it with
        // U+FFFD, which is why a bytes-only round-trip check cannot see it:
        // by the time the bytes exist, the substitution has happened.
        var lone = Path.Combine(_root, "bad\uD800name.txt");

        try
        {
            File.WriteAllText(lone, "content");
        }
        catch (Exception exception) when (exception is IOException or ArgumentException)
        {
            return; // the platform refused to create it — nothing to assert
        }

        var events = await ScanAsync();

        var failure = Assert.Single(
            events.OfType<ScanEvent.Failure>(),
            f => f.Detail.Reason == CaptureFailureReason.NameNotRepresentable);
        Assert.Contains("a name the file does not have", failure.Detail.Detail, StringComparison.Ordinal);

        // 06 §4.3 forbids the substitute specifically: an entry stored as
        // "bad�name.txt" looks captured, lists, and restores as a file
        // the user never had.
        Assert.DoesNotContain(
            events.OfType<ScanEvent.Leaf>(),
            leaf => Encoding.UTF8.GetString(leaf.Entry.NameBytes.Span).Contains('�'));
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

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "symlink", SetLastError = true)]
    private static partial int NativeSymlink(byte[] target, byte[] linkPath);

    /// <summary>
    /// Advances a live scan to one leaf and stops there, leaving the walk
    /// suspended so the entry's handle is still open. Everything about the
    /// handle-relative walk is a claim about that moment, and a scan already
    /// collected into a list has passed it.
    /// </summary>
    private async Task<(ScanEntry Entry, IAsyncEnumerator<ScanEvent> Walk)> PauseAtLeafAsync(string relativePath)
    {
        var walk = _source.ScanAsync(_root, new ScanOptions(), CancellationToken.None).GetAsyncEnumerator();

        while (await walk.MoveNextAsync())
        {
            if (walk.Current is ScanEvent.Leaf leaf && leaf.Entry.RelativePath == relativePath)
            {
                return (leaf.Entry, walk);
            }
        }

        await walk.DisposeAsync();
        throw new InvalidOperationException($"The scan never reached '{relativePath}'.");
    }

    [PlatformFact(TestPlatforms.Posix, "openat and fstatat are the POSIX handle-relative calls")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Content_is_read_from_the_handle_even_after_the_name_is_repointed()
    {
        Write("victim.bin", "the bytes that were classified");

        var (entry, walk) = await PauseAtLeafAsync("victim.bin");

        await using (walk.ConfigureAwait(false))
        {
            // The premise: this platform took a handle. Without one the rest
            // of this test would prove nothing about the handle-relative walk.
            Assert.NotNull(entry.ContentHandle);
            Assert.False(entry.ContentHandle!.IsInvalid);

            // The attack: between classification and read, the name is made to
            // refer to a different object. A path-based scanner re-opens the
            // name and stores the substitute's bytes under the original's
            // metadata — the time-of-check-to-time-of-use gap.
            var path = Path.Combine(_root, "victim.bin");
            File.Delete(path);
            File.WriteAllText(path, "bytes the attacker substituted");

            // The substitution took: the name now yields the attacker's bytes.
            Assert.Equal("bytes the attacker substituted", await File.ReadAllTextAsync(path));

            using (var reader = new StreamReader(_source.OpenRead(entry)))
            {
                Assert.Equal("the bytes that were classified", await reader.ReadToEndAsync());
            }

            // And revalidation describes the object that was read, not the one
            // now at the name — which is what makes it a revalidation.
            var probe = _source.Revalidate(entry);
            Assert.NotNull(probe);
            Assert.Equal(entry.Identity!.Value.FileId, probe!.Identity!.Value.FileId);
            Assert.Equal(entry.Length, probe.Length);
        }
    }

    [PlatformFact(TestPlatforms.Posix, "a link target is a byte string only on POSIX; Windows targets are UTF-16")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task A_link_target_that_is_not_valid_utf8_is_recorded_as_the_bytes_it_holds()
    {
        // A link target is under exactly the same rules as a name: bytes that
        // are not NUL. Decoding it to a string first replaces the invalid
        // sequence with U+FFFD, and the stored target then points somewhere
        // the link does not.
        byte[] target = [(byte)'.', (byte)'/', 0xFF, 0xFE, (byte)'x', 0x00];
        var linkPath = Encoding.UTF8.GetBytes(Path.Combine(_root, "dangling") + "\0");

        if (NativeSymlink(target, linkPath) != 0)
        {
            return; // the filesystem refused the link — nothing to assert
        }

        var events = await ScanAsync();
        var link = events.OfType<ScanEvent.Leaf>().Single(leaf => leaf.Entry.RelativePath == "dangling").Entry;

        Assert.Equal(ScanEntryKind.Symlink, link.Kind);
        Assert.Equal(target[..^1], link.LinkTarget!.Value.ToArray());
    }

    [PlatformFact(TestPlatforms.Posix, "O_NOFOLLOW on the descent is what refuses this, and it is POSIX")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task A_symlink_standing_where_a_directory_was_is_not_descended()
    {
        Directory.CreateDirectory(Path.Combine(_root, "outside"));
        File.WriteAllText(Path.Combine(_root, "outside", "secret.txt"), "not in the backup set");

        Directory.CreateDirectory(Path.Combine(_root, "inside"));
        File.CreateSymbolicLink(Path.Combine(_root, "inside", "escape"), Path.Combine(_root, "outside"));

        var events = await ScanAsync(new ScanOptions
        {
            Rules = PathRuleSet.TryCreate([], ["outside/**"], caseSensitive: true, out var rules, out _)
                ? rules
                : throw new InvalidOperationException("The exclusion rule did not compile."),
        });

        // The link is captured as a link, and nothing beneath its target is
        // reachable through it — the excluded tree stays excluded.
        var escape = events.OfType<ScanEvent.Leaf>()
            .Single(leaf => leaf.Entry.RelativePath == "inside/escape").Entry;

        Assert.Equal(ScanEntryKind.Symlink, escape.Kind);
        Assert.DoesNotContain(
            events.OfType<ScanEvent.Leaf>(),
            leaf => leaf.Entry.RelativePath.EndsWith("secret.txt", StringComparison.Ordinal));
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
