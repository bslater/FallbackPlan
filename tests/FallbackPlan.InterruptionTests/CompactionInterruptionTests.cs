using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.InterruptionTests;

using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

/// <summary>
/// [ADR-0025](../../docs/adr/0025-compaction-reseals-records.md) exit
/// criteria 4, 7 and 12, and the order
/// [ADR-0067](../../docs/adr/0067-the-keyless-compactor.md) keeps to earn
/// them: a compaction cut at any step leaves a repository that verifies and
/// loses no live record, and no index entry it published ever names a blob
/// the store does not hold — even with a collector running in the window.
/// Establishes FR-GC-003 and FR-MAN-019.
/// </summary>
/// <remarks>
/// The cut points are the pass's own steps rather than wall-clock moments:
/// each one is a place where the durable state is a particular shape, and the
/// question at every one is the same — can a reader still get its data back,
/// and does anything on disk point at something that is not there.
/// </remarks>
[TestClass]
public sealed class CompactionInterruptionTests : InterruptionHarness
{
    private static readonly CompactionStep[] CutPoints =
    [
        CompactionStep.PublishIntent,
        CompactionStep.SealBlobs,
        CompactionStep.UploadBlobs,
        CompactionStep.PublishIndex,
        CompactionStep.RetireIntent,
    ];

    /// <summary>Criteria 4 and 7: cut anywhere, the snapshot still restores byte for byte.</summary>
    [TestMethod]
    public async Task ACompactionCutAtEveryStep_LeavesEverySnapshotRestorable()
    {
        foreach (var cut in CutPoints)
        {
            using var world = await CompactionWorld.CreateAsync(this, cut);

            if (cut != CompactionStep.Complete)
            {
                await Assert.ThrowsExactlyAsync<CompactionKilledException>(async () => await world.CompactAsync());
            }

            // Criterion 7: the live records are still readable. They are
            // either where they always were or where they were moved to, and
            // which of those it is is not the reader's problem.
            var restored = await RestoreSnapshotAsync(world.Store, world.Keys, CompactionWorld.SnapshotSeed, world.Repository.RepositoryId);
            Assert.IsTrue(
                world.Original.AsSpan().SequenceEqual(restored),
                $"cut after {cut} lost data");
        }
    }

    /// <summary>
    /// Criterion 12, at its root: at every cut, a blob this pass produced is
    /// either covered by its unretired intent or named by the published
    /// index — never neither. The window in which it is neither is the one a
    /// collector empties, and the pass then publishes entries into nothing.
    /// </summary>
    [TestMethod]
    public async Task AtEveryCutPoint_EveryProducedBlobIsEitherIntentCoveredOrIndexed()
    {
        foreach (var cut in CutPoints)
        {
            using var world = await CompactionWorld.CreateAsync(this, cut);
            await Assert.ThrowsExactlyAsync<CompactionKilledException>(async () => await world.CompactAsync());

            var named = await LoadIndexedBlobIdsAsync(world);
            var intents = await LoadIntentsAsync(world);

            foreach (var (_, blobId) in await ReadStoredBlobsAsync(world.Store))
            {
                if (world.CaptureBlobs.Contains(blobId))
                {
                    continue;
                }

                Assert.IsTrue(
                    named.Contains(blobId) || intents.IsCovered(blobId),
                    $"cut after {cut}: produced blob {blobId} is covered by nothing and named by nothing, "
                    + "so a collector running now would take it");
            }
        }
    }

    /// <summary>The same window, with a collector actually run in it.</summary>
    [TestMethod]
    public async Task ACollectorRunningAtEveryCutPoint_LeavesNoIndexEntryNamingAMissingBlob()
    {
        foreach (var cut in CutPoints)
        {
            using var world = await CompactionWorld.CreateAsync(this, cut);
            await Assert.ThrowsExactlyAsync<CompactionKilledException>(async () => await world.CompactAsync());

            // A collector that knows what 08 §8 obliges it to know and no
            // more: a blob the index names is reachable, a blob an unretired
            // intent covers is reachable, and everything else is garbage.
            // Everything it would take, it takes.
            var named = await LoadIndexedBlobIdsAsync(world);
            var intents = await LoadIntentsAsync(world);
            foreach (var (key, blobId) in await ReadStoredBlobsAsync(world.Store))
            {
                if (!named.Contains(blobId) && !intents.IsCovered(blobId))
                {
                    await world.Store.DeleteAsync(key, DeleteConditions.None, CancellationToken.None);
                }
            }

            // And the pass finishes, as a resumed one would.
            await world.CompleteAsync();

            var present = (await ReadStoredBlobsAsync(world.Store)).Select(blob => blob.BlobId).ToHashSet();
            foreach (var covered in await LoadIndexedBlobIdsAsync(world))
            {
                Assert.Contains(
                    covered,
                    present,
                    $"cut after {cut}: the index names blob {covered}, which the collector took");
            }
        }
    }

    private static async Task<IntentSurvey> LoadIntentsAsync(CompactionWorld world)
    {
        using var journal = new JournalReader(world.Store, world.Repository.RepositoryId, world.Credential);
        var (records, unparseable, _) = await journal.LoadAsync(0, CancellationToken.None);
        return IntentSurveyor.Survey(
            records, unparseable, currentGeneration: 0, CompactionWorld.NowMs, skewMarginMs: 60_000);
    }

