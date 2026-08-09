using FallbackPlan.Domain;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;

namespace FallbackPlan.Repository.Tests.EndToEnd;

using Catalogue = FallbackPlan.Repository.Catalogue.Catalogue;
using CatalogueRebuilder = FallbackPlan.Repository.Catalogue.CatalogueRebuilder;
using FallbackPlan.TestSupport;

/// <summary>
/// E1 — the exit-criterion-7 shape (FR-MAN-001, NFR-REL-002): the catalogue is
/// deleted outright, rebuilt from checkpoint plus deltas, and a restore
/// succeeds — with the rebuilt catalogue proving itself by locating a record's
/// blob without any repository scan (the NFR-PERF-004 path).
///
/// Deleting it and still restoring is what makes the catalogue a cache rather
/// than a second source of truth, which is the pair of claims FR-MAN-001 (no
/// local database is required to interpret a committed snapshot) and
/// NFR-REL-002 (losing it costs no repository data) each make from one side.
/// </summary>
[TestClass]
public sealed class CatalogueRebuildTests : ArchiveTestHarness
{
    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    [TestMethod]
    public async Task Catalogue_DeletedOutright_RebuildsFromTheIndexAndStillRestores()
    {
        var data = BuildTestFile(regions: 6);
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        // Archive, then publish the real record locations as an index delta
        // — entries projected from the blobs' own footers, exactly the
        // 07 §10 relationship ("every field in an index entry is present in
        // the footer").
        var archiver = CreateArchiver(store, keys);
        using var source = new MemoryStream(data);
        var archived = await archiver.ArchiveAsync(source, CancellationToken.None);

        var entries = new List<IndexEntry>();
        using (var probe = new RepositoryReader(Repo, keys, store))
        {
            await probe.LoadBlobsAsync(CancellationToken.None);

            foreach (var reference in archived.SegmentReferences.DistinctBy(reference => reference.ObjectId))
            {
                Assert.IsTrue(probe.TryLocateRecord(reference.ObjectId, out var locatedKey, out var entry));
                var blob = archived.Blobs.Single(archivedBlob => archivedBlob.StoreKey.Equals(locatedKey));

                entries.Add(new IndexEntry(
                    entry.ObjectId, blob.BlobId, entry.PhysicalOffset, entry.StoredLength,
                    entry.CompressionProfileValue, entry.EncryptionProfileValue, IndexEntryType.Insertion));
            }
        }

        var sequenceStore = new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"));
        using (var publisher = new IndexPublisher(store, Repo, Writer, hierarchy, new WriterSequence(sequenceStore)))
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
        using var loader = new IndexLoader(store, Repo, hierarchy);
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
            store, blobStoreKey, blobLength, Repo, keys.DeriveClassKey, objectIdDeriver, CancellationToken.None);

        var tableEntry = blobReader.RecordTable.Single(entry => entry.ObjectId == target.ObjectId);
        Assert.AreEqual(located.PhysicalOffset, tableEntry.PhysicalOffset);

        var read = await blobReader.ReadRecordAsync(tableEntry, CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome);
        SequenceAssert.AreEqual(
            data.AsSpan((int)target.LogicalOffset, (int)target.LogicalLength).ToArray(),
            read.Plaintext);

        // And the whole file still restores byte-identically.
        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);
        using var restored = new MemoryStream();
        var restore = await reader.RestoreAsync(archived.SegmentReferences, restored, CancellationToken.None);

        Assert.IsTrue(restore.Success, restore.FailureDetail);
        SequenceAssert.AreEqual(data, restored.ToArray());
    }
}
