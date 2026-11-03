using FallbackPlan.Domain;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// What the platform refuses where something already stands fails the item
/// it meets, and the run goes on to its receipt (FR-RST-004; ADR-0180
/// Amendment 7). A file an immutable flag keeps cannot be moved aside; a
/// folder an immutable flag keeps takes no new name, a folder or a file's
/// spool; an append-only folder lets nothing in it be renamed or removed, so
/// a file cannot be renamed into place from its spool, nor its spool removed
/// after. Each fails that item with the platform's words, and a spool the
/// folder will not let go is named. A run whose own folder cannot be made is
/// refused before anything is written. A move aside is a rename onto a refuge
/// nothing stands at, so one refused leaves nothing of its own under the
/// displaced folder, neither a copy at its refuge nor the folders it made,
/// and the run keeps those a move that landed made (ADR-0180 Amendment 10).
/// </summary>
/// <remarks>
/// The flags are set for real, as this platform and the account running the
/// suite may: immutable on macOS, or on Linux with <c>CAP_LINUX_IMMUTABLE</c>,
/// which CI's runner does not hold, so there these are inconclusive.
/// Append-only is held on Linux alone, whose rule for a folder is the one
/// stated here.
/// </remarks>
[TestClass]
public sealed class RestoreRefusedWhereItStandsTests : ArchiveTestHarness
{
    private const uint DarwinAppendOnly = 0x4;

