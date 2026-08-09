using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Packing;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// Manifests through the real container (specification 06 §1; FR-MAN-003):
/// archive a file, build its file-version manifest, write it as a record in
/// a metadata-class blob, read it back through footers alone, decode it,
/// and restore the file from the manifest's own references — plus the
/// standalone snapshot object sealed under FBPKSREC at its bounded prefix.
/// </summary>
[TestClass]
public sealed class ManifestRoundTripTests : ArchiveTestHarness
{
    [TestMethod]
    public async Task A_file_version_manifest_written_to_a_metadata_blob_reads_back_and_restores_the_file()
    {
        var data = BuildTestFile(regions: 6);
        var store = CreateStore();
        using var keys = CreateKeys();

        var archiver = CreateArchiver(store, keys);
        using var source = new MemoryStream(data);
        var archived = await archiver.ArchiveAsync(source, CancellationToken.None);

        // Build and store the manifest as a metadata record.
        var manifest = ManifestBuilder.FromArchiveResult(
            archived, "test-file.bin"u8.ToArray(), NameNormalisation.Nfc, EntryMetadata.Empty, parentVersion: null);
        var encoded = FileVersionManifestCodec.Encode(manifest);

        ObjectId manifestId;
        var builder = new ManifestBuilder(
            Repo, Writer, KeyGeneration.Zero, keys, store,
            new MonotonicBlobCounterAllocator(500), SpoolDirectory, BlobWriteProfile.LocalDefault);
        await using (builder.ConfigureAwait(false))
        {
            manifestId = await builder.AppendManifestAsync(ObjectType.FileVersionManifest, encoded, CancellationToken.None);
            await builder.FlushAsync(CancellationToken.None);

            Assert.ContainsSingle(builder.Blobs);
            Assert.StartsWith("blobs/meta/", builder.Blobs[0].StoreKey.Value, StringComparison.Ordinal);
        }

        // Read it back through footers alone and decode.
        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var read = await reader.ReadSegmentAsync(manifestId, CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome);

        var decoded = FileVersionManifestCodec.Decode(read.Plaintext!);

        Assert.AreEqual((ulong)data.Length, decoded.LogicalLength);
        SequenceAssert.AreEqual(archived.SegmentReferences, decoded.SegmentReferences);

        // Restore from the DECODED manifest's references — the manifest is
        // now the authority, exactly as a real restore would use it.
        using var restored = new MemoryStream();
        var restore = await reader.RestoreAsync(decoded.SegmentReferences, restored, CancellationToken.None);

        Assert.IsTrue(restore.Success, restore.FailureDetail);
        SequenceAssert.AreEqual(data, restored.ToArray());
        SequenceAssert.AreEqual(decoded.WholeFileHash.ToArray(), restore.WholeFileHash!.ToArray());
    }

    [TestMethod]
    public async Task A_standalone_snapshot_seals_under_its_bounded_prefix_and_opens_back()
    {
        var store = CreateStore();
        using var keys = CreateKeys();

        var snapshot = new SnapshotManifest
        {
            SnapshotId = Enumerable.Repeat((byte)0x11, 16).ToArray(),
            DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
            BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
            CaptureStartedAt = 1,
            CaptureCompletedAt = 2,
            RootTree = FallbackPlan.Domain.Identifiers.ObjectId.FromBytes(new byte[32]),
            PolicyManifest = FallbackPlan.Domain.Identifiers.ObjectId.FromBytes(Enumerable.Repeat((byte)9, 32).ToArray()),
            ConsistencyMethod = 1,
            CaptureStatus = 1,
            SourceFilesystem = new SourceFilesystem(true, true, "ext4"),
            PublicationGeneration = 0,
            ClientVersion = "tests/1.0",
        };
        var encoded = SnapshotManifestCodec.Encode(snapshot, new byte[64]);

        var builder = new ManifestBuilder(
            Repo, Writer, KeyGeneration.Zero, keys, store,
            new MonotonicBlobCounterAllocator(600), SpoolDirectory, BlobWriteProfile.LocalDefault);
        await using (builder.ConfigureAwait(false))
        {
            await builder.WriteStandaloneSnapshotAsync(snapshot, encoded, counter: 601, CancellationToken.None);
        }

        // Enumerable from the bounded /snapshots/ prefix without any index
        // (06 §6), then opened through the FBPKSREC framing.
        var entries = new List<FallbackPlan.Storage.Abstractions.ObjectEntry>();
        await foreach (var entry in store.ListAsync(
            FallbackPlan.Storage.Abstractions.ObjectPrefix.Parse("snapshots/"),
            FallbackPlan.Storage.Abstractions.ListOptions.Default,
            CancellationToken.None))
        {
            entries.Add(entry);
        }

        var stored = Assert.ContainsSingle(entries);

        using var open = await store.OpenReadAsync(stored.Key, range: null, CancellationToken.None);
        using var memory = new MemoryStream();
        await open.Content!.CopyToAsync(memory);

        var record = StandaloneRecordFraming.Parse(memory.ToArray());
        var metadataKey = keys.DeriveClassKey(BlobClass.Metadata, KeyGeneration.Zero);

        Assert.IsTrue(StandaloneRecordCipher.TryOpen(record, Repo, metadataKey, out var plaintext));

        var decoded = SnapshotManifestCodec.Decode(plaintext);
        SequenceAssert.AreEqual(snapshot.SnapshotId.ToArray(), decoded.Manifest.SnapshotId.ToArray());
    }
}
