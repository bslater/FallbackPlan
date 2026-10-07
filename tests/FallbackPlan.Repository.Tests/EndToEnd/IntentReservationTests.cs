using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;
using Counting = FallbackPlan.TestSupport.CountingObjectStore;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A backup names the blobs it will write before it writes them, a batch at
/// a time (ADR-0092; NFR-PERF-008): the write intent names the first batch
/// and one extension names each later one, so a blob costs no request of its
/// own. The ordering obligation still holds for every blob (FR-GC-003): the
/// record naming it is durable before its upload, and a collector surveying
/// a backup that died keeps everything it uploaded. A backup that completes
/// owes no number it reserved and never used, because its retirement
/// accounts for them; one that dies before retiring owes at most what it had
/// reserved and not used, which the next run voids.
/// </summary>
[TestClass]
public sealed class IntentReservationTests : ArchiveTestHarness
{
    private const int KiB = 1024;

    private const ulong Now = 1_722_600_000_000;

    private sealed class KilledBeforeRetirementException() : Exception("killed after the snapshot, before the retirement");

    private sealed class KillBeforeRetirement : IPublicationObserver
    {
        public void AfterStep(PublicationStep completedStep)
        {
            if (completedStep == PublicationStep.PublishSnapshot)
            {
                throw new KilledBeforeRetirementException();
            }
        }
    }

    private static byte[] Incompressible(int seed, int length)
    {
        var content = new byte[length];
        new Random(seed).NextBytes(content);
        return content;
    }

    /// <summary>
    /// <paramref name="files"/> incompressible files of 256 KiB: at the
    /// harness's 256 KiB blob target, about a data blob a file.
    /// </summary>
    private static FakeFileSystemSource Tree(int files, int seed)
    {
        var source = new FakeFileSystemSource();
        for (var index = 0; index < files; index++)
        {
            source.AddFile($"data/{index:d3}.bin", Incompressible(seed + index, 256 * KiB), fileId: (ulong)(20_000 + index));
        }

        return source;
    }

    private static SnapshotJob Job(FakeFileSystemSource source, byte snapshotSeed, ulong now) => new()
    {
        Source = source,
        Roots = [new ScanRoot("/")],
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = Enumerable.Repeat(snapshotSeed, 16).ToArray(),
        NowUnixMilliseconds = now,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "fallbackplan-tests/1.0",
    };

    private WriterSequence Sequence(string name) =>
        new(new FileSequenceStateStore(Path.Combine(SpoolDirectory, $"{name}-sequence.txt")));

    private PublicationOrchestrator Orchestrator(
        Storage.Abstractions.IObjectStore store,
        RepositoryKeySet keys,
        RepositoryWriteCredential credential,
        WriterSequence sequence,
        CatalogueDb catalogue,
        IPublicationObserver? observer = null) =>
        new(
            SmallBlobPolicy,
            Repo,
            Writer,
            KeyGeneration.Zero,
            keys,
            credential,
            store,
            sequence,
            SpoolDirectory,
            FormatVersions.SealedDataPlane,
            observer,
            catalogue);

    private static async Task<IReadOnlyList<JournalRecord>> JournalAsync(
        LocalFileSystemObjectStore store, RepositoryWriteCredential credential)
    {
        using var reader = new JournalReader(store, Repo, credential);
        var (records, unparseable, _) = await reader.LoadAsync(maxGeneration: 0, CancellationToken.None);
        Assert.AreEqual(0, unparseable);
        return records;
    }

    private static IEnumerable<ArchivedBlob> Blobs(PublishedTreeSnapshot published) =>
        published.ContentBlobs.Concat(published.MetadataBlobs);

    private static int CountUnder(LocalFileSystemObjectStore store, string prefix) =>
        store.ListAsync(Storage.Abstractions.ObjectPrefix.Parse(prefix), Storage.Abstractions.ListOptions.Default, CancellationToken.None)
            .ToBlockingEnumerable()
            .Count();