    private static readonly RestoreTargetProfile Posix = new()
    {
        CaseSensitive = true,
        SupportsPosixMetadata = true,
        SupportsSymlinks = true,
    };

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "Linux and macOS keep a file's immutable flag")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task AFileAFlagKeeps_FailsAloneWhetherMovedAsideOrReplaced_AndTheRunKeepsItsReceipt()
    {
        RequireImmutable();
        var source = new FakeFileSystemSource();
        source.AddFile("data/kept.txt", Content(1));
        source.AddFile("data/other.txt", Content(2));

        foreach (var (policy, seed) in new[] { (ExistingDestinationPolicy.Preserve, (byte)0xB1), (ExistingDestinationPolicy.Replace, (byte)0xB2) })
        {
            var output = Path.Combine(SpoolDirectory, $"live-{seed:x2}");
            var kept = Standing(output, "data/kept.txt", "as it stands");
            Hold(kept);
            try
            {
                var receipt = await PublishAndRestoreAsync(source, seed, output, RestoreDestinationMode.InPlace, policy);

                var failed = Item(receipt, "data/kept.txt");
                Assert.AreEqual("failed", failed.Outcome, $"{policy}");
                Assert.IsNotNull(failed.Detail, $"{policy}: the platform's refusal is the item's reason");
                Assert.AreEqual("restored", Item(receipt, "data/other.txt").Outcome, $"{policy}: the run goes on");
                Assert.IsEmpty(receipt.Displaced, $"{policy}: nothing was moved aside");
                Assert.AreEqual("as it stands", File.ReadAllText(kept), $"{policy}");
                Assert.IsFalse(File.Exists(RestorePaths.SpoolBeside(kept, "restore")), $"{policy}: the folder lets its spool go");
                Assert.IsFalse(Directory.Exists(Path.Combine(output, ".fbp-displaced")), $"{policy}: no copy, and no folder, made for a move refused");
            }
            finally
            {
                DiskFlags.ClearUnder(output);
            }
        }
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "Linux and macOS keep a file's immutable flag, and a link needs no privilege")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task AMoveAsideRefused_LeavesNoFolderItMade_AndKeepsThoseAMoveThatLandedMade()
    {
        // Moving aside makes the file's folders under the displaced folder
        // first. A file, a link and a hard link's later name are each moved
        // aside their own way, and a flag refuses each; a.txt moves.
        RequireImmutable();
        var source = new FakeFileSystemSource();
        source.AddFile("a/ok.txt", Content(9));
        source.AddFile("b/kept.txt", Content(10));
        source.AddNode(new FakeFileSystemSource.Node
        {
            RelativePath = "c/link",
            Kind = ScanEntryKind.Symlink,
            LinkTarget = "../a/ok.txt"u8.ToArray(),
            Metadata = new EntryMetadata { ModifiedAt = 1_722_000_000_000 },
        });
        var linked = Content(11);
        source.AddFile("d/one.bin", linked, linkCount: 2, fileId: 900);
        source.AddFile("d/two.bin", linked, linkCount: 2, fileId: 900);
        var output = Path.Combine(SpoolDirectory, "live-b7");
        Standing(output, "a/ok.txt", "mine");
        foreach (var path in new[] { "b/kept.txt", "c/link", "d/two.bin" })
        {
            Hold(Standing(output, path, "kept"));
        }

        try
        {
            var receipt = await PublishAndRestoreAsync(source, 0xB7, output, RestoreDestinationMode.InPlace, ExistingDestinationPolicy.Preserve);

            Assert.AreEqual("a/ok.txt", Assert.ContainsSingle(receipt.Displaced));
            var displaced = Path.Combine(output, ".fbp-displaced", "refused-b7");
            Assert.AreEqual("mine", File.ReadAllText(Path.Combine(displaced, "a", "ok.txt")), "the move that landed keeps its folders");
            foreach (var path in new[] { "b/kept.txt", "c/link", "d/two.bin" })
            {
                var item = Item(receipt, path);
                Assert.AreEqual("failed", item.Outcome, path);
                Assert.StartsWith("what stands here was not moved aside: ", item.Detail!, StringComparison.Ordinal, path);
                Assert.IsFalse(Directory.Exists(Path.Combine(displaced, path[..1])), $"{path}: no copy, and no folder, made for a move refused");
            }
        }
        finally
        {
            DiskFlags.ClearUnder(output);
        }
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "the executor's cases here restore to a POSIX target")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task AMoveAside_NeverReplacesWhatAlreadyStandsAtItsRefuge()
    {
        // The move is a rename, which would replace its refuge, so it looks
        // first: a file the run's name already holds there stays, and the
        // file in the way stays where it stood. No flag is needed for this.
        var source = new FakeFileSystemSource();
        source.AddFile("b/kept.txt", Content(12));
        var output = Path.Combine(SpoolDirectory, "live-b8");
        var inTheWay = Standing(output, "b/kept.txt", "in the way");
        var earlier = Standing(output, ".fbp-displaced/refused-b8/b/kept.txt", "moved aside before");

        var receipt = await PublishAndRestoreAsync(source, 0xB8, output, RestoreDestinationMode.InPlace, ExistingDestinationPolicy.Preserve);

        var item = Item(receipt, "b/kept.txt");
        Assert.AreEqual("failed", item.Outcome);
        Assert.StartsWith("what stands here was not moved aside: ", item.Detail!, StringComparison.Ordinal);
        Assert.AreEqual("moved aside before", File.ReadAllText(earlier));
        Assert.AreEqual("in the way", File.ReadAllText(inTheWay));
        Assert.IsEmpty(receipt.Displaced);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "Linux and macOS keep a folder's immutable flag")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task AFolderAFlagKeeps_FailsWhatTheRunWouldMakeInIt_FolderOrFile_AndTheRunGoesOn()
    {
        RequireImmutable();
        var source = new FakeFileSystemSource();
        source.AddFile("locked/a.txt", Content(3));
        source.AddFile("locked/sub/b.txt", Content(4));
        source.AddFile("free/c.txt", Content(5));
        var output = Path.Combine(SpoolDirectory, "live-b3");
        var locked = Directory.CreateDirectory(Path.Combine(output, "locked")).FullName;
        Hold(locked);
        try
        {
            var receipt = await PublishAndRestoreAsync(source, 0xB3, output, RestoreDestinationMode.InPlace, ExistingDestinationPolicy.Preserve);

            foreach (var path in new[] { "locked/a.txt", "locked/sub", "locked/sub/b.txt" })
            {
                var item = Item(receipt, path);
                Assert.AreEqual("failed", item.Outcome, path);
                Assert.IsNotNull(item.Detail, path);
            }

            Assert.AreEqual("restored", Item(receipt, "free/c.txt").Outcome, "the run goes on past the folder");
            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(locked), "nothing was made in it");
        }
        finally
        {
            DiskFlags.ClearUnder(output);
        }
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "Linux's append-only folder lets a name be made in it but none renamed or removed")]
    [PlatformTrait(TestPlatforms.Linux)]
    public async Task AnAppendOnlyFolder_FailsEachFileItCannotRenameIntoPlace_AndNamesTheSpoolItKeeps()
    {
        RequireImmutable();
        var source = new FakeFileSystemSource();
        source.AddFile("logs/old.txt", Content(6));
        source.AddFile("logs/new.txt", Content(7));
        var output = Path.Combine(SpoolDirectory, "live-b4");
        var old = Standing(output, "logs/old.txt", "as it stands");
        var logs = Path.GetDirectoryName(old)!;
        Hold(logs, appendOnly: true);
        try
        {
            var receipt = await PublishAndRestoreAsync(source, 0xB4, output, RestoreDestinationMode.InPlace, ExistingDestinationPolicy.Preserve);

            foreach (var name in new[] { "old.txt", "new.txt" })
            {
                var item = Item(receipt, $"logs/{name}");
                Assert.AreEqual("failed", item.Outcome, name);
                var spool = RestorePaths.SpoolName(name, "restore");
                Assert.Contains(spool, item.Detail!, StringComparison.Ordinal);
                Assert.IsTrue(File.Exists(Path.Combine(logs, spool)), $"{name}: the folder keeps the spool the receipt names");
            }

            Assert.AreEqual("as it stands", File.ReadAllText(old));
            Assert.IsEmpty(receipt.Displaced);
        }
        finally
        {
            DiskFlags.ClearUnder(output);
        }
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "Linux and macOS keep a folder's immutable flag")]
    [PlatformTrait(TestPlatforms.Posix)]
    public async Task ARunWhoseOwnFolderCannotBeMade_IsRefusedBeforeAnythingIsWritten()
    {
        RequireImmutable();
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.txt", Content(8));

        // A quarantined run makes its own folder in the output; a run in
        // place makes the output itself where it does not stand.
        var chosen = Directory.CreateDirectory(Path.Combine(SpoolDirectory, "chosen-b5")).FullName;
        var parent = Directory.CreateDirectory(Path.Combine(SpoolDirectory, "parent-b6")).FullName;
        Hold(chosen);
        Hold(parent);
        try
        {
            foreach (var (output, mode, seed) in new[]
            {
                (chosen, RestoreDestinationMode.Quarantine, (byte)0xB5),
                (Path.Combine(parent, "missing"), RestoreDestinationMode.InPlace, (byte)0xB6),
            })
            {
                var receipt = await PublishAndRestoreAsync(source, seed, output, mode, ExistingDestinationPolicy.Preserve);

                Assert.IsTrue(receipt.Items.Count > 0, $"{mode}");
                Assert.IsTrue(receipt.Items.All(item => item.Outcome == "failed" && item.Detail is not null), $"{mode}: every item names the refusal");
            }

            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(chosen));
            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(parent));
        }
        finally
        {
            DiskFlags.ClearUnder(chosen);
            DiskFlags.ClearUnder(parent);
        }
    }

    private static void RequireImmutable()
    {
        if (!FileFlags.Settable(EntryKind.DirectoryPlaceholder).HasFlag(FileFlagBits.Immutable))
        {
            Assert.Inconclusive("This account may not set a file or a folder immutable: Linux needs CAP_LINUX_IMMUTABLE.");
        }
    }

    private static void Hold(string path, bool appendOnly = false)
    {
        if (OperatingSystem.IsMacOS())
        {
            DiskFlags.SetOnMacOs(path, appendOnly ? DarwinAppendOnly : DiskFlags.DarwinImmutable);
        }
        else
        {
            DiskFlags.AddOnLinux(path, appendOnly ? DiskFlags.LinuxAppendOnly : DiskFlags.LinuxImmutable);
        }
    }

    private static string Standing(string output, string path, string text)
    {
        var at = Path.Combine(output, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(at)!);
        File.WriteAllText(at, text);
        return at;
    }

    private static ReceiptItem Item(RestoreReceipt receipt, string path) =>
        receipt.Items.Single(item => item.Path == path);

    private static byte[] SnapshotOf(byte seed) => Enumerable.Repeat(seed, 16).ToArray();

    private static byte[] Content(byte seed) => [.. Enumerable.Range(0, 600).Select(i => (byte)(seed + i * 31))];

    private static SnapshotJob Job(FakeFileSystemSource source, byte seed) => new()
    {
        Source = source,
        Roots = [new ScanRoot("/")],
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = SnapshotOf(seed),
        NowUnixMilliseconds = 1_722_600_000_000,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "restore-refused-tests/1.0",
    };

    private async Task<RestoreReceipt> PublishAndRestoreAsync(
        FakeFileSystemSource source, byte seed, string output, RestoreDestinationMode mode, ExistingDestinationPolicy existing)
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, $"refused-{seed:x2}.db"), Repo);
        using (var credential = CreateCredential())
        {
            await new PublicationOrchestrator(
                    SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
                    new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
                    SpoolDirectory, FormatVersions.SealedDataPlane, observer: null, catalogue)
                .PublishAsync(Job(source, seed), CancellationToken.None);
        }

        var plan = RestorePlanner.Plan(catalogue, SnapshotOf(seed), string.Empty, Posix);
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        return await new RestoreExecutor(reader, Posix).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = mode,
                ExistingDestination = existing,
                RunId = $"refused-{seed:x2}",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);
    }
}
