using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.InterruptionTests;

/// <summary>
/// Shared world for the interruption and corruption harnesses: a temp-rooted
/// store, deterministic keys, small-blob policy, and helpers that run whole
/// publications as separate "process lives" — each orchestrator instance
/// gets fresh in-memory state over the same durable store and sequence file,
/// which is exactly what a crash-and-restart is.
/// </summary>
/// <remarks>
/// Kill simulation is in-process (a throwing observer, a faulting store, an
/// abandoned writer). That covers every state the durable world can be left
/// in; true process-kill and machine-loss testing is future hardening and is
/// recorded here rather than implied.
/// </remarks>
public abstract class InterruptionHarness : IDisposable
{
    protected static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));

    protected static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));

    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-interruption-tests", Guid.NewGuid().ToString("n"));

    protected static CapturePolicy SmallBlobPolicy { get; } = CapturePolicy.Default with
    {
        SegmentSize = SegmentSize.Create(64 * 1024),
        BlobWriteProfile = BlobWriteProfile.LocalDefault with
        {
            TargetSizeBytes = 256 * 1024,
            MaximumSizeBytes = 512 * 1024,
        },
    };

    protected string StoreRoot => Path.Combine(_root, "store");

    protected string SpoolDirectory => Path.Combine(_root, "spool");

    protected LocalFileSystemObjectStore CreateStore() => new(StoreRoot);

    protected static RepositoryKeySet CreateKeys() => RepositoryKeySet.FromMasterKey(MasterKey);

    protected static KeyHierarchy CreateHierarchy() => new(MasterKey);

    /// <summary>A fresh "process life": new orchestrator, durable state shared through disk.</summary>
    protected PublicationOrchestrator CreateOrchestrator(
        IObjectStore store,
        RepositoryKeySet keys,
        KeyHierarchy hierarchy,
        IPublicationObserver? observer = null,
        int concurrency = 1,
        string? spoolDirectory = null,
        Repository.Catalogue.Catalogue? catalogue = null) =>
        new(
            SmallBlobPolicy with { Concurrency = concurrency },
            Repo, Writer, KeyGeneration.Zero, keys, hierarchy, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(spoolDirectory ?? SpoolDirectory, "sequence.txt"))),
            spoolDirectory ?? SpoolDirectory,
            observer,
            catalogue);

    protected static byte[] BuildFile(int seed, int regions = 6)
    {
        var random = new Random(seed);
        var data = new byte[regions * 128 * 1024];
        random.NextBytes(data);
        return data;
    }

    protected static BackupJob Job(Stream source, byte snapshotSeed, ulong now = 1_722_600_000_000) => new(
        source,
        "file.bin"u8.ToArray(),
        Enumerable.Repeat((byte)0x22, 16).ToArray(),
        Enumerable.Repeat((byte)0x33, 16).ToArray(),
        Enumerable.Repeat(snapshotSeed, 16).ToArray(),
        now,
        DeclaredMaxDurationMs: 3_600_000,
        ExpiryGeneration: 5,
        ClientVersion: "interruption-tests/1.0");

    /// <summary>Restores a published snapshot cold — the "previously committed snapshots stay readable" oracle.</summary>
    protected static async Task<byte[]> RestoreSnapshotAsync(IObjectStore store, RepositoryKeySet keys, byte snapshotSeed)
    {
        var wantedId = Enumerable.Repeat(snapshotSeed, 16).ToArray();

        await foreach (var entry in store.ListAsync(ObjectPrefix.Parse("snapshots/"), ListOptions.Default, CancellationToken.None))
        {
            using var read = await store.OpenReadAsync(entry.Key, range: null, CancellationToken.None);
            using var memory = new MemoryStream();
            await read.Content!.CopyToAsync(memory);

            var record = StandaloneRecordFraming.Parse(memory.ToArray());
            var metadataKey = keys.DeriveClassKey(BlobClass.Metadata, record.KeyGeneration);
            Assert.IsTrue(StandaloneRecordCipher.TryOpen(record, Repo, metadataKey, out var plaintext));

            var decoded = SnapshotManifestCodec.Decode(plaintext);
            if (!decoded.Manifest.SnapshotId.Span.SequenceEqual(wantedId))
            {
                continue;
            }

            using var reader = new RepositoryReader(Repo, keys, store);
            await reader.LoadBlobsAsync(CancellationToken.None);

            var treeRead = await reader.ReadSegmentAsync(decoded.Manifest.RootTree, CancellationToken.None);
            Assert.AreEqual(RecordReadOutcome.Ok, treeRead.Outcome);
            var tree = TreeManifestCodec.Decode(treeRead.Plaintext!);

            var manifestRead = await reader.ReadSegmentAsync(tree.Entries[0].ObjectId, CancellationToken.None);
            Assert.AreEqual(RecordReadOutcome.Ok, manifestRead.Outcome);
            var manifest = FileVersionManifestCodec.Decode(manifestRead.Plaintext!);

            using var restored = new MemoryStream();
            var restore = await new RestoreEngine(reader).RestoreFileAsync(manifest, restored, CancellationToken.None);
            Assert.IsTrue(restore.Success, restore.FailureDetail);

            return restored.ToArray();
        }

        throw new InvalidOperationException($"Snapshot {snapshotSeed:x2} was not found.");
    }

    /// <summary>Every blob in the store, paired with the id inside its envelope.</summary>
    /// <remarks>
    /// Nothing can map a store key back to a blob id — that is the point of
    /// keyed store keys — so the id comes from the envelope, exactly as a real
    /// collector must read it (08 §8).
    /// </remarks>
    protected static async Task<List<(ObjectKey Key, BlobId BlobId)>> ReadStoredBlobsAsync(IObjectStore store)
    {
        var blobs = new List<(ObjectKey, BlobId)>();

        await foreach (var entry in store.ListAsync(ObjectPrefix.Parse("blobs/"), ListOptions.Default, CancellationToken.None))
        {
            var envelopeBytes = new byte[Math.Min(BlobEnvelope.MaxLength, entry.Length)];
            using var read = await store.OpenReadAsync(entry.Key, new ObjectRange(0, envelopeBytes.Length), CancellationToken.None);
            await read.Content!.ReadExactlyAsync(envelopeBytes);
            blobs.Add((entry.Key, BlobEnvelope.Parse(envelopeBytes).BlobId));
        }

        return blobs;
    }

    protected int CountUnder(string prefix)
    {
        var path = Path.Combine(StoreRoot, prefix.Replace('/', Path.DirectorySeparatorChar));
        return Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count()
            : 0;
    }

    /// <summary>
    /// The 08 §8 collector obligations as code: enumerate the journal before
    /// marking, protect intent-covered blobs, treat the unparseable as live.
    /// Returns what it WOULD delete — callers assert emptiness.
    /// </summary>
    protected static async Task<List<ObjectKey>> SimulateCollectorMarkAsync(
        LocalFileSystemObjectStore store,
        KeyHierarchy hierarchy,
        ulong currentGeneration,
        ulong nowMs)
    {
        using var journalReader = new Repository.Index.Journal.JournalReader(store, Repo, hierarchy);
        var (records, unparseable, _) = await journalReader.LoadAsync((uint)currentGeneration, CancellationToken.None);
        var survey = Repository.Index.Journal.IntentSurveyor.Survey(
            records, unparseable, currentGeneration, nowMs, skewMarginMs: 60_000);

        // Reachability, phase-0 shape: a blob referenced by an index delta or
        // covered by a live intent is reachable; everything else is a
        // candidate. The candidates here are computed against intents only —
        // callers that also delete the index plane do so deliberately.
        var candidates = new List<ObjectKey>();

        await foreach (var entry in store.ListAsync(ObjectPrefix.Parse("blobs/"), ListOptions.Default, CancellationToken.None))
        {
            // The collector cannot map a store key back to a blob id (that
            // is the point of keyed store keys) — coverage is checked by
            // opening the envelope, exactly as a real collector must.
            var envelopeBytes = new byte[Math.Min(BlobEnvelope.MaxLength, entry.Length)];
            using var read = await store.OpenReadAsync(
                entry.Key, new ObjectRange(0, envelopeBytes.Length), CancellationToken.None);
            await read.Content!.ReadExactlyAsync(envelopeBytes);
            var envelope = BlobEnvelope.Parse(envelopeBytes);

            if (!survey.IsCovered(envelope.BlobId))
            {
                candidates.Add(entry.Key);
            }
        }

        return candidates;
    }

    /// <summary>The kill switch: throws after the named step's effects are durable.</summary>
    protected sealed class KillAfter(PublicationStep step) : IPublicationObserver
    {
        public void AfterStep(PublicationStep completedStep)
        {
            if (completedStep == step)
            {
                throw new PublicationKilledException(step);
            }
        }
    }

    protected sealed class PublicationKilledException(PublicationStep step) : Exception($"killed after step {step}")
    {
        public PublicationStep Step { get; } = step;
    }

    /// <summary>A source that dies part-way through, the way a failing disk does.</summary>
    protected sealed class FaultingStream(byte[] content, int failAfterBytes) : Stream
    {
        private readonly MemoryStream _inner = new(content);

        public override bool CanRead => true;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_inner.Position >= failAfterBytes)
            {
                throw new IOException("Injected fault: the source failed mid-file.");
            }

            return _inner.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void Flush() => _inner.Flush();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Deletes the temp root.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}

/// <summary>A store decorator that fails puts after a budget — out-of-space and network-loss shapes.</summary>
public sealed class FaultInjectingObjectStore(IObjectStore inner, int putBudget) : IObjectStore
{
    private int _puts;

    /// <inheritdoc />
    public StoreCapabilities Capabilities => inner.Capabilities;

    /// <inheritdoc />
    public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
        inner.GetMetadataAsync(key, cancellationToken);

    /// <inheritdoc />
    public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken) =>
        inner.OpenReadAsync(key, range, cancellationToken);

    /// <inheritdoc />
    public ValueTask<PutResult> PutAsync(
        ObjectKey key,
        Func<CancellationToken, ValueTask<Stream>> openContent,
        PutConditions conditions,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _puts) > putBudget)
        {
            throw new IOException($"Injected fault: the store failed after {putBudget} puts.");
        }

        return inner.PutAsync(key, openContent, conditions, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
        inner.ListAsync(prefix, options, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DeleteResult> DeleteAsync(ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
        inner.DeleteAsync(key, conditions, cancellationToken);
}
