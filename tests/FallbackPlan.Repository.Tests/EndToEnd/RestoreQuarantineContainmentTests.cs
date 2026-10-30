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
/// What stands at the folder a quarantined restore lands in (FR-RST-005;
/// ADR-0152 Amendment 3). That folder's name is fixed, so an earlier run in
/// place into the same output can have restored a repository's link there.
/// The run's folder is resolved under the output's root, as a restored path
/// is, so a link there that leads out refuses the run before anything is
/// written, and a link that leads elsewhere inside the output is followed.
/// </summary>
[TestClass]
public sealed class RestoreQuarantineContainmentTests : ArchiveTestHarness
{
    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "creating symlinks needs no privilege on POSIX; on Windows it does")]
    public async Task ALinkAnEarlierRunPutsAtTheQuarantineFolder_LeadingOut_LandsNothingThere()
    {
        var elsewhere = Directory.CreateDirectory(Path.Combine(SpoolDirectory, "elsewhere")).FullName;
        var source = new FakeFileSystemSource();
        source.AddNode(new FakeFileSystemSource.Node
        {
            RelativePath = ".fbp-quarantine",
            Kind = ScanEntryKind.Symlink,
            LinkTarget = System.Text.Encoding.UTF8.GetBytes(elsewhere),
            Metadata = new EntryMetadata { ModifiedAt = 1_722_000_000_000 },
        });
        source.AddFile("data/a.txt", Deterministic(64, 1), fileId: 9_501);
        var output = Path.Combine(SpoolDirectory, "planted-out");

        var receipts = await RestoreAsync(
            source, 0xC9, "planted", output,
            (RestoreDestinationMode.InPlace, "first-run"),
            (RestoreDestinationMode.Quarantine, "second-run"));

        Assert.AreEqual(elsewhere, new FileInfo(Path.Combine(output, ".fbp-quarantine")).LinkTarget, "the earlier run in place restores the link");
        Assert.IsEmpty(Directory.GetFileSystemEntries(elsewhere, "*", SearchOption.AllDirectories), "the quarantined run wrote out of the restore");
        var refused = receipts[1];
        Assert.AreEqual(RestoreOutcome.Failed, refused.Outcome);
        Assert.HasCount(receipts[0].Items.Count, refused.Items);
        foreach (var item in refused.Items)
        {
            Assert.AreEqual("failed", item.Outcome, item.Path);
            Assert.Contains("symlink", item.Detail!, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "creating symlinks needs no privilege on POSIX; on Windows it does")]
    public async Task ALinkAtTheQuarantineFolder_LeadingElsewhereInsideTheOutput_IsFollowed()
    {
        var output = Path.Combine(SpoolDirectory, "inside-out");
        var kept = Directory.CreateDirectory(Path.Combine(output, "kept")).FullName;
        File.CreateSymbolicLink(Path.Combine(output, ".fbp-quarantine"), kept);
        var source = new FakeFileSystemSource();
        var content = Deterministic(64, 2);
        source.AddFile("data/a.txt", content, fileId: 9_502);

        var receipts = await RestoreAsync(source, 0xC8, "inside", output, (RestoreDestinationMode.Quarantine, "inside-run"));

        Assert.AreEqual(RestoreOutcome.Complete, Assert.ContainsSingle(receipts).Outcome);
        SequenceAssert.AreEqual(content, File.ReadAllBytes(Path.Combine(kept, "inside-run", "data", "a.txt")));
    }

    [TestMethod]
    public async Task AQuarantinedRun_WithNothingInTheWay_LandsInItsOwnFolder()
    {
        // The control: with nothing standing at the quarantine folder's name,
        // the run lands in its own folder below it as it always did.
        var output = Path.Combine(SpoolDirectory, "plain-out");
        var source = new FakeFileSystemSource();
        var content = Deterministic(64, 3);
        source.AddFile("data/a.txt", content, fileId: 9_503);

        var receipt = Assert.ContainsSingle(
            await RestoreAsync(source, 0xC7, "plain", output, (RestoreDestinationMode.Quarantine, "plain-run")));

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        var folder = Path.Combine(output, ".fbp-quarantine", "plain-run");
        Assert.AreEqual(folder, receipt.WrittenTo);
        SequenceAssert.AreEqual(content, File.ReadAllBytes(Path.Combine(folder, "data", "a.txt")));
    }

    /// <summary>
    /// Backs <paramref name="source"/> up as the snapshot <paramref name="seed"/>
    /// names and restores it into <paramref name="into"/> once for each of
    /// <paramref name="runs"/>, in order, with links where the platform makes
    /// them without a privilege.
    /// </summary>
    private async Task<IReadOnlyList<RestoreReceipt>> RestoreAsync(
        FakeFileSystemSource source, byte seed, string name, string into,
        params (RestoreDestinationMode Mode, string Run)[] runs)
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
                    ClientVersion = "restore-quarantine-containment-tests/1.0",
                },
                CancellationToken.None);

        var target = RestoreTargetProfile.ForLocalPlatform() with { SupportsSymlinks = !OperatingSystem.IsWindows() };
        var plan = RestorePlanner.Plan(catalogue, Enumerable.Repeat(seed, 16).ToArray(), string.Empty, target);
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var receipts = new List<RestoreReceipt>();
        foreach (var (mode, run) in runs)
        {
            receipts.Add(await new RestoreExecutor(reader, target).ExecuteAsync(
                plan, into,
                new RestoreExecutionOptions
                {
                    DestinationMode = mode,
                    RunId = run,
                    NowUnixMilliseconds = 1_722_700_000_000,
                },
                CancellationToken.None));
        }

        return receipts;
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
