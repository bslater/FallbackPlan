using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;
using FallbackPlan.Filesystem;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The restore-breadth debt (phase-2 pickup item 11): the two
/// <see cref="ExistingDestinationPolicy"/> values no test had ever set, and
/// the NFR-PERF-009 GET budget measured honestly against what the read path
/// actually issues. ADR-0041 widened it: the write-beside policy that keeps
/// both files under a dated name (FR-RST-006's explicit-choice posture), the
/// receipt pinned whole at schema 4 with <c>written_as</c> (FR-RST-004),
/// several prefixes in one plan, and the targeted blob load.
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

    [TestMethod]
    public async Task RestoreReceipt_ADeterministicRun_MatchesTheGoldenFixtureByteForByte()
    {
        // The receipt is the operator's durable record of what a restore did,
        // and its JSON is a schema other tooling will parse — so the WHOLE
        // document is pinned, not two properties of it. Every field is
        // deterministic here except written_to (an absolute temp path), which
        // is redacted through the record before serializing. A change that
        // breaks this fixture is a receipt schema change and must bump
        // CurrentSchemaVersion with it.
        var content = Deterministic(50_000, 5);
        var (plan, target, store, keys) = await PublishOneFileAsync("golden", content, 0xE4);
        using var _ = keys;

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, Path.Combine(SpoolDirectory, "golden-out"),
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                RunId = "golden-run",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);

        var actual = (receipt with { WrittenTo = "REDACTED" }).ToJson().ReplaceLineEndings("\n");

        Assert.AreEqual(GoldenReceipt.ReplaceLineEndings("\n"), actual, $"the receipt JSON changed:\n{actual}");
    }

    private const string GoldenReceipt = """
        {
          "schema_version": 4,
          "snapshot_id": "e4e4e4e4e4e4e4e4e4e4e4e4e4e4e4e4",
          "started_at": 1722700000000,
          "completed_at": 1722700000000,
          "items": [
            {
              "path": "data",
              "outcome": "restored",
              "bytes": 0,
              "detail": null
            },
            {
              "path": "data/file.bin",
              "outcome": "restored",
              "bytes": 50000,
              "detail": null
            }
          ],
          "displaced": [],
          "written_to": "REDACTED",
          "outcome": "Complete"
        }
        """;

    [TestMethod]
    public async Task RestoreExecution_AnExistingFileUnderWriteBeside_KeepsBothAndNamesTheCopy()
    {
        var content = Deterministic(50_000, 5);
        var (plan, target, store, keys) = await PublishOneFileAsync("beside", content, 0xE5);
        using var _ = keys;

        var output = Path.Combine(SpoolDirectory, "beside-out");
        var destination = Path.Combine(output, "data", "file.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "the live file, kept");

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, output,
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                ExistingDestination = ExistingDestinationPolicy.WriteBeside,
                RunId = "beside-run",
                NowUnixMilliseconds = 1_722_700_000_000, // 2024-08-03 UTC
            },
            CancellationToken.None);

        // Both survive: the live file byte-untouched, the restored copy
        // beside it under the dated name, and the receipt says exactly where
        // (ADR-0041). Nothing was displaced — displacement is Preserve's
        // mechanism, not this policy's.
        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        Assert.AreEqual("the live file, kept", File.ReadAllText(destination));
        var beside = Path.Combine(output, "data", "file (restored 2024-08-03).bin");
        SequenceAssert.AreEqual(content, File.ReadAllBytes(beside));
        Assert.IsEmpty(receipt.Displaced);

        var item = Assert.ContainsSingle(receipt.Items.Where(current => current.Path == "data/file.bin"));
        Assert.AreEqual("restored", item.Outcome);
        Assert.AreEqual("data/file (restored 2024-08-03).bin", item.WrittenAs);
    }

    [TestMethod]
    public async Task RestoreExecution_WriteBesideAgain_DedupesTheBesideName()
    {
        var content = Deterministic(50_000, 5);
        var (plan, target, store, keys) = await PublishOneFileAsync("beside2", content, 0xE6);
        using var _ = keys;

        var output = Path.Combine(SpoolDirectory, "beside2-out");
        var destination = Path.Combine(output, "data", "file.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "the live file, kept");

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var options = new RestoreExecutionOptions
        {
            DestinationMode = RestoreDestinationMode.InPlace,
            ExistingDestination = ExistingDestinationPolicy.WriteBeside,
            RunId = "beside2-run",
            NowUnixMilliseconds = 1_722_700_000_000,
        };
        var executor = new RestoreExecutor(reader, target);
        await executor.ExecuteAsync(plan, output, options, CancellationToken.None);
        var second = await executor.ExecuteAsync(plan, output, options, CancellationToken.None);

        // The first run's copy is not this run's to destroy: the second run
        // takes the next numbered name rather than overwriting it.
        Assert.AreEqual(RestoreOutcome.Complete, second.Outcome);
        SequenceAssert.AreEqual(
            content, File.ReadAllBytes(Path.Combine(output, "data", "file (restored 2024-08-03).bin")));
        SequenceAssert.AreEqual(
            content, File.ReadAllBytes(Path.Combine(output, "data", "file (restored 2024-08-03-2).bin")));
        Assert.AreEqual(
            "data/file (restored 2024-08-03-2).bin",
            Assert.ContainsSingle(second.Items.Where(current => current.Path == "data/file.bin")).WrittenAs);
    }

    [TestMethod]
    public async Task RestorePlanner_SeveralPrefixes_UnionInOnePlanWithSubsumptionAndHonestMisses()
    {
        var one = Deterministic(30_000, 3);
        var two = Deterministic(30_000, 9);
        var three = Deterministic(30_000, 17);

        var source = new FakeFileSystemSource();
        source.AddFile("data/a.bin", one, fileId: 9_001);
        source.AddFile("data/b.bin", two, fileId: 9_002);
        source.AddFile("docs/c.bin", three, fileId: 9_003);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue("multi-prefix");
        await CreateOrchestrator(store, keys, hierarchy, catalogue, "multi-prefix")
            .PublishAsync(Job(source, 0xE7), CancellationToken.None);

        var target = RestoreTargetProfile.ForLocalPlatform();
        var snapshotId = Enumerable.Repeat((byte)0xE7, 16).ToArray();

        // A file and a folder in one plan — one run, one receipt (ADR-0041).
        var plan = RestorePlanner.Plan(catalogue, snapshotId, ["docs", "data/a.bin"], target);
        SequenceAssert.AreEqual(
            new[] { "data/a.bin", "docs", "docs/c.bin" },
            [.. plan.Items.Select(item => item.Path)]);
        Assert.IsEmpty(plan.Conflicts);

        // A prefix under another prefix is subsumed, not walked twice.
        var subsumed = RestorePlanner.Plan(catalogue, snapshotId, ["data", "data/a.bin"], target);
        SequenceAssert.AreEqual(
            new[] { "data", "data/a.bin", "data/b.bin" },
            [.. subsumed.Items.Select(item => item.Path)]);

        // A miss is a per-prefix conflict, never a silent shrink.
        var missing = RestorePlanner.Plan(catalogue, snapshotId, ["docs", "gone/nowhere.bin"], target);
        Assert.AreEqual("gone/nowhere.bin", Assert.ContainsSingle(missing.Conflicts).Path);

        // The union restores in one run: both subtrees land, nothing else.
        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, Path.Combine(SpoolDirectory, "multi-prefix-out"),
            new RestoreExecutionOptions
            {
                DestinationMode = RestoreDestinationMode.InPlace,
                RunId = "multi-prefix-run",
                NowUnixMilliseconds = 1_722_700_000_000,
            },
            CancellationToken.None);

        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
        var restoredRoot = Path.Combine(SpoolDirectory, "multi-prefix-out");
        SequenceAssert.AreEqual(one, File.ReadAllBytes(Path.Combine(restoredRoot, "data", "a.bin")));
        SequenceAssert.AreEqual(three, File.ReadAllBytes(Path.Combine(restoredRoot, "docs", "c.bin")));
        Assert.IsFalse(File.Exists(Path.Combine(restoredRoot, "data", "b.bin")),
            "data/b.bin was in neither prefix and must not restore");
    }

    [TestMethod]
    public async Task RepositoryReader_TargetedLoad_OpensOnlyTheNamedBlobsAndNamesTheAbsent()
    {
        var content = Deterministic(50_000, 5);
        var (plan, target, store, keys) = await PublishOneFileAsync("targeted", content, 0xE8);
        using var _ = keys;

        // The full load knows every blob; the targeted load is handed that
        // set and must restore identically without listing the namespace.
        List<ObjectKey> everyBlob;
        using (var census = new RepositoryReader(Repo, keys, store))
        {
            await census.LoadBlobsAsync(CancellationToken.None);
            everyBlob = [.. census.Blobs.Select(blob => blob.StoreKey)];
        }

        using var reader = new RepositoryReader(Repo, keys, store);
        var opened = await reader.LoadBlobsAsync(everyBlob, CancellationToken.None);
        Assert.AreEqual(everyBlob.Count, opened);
        Assert.IsEmpty(reader.SkippedBlobs);

        var receipt = await new RestoreExecutor(reader, target).ExecuteAsync(
            plan, Path.Combine(SpoolDirectory, "targeted-out"),
            new RestoreExecutionOptions { RunId = "targeted-run", NowUnixMilliseconds = 1_722_700_000_000 },
            CancellationToken.None);
        Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);

        // A named blob the store does not hold is a skip the caller can see,
        // and the records it would have carried read as missing downstream —
        // loudly, per item — never as a quietly narrower world.
        using var partial = new RepositoryReader(Repo, keys, store);
        var absent = ObjectKey.Parse("blobs/aa/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        await partial.LoadBlobsAsync([absent], CancellationToken.None);
        Assert.AreEqual(absent, Assert.ContainsSingle(partial.SkippedBlobs).Key);
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
        Roots = [new ScanRoot("/")],
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
