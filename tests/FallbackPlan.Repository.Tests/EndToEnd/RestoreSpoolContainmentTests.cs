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
/// What a repository puts at the name a later file spools under (FR-RST-005;
/// ADR-0152 Amendment 1). A repository is untrusted, and the spool's name is
/// one anyone can work out from the file's. The executor removes whatever
/// stands there and makes the spool new, as the recovery tool does, so a
/// link there carries nothing out of the restore. What it cannot remove fails
/// that file alone, named, and the run goes on.
/// </summary>
[TestClass]
public sealed class RestoreSpoolContainmentTests : ArchiveTestHarness
{
    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "creating symlinks needs no privilege on POSIX; on Windows it does")]
    public async Task ALinkTheRepositoryPutsAtAFilesSpool_IsReplaced_AndNothingIsWrittenThroughIt()
    {
        var outside = Path.Combine(Directory.CreateDirectory(SpoolDirectory).FullName, "outside.txt");
        File.WriteAllText(outside, "not the restore's to write");
        var content = Deterministic(4_096, 9);
        var source = new FakeFileSystemSource();
        source.AddNode(new FakeFileSystemSource.Node
        {
            RelativePath = "data/" + SpoolNameOf("victim.bin"),
            Kind = ScanEntryKind.Symlink,
            LinkTarget = System.Text.Encoding.UTF8.GetBytes(outside),
            Metadata = new EntryMetadata { ModifiedAt = 1_722_000_000_000 },
        });
        source.AddFile("data/victim.bin", content, fileId: 9_301);

        var (output, receipt) = await RestoreAsync(source, 0xCF, "spool-link");

        Assert.AreEqual("not the restore's to write", File.ReadAllText(outside), "the file was written through the link");
        SequenceAssert.AreEqual(content, File.ReadAllBytes(Path.Combine(output, "data", "victim.bin")));
        Assert.IsNull(new FileInfo(Path.Combine(output, "data", "victim.bin")).LinkTarget, "the file landed as a link");
        Assert.AreEqual("restored", Assert.ContainsSingle(receipt.Items.Where(item => item.Path == "data/victim.bin")).Outcome);
    }

    [TestMethod]
    public async Task AFolderTheRepositoryPutsAtAFilesSpool_FailsThatFileAlone_AndTheRunGoesOn()
    {
        var kept = Deterministic(512, 10);
        var source = new FakeFileSystemSource();
        source.AddFile("data/" + SpoolNameOf("victim.bin") + "/inside.bin", Deterministic(256, 11), fileId: 9_311);
        source.AddFile("data/victim.bin", Deterministic(4_096, 9), fileId: 9_312);
        source.AddFile("data/zz-after.bin", kept, fileId: 9_313);

        var (output, receipt) = await RestoreAsync(source, 0xCE, "spool-folder");

        Assert.AreEqual("failed", Assert.ContainsSingle(receipt.Items.Where(item => item.Path == "data/victim.bin")).Outcome);
        Assert.AreEqual("restored", Assert.ContainsSingle(receipt.Items.Where(item => item.Path == "data/zz-after.bin")).Outcome);
        SequenceAssert.AreEqual(kept, File.ReadAllBytes(Path.Combine(output, "data", "zz-after.bin")));
    }

    /// <summary>The name the executor spools a file named <paramref name="name"/> under.</summary>
    private string SpoolNameOf(string name) =>
        Path.GetFileName(RestorePaths.SpoolBeside(Path.Combine(SpoolDirectory, name), "restore"));

    /// <summary>
    /// Backs <paramref name="source"/> up as the snapshot <paramref name="seed"/>
    /// names and restores it in place under a folder of its own, with links
    /// where the platform makes them without a privilege.
    /// </summary>
    private async Task<(string Output, RestoreReceipt Receipt)> RestoreAsync(FakeFileSystemSource source, byte seed, string name)
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
                    ClientVersion = "restore-spool-containment-tests/1.0",
                },
                CancellationToken.None);

        var target = RestoreTargetProfile.ForLocalPlatform() with { SupportsSymlinks = !OperatingSystem.IsWindows() };
        var plan = RestorePlanner.Plan(catalogue, Enumerable.Repeat(seed, 16).ToArray(), string.Empty, target);
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var output = Path.Combine(SpoolDirectory, name + "-out");
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
