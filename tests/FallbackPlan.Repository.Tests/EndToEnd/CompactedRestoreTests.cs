using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

/// <summary>
/// A restore that spans both halves of a compacted repository
/// ([ADR-0067](../../../docs/adr/0067-the-keyless-compactor.md); ADR-0025
/// exit criterion 3): records lifted into a rewritten blob and records left
/// where the capture put them, assembled into one file through the ordinary
/// reader. Establishes FR-GC-004, FR-MAN-019 and FR-RST-002: the manifest
/// the capture wrote is read back unchanged, and every reference in it still
/// resolves after half its segments have moved.
/// </summary>
/// <remarks>
/// This is the case the whole of format 3 exists for. The source blob is
/// <b>deleted</b> before the restore, so nothing can quietly fall back to it:
/// what comes back comes back out of bytes that were moved without a content
/// key ever being held.
/// </remarks>
[TestClass]
public sealed class CompactedRestoreTests : ArchiveTestHarness
{
    [TestMethod]
    public async Task ARestoreSpanningACompactedBlobAndAnUntouchedOne_ReturnsTheFileByteForByte()
    {
        var data = BuildTestFile(regions: 24);
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        var sequence = new WriterSequence(
            new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt")));

        var orchestrator = new PublicationOrchestrator(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store, sequence,
            SpoolDirectory, FormatVersions.RelocatableRecords);

        using (var source = new MemoryStream(data))
        {
            await orchestrator.PublishAsync(
                new BackupJob(
                    source,
                    "report.bin"u8.ToArray(),
                    Enumerable.Repeat((byte)0x22, 16).ToArray(),
                    Enumerable.Repeat((byte)0x33, 16).ToArray(),
                    Enumerable.Repeat((byte)0x11, 16).ToArray(),
                    1_722_600_000_000, 3_600_000, 5, "tests/1.0"),
                CancellationToken.None);
        }

        // One data blob is compacted whole and the rest are left alone, so
        // the file's segments end up on both sides of the move.
        var candidate = await FirstDataBlobAsync(store, keys);
        var produced = await CompactAsync(store, keys, sequence, candidate);
        var moved = Assert.ContainsSingle(produced);

        using (var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, "compacted.db"), Repo))
        using (var storeKeys = new StoreBlobKeyDeriver(keys.KeyIdKey))
        using (var publisher = new IndexPublisher(store, Repo, Writer, credential, sequence))
        {
            await CompactionPublication.PublishAsync(
                produced, store, storeKeys, publisher, catalogue, generation: 0, CancellationToken.None);
        }

        Assert.AreNotEqual(candidate.BlobId, moved.Sealed.BlobId);
        await store.DeleteAsync(candidate.StoreKey, DeleteConditions.None, CancellationToken.None);

        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var snapshot = SnapshotManifestCodec.Decode(await ReadSnapshotAsync(store, keys));
        var tree = TreeManifestCodec.Decode(
            (await reader.ReadSegmentAsync(snapshot.Manifest.RootTree, CancellationToken.None)).Plaintext!);
        var manifest = FileVersionManifestCodec.Decode(
            (await reader.ReadSegmentAsync(tree.Entries[0].ObjectId, CancellationToken.None)).Plaintext!);

        // The file genuinely spans both halves: some of its segments are in
        // the rewritten blob and some were never touched.
        var relocated = moved.Sealed.RecordTable.Select(record => record.ObjectId).ToHashSet();
        var segments = manifest.SegmentReferences.Select(segment => segment.ObjectId).ToList();
        Assert.IsGreaterThan(0, segments.Count(relocated.Contains), "no segment was relocated");
        Assert.IsGreaterThan(0, segments.Count(id => !relocated.Contains(id)), "every segment was relocated");

        using var restored = new MemoryStream();
        var restore = await new RestoreEngine(reader).RestoreFileAsync(manifest, restored, CancellationToken.None);

        Assert.IsTrue(restore.Success, restore.FailureDetail);
        SequenceAssert.AreEqual(data, restored.ToArray());
    }

    private static async Task<CompactionSource> FirstDataBlobAsync(LocalFileSystemObjectStore store, RepositoryKeySet keys)
    {
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var blobs = reader.Blobs
            .Where(blob => blob.StoreKey.ToString().StartsWith("blobs/data/", StringComparison.Ordinal))
            .OrderBy(blob => blob.StoreKey.ToString(), StringComparer.Ordinal)
            .ToList();

        Assert.IsGreaterThan(1, blobs.Count, "one data blob only, so nothing would be left untouched");

        var chosen = blobs[0];
        return new CompactionSource(chosen.StoreKey, chosen.BlobId, chosen.Records);
    }

    private async Task<IReadOnlyList<CompactedBlob>> CompactAsync(
        LocalFileSystemObjectStore store,
        RepositoryKeySet keys,
        WriterSequence sequence,
        CompactionSource candidate)
    {
        using var compactor = new BlobCompactor(
            Repo, Writer, KeyGeneration.Zero, keys, SmallBlobPolicy, store, sequence,
            SpoolDirectory, FormatVersions.RelocatableRecords);

        return await compactor.CompactAsync([candidate], CancellationToken.None);
    }

    private static async Task<byte[]> ReadSnapshotAsync(LocalFileSystemObjectStore store, RepositoryKeySet keys)
    {
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("snapshots/"), ListOptions.Default, CancellationToken.None))
        {
            using var read = await store.OpenReadAsync(entry.Key, range: null, CancellationToken.None);
            using var memory = new MemoryStream();
            await read.Content!.CopyToAsync(memory);

            var record = StandaloneRecordFraming.Parse(memory.ToArray());
            var metadataKey = keys.DeriveClassKey(BlobClass.Metadata, record.KeyGeneration);
            Assert.IsTrue(StandaloneRecordCipher.TryOpen(record, Repo, metadataKey, out var plaintext));
            return plaintext;
        }

        throw new InvalidOperationException("No snapshot object found.");
    }
}
