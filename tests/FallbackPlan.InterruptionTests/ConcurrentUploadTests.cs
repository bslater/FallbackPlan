using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.TestSupport;

namespace FallbackPlan.InterruptionTests;

/// <summary>
/// What ADR-0029 §2 makes reachable: several blobs simultaneously in the
/// "durable but unreferenced" state of architecture 04 §5.1.
/// </summary>
/// <remarks>
/// The rest of this suite proves that state at <i>N</i>=1. Deferring uploads
/// makes it reachable <i>N</i> at a time, and the invariant that has to survive
/// is per blob rather than per job: <b>the covering write intent is durable
/// before that blob's own PUT</b> (specification 08 §3.1; FR-SNP-004). If it were not, a
/// collector could delete a blob a job is still using — which is [C4], the
/// finding the whole intent journal exists to answer.
/// </remarks>
[TestClass]
public sealed class ConcurrentUploadTests : InterruptionHarness
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(4)]
    public async Task Upload_AtAnyConcurrency_WritesEachBlobsCoveringIntentBeforeItsPut(int concurrency)
    {
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();
        var store = CreateStore();

        // The store records the order it saw things in. Intents are journal
        // objects and blobs are blob objects, so "was this blob's intent
        // written first" is answerable from the sequence alone.
        var observed = new RecordingStore(store);

        var orchestrator = CreateOrchestrator(observed, keys, hierarchy, concurrency: concurrency);

        using var source = new MemoryStream(BuildFile(seed: 7, regions: 40));
        var published = await orchestrator.PublishAsync(Job(source, snapshotSeed: 0x51), TestCancellation);

        // Several data blobs, or the test proves nothing about several blobs.
        var blobs = published.Archive.Blobs;
        Assert.IsTrue(
            blobs.Count > 1,
            $"expected more than one blob to exercise concurrent upload; got {blobs.Count}");

        // The covering record for each blob, by content rather than by
        // count: "some journal record preceded it" holds even when blob A
        // rides on blob B's extension, which is exactly the interleaving a
        // concurrent uploader could produce. The extension that names THIS
        // blob id is the one 08 §3.1 requires to be durable first.
        using var journalReader = new JournalReader(store, Repo, hierarchy);
        var (records, unparseable, _) = await journalReader.LoadAsync(maxGeneration: 0, TestCancellation);
        Assert.AreEqual(0, unparseable);

        foreach (var blob in blobs)
        {
            var blobKey = blob.StoreKey.ToString();
            var blobIndex = observed.Keys.IndexOf(blobKey);
            Assert.IsTrue(blobIndex >= 0, $"blob '{blobKey}' was never put");

            var covering = records.FirstOrDefault(record =>
                record.Payload is JournalPayload.IntentExtension extension &&
                extension.AdditionalBlobIds.Contains(blob.BlobId));
            Assert.IsNotNull(covering, $"no intent extension names blob '{blobKey}'");

            var coveringKey = MetadataStoreKeys.Journal(covering.WriterId, covering.Sequence).ToString();
            var coveringIndex = observed.Keys.IndexOf(coveringKey);

            Assert.IsTrue(
                coveringIndex >= 0 && coveringIndex < blobIndex,
                $"blob '{blobKey}' was uploaded before the extension that names it was durable (08 §3.1, C4).");
        }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(4)]
    public async Task BackupAndRestore_AtAnyConcurrency_RestoresBytesIdenticalToTheSource(int concurrency)
    {
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();
        var store = CreateStore();

        var content = BuildFile(seed: 11, regions: 40);
        var orchestrator = CreateOrchestrator(store, keys, hierarchy, concurrency: concurrency);

        using var source = new MemoryStream(content);
        await orchestrator.PublishAsync(Job(source, snapshotSeed: 0x52), TestCancellation);

        // NFR-PERF-002's acceptance criterion. Now that ADR-0029 §1's staged
        // pipeline is built, the setting reaches segmentation, hashing and
        // compression too — not just the upload workers — so this is the whole
        // claim rather than the part of it that was real. 2 is included because
        // it is the shipped default and was the one value never tested.
        var restored = await RestoreSnapshotAsync(store, keys, snapshotSeed: 0x52);
        SequenceAssert.AreEqual(content, restored);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(4)]
    public async Task Upload_AtAnyConcurrency_GivesEveryBlobDenseAscendingOrdinals(int concurrency)
    {
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();
        var store = CreateStore();

        var content = BuildFile(seed: 13, regions: 40);
        var orchestrator = CreateOrchestrator(store, keys, hierarchy, concurrency: concurrency);

        using var source = new MemoryStream(content);
        var published = await orchestrator.PublishAsync(Job(source, snapshotSeed: 0x53), TestCancellation);

        Assert.IsNotEmpty(published.Archive.Blobs);

        // The property the ordering barrier exists for, asserted on blobs a
        // concurrent pipeline actually produced. Nothing else checks it:
        // BlobFooterTests feeds hand-built tables to the decoder, and
        // BlobWriter's own density check runs only on resume. The ordinal is
        // the AEAD nonce, so a gap or a repeat here is not an ordering blemish
        // — it is the nonce reuse specification 05 §6.1 calls catastrophic.
        foreach (var blob in published.Archive.Blobs.Concat(published.MetadataBlobs))
        {
            ulong previousEnd = 0;

            for (var i = 0; i < blob.RecordTable.Count; i++)
            {
                var entry = blob.RecordTable[i];

                Assert.AreEqual((uint)i, entry.Ordinal);
                Assert.IsTrue(
                    entry.PhysicalOffset >= previousEnd,
                    $"record {i} of blob '{blob.BlobId}' starts at {entry.PhysicalOffset}, inside the record before it "
                    + "— offsets must strictly increase (specification 05 §3.1).");

                previousEnd = entry.PhysicalOffset + (ulong)entry.StoredLength;
            }
        }
    }

    private static CancellationToken TestCancellation => CancellationToken.None;

    /// <summary>Wraps a store and records the order keys were put in.</summary>
    private sealed class RecordingStore(IObjectStore inner) : IObjectStore
    {
        private readonly List<string> _keys = [];
        private readonly Lock _gate = new();

        public List<string> Keys
        {
            get
            {
                lock (_gate)
                {
                    return [.. _keys];
                }
            }
        }

        public StoreCapabilities Capabilities => inner.Capabilities;

        public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.GetMetadataAsync(key, cancellationToken);

        public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(key, range, cancellationToken);

        public async ValueTask<PutResult> PutAsync(
            ObjectKey key,
            Func<CancellationToken, ValueTask<Stream>> openContent,
            PutConditions conditions,
            CancellationToken cancellationToken)
        {
            var result = await inner.PutAsync(key, openContent, conditions, cancellationToken).ConfigureAwait(false);

            // Recorded *after* the put returns, so the order is the order the
            // store actually accepted them in rather than the order callers
            // started.
            lock (_gate)
            {
                _keys.Add(key.ToString());
            }

            return result;
        }

        public IAsyncEnumerable<ObjectEntry> ListAsync(
            ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
            inner.ListAsync(prefix, options, cancellationToken);

        public ValueTask<DeleteResult> DeleteAsync(
            ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
            inner.DeleteAsync(key, conditions, cancellationToken);
    }
}
