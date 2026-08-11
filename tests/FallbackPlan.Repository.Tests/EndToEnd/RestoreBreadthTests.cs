using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The restore-breadth debt (phase-2 pickup item 11): the two
/// <see cref="ExistingDestinationPolicy"/> values no test had ever set, and
/// the NFR-PERF-009 GET budget measured honestly against what the read path
/// actually issues.
/// </summary>
[TestClass]
public sealed class RestoreBreadthTests : ArchiveTestHarness
{
    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    [TestMethod]
    public async Task RestoreExecution_AnExistingFileUnderReplacePolicy_OverwritesItAndDisplacesNothing()
    {
        var content = Deterministic(50_000, 5);
        var (plan, target, store, keys) = await PublishOneFileAsync("replace", content, 0xE1);
        using var _ = keys;

        var output = Path.Combine(SpoolDirectory, "replace-out");
        var destination = Path.Combine(output, "data", "file.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "local edits, knowingly forfeited");

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                ExistingDestination = ExistingDestinationPolicy.Replace,
                RunId = "replace-run",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);

        // Destructive and never the default — and exactly destructive: the
        // existing file is gone, nothing is moved aside, and the receipt
        // does not pretend otherwise by listing a displacement it never did.
        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        SequenceAssert.AreEqual(content, File.ReadAllBytes(destination));
        Assert.IsEmpty(receipt.Displaced);
        Assert.IsFalse(Directory.Exists(Path.Combine(output, ".fbp-displaced")));
    }

    [TestMethod]
    public async Task RestoreExecution_AnExistingFileUnderFailPolicy_FailsTheItemAndLeavesTheFileUntouched()
    {
        var blocked = Deterministic(50_000, 7);
        var free = Deterministic(50_000, 11);

        var source = new FakeFileSystemSource();
        source.AddFile("data/blocked.bin", blocked, fileId: 9_001);
        source.AddFile("data/free.bin", free, fileId: 9_002);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue("breadth-fail");
        await CreateOrchestrator(store, keys, hierarchy, catalogue, "breadth-fail")
            .PublishAsync(Job(source, 0xE2), CancellationToken.None);

        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(catalogue, Enumerable.Repeat((byte)0xE2, 16).ToArray(), string.Empty, target);

        var output = Path.Combine(SpoolDirectory, "fail-out");
        var occupied = Path.Combine(output, "data", "blocked.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(occupied)!);
        File.WriteAllText(occupied, "precious local edits");

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                ExistingDestination = ExistingDestinationPolicy.Fail,
                RunId = "fail-run",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);

        // The occupied path fails — named, byte-untouched — while the rest
        // of the run restores; one refusal never becomes a refused run.
        Assert.AreEqual(RestoreOutcome.Failed, receipt.Outcome);
        Assert.AreEqual("precious local edits", File.ReadAllText(occupied));

        var failed = Assert.ContainsSingle(receipt.Items.Where(item => item.Outcome == "failed"));
        Assert.AreEqual("data/blocked.bin", failed.Path);
        Assert.IsNotNull(failed.Detail);
        Assert.Contains("policy is to fail", failed.Detail, StringComparison.Ordinal);

        SequenceAssert.AreEqual(free, File.ReadAllBytes(Path.Combine(output, "data", "free.bin")));
        Assert.IsEmpty(receipt.Displaced);
    }

