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
/// What a repository puts at the folder a restore in place moves the
/// person's own files aside into (FR-RST-005; ADR-0152 Amendment 2). That
/// folder's name is fixed, so a repository can restore a link there before
/// it restores the file that displaces one. Each displaced path is resolved
/// under the output's root, as a restored path is, so a link there that
/// leads out is refused, and the person's file stays where it was.
/// </summary>
[TestClass]
public sealed class RestoreDisplacedContainmentTests : ArchiveTestHarness
{
    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "creating symlinks needs no privilege on POSIX; on Windows it does")]
    public async Task ALinkTheRepositoryPutsAtTheDisplacedFolder_LeadingOut_MovesNothingOutOfTheRestore()
    {
        var elsewhere = Directory.CreateDirectory(Path.Combine(SpoolDirectory, "elsewhere")).FullName;
        var source = new FakeFileSystemSource();
        source.AddNode(new FakeFileSystemSource.Node
        {
            RelativePath = ".fbp-displaced",
            Kind = ScanEntryKind.Symlink,
            LinkTarget = System.Text.Encoding.UTF8.GetBytes(elsewhere),
            Metadata = new EntryMetadata { ModifiedAt = 1_722_000_000_000 },
        });
        source.AddFile("data/mine.txt", Deterministic(64, 1), fileId: 9_401);
        var output = Path.Combine(SpoolDirectory, "displaced-out");
        Directory.CreateDirectory(Path.Combine(output, "data"));
        File.WriteAllText(Path.Combine(output, "data", "mine.txt"), "the person's own file");

        var (_, receipt) = await RestoreAsync(source, 0xCD, "displaced", output);

        Assert.IsEmpty(Directory.GetFiles(elsewhere, "*", SearchOption.AllDirectories), "a file was moved out of the restore");
        Assert.AreEqual("the person's own file", File.ReadAllText(Path.Combine(output, "data", "mine.txt")));
        var item = Assert.ContainsSingle(receipt.Items.Where(each => each.Path == "data/mine.txt"));
        Assert.AreEqual("failed", item.Outcome);
        Assert.Contains("symlink", item.Detail!, StringComparison.Ordinal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "creating symlinks needs no privilege on POSIX; on Windows it does")]
    public async Task ALinkTheRepositoryPutsAtTheDisplacedFolder_ALinkRestoredOverThePersonsFile_MovesNothingOut()
    {
        var elsewhere = Directory.CreateDirectory(Path.Combine(SpoolDirectory, "elsewhere-link")).FullName;
        var source = new FakeFileSystemSource();
        source.AddNode(new FakeFileSystemSource.Node
        {
            RelativePath = ".fbp-displaced",
            Kind = ScanEntryKind.Symlink,
            LinkTarget = System.Text.Encoding.UTF8.GetBytes(elsewhere),
            Metadata = new EntryMetadata { ModifiedAt = 1_722_000_000_000 },
        });
        source.AddNode(new FakeFileSystemSource.Node
        {
            RelativePath = "data/mine.lnk",
            Kind = ScanEntryKind.Symlink,
            LinkTarget = "mine.txt"u8.ToArray(),
            Metadata = new EntryMetadata { ModifiedAt = 1_722_000_000_000 },
        });
        var output = Path.Combine(SpoolDirectory, "displaced-link-out");
        Directory.CreateDirectory(Path.Combine(output, "data"));
        File.WriteAllText(Path.Combine(output, "data", "mine.lnk"), "the person's own file");

        var (_, receipt) = await RestoreAsync(source, 0xCB, "displaced-link", output);

        Assert.IsEmpty(Directory.GetFileSystemEntries(elsewhere, "*", SearchOption.AllDirectories), "an entry was moved out of the restore");
        Assert.AreEqual("the person's own file", File.ReadAllText(Path.Combine(output, "data", "mine.lnk")));
        var item = Assert.ContainsSingle(receipt.Items.Where(each => each.Path == "data/mine.lnk"));
        Assert.AreEqual("failed", item.Outcome);
        Assert.Contains("symlink", item.Detail!, StringComparison.Ordinal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "creating symlinks needs no privilege on POSIX; on Windows it does")]
    public async Task ALinkTheRepositoryPutsAtTheDisplacedFolder_AHardLinkedNameOverThePersonsFile_MovesNothingOut()
    {
        var elsewhere = Directory.CreateDirectory(Path.Combine(SpoolDirectory, "elsewhere-hard")).FullName;
        var source = new FakeFileSystemSource();
        source.AddNode(new FakeFileSystemSource.Node
        {
            RelativePath = ".fbp-displaced",
            Kind = ScanEntryKind.Symlink,
            LinkTarget = System.Text.Encoding.UTF8.GetBytes(elsewhere),
            Metadata = new EntryMetadata { ModifiedAt = 1_722_000_000_000 },
        });
        var content = Deterministic(64, 3);
        source.AddFile("data/a.bin", content, linkCount: 2, fileId: 9_403);
        source.AddFile("data/b.bin", content, linkCount: 2, fileId: 9_403);
        var output = Path.Combine(SpoolDirectory, "displaced-hard-out");
        Directory.CreateDirectory(Path.Combine(output, "data"));
        File.WriteAllText(Path.Combine(output, "data", "b.bin"), "the person's own file");

        var (_, receipt) = await RestoreAsync(source, 0xCA, "displaced-hard", output);

        Assert.IsEmpty(Directory.GetFiles(elsewhere, "*", SearchOption.AllDirectories), "a file was moved out of the restore");
        Assert.AreEqual("the person's own file", File.ReadAllText(Path.Combine(output, "data", "b.bin")));
        SequenceAssert.AreEqual(content, File.ReadAllBytes(Path.Combine(output, "data", "a.bin")));
        var item = Assert.ContainsSingle(receipt.Items.Where(each => each.Path == "data/b.bin"));
        Assert.AreEqual("failed", item.Outcome);
        Assert.Contains("symlink", item.Detail!, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AFileInTheWay_IsStillMovedAsideIntoTheRunsOwnFolder()
    {
        // The control: with nothing a repository put in the way, the
        // person's file is displaced where it always was.
        var source = new FakeFileSystemSource();
        var restored = Deterministic(64, 2);
        source.AddFile("data/mine.txt", restored, fileId: 9_402);
        var output = Path.Combine(SpoolDirectory, "aside-out");
        Directory.CreateDirectory(Path.Combine(output, "data"));
        File.WriteAllText(Path.Combine(output, "data", "mine.txt"), "the person's own file");

        var (_, receipt) = await RestoreAsync(source, 0xCC, "aside", output);

        SequenceAssert.AreEqual(restored, File.ReadAllBytes(Path.Combine(output, "data", "mine.txt")));
        Assert.AreEqual(
            "the person's own file",
            File.ReadAllText(Path.Combine(output, ".fbp-displaced", "aside-run", "data", "mine.txt")));
        Assert.Contains("data/mine.txt", receipt.Displaced);
    }

    /// <summary>
    /// Backs <paramref name="source"/> up as the snapshot <paramref name="seed"/>
    /// names and restores it in place into <paramref name="into"/>, with links
    /// where the platform makes them without a privilege.
    /// </summary>
    private async Task<(string Output, RestoreReceipt Receipt)> RestoreAsync(
        FakeFileSystemSource source, byte seed, string name, string into)
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, $"catalogue-{name}.db"), Repo);
        var spool = Directory.CreateDirectory(Path.Combine(SpoolDirectory, name)).FullName;
        await new PublicationOrchestrator(
                SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
                new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
                spool,
                FormatVersions.SealedDataPlane, observer: null, catalogue)
            .PublishAsync(
                new SnapshotJob
                {
                    Source = source,
                    Roots = [new ScanRoot("/")],
                    DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                    BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                    SnapshotId = Enumerable.Repeat(seed, 16).ToArray(),
                    NowUnixMilliseconds = 1_722_600_000_000,
                    DeclaredMaxDurationMs = 3_600_000,
                    ExpiryGeneration = 5,
                    ClientVersion = "restore-displaced-containment-tests/1.0",
                },
                CancellationToken.None);

        var target = RestoreTargetProfile.ForLocalPlatform() with { SupportsSymlinks = !OperatingSystem.IsWindows() };
        var plan = RestorePlanner.Plan(catalogue, Enumerable.Repeat(seed, 16).ToArray(), string.Empty, target);
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var output = into;
        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                RunId = name + "-run",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);
        return (output, receipt);
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
}
