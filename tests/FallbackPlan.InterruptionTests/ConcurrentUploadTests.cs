using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.InterruptionTests;

/// <summary>
/// What ADR-0029 §2 makes reachable: several blobs simultaneously in the
/// "durable but unreferenced" state of architecture 04 §5.1.
/// </summary>
/// <remarks>
/// The rest of this suite proves that state at <i>N</i>=1. Deferring uploads
/// makes it reachable <i>N</i> at a time, and the invariant that has to survive
/// is per blob rather than per job: <b>the covering write intent is durable
/// before that blob's own PUT</b> (specification 08 §3.1). If it were not, a
/// collector could delete a blob a job is still using — which is [C4], the
/// finding the whole intent journal exists to answer.
/// </remarks>
public sealed class ConcurrentUploadTests : InterruptionHarness
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Every_blob_is_intent_covered_before_its_own_put(int concurrency)
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
        Assert.True(
            blobs.Count > 1,
            $"expected more than one blob to exercise concurrent upload; got {blobs.Count}");

        foreach (var blob in blobs)
        {
            var blobKey = blob.StoreKey.ToString();
            var blobIndex = observed.Keys.IndexOf(blobKey);
            Assert.True(blobIndex >= 0, $"blob '{blobKey}' was never put");

            // Some journal record naming this blob has to precede it. The
            // intent extension is the only thing that writes one.
            var journalBefore = observed.Keys
                .Take(blobIndex)
                .Count(key => key.StartsWith("journal/", StringComparison.Ordinal));

            Assert.True(
                journalBefore > 0,
                $"blob '{blobKey}' was uploaded with no journal record before it — its covering intent was not durable "
                + "first (08 §3.1, C4).");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task A_restore_is_byte_identical_whatever_the_concurrency(int concurrency)
    {
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();
        var store = CreateStore();

        var content = BuildFile(seed: 11, regions: 40);
        var orchestrator = CreateOrchestrator(store, keys, hierarchy, concurrency: concurrency);

        using var source = new MemoryStream(content);
        await orchestrator.PublishAsync(Job(source, snapshotSeed: 0x52), TestCancellation);

        // NFR-PERF-002's acceptance criterion, applied to the part of the
        // concurrency setting that is real today: the upload workers.
        var restored = await RestoreSnapshotAsync(store, keys, snapshotSeed: 0x52);
        Assert.Equal(content, restored);
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
