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
[TestClass]
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

    [TestMethod]
    public async Task Scan_ATreeOfDirectories_EmitsDepthFirstInByteOrder()
    {
        Write("b/inner.txt");
        Write("a-file.txt");
        Write("c.txt");
        Directory.CreateDirectory(Path.Combine(_root, "B-upper"));

        var events = await ScanAsync();

        // 'B' (0x42) sorts before 'a' (0x61) and 'b' (0x62) in raw bytes —
        // exactly what tree entry ordering requires (06 §5).
        SequenceAssert.AreEqual(
            ["> ", "> B-upper", "< B-upper", "F a-file.txt", "> b", "F b/inner.txt", "< b", "F c.txt", "< "],
            Paths(events));
    }

    [TestMethod]
    public async Task Scan_TheSameTreeTwice_EmitsIdenticalEventSequences()
    {
        Write("x/deep/file1.bin", "one");
        Write("x/file2.bin", "two");
        Write("y.bin", "three");

        SequenceAssert.AreEqual(Paths(await ScanAsync()), Paths(await ScanAsync()));
    }

    [TestMethod]
    public async Task Scan_APathAnExcludeRuleMatches_PrunesItWithoutFailing()
    {
        Write("keep/data.txt");
        Write("skip/cache.tmp");
        Write("skip/deep/more.tmp");

        Assert.IsTrue(PathRuleSet.TryCreate([], ["skip"], caseSensitive: true, out var rules, out _));
        var events = await ScanAsync(new ScanOptions { Rules = rules });

        var paths = Paths(events).ToList();
        Assert.DoesNotContain(path => path.Contains("skip", StringComparison.Ordinal), paths);
        Assert.IsEmpty(events.OfType<ScanEvent.Failure>());
        Assert.Contains("F keep/data.txt", paths);
    }

    [TestMethod]
    public async Task Scan_APosixFile_CapturesTimesModeAndOwnership()
    {
        var full = Write("meta.txt", "metadata");

        var events = await ScanAsync();
        var entry = events.OfType<ScanEvent.Leaf>().Single().Entry;

        Assert.AreEqual(ScanEntryKind.File, entry.Kind);
        Assert.AreEqual(8, entry.Length);
        Assert.IsNotNull(entry.Metadata.ModifiedAt);
        Assert.IsTrue(entry.Identity.HasValue && entry.Identity.Value.FileId != 0);

        // The captured mtime agrees with the filesystem's own report to the
        // millisecond — the value NFR-PERF-003's short-circuit will compare.
        var expected = (ulong)new DateTimeOffset(File.GetLastWriteTimeUtc(full)).ToUnixTimeMilliseconds();
        Assert.AreEqual(expected, entry.Metadata.ModifiedAt!.Value);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "creating a symlink on Windows needs a privilege the runner lacks")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Scan_TreeContainsASymlink_CapturesItAsALinkAndNeverFollowsIt()
    {
        Write("target/secret.txt");
        File.CreateSymbolicLink(Path.Combine(_root, "link"), Path.Combine(_root, "target"));

        var events = await ScanAsync();
        var link = events.OfType<ScanEvent.Leaf>().Single(leaf => leaf.Entry.RelativePath == "link").Entry;

        Assert.AreEqual(ScanEntryKind.Symlink, link.Kind);
        Assert.IsNotNull(link.LinkTarget);
        Assert.Contains("target", Encoding.UTF8.GetString(link.LinkTarget!.Value.Span), StringComparison.Ordinal);

        // Followed content would appear twice; it must appear exactly once.
        Assert.ContainsSingle(leaf => leaf.Entry.RelativePath.EndsWith("secret.txt", StringComparison.Ordinal), events.OfType<ScanEvent.Leaf>());
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "the link(2) syscall this drives is POSIX; Windows hardlinks are covered separately")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Scan_TwoNamesShareAnInode_ReportTheSameIdentityAndLinkCount()
    {
        var original = Write("one.bin", "linked");
        Assert.AreEqual(0, Link(original, Path.Combine(_root, "two.bin")));

        var events = await ScanAsync();
        var leaves = events.OfType<ScanEvent.Leaf>().ToList();

        var one = leaves.Single(leaf => leaf.Entry.RelativePath == "one.bin").Entry;
        var two = leaves.Single(leaf => leaf.Entry.RelativePath == "two.bin").Entry;

        Assert.AreEqual(one.Identity!.Value.FileId, two.Identity!.Value.FileId);
        Assert.AreEqual(one.Identity.Value.Device, two.Identity.Value.Device);
        Assert.AreEqual(2u, one.Identity.Value.LinkCount);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "FIFOs, sockets and device nodes are POSIX entry kinds with no Windows analogue")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Scan_TreeContainsAFifo_CapturesItsKindAsADiagnosticRatherThanAFailure()
    {
        var fifo = Path.Combine(_root, "pipe");
        if (MkFifo(fifo, 0x1B6 /* 0666 */) != 0)
        {
            return; // filesystem refuses FIFOs (some CI mounts) — nothing to assert
        }

        var events = await ScanAsync();
        var entry = events.OfType<ScanEvent.Leaf>().Single().Entry;

        Assert.AreEqual(ScanEntryKind.Special, entry.Kind);
        Assert.Contains("special-kind: fifo", entry.Diagnostics);
        Assert.AreEqual(0, entry.Length);
        Assert.IsEmpty(events.OfType<ScanEvent.Failure>());
    }

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "mkfifo", SetLastError = true,
        StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    private static partial int MkFifo(string path, uint mode);

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "link", SetLastError = true,
        StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    private static partial int Link(string existingPath, string newPath);

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "setxattr(2) is POSIX; the Windows analogue is alternate data streams, tested separately")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Scan_FileCarriesAnExtendedAttribute_CapturesItsNameAndValueUnchanged()
    {
        var full = Write("attr.txt");
        if (SetXattr(full, "user.fbp-test", "hello"u8.ToArray(), 5, 0) != 0)
        {
            return; // tmpfs without user xattrs etc. — skip, never fake
        }

        var events = await ScanAsync();
        var entry = events.OfType<ScanEvent.Leaf>().Single().Entry;

        var attribute = Assert.ContainsSingle(entry.Metadata.ExtendedAttributes);
        Assert.AreEqual("user.fbp-test", Encoding.UTF8.GetString(attribute.Name.Span));
        SequenceAssert.AreEqual("hello"u8.ToArray(), attribute.Value.ToArray());
    }

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "setxattr", SetLastError = true,
        StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    private static partial int SetXattr(string path, string name, byte[] value, nuint size, int flags);

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "hole discovery here uses SEEK_HOLE; Windows sparse files use a different query")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Scan_FileContainsASparseHole_ReportsTheHoleAsAnExtentInsideTheGap()
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
        Assert.IsTrue(hole.Offset >= 4 && hole.Offset + hole.Length <= 1024 * 1024 + 4,
            $"hole [{hole.Offset}, +{hole.Length}) must sit inside the written gap");
    }

    [TestMethod]
    [UnprivilegedPlatformCondition(TestPlatforms.Posix, "denial is expressed here with chmod, a POSIX permission shape")]
    [PlatformTrait(TestPlatforms.Posix)]
    [UnsupportedOSPlatform("windows")]
    public async Task Scan_ADirectoryIsUnreadable_RaisesAFailureEventAndKeepsScanning()
    {
        Write("open/readable.txt");
        var denied = Path.Combine(_root, "denied");
        Directory.CreateDirectory(denied);
        File.WriteAllText(Path.Combine(denied, "hidden.txt"), "x");
        File.SetUnixFileMode(denied, UnixFileMode.None);

        try
        {
            var events = await ScanAsync();

            var failure = Assert.ContainsSingle(events.OfType<ScanEvent.Failure>());
            Assert.AreEqual(CaptureFailureReason.Permission, failure.Detail.Reason);
            Assert.StartsWith("denied", failure.Detail.RelativePath, StringComparison.Ordinal);

            // The rest of the tree still captured (architecture 06 §1).
            Assert.Contains(leaf => leaf.Entry.RelativePath == "open/readable.txt", events.OfType<ScanEvent.Leaf>());
        }
        finally
        {
            File.SetUnixFileMode(denied, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [TestMethod]
    public void Revalidate_WhenTheFileChangedAfterTheScan_ShouldReportTheNewLengthAndTime()
    {
        var full = Write("reval.txt", "before");
        Assert.IsTrue(LocalFileSystemSource.TryStat(full, out var before));

        File.WriteAllText(full, "after-longer");
        Assert.IsTrue(LocalFileSystemSource.TryStat(full, out var after));

        Assert.AreNotEqual(before.Size, after.Size);
        Assert.AreEqual(before.FileId, after.FileId);
    }

    [TestMethod]
    public void Probe_ARealFilesystem_ReportsItsNameAndLimits()
    {
        var info = _source.Probe(_root);

        Assert.IsFalse(string.IsNullOrEmpty(info.Name));
        Assert.IsTrue(info.MaxComponentBytes is null or > 0);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "POSIX mode bits and owner names have no NTFS equivalent — Windows carries a security descriptor instead")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Scan_RegularFileOnPosix_CapturesItsModeBitsAndOwnerName()
    {
        Write("file.txt", "contents");

        var entry = (await ScanAsync()).OfType<ScanEvent.Leaf>().Single().Entry;

        Assert.IsNotNull(entry.Metadata.PosixMode);
        Assert.IsFalse(string.IsNullOrEmpty(entry.Metadata.OwnerName));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "the readdir ABI and non-UTF-8 filenames are POSIX shapes")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Scan_NamesUseMultiByteAndCombiningSequences_CapturesTheFilesystemBytes()
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

        SequenceAssert.AreEqual(expected, scanned);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a filename that is not valid UTF-8 is only expressible on POSIX")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Scan_NameIsNotValidUtf8_ReportsItRatherThanManglesIt()
    {
        // 0xFF 0xFE is not a valid UTF-8 sequence and is a perfectly legal
        // POSIX filename. Created through the syscall directly, because the
        // managed API cannot express it either.
        byte[] raw = [.. "bad"u8, (byte)0xFF, (byte)0xFE, .. "name.txt"u8];

        var path = RawPath(_root, raw);
        Assert.IsTrue(CreateFileWithRawName(path), "could not create a file with a non-UTF-8 name");

        try
        {
            var events = await ScanAsync();

        // Before this, the name decoded to U+FFFD, re-encoded to different
        // bytes, and the path built from it did not open the file — so the
        // entry was captured under a name it did not have, with content that
        // had never been read.
        var failure = Assert.ContainsSingle(f => f.Detail.Reason == CaptureFailureReason.NameNotRepresentable, events.OfType<ScanEvent.Failure>());
        Assert.Contains("a name the file does not have", failure.Detail.Detail, StringComparison.Ordinal);

            // And it is not also captured under a substituted name.
            Assert.DoesNotContain(leaf => leaf.Entry.NameBytes.Span.StartsWith("bad"u8), events.OfType<ScanEvent.Leaf>());
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

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "an unpaired surrogate is a UTF-16 filename shape; POSIX names are bytes")]
    [PlatformTrait(TestPlatforms.Windows)]
    public async Task Scan_NameHasAnUnpairedSurrogate_ReportsItRatherThanSubstitutesIt()
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

        var failure = Assert.ContainsSingle(f => f.Detail.Reason == CaptureFailureReason.NameNotRepresentable, events.OfType<ScanEvent.Failure>());
        Assert.Contains("a name the file does not have", failure.Detail.Detail, StringComparison.Ordinal);

        // 06 §4.3 forbids the substitute specifically: an entry stored as
        // "bad�name.txt" looks captured, lists, and restores as a file
        // the user never had.
        Assert.DoesNotContain(leaf => Encoding.UTF8.GetString(leaf.Entry.NameBytes.Span).Contains('�'), events.OfType<ScanEvent.Leaf>());
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "directory junctions are an NTFS reparse-point shape with no POSIX analogue")]
    [PlatformTrait(TestPlatforms.Windows)]
    public async Task Scan_TreeContainsADirectoryJunction_CapturesItAsALinkAndDoesNotDescend()
    {
        Write("target/secret.txt");

        // A junction carries BOTH the Directory and ReparsePoint attributes.
        // Testing Directory first classified it as an ordinary directory, and
        // the scanner descended through it — out of the approved root, which
        // architecture 06 §2 forbids. Junctions need no privilege to create,
        // so this is the shape an unprivileged attacker actually has.
        //
        // It has to be a real junction, which means mklink: the managed
        // Directory.CreateSymbolicLink makes a directory *symlink*, and that
        // does need SeCreateSymbolicLinkPrivilege — a privilege an ordinary
        // Windows account and an ordinary test runner both lack. Building the
        // link that way tested a shape the attacker cannot make and threw
        // "A required privilege is not held by the client" everywhere else.
        var junction = Path.Combine(_root, "junction");
        Assert.IsTrue(CreateJunction(junction, Path.Combine(_root, "target")), "could not create a directory junction");

        var events = await ScanAsync();
        var link = events.OfType<ScanEvent.Leaf>()
            .Single(leaf => leaf.Entry.RelativePath == "junction").Entry;

        Assert.AreEqual(ScanEntryKind.Symlink, link.Kind);
        Assert.IsNotNull(link.LinkTarget);

        // Followed content would appear twice; it must appear exactly once.
        Assert.ContainsSingle(leaf => leaf.Entry.RelativePath.EndsWith("secret.txt", StringComparison.Ordinal), events.OfType<ScanEvent.Leaf>());
    }

    /// <summary>
    /// Creates a directory junction at <paramref name="path"/>. <c>mklink</c>
    /// is a <c>cmd</c> built-in, so it has to be run through the shell; there
    /// is no managed API that makes a mount point rather than a symlink.
    /// </summary>
    private static bool CreateJunction(string path, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            ArgumentList = { "/c", "mklink", "/J", path, target },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });

        if (process is null)
        {
            return false;
        }

        process.WaitForExit(TimeSpan.FromSeconds(30));
        return process.ExitCode == 0 && Directory.Exists(path);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "reserved device names (CON, NUL) and case-insensitive-by-default volumes are Windows filesystem shapes")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void Probe_VolumeIsNtfs_ReportsReservedNamesAndCaseInsensitivity()
    {
        var info = _source.Probe(_root);

        Assert.IsTrue(info.ReservedNames);
        Assert.IsFalse(info.CaseSensitive);
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

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "openat and fstatat are the POSIX handle-relative calls")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Scan_NameIsRepointedAfterClassification_ReadsContentFromTheOpenHandle()
    {
        Write("victim.bin", "the bytes that were classified");

        var (entry, walk) = await PauseAtLeafAsync("victim.bin");

        await using (walk.ConfigureAwait(false))
        {
            // The premise: this platform took a handle. Without one the rest
            // of this test would prove nothing about the handle-relative walk.
            Assert.IsNotNull(entry.ContentHandle);
            Assert.IsFalse(entry.ContentHandle!.IsInvalid);

            // The attack: between classification and read, the name is made to
            // refer to a different object. A path-based scanner re-opens the
            // name and stores the substitute's bytes under the original's
            // metadata — the time-of-check-to-time-of-use gap.
            var path = Path.Combine(_root, "victim.bin");
            File.Delete(path);
            File.WriteAllText(path, "bytes the attacker substituted");

            // The substitution took: the name now yields the attacker's bytes.
            Assert.AreEqual("bytes the attacker substituted", await File.ReadAllTextAsync(path));

            using (var reader = new StreamReader(_source.OpenRead(entry)))
            {
                Assert.AreEqual("the bytes that were classified", await reader.ReadToEndAsync());
            }

            // And revalidation describes the object that was read, not the one
            // now at the name — which is what makes it a revalidation.
            var probe = _source.Revalidate(entry);
            Assert.IsNotNull(probe);
            Assert.AreEqual(entry.Identity!.Value.FileId, probe!.Identity!.Value.FileId);
            Assert.AreEqual(entry.Length, probe.Length);
        }
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link target is a byte string only on POSIX; Windows targets are UTF-16")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Scan_LinkTargetIsNotValidUtf8_RecordsTheExactBytesItHolds()
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

        Assert.AreEqual(ScanEntryKind.Symlink, link.Kind);
        SequenceAssert.AreEqual(target[..^1], link.LinkTarget!.Value.ToArray());
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "O_NOFOLLOW on the descent is what refuses this, and it is POSIX")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task Scan_ASymlinkStandsWhereADirectoryWas_DoesNotDescendThroughIt()
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

        Assert.AreEqual(ScanEntryKind.Symlink, escape.Kind);
        Assert.DoesNotContain(leaf => leaf.Entry.RelativePath.EndsWith("secret.txt", StringComparison.Ordinal), events.OfType<ScanEvent.Leaf>());
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
