using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The stale-local-state family (architecture 02 §8's "the catalogue is a
/// cache", read adversarially; FR-MAN-002, FR-DED-002): a catalogue BEHIND
/// the store must only ever cost a rewrite, and a catalogue AHEAD of the
/// store — claiming objects the store has lost — must never let a
/// publication commit a reference nothing can restore. The rolled-back
/// sequence file is SequenceRollbackTests' territory; this suite is the
/// rest of the family the pipeline review left open.
/// </summary>
[TestClass]
public sealed class StaleCatalogueTests : ArchiveTestHarness
{
    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    [TestMethod]
    public async Task Publication_TheCatalogueClaimsSegmentsTheStoreLost_WritesThemAgainRatherThanDanglingThem()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue("stale-ahead");

        var content = Deterministic(120_000, 17);
        await Publish(store, keys, hierarchy, catalogue, "stale-ahead")
            .PublishAsync(Job(SourceWith(content), 0xA1), CancellationToken.None);

        // The store loses the data blobs — a GC race, a provider rollback,
        // another participant's compaction — and nothing tells the catalogue,
        // whose location rows now claim segments no blob carries.
        DeleteEveryDataBlob();

        // The same content again, presented as a changed file (new mtime, no
        // prior snapshot) so every segment goes through the reuse gate rather
        // than the unchanged-file short-circuit. The gate asks the catalogue,
        // the catalogue says "this writer already stored that", and trusting
        // the row with no store I/O publishes a manifest whose references
        // dangle — a snapshot that commits cleanly and can never restore,
        // the exact inverse of ADR-0006's write-time-detection purpose.
        var republished = await Publish(store, keys, hierarchy, catalogue, "stale-ahead")
            .PublishAsync(
                Job(SourceWith(content, modifiedAt: 1_722_000_000_777), 0xA2, now: 1_722_600_000_002),
                CancellationToken.None);

        Assert.IsTrue(
            republished.ContentBlobs.Sum(blob => blob.RecordCount) > 0,
            "every segment was reused on the stale catalogue's word alone — the published manifest dangles.");

        var file = Assert.ContainsSingle(republished.Files);
        var (success, restored, detail) = await TryRestoreFileAsync(store, keys, file.ObjectId);
        Assert.IsTrue(success, $"the republished snapshot does not restore: {detail}");
        SequenceAssert.AreEqual(content, restored);
    }

    [TestMethod]
    public async Task Publication_TheUnchangedShortCircuitOverALostBlob_FailsOnlyThatFileAndOnlyAtRestore()
    {
        // The companion boundary, pinned rather than fixed: the NFR-PERF-003
        // short-circuit re-emits the prior file version WITHOUT opening the
        // content or consulting the reuse gate — that is its entire point,
        // zero I/O for an unchanged tree. A catalogue row stale against a
        // store that lost the prior version's segments therefore flows into
        // the new snapshot. Making the short-circuit confirm would re-read
        // every unchanged file and undo the optimisation; the honest posture
        // is containment — the loss surfaces as one file's clean restore
        // failure, which is verify's business to find earlier — and the
        // review amendment records the reasoning.
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        using var catalogue = OpenCatalogue("short-circuit");

        var content = Deterministic(120_000, 23);
        await Publish(store, keys, hierarchy, catalogue, "short-circuit")
            .PublishAsync(Job(SourceWith(content), 0xB1), CancellationToken.None);

        DeleteEveryDataBlob();

        // Same mtime, same length, same identity, prior snapshot named: the
        // short-circuit fires and the publication succeeds — nothing on this
        // path could know the store lost the bytes without reading them.
        var republished = await Publish(store, keys, hierarchy, catalogue, "short-circuit")
            .PublishAsync(
                Job(SourceWith(content), 0xB2, now: 1_722_600_000_002) with
                {
                    PriorSnapshotId = Enumerable.Repeat((byte)0xB1, 16).ToArray(),
                },
                CancellationToken.None);

        var file = Assert.ContainsSingle(republished.Files);
        Assert.IsTrue(file.Reused);
        Assert.IsEmpty(republished.Failures);

        // Containment: the loss is one file's clean, named restore failure —
        // never a crash, never a partial file (FR-RST-005).
        var (success, _, detail) = await TryRestoreFileAsync(store, keys, file.ObjectId);
        Assert.IsFalse(success);
        Assert.IsNotNull(detail);
    }

    [TestMethod]
    public async Task Publication_TheCatalogueIsBehindTheStore_CostsARewriteAndNothingElse()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        var cataloguePath = Path.Combine(SpoolDirectory, "catalogue-behind.db");

        var first = Deterministic(120_000, 31);
        var second = Deterministic(120_000, 37);

        byte[] fileVersion2;
        using (var catalogue = CatalogueDb.Open(cataloguePath, Repo))
        {
            await Publish(store, keys, hierarchy, catalogue, "behind")
                .PublishAsync(Job(SourceWith(first), 0xC1), CancellationToken.None);
        }

        // A copy of the catalogue as it stood after the first snapshot: the
        // state a restored-from-backup client disk would present.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var staleCopy = Path.Combine(SpoolDirectory, "catalogue-behind.stale.db");
        File.Copy(cataloguePath, staleCopy);

        using (var catalogue = CatalogueDb.Open(cataloguePath, Repo))
        {
            var published = await Publish(store, keys, hierarchy, catalogue, "behind")
                .PublishAsync(Job(SourceWith(second, fileId: 9_002), 0xC2, now: 1_722_600_000_002), CancellationToken.None);
            fileVersion2 = Assert.ContainsSingle(published.Files).ObjectId.ToArray();
        }

        // The client's local state rolls back — the catalogue is now BEHIND
        // the store, ignorant of snapshot C2's objects.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Copy(staleCopy, cataloguePath, overwrite: true);

        using (var catalogue = CatalogueDb.Open(cataloguePath, Repo))
        {
            // Publishing C2's content again: the behind catalogue cannot
            // offer the reuse, so the segments are written a second time —
            // a cost, never a correctness loss. The store's idempotent-put
            // posture (05 §5.1) absorbs any byte-identical collisions.
            var republished = await Publish(store, keys, hierarchy, catalogue, "behind")
                .PublishAsync(
                    Job(SourceWith(second, fileId: 9_002, modifiedAt: 1_722_000_000_777), 0xC3, now: 1_722_600_000_003),
                    CancellationToken.None);

            Assert.IsEmpty(republished.Failures);

            // Everything the store ever committed still restores — the
            // rolled-back cache changed which work was repeated, not what is
            // true.
            var file = Assert.ContainsSingle(republished.Files);
            var (success3, restored3, detail3) = await TryRestoreFileAsync(store, keys, file.ObjectId);
            Assert.IsTrue(success3, detail3);
            SequenceAssert.AreEqual(second, restored3);
        }

        var (success2, restored2, detail2) = await TryRestoreFileAsync(
            store, keys, ObjectId.FromBytes(fileVersion2));
        Assert.IsTrue(success2, detail2);
        SequenceAssert.AreEqual(second, restored2);
    }

    [TestMethod]
    public async Task Publication_APoisonedSegmentDedupTable_InfluencesNothing()
    {
        // segment_dedup is written by the projection and read by nothing on
        // the publication path — cross-run reuse rides object_locations, and
        // in-session reuse rides the session's own memory. This pins that
        // fact: if the table ever silently became load-bearing, poisoned
        // rows would surface here as reused-but-wrong references.
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        var cataloguePath = Path.Combine(SpoolDirectory, "catalogue-poison.db");

        var content = Deterministic(120_000, 41);
        using (var catalogue = CatalogueDb.Open(cataloguePath, Repo))
        {
            await Publish(store, keys, hierarchy, catalogue, "poison")
                .PublishAsync(Job(SourceWith(content), 0xD1), CancellationToken.None);
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={cataloguePath}"))
        {
            raw.Open();
            using var poison = raw.CreateCommand();
            poison.CommandText = "UPDATE segment_dedup SET object_id = randomblob(32);";
            Assert.IsTrue(poison.ExecuteNonQuery() > 0, "the projection stopped writing segment_dedup rows");
        }

        using (var catalogue = CatalogueDb.Open(cataloguePath, Repo))
        {
            var republished = await Publish(store, keys, hierarchy, catalogue, "poison")
                .PublishAsync(
                    Job(SourceWith(content, modifiedAt: 1_722_000_000_777), 0xD2, now: 1_722_600_000_002),
                    CancellationToken.None);

            var file = Assert.ContainsSingle(republished.Files);
            var (success, restored, detail) = await TryRestoreFileAsync(store, keys, file.ObjectId);
            Assert.IsTrue(success, detail);
            SequenceAssert.AreEqual(content, restored);
        }
    }

    private void DeleteEveryDataBlob()
    {
        foreach (var blob in Directory.EnumerateFiles(
            Path.Combine(StoreRoot, "blobs", "data"), "*", SearchOption.AllDirectories))
        {
            File.Delete(blob);
        }
    }

    private static async Task<(bool Success, byte[] Bytes, string? Detail)> TryRestoreFileAsync(
        Storage.Local.LocalFileSystemObjectStore store, RepositoryKeySet keys, ObjectId fileVersionId)
    {
        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var read = await reader.ReadSegmentAsync(fileVersionId, CancellationToken.None);
        if (read.Outcome != RecordReadOutcome.Ok)
        {
            return (false, [], $"{read.Outcome} — {read.Detail}");
        }

        var manifest = FileVersionManifestCodec.Decode(read.Plaintext!);
        using var restored = new MemoryStream();
        var result = await new RestoreEngine(reader).RestoreFileAsync(manifest, restored, CancellationToken.None);
        return (result.Success, restored.ToArray(), result.FailureDetail);
    }

    private CatalogueDb OpenCatalogue(string name) =>
        CatalogueDb.Open(Path.Combine(SpoolDirectory, $"catalogue-{name}.db"), Repo);

    private PublicationOrchestrator Publish(
        IObjectStore store,
        RepositoryKeySet keys,
        KeyHierarchy hierarchy,
        CatalogueDb catalogue,
        string spoolName)
    {
        var spool = Path.Combine(SpoolDirectory, spoolName);
        Directory.CreateDirectory(spool);

        return new PublicationOrchestrator(
            SmallBlobPolicy,
            Repo,
            Writer,
            KeyGeneration.Zero,
            keys,
            hierarchy,
            store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(spool, "sequence.txt"))),
            spool,
            observer: null,
            catalogue);
    }

    private static FakeFileSystemSource SourceWith(byte[] content, ulong fileId = 9_001, ulong? modifiedAt = null)
    {
        var source = new FakeFileSystemSource();
        var node = source.AddFile("shared/payload.bin", content, fileId: fileId);
        if (modifiedAt is { } stamp)
        {
            node.Metadata = node.Metadata with { ModifiedAt = stamp };
        }

        return source;
    }

    private static SnapshotJob Job(FakeFileSystemSource source, byte snapshotSeed, ulong now = 1_722_600_000_000) => new()
    {
        Source = source,
        RootPath = "/",
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = Enumerable.Repeat(snapshotSeed, 16).ToArray(),
        NowUnixMilliseconds = now,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "fallbackplan-tests/1.0",
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