    [TestMethod]
    public async Task Restore_GetRequests_AreCharacterisedAgainstTheDistinctBlobBudget()
    {
        // NFR-PERF-009: restore GETs ≤ 1.2 × the distinct blobs holding the
        // required segments. This is a CHARACTERIZATION, not a compliance
        // pass: the read path opens EVERY blob in the repository at load
        // (three range reads each — locator, footer, envelope) and then
        // reads one range per manifest and per segment with no coalescing,
        // so the budget is architecturally unmet today. The exact counts are
        // pinned so the day the read path learns targeted loading or range
        // coalescing, this test fails and gets rewritten as the compliance
        // test it should become — and until then, nobody can mistake the
        // budget for met.
        var source = new FakeFileSystemSource();
        source.AddFile("data/one.bin", Deterministic(120_000, 13), fileId: 9_001);
        source.AddFile("data/two.bin", Deterministic(120_000, 19), fileId: 9_002);

        var inner = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue("breadth-budget");
        var published = await CreateOrchestrator(inner, keys, hierarchy, catalogue, "breadth-budget")
            .PublishAsync(Job(source, 0xE3), CancellationToken.None);

        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(catalogue, Enumerable.Repeat((byte)0xE3, 16).ToArray(), string.Empty, target);

        var blobsInStore = Directory
            .EnumerateFiles(Path.Combine(StoreRoot, "blobs"), "*", SearchOption.AllDirectories)
            .Count();

        var counting = new CountingObjectStore(inner);
        using var reader = new RepositoryReader(Repo, keys, counting);
        await reader.LoadBlobsAsync(CancellationToken.None);

        // The load alone: three range reads per blob in the repository —
        // proportional to repository size, not to this restore.
        var loadReads = counting.Reads;
        Assert.AreEqual(3 * blobsInStore, loadReads);

        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, Path.Combine(SpoolDirectory, "budget-out"),
            new RestoreExecutionOptions { RunId = "budget-run", NowUnixMilliseconds = 1_722_700_000_000 },
            CancellationToken.None);

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);

        // One read per file-version manifest, one per segment reference, no
        // coalescing of neighbours in the same blob.
        var expectedRestoreReads = published.Files.Sum(
            file => 1 + file.Archive!.SegmentReferences.Count);
        Assert.AreEqual(expectedRestoreReads, counting.Reads - loadReads);

        // The budget arithmetic, stated where it can be seen: even counting
        // generously (every blob in this store holds required segments), the
        // path exceeds 1.2 × distinct blobs — the finding the pickup list
        // now carries as read-path engine work.
        var budget = Math.Ceiling(1.2 * blobsInStore);
        Assert.IsTrue(
            counting.Reads > budget,
            $"the read path issued {counting.Reads} GETs against a budget of {budget} — if this now passes, "
            + "targeted loading or coalescing has landed and this characterization must become a compliance test.");
    }

    private async Task<(RestorePlan Plan, RestoreTargetProfile Target, Storage.Local.LocalFileSystemObjectStore Store, RepositoryKeySet Keys)>
        PublishOneFileAsync(string name, byte[] content, byte seed)
    {
        var source = new FakeFileSystemSource();
        source.AddFile("data/file.bin", content, fileId: 9_001);

        var store = CreateStore();
        var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue($"breadth-{name}");
        await CreateOrchestrator(store, keys, hierarchy, catalogue, $"breadth-{name}")
            .PublishAsync(Job(source, seed), CancellationToken.None);

        var target = RestoreTargetProfile.ForLocalPlatform();
        var plan = RestorePlanner.Plan(catalogue, Enumerable.Repeat(seed, 16).ToArray(), string.Empty, target);
        return (plan, target, store, keys);
    }

    private CatalogueDb OpenCatalogue(string name) =>
        CatalogueDb.Open(Path.Combine(SpoolDirectory, $"catalogue-{name}.db"), Repo);

    private PublicationOrchestrator CreateOrchestrator(
        IObjectStore store, RepositoryKeySet keys, KeyHierarchy hierarchy, CatalogueDb catalogue, string spoolName)
    {
        var spool = Path.Combine(SpoolDirectory, spoolName);
        Directory.CreateDirectory(spool);

        return new PublicationOrchestrator(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, hierarchy, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
            spool, observer: null, catalogue);
    }

    private static SnapshotJob Job(FakeFileSystemSource source, byte seed) => new()
    {
        Source = source,
        RootPath = "/",
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = Enumerable.Repeat(seed, 16).ToArray(),
        NowUnixMilliseconds = 1_722_600_000_000,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "restore-breadth-tests/1.0",
    };

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