    [TestMethod]
    public async Task ASmallBackup_IsNamedByItsIntent_AndWritesNoExtension()
    {
        // Four files, four data blobs and a metadata blob: under the first
        // batch, so the intent that has to be written anyway is all it takes.
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, "small.db"), Repo);

        var published = await Orchestrator(store, keys, credential, Sequence("small"), catalogue)
            .PublishAsync(Job(Tree(files: 4, seed: 100), 0xA1, Now), CancellationToken.None);
        Assert.IsLessThanOrEqualTo(
            BlobCounterReservation.FirstBatch, Blobs(published).Count(), "the fixture's premise: one batch holds the backup");

        var records = await JournalAsync(store, credential);
        var intent = (JournalPayload.WriteIntent)Assert.ContainsSingle(
            records.Where(record => record.Payload is JournalPayload.WriteIntent)).Payload;
        Assert.HasCount(BlobCounterReservation.FirstBatch, intent.IntendedBlobIds);
        Assert.IsEmpty(records.Where(record => record.Payload is JournalPayload.IntentExtension));
        foreach (var blob in Blobs(published))
        {
            Assert.Contains(blob.BlobId, intent.IntendedBlobIds, $"blob {blob.StoreKey} was uploaded unnamed");
        }
    }

    [TestMethod]
    public async Task ALargerBackup_IsNamedInDoublingBatches_EachDurableBeforeItsBlobs()
    {
        var counting = new Counting(CreateStore());
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, "larger.db"), Repo);

        var published = await Orchestrator(counting, keys, credential, Sequence("larger"), catalogue)
            .PublishAsync(Job(Tree(files: 30, seed: 200), 0xA2, Now), CancellationToken.None);

        var blobs = Blobs(published).ToList();
        Assert.IsGreaterThan(
            BlobCounterReservation.FirstBatch + BlobCounterReservation.After(BlobCounterReservation.FirstBatch),
            blobs.Count,
            "the fixture's premise: more blobs than the intent and its first extension name");

        var records = await JournalAsync(CreateStore(), credential);
        var intentRecord = Assert.ContainsSingle(records.Where(record => record.Payload is JournalPayload.WriteIntent));
        var extensions = records
            .Where(record => record.Payload is JournalPayload.IntentExtension)
            .OrderBy(record => record.Sequence)
            .ToList();

        Assert.HasCount(BlobCounterReservation.ExtensionsFor(blobs.Count), extensions);
        var batch = BlobCounterReservation.FirstBatch;
        foreach (var extension in extensions)
        {
            batch = BlobCounterReservation.After(batch);
            Assert.HasCount(batch, ((JournalPayload.IntentExtension)extension.Payload).AdditionalBlobIds);
        }

        // 08 §3.1, per blob: the record that names it was put before it.
        var puts = counting.PutKeys.ToList();
        foreach (var blob in blobs)
        {
            var covering = new[] { intentRecord }.Concat(extensions).FirstOrDefault(record => record.Payload switch
            {
                JournalPayload.WriteIntent intent => intent.IntendedBlobIds.Contains(blob.BlobId),
                JournalPayload.IntentExtension extension => extension.AdditionalBlobIds.Contains(blob.BlobId),
                _ => false,
            });
            Assert.IsNotNull(covering, $"no record names blob {blob.StoreKey}");

            var coveringAt = puts.IndexOf(MetadataStoreKeys.Journal(covering.WriterId, covering.Sequence).ToString());
            var blobAt = puts.IndexOf(blob.StoreKey.ToString());
            Assert.IsTrue(
                coveringAt >= 0 && coveringAt < blobAt,
                $"blob {blob.StoreKey} was put before the record naming it (08 §3.1)");
        }
    }

    [TestMethod]
    public async Task ACompletedBackup_OwesNoNumberItReserved_SoTheNextWritesNoVoidDelta()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, "completed.db"), Repo);
        var sequence = Sequence("completed");
        var orchestrator = Orchestrator(store, keys, credential, sequence, catalogue);

        var first = await orchestrator.PublishAsync(Job(Tree(files: 3, seed: 300), 0xA3, Now), CancellationToken.None);
        Assert.IsLessThan(
            BlobCounterReservation.FirstBatch, Blobs(first).Count(), "the premise: the intent reserved more than the backup used");

        Assert.IsEmpty(sequence.OutstandingObligations, "the retirement accounts for every number the intent named");

        // A void delta would be a delta of its own; two backups make two.
        await orchestrator.PublishAsync(Job(Tree(files: 3, seed: 400), 0xA4, Now + 60_000), CancellationToken.None);
        Assert.AreEqual(2, CountUnder(store, "index/delta/"));
        Assert.IsEmpty(sequence.OutstandingObligations);
    }

    [TestMethod]
    public async Task ABackupThatDiesBeforeRetiring_KeepsItsBlobsCovered_AndOwesOnlyWhatItReservedAndDidNotUse()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, "died.db"), Repo);
        var sequence = Sequence("died");

        await Assert.ThrowsExactlyAsync<KilledBeforeRetirementException>(async () =>
            await Orchestrator(store, keys, credential, sequence, catalogue, new KillBeforeRetirement())
                .PublishAsync(Job(Tree(files: 3, seed: 500), 0xA5, Now), CancellationToken.None));

        // What a collector surveying now would keep: every blob that reached
        // the store is named by the live intent (FR-GC-003).
        var records = await JournalAsync(store, credential);
        var survey = IntentSurveyor.Survey(records, 0, currentGeneration: 0, Now, skewMarginMs: 60_000);
        var live = Assert.ContainsSingle(survey.LiveIntents);
        using var storeKeys = new StoreBlobKeyDeriver(keys.KeyIdKey);
        var coveredKeys = live.CoveredBlobIds
            .SelectMany(id => new[] { BlobClass.Data, BlobClass.Metadata }
                .Select(blobClass => BlobStoreKeys.ForBlob(blobClass, storeKeys.Derive(id)).ToString()))
            .ToHashSet(StringComparer.Ordinal);
        var stored = store.ListAsync(Storage.Abstractions.ObjectPrefix.Parse("blobs/"), Storage.Abstractions.ListOptions.Default, CancellationToken.None)
            .ToBlockingEnumerable()
            .Select(entry => entry.Key.Value)
            .ToList();
        Assert.IsNotEmpty(stored);
        foreach (var key in stored)
        {
            Assert.Contains(key, coveredKeys, $"blob {key} reached the store and no live intent names it");
        }

        // Owed: the numbers it reserved and did not use, and nothing else —
        // every blob it uploaded and every record it wrote is accounted for.
        var owed = sequence.OutstandingObligations;
        Assert.IsNotEmpty(owed, "the premise: the intent reserved more than the backup used");
        Assert.HasCount(BlobCounterReservation.FirstBatch - stored.Count, owed);

        // The next run voids them, and owes nothing after it.
        var deltasBefore = CountUnder(store, "index/delta/");
        await Orchestrator(store, keys, credential, sequence, catalogue)
            .PublishAsync(Job(Tree(files: 3, seed: 600), 0xA6, Now + 60_000), CancellationToken.None);
        Assert.AreEqual(deltasBefore + owed.Count + 1, CountUnder(store, "index/delta/"));
        Assert.IsEmpty(sequence.OutstandingObligations);
    }

    [TestMethod]
    public void TheReservationSchedule_DoublesFromTheFirstBatch_AndStopsAtTheLargest()
    {
        Assert.AreEqual(8, BlobCounterReservation.FirstBatch);
        Assert.AreEqual(64, BlobCounterReservation.LargestBatch);
        Assert.AreEqual(16, BlobCounterReservation.After(8));
        Assert.AreEqual(64, BlobCounterReservation.After(32));
        Assert.AreEqual(64, BlobCounterReservation.After(64));

        // The extensions a backup of so many blobs writes: none within the
        // first batch, then one per batch as each runs out.
        Assert.AreEqual(0, BlobCounterReservation.ExtensionsFor(0));
        Assert.AreEqual(0, BlobCounterReservation.ExtensionsFor(8));
        Assert.AreEqual(1, BlobCounterReservation.ExtensionsFor(9));
        Assert.AreEqual(1, BlobCounterReservation.ExtensionsFor(24));
        Assert.AreEqual(2, BlobCounterReservation.ExtensionsFor(25));
        Assert.AreEqual(3, BlobCounterReservation.ExtensionsFor(120));
        Assert.AreEqual(4, BlobCounterReservation.ExtensionsFor(121));
    }
}
