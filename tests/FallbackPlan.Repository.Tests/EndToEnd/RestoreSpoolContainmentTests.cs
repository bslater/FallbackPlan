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
/// What stands at the name a file spools under (FR-RST-005, FR-RST-003;
/// ADR-0152 Amendments 1 and 4). The spool's name is one anyone can work
/// out from the file's. An entry a snapshot names there, or below it, is the
/// plan's conflict and fails, named, so nothing the receipt counts restored
/// is removed when the file spools. What an earlier run or another account
/// left there is removed, and the spool made new, as the recovery tool makes
/// its own, so a link there carries nothing out of the restore. What cannot
/// be removed fails that file alone, named, and the run goes on.
/// </summary>
[TestClass]
public sealed class RestoreSpoolContainmentTests : ArchiveTestHarness
{
    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "creating symlinks needs no privilege on POSIX; on Windows it does")]
    public async Task ALinkStandingAtAFilesSpool_IsReplaced_AndNothingIsWrittenThroughIt()
    {
        // As a run in place of another snapshot, one with no such file, can
        // leave one: it is not this snapshot's entry.
        var outside = Path.Combine(Directory.CreateDirectory(SpoolDirectory).FullName, "outside.txt");
        File.WriteAllText(outside, "not the restore's to write");
        var content = Deterministic(4_096, 9);
        var source = new FakeFileSystemSource();
        source.AddFile("data/victim.bin", content, fileId: 9_301);
        var output = Path.Combine(SpoolDirectory, "spool-link-out");
        Directory.CreateDirectory(Path.Combine(output, "data"));
        File.CreateSymbolicLink(Path.Combine(output, "data", SpoolNameOf("victim.bin")), outside);

        var (_, receipt) = await RestoreAsync(source, 0xCF, "spool-link");

        Assert.AreEqual("not the restore's to write", File.ReadAllText(outside), "the file was written through the link");
        SequenceAssert.AreEqual(content, File.ReadAllBytes(Path.Combine(output, "data", "victim.bin")));
        Assert.IsNull(new FileInfo(Path.Combine(output, "data", "victim.bin")).LinkTarget, "the file landed as a link");
        Assert.AreEqual("restored", Assert.ContainsSingle(receipt.Items.Where(item => item.Path == "data/victim.bin")).Outcome);
    }

    [TestMethod]
    public async Task AFolderStandingAtAFilesSpool_FailsThatFileAlone_AndTheRunGoesOn()
    {
        var kept = Deterministic(512, 10);
        var source = new FakeFileSystemSource();
        source.AddFile("data/victim.bin", Deterministic(4_096, 9), fileId: 9_312);
        source.AddFile("data/zz-after.bin", kept, fileId: 9_313);
        var output = Path.Combine(SpoolDirectory, "spool-folder-out");
        Directory.CreateDirectory(Path.Combine(output, "data", SpoolNameOf("victim.bin")));

        var (_, receipt) = await RestoreAsync(source, 0xCE, "spool-folder");

        Assert.AreEqual("failed", Assert.ContainsSingle(receipt.Items.Where(item => item.Path == "data/victim.bin")).Outcome);
        Assert.AreEqual("restored", Assert.ContainsSingle(receipt.Items.Where(item => item.Path == "data/zz-after.bin")).Outcome);
        SequenceAssert.AreEqual(kept, File.ReadAllBytes(Path.Combine(output, "data", "zz-after.bin")));
    }

    [TestMethod]
    [DataRow(false, DisplayName = "named as the spool is")]
    [DataRow(true, DisplayName = "named so in another case")]
    public async Task AFileTheSnapshotNamesAtAFilesSpool_IsTheConflict_AndFails_AndTheFileRestores(bool otherCase)
    {
        var content = Deterministic(4_096, 12);
        var spool = otherCase ? SpoolNameOf("victim.bin").ToUpperInvariant() : SpoolNameOf("victim.bin");
        var source = new FakeFileSystemSource();
        source.AddFile("data/" + spool, Deterministic(256, 13), fileId: 9_321);
        source.AddFile("data/victim.bin", content, fileId: 9_322);

        var (output, receipt, plan) = await RestoreWithPlanAsync(source, 0xCD, otherCase ? "spool-entry-case" : "spool-entry");

        var conflict = Assert.ContainsSingle(plan.Conflicts);
        Assert.AreEqual("data/" + spool, conflict.Path);
        Assert.Contains("data/victim.bin", conflict.Reason, StringComparison.Ordinal);
        var refused = Assert.ContainsSingle(receipt.Items.Where(item => item.Path == "data/" + spool));
        Assert.AreEqual("failed", refused.Outcome);
        Assert.Contains("data/victim.bin", refused.Detail!, StringComparison.Ordinal);
        Assert.AreEqual("restored", Assert.ContainsSingle(receipt.Items.Where(item => item.Path == "data/victim.bin")).Outcome);
        SequenceAssert.AreEqual(content, File.ReadAllBytes(Path.Combine(output, "data", "victim.bin")));
        Assert.ContainsSingle(Directory.GetFileSystemEntries(Path.Combine(output, "data")), "nothing stands at the spool's name");
        Assert.AreEqual(RestoreOutcome.Failed, receipt.Outcome);
    }

    [TestMethod]
    public async Task AFolderTheSnapshotNamesAtAFilesSpool_FailsWithAllBelowIt_AndTheFileRestores()
    {
        var content = Deterministic(4_096, 14);
        var source = new FakeFileSystemSource();
        source.AddFile("data/" + SpoolNameOf("victim.bin") + "/inside.bin", Deterministic(256, 11), fileId: 9_331);
        source.AddFile("data/victim.bin", content, fileId: 9_332);

        var (output, receipt, plan) = await RestoreWithPlanAsync(source, 0xCC, "spool-entry-folder");

        var below = "data/" + SpoolNameOf("victim.bin");
        CollectionAssert.AreEquivalent(
            new[] { below, below + "/inside.bin" }, plan.Conflicts.Select(conflict => conflict.Path).ToArray());
        foreach (var item in receipt.Items.Where(item => item.Path.StartsWith(below, StringComparison.Ordinal)))
        {
            Assert.AreEqual("failed", item.Outcome, item.Path);
        }

        Assert.AreEqual("restored", Assert.ContainsSingle(receipt.Items.Where(item => item.Path == "data/victim.bin")).Outcome);
        SequenceAssert.AreEqual(content, File.ReadAllBytes(Path.Combine(output, "data", "victim.bin")));
        Assert.ContainsSingle(Directory.GetFileSystemEntries(Path.Combine(output, "data")), "nothing stands at the spool's name");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "creating symlinks needs no privilege on POSIX; on Windows it does")]
    public async Task ALinkTheSnapshotNamesAtAFilesSpool_IsNotMade_AndNothingIsWrittenThroughIt()
    {
        var outside = Path.Combine(Directory.CreateDirectory(SpoolDirectory).FullName, "outside-entry.txt");
        File.WriteAllText(outside, "not the restore's to write");
        var content = Deterministic(4_096, 15);
        var source = new FakeFileSystemSource();
        source.AddNode(new FakeFileSystemSource.Node
        {
            RelativePath = "data/" + SpoolNameOf("victim.bin"),
            Kind = ScanEntryKind.Symlink,
            LinkTarget = System.Text.Encoding.UTF8.GetBytes(outside),
            Metadata = new EntryMetadata { ModifiedAt = 1_722_000_000_000 },
        });
        source.AddFile("data/victim.bin", content, fileId: 9_341);

        var (output, receipt, _) = await RestoreWithPlanAsync(source, 0xCB, "spool-entry-link");

        Assert.AreEqual("not the restore's to write", File.ReadAllText(outside), "the file was written through the link");
        Assert.AreEqual(
            "failed", Assert.ContainsSingle(receipt.Items.Where(item => item.Path == "data/" + SpoolNameOf("victim.bin"))).Outcome);
        SequenceAssert.AreEqual(content, File.ReadAllBytes(Path.Combine(output, "data", "victim.bin")));
        Assert.ContainsSingle(Directory.GetFileSystemEntries(Path.Combine(output, "data")), "nothing stands at the spool's name");
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
        var (output, receipt, _) = await RestoreWithPlanAsync(source, seed, name);
        return (output, receipt);
    }

    /// <summary>As <see cref="RestoreAsync"/>, answering the plan the run was given too.</summary>
    private async Task<(string Output, RestoreReceipt Receipt, RestorePlan Plan)> RestoreWithPlanAsync(
        FakeFileSystemSource source, byte seed, string name)
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
        return (output, receipt, plan);
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