    /// <summary>Every blob the published index points at, by either route.</summary>
    private static async Task<HashSet<BlobId>> LoadIndexedBlobIdsAsync(CompactionWorld world)
    {
        using var loader = new IndexLoader(world.Store, world.Repository.RepositoryId, world.Credential);
        var state = await loader.LoadAsync(
            currentGeneration: 0, gapPatienceGenerations: 2, isSequenceAccountedAsync: null,
            blobState: null, CancellationToken.None);

        return
        [
            .. state.Deltas.SelectMany(delta => delta.Delta.CoveredBlobIds),
            .. state.Deltas.SelectMany(delta => delta.Delta.Entries.Select(entry => entry.BlobId)),
        ];
    }

    private sealed class KillCompactionAfter(CompactionStep step) : ICompactionObserver
    {
        public void AfterStep(CompactionStep completedStep)
        {
            if (completedStep == step)
            {
                throw new CompactionKilledException(step);
            }
        }
    }

    private sealed class CompactionKilledException(CompactionStep step)
        : Exception($"killed after compaction step {step}");

    /// <summary>A format-3 archive with one snapshot, ready to be compacted and cut.</summary>
    private sealed class CompactionWorld : IDisposable
    {
        public const byte SnapshotSeed = 0xC7;

        public const ulong NowMs = 1_722_600_000_000;

        private readonly string _spool;
        private readonly CompactionStep _cut;

        private CompactionWorld(
            LocalFileSystemObjectStore store,
            RepositoryKeySet keys,
            RepositoryWriteCredential credential,
            OpenedRepository repository,
            CatalogueDb catalogue,
            WriterSequence sequence,
            string spool,
            byte[] original,
            CompactionStep cut)
        {
            Store = store;
            Keys = keys;
            Credential = credential;
            Repository = repository;
            Catalogue = catalogue;
            Sequence = sequence;
            _spool = spool;
            Original = original;
            _cut = cut;
        }

        public LocalFileSystemObjectStore Store { get; }

        public RepositoryKeySet Keys { get; }

        public RepositoryWriteCredential Credential { get; }

        public OpenedRepository Repository { get; }

        public CatalogueDb Catalogue { get; }

        public WriterSequence Sequence { get; }

        public byte[] Original { get; }

        /// <summary>What the capture left behind, so the produced blobs can be told apart from it.</summary>
        public HashSet<BlobId> CaptureBlobs { get; } = [];

        public static async Task<CompactionWorld> CreateAsync(CompactionInterruptionTests owner, CompactionStep cut)
        {
            // Each cut gets its own store: a pass is a process life, and a
            // half-done one must not be inherited by the next case.
            var root = Path.Combine(owner.StoreRoot, "worlds", cut.ToString());
            var spool = Path.Combine(owner.SpoolDirectory, "worlds", cut.ToString());
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(spool);

            var store = new LocalFileSystemObjectStore(root);
            var keys = CreateKeys();
            var credential = CreateCredential();
            var sequence = new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt")));

            var repository = await RepositoryLifecycle.CreateAsync(
                store,
                credential,
                Enumerable.Repeat((byte)0x4C, KekDerivation.SaltLength).ToArray(),
                new Argon2Parameters { MemoryKiB = 64, Iterations = 1, Parallelism = 1 },
                createdBy: "compaction-interruption-tests",
                createdAtUnixMilliseconds: NowMs,
                CancellationToken.None,
                formatVersion: FormatVersions.RelocatableRecords);

            var original = BuildFile(seed: 0x5C, regions: 6);
            var orchestrator = new PublicationOrchestrator(
                SmallBlobPolicy, repository.RepositoryId, Writer, KeyGeneration.Zero, keys, credential, store,
                sequence, spool, FormatVersions.RelocatableRecords);

            using (var source = new MemoryStream(original))
            {
                await orchestrator.PublishAsync(Job(source, SnapshotSeed, NowMs), CancellationToken.None);
            }

            var world = new CompactionWorld(
                store, keys, credential, repository,
                CatalogueDb.Open(Path.Combine(spool, "catalogue.db"), repository.RepositoryId),
                sequence, spool, original, cut);

            foreach (var (_, blobId) in await ReadStoredBlobsAsync(store))
            {
                world.CaptureBlobs.Add(blobId);
            }

            return world;
        }

        /// <summary>Compacts the first data blob whole, cut where this world was built to be cut.</summary>
        public Task CompactAsync() => CompactAsync(new KillCompactionAfter(_cut));

        /// <summary>The same pass, run to the end — what a resumed compaction does.</summary>
        public Task CompleteAsync() => CompactAsync(observer: null);

        private async Task CompactAsync(ICompactionObserver? observer)
        {
            using var reader = new RepositoryReader(Repository.RepositoryId, Keys, Store);
            await reader.LoadBlobsAsync(CancellationToken.None);

            var candidate = reader.Blobs
                .Where(blob => blob.StoreKey.ToString().StartsWith("blobs/data/", StringComparison.Ordinal))
                .OrderBy(blob => blob.StoreKey.ToString(), StringComparer.Ordinal)
                .First();

            await CompactionPass.RunAsync(
                [new CompactionSource(candidate.StoreKey, candidate.BlobId, candidate.Records)],
                Repository, Store, Store, Writer, SmallBlobPolicy, Sequence, Catalogue, _spool,
                NowMs, declaredMaxDurationMs: 3_600_000, expiryGeneration: 5, CancellationToken.None,
                observer);
        }

        public void Dispose()
        {
            Catalogue.Dispose();
            Repository.Dispose();
            Keys.Dispose();
            Credential.Dispose();
        }
    }
}
