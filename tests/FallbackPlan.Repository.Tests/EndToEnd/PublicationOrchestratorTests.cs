using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The canonical publication order end to end (specification 08 §10;
/// FR-SNP-001): the intent is durable before the first blob byte, every
/// blob's covering extension precedes its upload, deltas precede the
/// discoverable snapshot, retirement follows it — and the published
/// snapshot reopens and restores from a cold reader.
/// </summary>
public sealed class PublicationOrchestratorTests : ArchiveTestHarness
{
    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    /// <summary>Records every put's key in order — the ordering oracle.</summary>
    private sealed class RecordingStore(IObjectStore inner) : IObjectStore
    {
        public List<string> Puts { get; } = [];

        public StoreCapabilities Capabilities => inner.Capabilities;

        public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.GetMetadataAsync(key, cancellationToken);

        public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(key, range, cancellationToken);

        public ValueTask<PutResult> PutAsync(
            ObjectKey key,
            Func<CancellationToken, ValueTask<Stream>> openContent,
            PutConditions conditions,
            CancellationToken cancellationToken)
        {
            Puts.Add(key.Value);
            return inner.PutAsync(key, openContent, conditions, cancellationToken);
        }

        public IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
            inner.ListAsync(prefix, options, cancellationToken);

        public ValueTask<DeleteResult> DeleteAsync(ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
            inner.DeleteAsync(key, conditions, cancellationToken);
    }

    private PublicationOrchestrator CreateOrchestrator(
        IObjectStore store, RepositoryKeySet keys, KeyHierarchy hierarchy, IPublicationObserver? observer = null) =>
        new(
            SmallBlobPolicy,
            Repo,
            Writer,
            KeyGeneration.Zero,
            keys,
            hierarchy,
            store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
            SpoolDirectory,
            observer);

    private static BackupJob Job(Stream source) => new(
        source,
        "report.bin"u8.ToArray(),
        Enumerable.Repeat((byte)0x22, 16).ToArray(),
        Enumerable.Repeat((byte)0x33, 16).ToArray(),
        Enumerable.Repeat((byte)0x11, 16).ToArray(),
        NowUnixMilliseconds: 1_722_600_000_000,
        DeclaredMaxDurationMs: 3_600_000,
        ExpiryGeneration: 5,
        ClientVersion: "fallbackplan-tests/1.0");

    [Fact]
    public async Task The_nine_step_order_holds_on_the_wire()
    {
        var data = BuildTestFile(regions: 6);
        var store = new RecordingStore(CreateStore());
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        using var source = new MemoryStream(data);
        var published = await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source), CancellationToken.None);

        var puts = store.Puts;

        int FirstIndex(string prefix) => puts.FindIndex(key => key.StartsWith(prefix, StringComparison.Ordinal));
        int LastIndex(string prefix) => puts.FindLastIndex(key => key.StartsWith(prefix, StringComparison.Ordinal));

        // Step 1 before steps 3-4: the intent (the writer's first journal
        // put) precedes the first blob put (08 §3.1).
        Assert.True(FirstIndex("journal/") < FirstIndex("blobs/"), "the intent must be durable before any blob byte");

        // Every blob put is preceded by a journal put covering it — an
        // extension published immediately before the upload (08 §4).
        for (var i = 0; i < puts.Count; i++)
        {
            if (puts[i].StartsWith("blobs/", StringComparison.Ordinal))
            {
                Assert.Contains(puts.Take(i), key => key.StartsWith("journal/", StringComparison.Ordinal));
            }
        }

        // Steps 4 → 6 → 7: every blob durable before the delta; the delta
        // before the discoverable snapshot (FR-SNP-001's invariant).
        Assert.True(LastIndex("blobs/") < FirstIndex("index/delta/"), "deltas reference only durable blobs");
        Assert.True(FirstIndex("index/delta/") < FirstIndex("snapshots/"), "the snapshot follows its index deltas");

        // Step 8: retirement is the LAST journal put, after the snapshot.
        Assert.True(FirstIndex("snapshots/") < LastIndex("journal/"), "retirement follows the snapshot");

        Assert.NotEqual(default, published.DeltaId);
    }

    [Fact]
    public async Task A_published_snapshot_reopens_from_a_cold_reader_and_restores()
    {
        var data = BuildTestFile(regions: 6);
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        using var source = new MemoryStream(data);
        var published = await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source), CancellationToken.None);

        // Cold start: enumerate /snapshots/ (01 §6 step 4), open the
        // standalone object, verify its signature, follow the graph.
        var snapshotKeys = new List<ObjectKey>();
        await foreach (var entry in store.ListAsync(ObjectPrefix.Parse("snapshots/"), ListOptions.Default, CancellationToken.None))
        {
            snapshotKeys.Add(entry.Key);
        }

        var snapshotKey = Assert.Single(snapshotKeys);

        byte[] snapshotBytes;
        using (var read = await store.OpenReadAsync(snapshotKey, range: null, CancellationToken.None))
        {
            using var memory = new MemoryStream();
            await read.Content!.CopyToAsync(memory);
            snapshotBytes = memory.ToArray();
        }

        var record = StandaloneRecordFraming.Parse(snapshotBytes);
        var metadataKey = keys.DeriveClassKey(BlobClass.Metadata, KeyGeneration.Zero);
        Assert.True(StandaloneRecordCipher.TryOpen(record, Repo, metadataKey, out var snapshotPlain));

        var decoded = SnapshotManifestCodec.Decode(snapshotPlain);
        using (var signer = RepositorySigner.Create(hierarchy, KeyGeneration.Zero))
        {
            Assert.True(signer.Verify(decoded.SignedBytes.Span, decoded.Signature.Span),
                "a published snapshot's signature must verify against the derived key (06 §6.1)");
        }

        Assert.Equal(published.RootTreeObjectId, decoded.Manifest.RootTree);

        // Follow root tree → file version → segments, all through footers.
        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var treeRead = await reader.ReadSegmentAsync(decoded.Manifest.RootTree, CancellationToken.None);
        Assert.Equal(RecordReadOutcome.Ok, treeRead.Outcome);
        var tree = TreeManifestCodec.Decode(treeRead.Plaintext!);
        var fileEntry = Assert.Single(tree.Entries);

        var manifestRead = await reader.ReadSegmentAsync(fileEntry.ObjectId, CancellationToken.None);
        Assert.Equal(RecordReadOutcome.Ok, manifestRead.Outcome);
        var manifest = FileVersionManifestCodec.Decode(manifestRead.Plaintext!);

        using var restored = new MemoryStream();
        var restore = await reader.RestoreAsync(manifest.SegmentReferences, restored, CancellationToken.None);

        Assert.True(restore.Success, restore.FailureDetail);
        Assert.Equal(data, restored.ToArray());
        Assert.Equal(manifest.WholeFileHash.ToArray(), restore.WholeFileHash!.ToArray());
    }

    [Fact]
    public async Task The_intent_is_retired_and_the_survey_shows_nothing_live()
    {
        var data = BuildTestFile(regions: 4);
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        using var source = new MemoryStream(data);
        await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source), CancellationToken.None);

        using var journalReader = new JournalReader(store, Repo, hierarchy);
        var (records, unparseable, findings) = await journalReader.LoadAsync(maxGeneration: 0, CancellationToken.None);

        Assert.Equal(0, unparseable);
        Assert.Empty(findings);

        var survey = IntentSurveyor.Survey(records, 0, currentGeneration: 0, nowMs: 1_722_600_000_000, skewMarginMs: 0);

        // A completed publication leaves no live intent — retirement is the
        // event that says the covered work is reachable (08 §5).
        Assert.Empty(survey.LiveIntents);
    }

    [Fact]
    public async Task Observer_steps_arrive_in_order_and_a_throwing_observer_kills_between_steps()
    {
        var data = BuildTestFile(regions: 4);
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        var steps = new List<PublicationStep>();
        var recorder = new StepRecorder(steps, killAfter: null);

        using (var source = new MemoryStream(data))
        {
            await CreateOrchestrator(CreateStore(), keys, hierarchy, recorder).PublishAsync(Job(source), CancellationToken.None);
        }

        Assert.Equal(
            [
                PublicationStep.PublishIntent, PublicationStep.ScanSource, PublicationStep.SegmentAndSeal,
                PublicationStep.UploadBlobs, PublicationStep.VerifyAcknowledgements, PublicationStep.PublishIndexDeltas,
                PublicationStep.PublishSnapshot, PublicationStep.RetireIntent, PublicationStep.Complete,
            ],
            steps);
    }

    [Fact]
    public async Task A_throwing_observer_kills_between_steps_leaving_the_interrupted_state()
    {
        var data = BuildTestFile(regions: 4);
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        // A throwing observer is the F1 kill switch: publication stops
        // between steps, leaving exactly the interrupted state on the wire.
        var killer = new StepRecorder([], killAfter: PublicationStep.PublishIndexDeltas);
        var store = CreateStore();

        using (var source = new MemoryStream(data))
        {
            await Assert.ThrowsAsync<PublicationKilledException>(async () =>
                await CreateOrchestrator(store, keys, hierarchy, killer).PublishAsync(Job(source), CancellationToken.None));
        }

        // Deltas exist; the discoverable snapshot does not.
        var snapshots = 0;
        await foreach (var _ in store.ListAsync(ObjectPrefix.Parse("snapshots/"), ListOptions.Default, CancellationToken.None))
        {
            snapshots++;
        }

        var deltas = 0;
        await foreach (var _ in store.ListAsync(ObjectPrefix.Parse("index/delta/"), ListOptions.Default, CancellationToken.None))
        {
            deltas++;
        }

        Assert.Equal(0, snapshots);
        Assert.True(deltas > 0);
    }

    private sealed class PublicationKilledException : Exception;

    private sealed class StepRecorder(List<PublicationStep> steps, PublicationStep? killAfter) : IPublicationObserver
    {
        public void AfterStep(PublicationStep completedStep)
        {
            steps.Add(completedStep);

            if (completedStep == killAfter)
            {
                throw new PublicationKilledException();
            }
        }
    }
}
