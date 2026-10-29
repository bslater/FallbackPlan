using FallbackPlan.Domain;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository.Tests.EndToEnd;

using Catalogue = FallbackPlan.Repository.Catalogue.Catalogue;
using CatalogueRebuilder = FallbackPlan.Repository.Catalogue.CatalogueRebuilder;
using FallbackPlan.TestSupport;

/// <summary>
/// E1 — the exit-criterion-7 shape (FR-MAN-001, NFR-REL-002): the catalogue is
/// deleted outright, rebuilt from the index deltas, and a restore succeeds —
/// with the rebuilt catalogue proving itself by locating a record's blob
/// without any repository scan (the NFR-PERF-004 path). No checkpoint is
/// published here, so the criterion's "checkpoint plus deltas" is met from
/// deltas alone; a rebuild through a format-5 checkpoint is
/// <c>Index/CheckpointBundleTests</c>'s (ADR-0097).
///
/// Deleting it and still restoring is what makes the catalogue a cache rather
/// than a second source of truth, which is the pair of claims FR-MAN-001 (no
/// local database is required to interpret a committed snapshot) and
/// NFR-REL-002 (losing it costs no repository data) each make from one side.
///
/// A rebuild cut short leaves a catalogue that still says it needs
/// rebuilding, and the retry over it finishes whole and restores
/// (FR-MAN-002).
/// </summary>
[TestClass]
public sealed class CatalogueRebuildTests : ArchiveTestHarness
{
    [TestMethod]
    public async Task Catalogue_DeletedOutright_RebuildsFromTheIndexAndStillRestores()
    {
        var data = BuildTestFile(regions: 6);
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();

        // Archive, then publish the real record locations as an index delta
        // — entries projected from the blobs' own footers, exactly the
        // 07 §10 relationship ("every field in an index entry is present in
        // the footer").
        var archiver = CreateArchiver(store, keys);
        using var source = new MemoryStream(data);
        var archived = await archiver.ArchiveAsync(source, CancellationToken.None);

        var entries = await IndexEntriesOfAsync(store, keys, archived);

        var sequenceStore = new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"));
        using (var publisher = new IndexPublisher(store, Repo, Writer, credential, new WriterSequence(sequenceStore)))
        {
            await publisher.PublishDeltaAsync(
                0, [.. archived.Blobs.Select(blob => blob.BlobId)], entries, CancellationToken.None);
        }

        // The catalogue is populated, then destroyed outright.
        var cataloguePath = Path.Combine(SpoolDirectory, "catalogue.db");
        using (var live = Catalogue.Open(cataloguePath, Repo))
        {
            Assert.AreEqual(0, live.AppliedDeltaCount());
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(cataloguePath);

        // Rebuild from the store's index objects alone.
        using var loader = new IndexLoader(store, Repo, credential);
        using var rebuilt = Catalogue.Open(cataloguePath, Repo);

        var report = await new CatalogueRebuilder(loader).RebuildAsync(
            rebuilt, currentGeneration: 0, gapPatienceGenerations: 2, isSequenceAccountedAsync: null, CancellationToken.None);

        Assert.AreEqual(1, report.DeltasApplied);
        Assert.IsEmpty(report.Findings);

        // The rebuilt catalogue locates a record's blob directly — derive
        // the store key from the blob id, open THAT one blob, read the
        // record. No listing of blobs, no whole-repository scan.
        var target = archived.SegmentReferences[7];
        var located = rebuilt.ResolveLocation(target.ObjectId);
        Assert.IsNotNull(located);

        using var storeKeyDeriver = new StoreBlobKeyDeriver(keys.KeyIdKey);
        var blobStoreKey = BlobStoreKeys.ForBlob(BlobClass.Data, storeKeyDeriver.Derive(located!.BlobId));
        var blobLength = archived.Blobs.Single(blob => blob.BlobId.Equals(located.BlobId)).Length;

        using var objectIdDeriver = new ObjectIdDeriver(keys.ContentIdKey);
        using var blobReader = await BlobReader.OpenAsync(
            store, blobStoreKey, blobLength, Repo, keys.DeriveClassKey, objectIdDeriver, CancellationToken.None,
            ContentKeyOpener);

        var tableEntry = blobReader.RecordTable.Single(entry => entry.ObjectId == target.ObjectId);
        Assert.AreEqual(located.PhysicalOffset, tableEntry.PhysicalOffset);

        var read = await blobReader.ReadRecordAsync(tableEntry, CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome);
        SequenceAssert.AreEqual(
            data.AsSpan((int)target.LogicalOffset, (int)target.LogicalLength).ToArray(),
            read.Plaintext);

        // And the whole file still restores byte-identically.
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        using var restored = new MemoryStream();
        var restore = await reader.RestoreAsync(archived.SegmentReferences, restored, CancellationToken.None);

        Assert.IsTrue(restore.Success, restore.FailureDetail);
        SequenceAssert.AreEqual(data, restored.ToArray());
    }

    [TestMethod]
    public async Task CatalogueRebuild_InterruptedPartway_LeavesNoCatalogueThatBlocksTheRetry()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();

        // Three files, each published in a delta of its own, so the rebuild
        // has several index objects to read and can be cut between them.
        var files = new List<(byte[] Data, ArchiveResult Archived)>();
        var sequenceStore = new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"));
        var archiver = CreateArchiver(store, keys);
        using (var publisher = new IndexPublisher(store, Repo, Writer, credential, new WriterSequence(sequenceStore)))
        {
            for (var file = 0; file < 3; file++)
            {
                var data = BuildTestFile(seed: 0x7E30 + (file * 101), regions: 3);
                using var source = new MemoryStream(data);
                var archived = await archiver.ArchiveAsync(source, CancellationToken.None);
                await publisher.PublishDeltaAsync(
                    0, [.. archived.Blobs.Select(blob => blob.BlobId)],
                    await IndexEntriesOfAsync(store, keys, archived), CancellationToken.None);
                files.Add((data, archived));
            }
        }

        var deltas = new List<ObjectKey>();
        await foreach (var entry in store.ListAsync(ObjectPrefix.Parse("index/delta/"), ListOptions.Default, CancellationToken.None))
        {
            deltas.Add(entry.Key);
        }

        Assert.HasCount(3, deltas);

        // The first rebuild is cut short as it reaches the second delta, the
        // way a stop or a lost connection cuts one.
        var cataloguePath = Path.Combine(SpoolDirectory, "catalogue.db");
        using (var cut = new CancellationTokenSource())
        {
            var cutting = new InterleavingOnReadObjectStore(store, deltas[1], () => cut.CancelAsync());
            using var loader = new IndexLoader(cutting, Repo, credential);
            using var first = Catalogue.Open(cataloguePath, Repo);

            await Assert.ThrowsAsync<OperationCanceledException>(async () => await new CatalogueRebuilder(loader).RebuildAsync(
                first, currentGeneration: 0, gapPatienceGenerations: 2, isSequenceAccountedAsync: null, cut.Token));
            Assert.IsTrue(cutting.Fired, "the cut must land inside the rebuild, not before it");
        }

        // What it left still says it needs rebuilding, and the retry over it
        // starts nothing from scratch by hand and finishes whole.
        using var retryLoader = new IndexLoader(store, Repo, credential);
        using var retried = Catalogue.Open(cataloguePath, Repo);
        Assert.IsTrue(retried.NeedsRebuild, "a rebuild cut short must not read as one that finished");

        var report = await new CatalogueRebuilder(retryLoader).RebuildAsync(
            retried, currentGeneration: 0, gapPatienceGenerations: 2, isSequenceAccountedAsync: null, CancellationToken.None);

        Assert.AreEqual(3, report.DeltasApplied);
        Assert.IsEmpty(report.Findings);
        Assert.AreEqual(3, retried.AppliedDeltaCount());

        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        reader.UseLocationSource(retried.ResolveLocation);
        await reader.LoadBlobsAsync(CancellationToken.None);
        foreach (var (data, archived) in files)
        {
            foreach (var reference in archived.SegmentReferences)
            {
                Assert.IsNotNull(retried.ResolveLocation(reference.ObjectId), "every segment the index names is located");
            }

            using var restored = new MemoryStream();
            var restore = await reader.RestoreAsync(archived.SegmentReferences, restored, CancellationToken.None);
            Assert.IsTrue(restore.Success, restore.FailureDetail);
            SequenceAssert.AreEqual(data, restored.ToArray());
        }
    }
}
