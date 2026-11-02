using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Recovery;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The recovery tool gives a file's, a folder's and a link's flags back as
/// the engine's restore does, where this platform and the account running
/// it let it, and notes only those that did not land (FR-RST-004; ADR-0180
/// Amendment 5). A file's go on once the whole tree has landed, so each
/// later name of a hard-link group is made before its file can refuse one,
/// and a folder's last in its settle, after its times. Nothing is set
/// through a link put where a file landed, and nothing by a run that stops.
/// </summary>
/// <remarks>
/// The tool has no target but this platform, so these cases set flags for
/// real and say what this platform and the account running the suite may:
/// no-dump on Linux and macOS, immutable on macOS, or on Linux with
/// <c>CAP_LINUX_IMMUTABLE</c>, which CI's runner does not hold.
/// </remarks>
[TestClass]
public sealed class RecoveryFileFlagsTests : IDisposable
{
    private const ulong Shared = 900;

    private const ulong FolderModified = 1_721_500_000_000;

    private const string PassphraseText = "recovery-file-flags-passphrase!!";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-recovery-flags", Guid.NewGuid().ToString("n"));

    public RecoveryFileFlagsTests() => Directory.CreateDirectory(_root);

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "Linux and macOS keep a file's and a folder's flags")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task RecoveryRestore_GivesAFileItsGroupAndAFolderWhatThisPlatformMaySet_AndNotesTheRest()
    {
        var source = new FakeFileSystemSource();
        var folderFlags = FileFlagBits.Immutable | FileFlagBits.NoDump;
        source.Folders["docs"] = new EntryMetadata { ModifiedAt = FolderModified, FileFlags = folderFlags };
        var content = Deterministic(5_000, 1);
        foreach (var name in new[] { "docs/a.bin", "docs/b.bin" })
        {
            source.AddFile(name, content, linkCount: 2, fileId: Shared).Diagnostics = ["file-flags: immutable, no-dump"];
        }

        source.AddFile("docs/c.bin", Deterministic(700, 2)).Diagnostics = ["file-flags: no-dump, hidden"];
        var output = Path.Combine(_root, "restored");
        var settable = FileFlags.Settable(EntryKind.File);
        var grouped = (FileFlagBits.Immutable | FileFlagBits.NoDump) & settable;
        var single = (FileFlagBits.NoDump | FileFlagBits.Hidden) & settable;
        var folder = folderFlags & settable;

        var report = await RestoreIntoAsync(source, output);

        Assert.AreEqual((3, 0), (report.Restored, report.Failed));
        CollectionAssert.AreEqual(
            new[]
            {
                Noted("docs/a.bin", FileFlagBits.Immutable | FileFlagBits.NoDump, grouped),
                Noted("docs/b.bin", FileFlagBits.Immutable | FileFlagBits.NoDump, grouped),
                Noted("docs/c.bin", FileFlagBits.NoDump | FileFlagBits.Hidden, single),
                Noted("docs", folderFlags, folder),
            }.OfType<string>().ToArray(),
            report.Notes.Select(note => note.ToString()).ToArray());
        Assert.IsTrue(report.Notes.All(note => note.Kind == RecoveryNoteKind.FlagsNotApplied));
        Assert.AreEqual(grouped, OnDisk(Path.Combine(output, "docs", "a.bin")));
        Assert.AreEqual(single & ~FileFlagBits.Hidden, OnDisk(Path.Combine(output, "docs", "c.bin")));
        Assert.AreEqual(folder, OnDisk(Path.Combine(output, "docs")));
        Assert.AreEqual(
            DateTimeOffset.FromUnixTimeMilliseconds((long)FolderModified).UtcDateTime,
            Directory.GetLastWriteTimeUtc(Path.Combine(output, "docs")),
            "a folder's times went on before its flags");

        DiskFlags.ClearUnder(output);
        AssertOneFile(Path.Combine(output, "docs", "a.bin"), Path.Combine(output, "docs", "b.bin"));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a symlink needs no privilege to create only on a POSIX host")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task RecoveryRestore_SetsNoFlagThroughALinkPutWhereAFileLanded_AndNotesThem()
    {
        // The flags go on once the tree has landed, so whatever can write
        // beside a file has had the run to put a link in its place.
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.bin", Deterministic(700, 3)).Diagnostics = ["file-flags: no-dump"];
        var content = Deterministic(5_000, 4);
        foreach (var name in new[] { "docs/g1.bin", "docs/g2.bin" })
        {
            source.AddFile(name, content, linkCount: 2, fileId: Shared);
        }

        var output = Path.Combine(_root, "restored");
        var elsewhere = Path.Combine(_root, "elsewhere.bin");
        File.WriteAllBytes(elsewhere, [1]);

        var report = await RestoreIntoAsync(source, output, (existing, name) =>
        {
            var landed = Path.Combine(output, "docs", "a.bin");
            File.Delete(landed);
            File.CreateSymbolicLink(landed, elsewhere);
            return HardLinks.TryCreate(existing, name);
        });

        Assert.AreEqual("docs/a.bin: file flag(s) not applied: no-dump", Assert.ContainsSingle(report.Notes).ToString());
        Assert.AreEqual(FileFlagBits.None, OnDisk(elsewhere), "nothing is set on what the link points at");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a symlink needs no privilege to create only on a POSIX host")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task RecoveryRestore_SetsNoFlagThroughALinkPutWhereAFolderAboveAFileLanded()
    {
        // The platform's call refuses a link in the file's own place; one in
        // a folder's place above it is followed, so the tool must look.
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a/x.bin", Deterministic(700, 7)).Diagnostics = ["file-flags: no-dump"];
        var content = Deterministic(5_000, 8);
        foreach (var name in new[] { "docs/z1.bin", "docs/z2.bin" })
        {
            source.AddFile(name, content, linkCount: 2, fileId: Shared);
        }

        var output = Path.Combine(_root, "restored");
        var elsewhere = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;

        var report = await RestoreIntoAsync(source, output, (existing, name) =>
        {
            var folder = Path.Combine(output, "docs", "a");
            Directory.Move(folder, Path.Combine(output, "docs", "a-moved"));
            File.WriteAllBytes(Path.Combine(elsewhere, "x.bin"), [1]);
            Directory.CreateSymbolicLink(folder, elsewhere);
            return HardLinks.TryCreate(existing, name);
        });

        Assert.Contains("docs/a/x.bin: file flag(s) not applied: no-dump", report.Notes.Select(note => note.ToString()).ToArray());
        Assert.AreEqual(FileFlagBits.None, OnDisk(Path.Combine(elsewhere, "x.bin")), "nothing is set on what the folder's link leads to");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "Linux and macOS keep a file's and a folder's flags")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task RecoveryRestore_ThatStopsPartWay_SetsNoFlag()
    {
        // What a run that stops landed is left as a later run can write
        // into it: no file's flag and no folder's.
        var source = new FakeFileSystemSource();
        source.Folders["docs"] = new EntryMetadata { ModifiedAt = FolderModified, FileFlags = FileFlagBits.NoDump };
        source.AddFile("docs/a.bin", Deterministic(700, 5)).Diagnostics = ["file-flags: no-dump"];
        var content = Deterministic(5_000, 6);
        foreach (var name in new[] { "docs/g1.bin", "docs/g2.bin" })
        {
            source.AddFile(name, content, linkCount: 2, fileId: Shared);
        }

        var output = Path.Combine(_root, "restored");

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => RestoreIntoAsync(source, output, (_, _) => throw new OperationCanceledException()));

        Assert.AreEqual(FileFlagBits.None, OnDisk(Path.Combine(output, "docs", "a.bin")), "the file landed, and its flag waits");
        Assert.AreEqual(FileFlagBits.None, OnDisk(Path.Combine(output, "docs")));
    }

    public void Dispose()
    {
        DiskFlags.ClearUnder(_root);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>The note the tool gives <paramref name="path"/>, given back <paramref name="landed"/> of <paramref name="wanted"/>, or null where every one landed.</summary>
    private static string? Noted(string path, FileFlagBits wanted, FileFlagBits landed) =>
        MetadataRules.FileFlagsNotApplied(wanted, landed) is { } words ? $"{path}: {words}" : null;

    /// <summary>The immutable and no-dump flags the file or folder at <paramref name="path"/> carries.</summary>
    private static FileFlagBits OnDisk(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            var darwin = DiskFlags.OnMacOs(path);
            return ((darwin & DiskFlags.DarwinImmutable) != 0 ? FileFlagBits.Immutable : FileFlagBits.None)
                | ((darwin & DiskFlags.DarwinNoDump) != 0 ? FileFlagBits.NoDump : FileFlagBits.None);
        }

        var linux = DiskFlags.OnLinux(path);
        return ((linux & DiskFlags.LinuxImmutable) != 0 ? FileFlagBits.Immutable : FileFlagBits.None)
            | ((linux & DiskFlags.LinuxNoDump) != 0 ? FileFlagBits.NoDump : FileFlagBits.None);
    }

    private static void AssertOneFile(string first, string second)
    {
        var before = File.ReadAllBytes(first);
        File.AppendAllText(second, "+");
        Assert.HasCount(before.Length + 1, File.ReadAllBytes(first), $"{second} is a copy of {first}, not a second name for it");
    }

    private static byte[] Deterministic(int length, byte seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + i * 31);
        }

        return data;
    }

    /// <summary>
    /// Backs <paramref name="source"/> up at format 6 into a repository made
    /// from the passphrase, then restores its one snapshot into
    /// <paramref name="output"/> with the recovery session alone.
    /// </summary>
    private async Task<RecoveryRestoreReport> RestoreIntoAsync(
        FakeFileSystemSource source, string output, Func<string, string, bool>? makeLink = null)
    {
        var store = new LocalFileSystemObjectStore(Path.Combine(_root, "repo-" + Guid.NewGuid().ToString("n")));
        using (var passphrase = Passphrase.Create(PassphraseText))
        {
            var (repository, authority) = await RepositoryLifecycle.CreateFromPassphraseAsync(
                store, passphrase, RepositoryCreationSettings.Default,
                createdAtUnixMilliseconds: 1_722_600_000_000, CancellationToken.None);
            using var _repository = repository;
            using var _authority = authority;

            var spool = Directory.CreateDirectory(Path.Combine(_root, "spool-" + Guid.NewGuid().ToString("n"))).FullName;
            await new PublicationOrchestrator(
                    CapturePolicy.Default with { SegmentSize = SegmentSize.Create(64 * 1024) },
                    repository.RepositoryId,
                    WriterId.FromBytes(Enumerable.Repeat((byte)0xA0, 16).ToArray()),
                    repository.CurrentDataGeneration,
                    repository.Keys,
                    repository.Credential,
                    store,
                    new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
                    spool,
                    FormatVersions.FileFlagsAndLinkForms)
                .PublishAsync(
                    new SnapshotJob
                    {
                        Source = source,
                        Roots = [new ScanRoot("/")],
                        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                        SnapshotId = Enumerable.Repeat((byte)0x6A, 16).ToArray(),
                        NowUnixMilliseconds = 1_722_600_000_001,
                        DeclaredMaxDurationMs = 3_600_000,
                        ExpiryGeneration = 5,
                        ClientVersion = "recovery-file-flags/1.0",
                    },
                    CancellationToken.None);
        }

        using var open = Passphrase.Create(PassphraseText);
        using var session = await RecoverySession.OpenAsync(open, store, CancellationToken.None);
        await session.LoadBlobsAsync(CancellationToken.None);
        var snapshot = Assert.ContainsSingle(await session.ListSnapshotsAsync(CancellationToken.None));
        return await session.RestoreTreeAsync(snapshot.Manifest.RootTree, output, makeLink, CancellationToken.None);
    }
}
