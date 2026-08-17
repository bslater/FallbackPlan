using System.Security.Cryptography;
using FallbackPlan.Filesystem.Local;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Packing;
using FallbackPlan.TestSupport;

namespace FallbackPlan.InterruptionTests;

/// <summary>
/// The interruption matrix over the multi-file path (phase-1 wave T1): a
/// kill at each publication step of a <see cref="SnapshotJob"/> leaves the
/// previously committed snapshot restorable, and a fresh process completes
/// the tree job afterwards — the same guarantees as the single-stream rows,
/// proven over the scanner-driven pipeline. Wave V grows this into the full
/// row-by-row matrix.
/// </summary>
[TestClass]
public sealed class TreeSnapshotInterruptionTests : InterruptionHarness
{
    private readonly string _sourceRoot =
        Path.Combine(Path.GetTempPath(), "fbp-tree-interruption", Guid.NewGuid().ToString("n"));

    private Dictionary<string, byte[]> BuildSourceTree()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["a.bin"] = BuildFile(seed: 11, regions: 2),
            ["nested/b.bin"] = BuildFile(seed: 12, regions: 1),
            ["nested/deep/c.bin"] = BuildFile(seed: 13, regions: 1),
        };

        foreach (var (path, content) in files)
        {
            var full = Path.Combine(_sourceRoot, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, content);
        }

        return files;
    }

    private SnapshotJob TreeJob(byte snapshotSeed) => new()
    {
        Source = new LocalFileSystemSource(),
        RootPath = _sourceRoot,
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = Enumerable.Repeat(snapshotSeed, 16).ToArray(),
        NowUnixMilliseconds = 1_722_600_000_000,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "interruption-tests/1.0",
    };

    // Every one of the nine step boundaries (04 §5.1). On this path step 2
    // is distinct durable state (uploads start during the walk, so blobs may
    // exist by the scan boundary), step 5 equals step 4's by construction,
    // and step 9 fails the caller over a committed publication whose
    // catalogue projection already ran. The universal claims must hold at
    // all nine.
    //
    // An explicit initializer rather than a collection expression: Visual
    // Studio's analyzer lowers the expression through a path that trips
    // CA1825 (observed on Windows), and warnings are errors everywhere.
    public static IEnumerable<object[]> KillPoints() =>
    [
        [PublicationStep.PublishIntent],
        [PublicationStep.ScanSource],
        [PublicationStep.SegmentAndSeal],
        [PublicationStep.UploadBlobs],
        [PublicationStep.VerifyAcknowledgements],
        [PublicationStep.PublishIndexDeltas],
        [PublicationStep.PublishSnapshot],
        [PublicationStep.RetireIntent],
        [PublicationStep.Complete],
    ];

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(4)]
    public async Task PublishTree_AtAnyConcurrency_ProducesAnIdenticalSnapshot(int concurrency)
    {
        var files = BuildSourceTree();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var published = await CreateOrchestrator(store, keys, hierarchy, concurrency: concurrency)
            .PublishAsync(TreeJob(0xC4), CancellationToken.None);

        Assert.AreEqual(files.Count, published.Files.Count);
        Assert.IsEmpty(published.Failures);

        // NFR-PERF-002 over the scanner-driven path. The single-stream theory
        // in ConcurrentUploadTests covers one file; this is the multi-file case,
        // which is the one where a session's blob continuity, its dedup set and
        // its ordinal sequence are all shared across files rather than reset.
        foreach (var file in published.Files)
        {
            var archive = file.Archive;
            Assert.IsNotNull(archive);

            var content = files[file.RelativePath.Replace('\\', '/')];
            SequenceAssert.AreEqual(SHA256.HashData(content), archive.WholeFileHash);
            Assert.AreEqual(content.Length, archive.LogicalLength);

            // 06 §3.2: the encoded references must ascend by logical offset and
            // tile the file exactly. Producing segments concurrently is allowed;
            // emitting them out of order is not.
            long expectedOffset = 0;
            foreach (var reference in archive.SegmentReferences)
            {
                Assert.AreEqual(expectedOffset, reference.LogicalOffset);
                expectedOffset += reference.LogicalLength;
            }

            Assert.AreEqual(content.Length, expectedOffset);
        }
    }

    [TestMethod]
    [DynamicData(nameof(KillPoints))]
    public async Task PublishTree_KilledAtAnyStep_LeavesTheCommittedSnapshotIntactAndCompletesOnRerun(
        PublicationStep killAfter)
    {
        var files = BuildSourceTree();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        // A committed single-stream baseline predates the tree job.
        var baseline = BuildFile(seed: 1);
        using (var source = new MemoryStream(baseline))
        {
            await CreateOrchestrator(store, keys, hierarchy)
                .PublishAsync(Job(source, snapshotSeed: 0xA1), CancellationToken.None);
        }

        // The tree publication dies between steps.
        await Assert.ThrowsExactlyAsync<PublicationKilledException>(async () =>
            await CreateOrchestrator(store, keys, hierarchy, new KillAfter(killAfter))
                .PublishAsync(TreeJob(0xB2), CancellationToken.None));

        // The committed snapshot is untouched by the wreckage.
        SequenceAssert.AreEqual(baseline, await RestoreSnapshotAsync(store, keys, snapshotSeed: 0xA1));

        // A fresh process completes the same tree job end to end.
        var published = await CreateOrchestrator(store, keys, hierarchy)
            .PublishAsync(TreeJob(0xB3), CancellationToken.None);

        Assert.AreEqual(3, published.Files.Count);
        Assert.IsEmpty(published.Failures);

        // The rerun snapshot is byte-verified, not merely counted: a crash,
        // a rerun over the crash's leftovers, and then a restore of every
        // file is the whole journey, and "3 files, no failures" holds
        // without the restored bytes being right.
        using (var reader = new RepositoryReader(Repo, keys, store))
        {
            await reader.LoadBlobsAsync(CancellationToken.None);
            var engine = new RestoreEngine(reader);

            foreach (var file in published.Files)
            {
                var read = await reader.ReadSegmentAsync(file.ObjectId, CancellationToken.None);
                Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome);
                var manifest = FileVersionManifestCodec.Decode(read.Plaintext!);

                using var restored = new MemoryStream();
                var restore = await engine.RestoreFileAsync(manifest, restored, CancellationToken.None);
                Assert.IsTrue(restore.Success, restore.FailureDetail);
                SequenceAssert.AreEqual(files[file.RelativePath.Replace('\\', '/')], restored.ToArray());
            }
        }

        // And the baseline still restores after completion.
        SequenceAssert.AreEqual(baseline, await RestoreSnapshotAsync(store, keys, snapshotSeed: 0xA1));
    }

    [TestMethod]
    public async Task PublishTree_WithAnObserverAttached_DeliversItsStepsInOrder()
    {
        // The single-stream twin lives in PublicationOrchestratorTests; this
        // is the scanner-driven path's own proof that all nine callbacks
        // fire, in order, exactly once — the wiring every kill-matrix row
        // stands on.
        BuildSourceTree();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var steps = new List<PublicationStep>();
        await CreateOrchestrator(store, keys, hierarchy, new StepRecorder(steps))
            .PublishAsync(TreeJob(0xC4), CancellationToken.None);

        SequenceAssert.AreEqual(
            [
                PublicationStep.PublishIntent, PublicationStep.ScanSource, PublicationStep.SegmentAndSeal,
                PublicationStep.UploadBlobs, PublicationStep.VerifyAcknowledgements, PublicationStep.PublishIndexDeltas,
                PublicationStep.PublishSnapshot, PublicationStep.RetireIntent, PublicationStep.Complete,
            ],
            steps);
    }

    [TestMethod]
    public async Task PublishTree_AHintPutFails_StillPublishesTheSnapshot()
    {
        // Source-identity hints are advisory (06 §11): "a store that refuses
        // a put is not a publication failure — a later reader simply falls
        // back to matching by path". A store FAULT during a hint put is the
        // same bargain: losing a rename optimisation must not fail a
        // publication whose blobs and deltas are already durable.
        var files = BuildSourceTree();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var faulting = new PutFaultingObjectStore(store);
        faulting.Arm(key => key.StartsWith("hints/", StringComparison.Ordinal));

        var published = await CreateOrchestrator(faulting, keys, hierarchy)
            .PublishAsync(TreeJob(0xB2), CancellationToken.None);

        Assert.AreEqual(files.Count, published.Files.Count);
        Assert.IsEmpty(published.Failures);
        Assert.AreEqual(0, CountUnder("hints"));
        Assert.AreEqual(1, CountUnder("snapshots"));
    }

    [TestMethod]
    public async Task PublishTree_KilledBeforeTheCatalogueProjection_LeavesTheStoreCompleteAndTheCatalogueACache()
    {
        // The row the enum's step 9 owns on this path: between retirement
        // and Complete sits the live catalogue projection (02 §7). A kill
        // there leaves a fully committed, restorable publication whose
        // catalogue never heard of it — and that must cost nothing, because
        // the catalogue is a cache, never a correctness step.
        var files = BuildSourceTree();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        using var catalogue = Repository.Catalogue.Catalogue.Open(Path.Combine(SpoolDirectory, "live.db"), Repo);

        await Assert.ThrowsExactlyAsync<PublicationKilledException>(async () =>
            await CreateOrchestrator(store, keys, hierarchy, new KillAfter(PublicationStep.RetireIntent), catalogue: catalogue)
                .PublishAsync(TreeJob(0xB2), CancellationToken.None));

        // The store is complete — snapshot durable, intent retired — and the
        // catalogue knows nothing.
        Assert.AreEqual(1, CountUnder("snapshots"));
        Assert.IsEmpty(catalogue.EnumerateSnapshots());

        // A cold reader restores the whole tree from the store alone: the
        // projection's absence is invisible to correctness (FR-ARCH-006).
        using (var reader = new RepositoryReader(Repo, keys, store))
        {
            await reader.LoadBlobsAsync(CancellationToken.None);
            var restoredRoot = await RestoreTreeFileAsync(reader, store, keys, 0xB2, "a.bin");
            SequenceAssert.AreEqual(files["a.bin"], restoredRoot);
        }

        // The next completed publication projects itself — the cache catches
        // up by ordinary operation, not by repair.
        await CreateOrchestrator(store, keys, hierarchy, catalogue: catalogue)
            .PublishAsync(TreeJob(0xC3), CancellationToken.None);

        var projected = Assert.ContainsSingle(catalogue.EnumerateSnapshots());
        SequenceAssert.AreEqual(Enumerable.Repeat((byte)0xC3, 16).ToArray(), projected.SnapshotId.ToArray());
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    [DataRow(8)]
    [DataRow(9)]
    [DataRow(10)]
    [DataRow(11)]
    [DataRow(12)]
    public async Task PublishTree_TheStoreDiesAfterAnyPut_LeavesARecoverableRepository(int putBudget)
    {
        // The in-step rows of the matrix, tree path — the first time the
        // scanner-driven pipeline meets a fault store at every put boundary.
        // At concurrency 1 the put order is deterministic: intent; extension
        // and blob per data blob; extension and metadata blob; delta; one
        // hint per file; snapshot; retirement. Whatever put the budget kills,
        // the same universal claims must hold.
        var files = BuildSourceTree();
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        var faulting = new FaultInjectingObjectStore(store, putBudget);
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await CreateOrchestrator(faulting, keys, hierarchy).PublishAsync(TreeJob(0xB2), CancellationToken.None));

        // Nothing durable is collectable: every blob that made it to the
        // store is covered by the live intent (08 §8, C4).
        Assert.IsEmpty(await SimulateCollectorMarkAsync(store, hierarchy, currentGeneration: 0, nowMs: 1_722_600_000_000));

        // No partial snapshot exists at any budget: either the snapshot put
        // never ran, or the object is complete and restorable.
        var snapshotDurable = CountUnder("snapshots") == 1;
        if (snapshotDurable)
        {
            SequenceAssert.AreEqual(
                files["a.bin"],
                await RestoreTreeFileFromColdReaderAsync(store, keys, 0xB2, "a.bin"));
        }

        // A fresh process completes the job over whatever the fault left —
        // spool leftovers, covered blobs, published deltas — with no repair.
        var published = await CreateOrchestrator(store, keys, hierarchy).PublishAsync(TreeJob(0xC3), CancellationToken.None);
        Assert.AreEqual(files.Count, published.Files.Count);
        Assert.IsEmpty(published.Failures);

        SequenceAssert.AreEqual(files["nested/b.bin"], await RestoreTreeFileFromColdReaderAsync(store, keys, 0xC3, "nested/b.bin"));
    }

    private static async Task<byte[]> RestoreTreeFileFromColdReaderAsync(
        Storage.Local.LocalFileSystemObjectStore store, RepositoryKeySet keys, byte snapshotSeed, string relativePath)
    {
        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);
        return await RestoreTreeFileAsync(reader, store, keys, snapshotSeed, relativePath);
    }

    /// <summary>
    /// Restores one file of a published tree snapshot cold: standalone
    /// snapshot → root tree → per-file version manifest → bytes.
    /// </summary>
    private static async Task<byte[]> RestoreTreeFileAsync(
        RepositoryReader reader,
        Storage.Local.LocalFileSystemObjectStore store,
        RepositoryKeySet keys,
        byte snapshotSeed,
        string relativePath)
    {
        var wantedId = Enumerable.Repeat(snapshotSeed, 16).ToArray();

        await foreach (var entry in store.ListAsync(
            Storage.Abstractions.ObjectPrefix.Parse("snapshots/"), Storage.Abstractions.ListOptions.Default, CancellationToken.None))
        {
            using var read = await store.OpenReadAsync(entry.Key, range: null, CancellationToken.None);
            using var memory = new MemoryStream();
            await read.Content!.CopyToAsync(memory);

            var record = StandaloneRecordFraming.Parse(memory.ToArray());
            var metadataKey = keys.DeriveClassKey(FallbackPlan.Domain.BlobClass.Metadata, record.KeyGeneration);
            Assert.IsTrue(StandaloneRecordCipher.TryOpen(record, Repo, metadataKey, out var plaintext));

            var decoded = SnapshotManifestCodec.Decode(plaintext);
            if (!decoded.Manifest.SnapshotId.Span.SequenceEqual(wantedId))
            {
                continue;
            }

            var fileVersion = await ResolveFileVersionAsync(reader, decoded.Manifest.RootTree, relativePath);

            using var restored = new MemoryStream();
            var restore = await new RestoreEngine(reader).RestoreFileAsync(fileVersion, restored, CancellationToken.None);
            Assert.IsTrue(restore.Success, restore.FailureDetail);
            return restored.ToArray();
        }

        throw new InvalidOperationException($"Snapshot {snapshotSeed:x2} was not found.");
    }

    private static async Task<FileVersionManifest> ResolveFileVersionAsync(
        RepositoryReader reader, FallbackPlan.Domain.Identifiers.ObjectId treeId, string relativePath)
    {
        var components = relativePath.Split('/');
        var current = treeId;

        for (var depth = 0; ; depth++)
        {
            var treeRead = await reader.ReadSegmentAsync(current, CancellationToken.None);
            Assert.AreEqual(RecordReadOutcome.Ok, treeRead.Outcome);
            var tree = TreeManifestCodec.Decode(treeRead.Plaintext!);

            var wanted = System.Text.Encoding.UTF8.GetBytes(components[depth]);
            var entry = tree.Entries.Single(candidate => candidate.Name.Span.SequenceEqual(wanted));

            if (depth == components.Length - 1)
            {
                var manifestRead = await reader.ReadSegmentAsync(entry.ObjectId, CancellationToken.None);
                Assert.AreEqual(RecordReadOutcome.Ok, manifestRead.Outcome);
                return FileVersionManifestCodec.Decode(manifestRead.Plaintext!);
            }

            current = entry.ObjectId;
        }
    }

    /// <summary>Records every observer callback, in order.</summary>
    private sealed class StepRecorder(List<PublicationStep> steps) : IPublicationObserver
    {
        public void AfterStep(PublicationStep completedStep) => steps.Add(completedStep);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && Directory.Exists(_sourceRoot))
        {
            Directory.Delete(_sourceRoot, recursive: true);
        }

        base.Dispose(disposing);
    }
}
