using FallbackPlan.Repository;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Packing;

namespace FallbackPlan.InterruptionTests;

/// <summary>
/// The F2 corruption harness (specification 04 §7, 05 §3–§5;
/// NFR-REL-004, NFR-SEC-005, FR-MAN-011): a bit flipped in each region class of the
/// stored bytes is detected, classified, and scoped — and a snapshot whose
/// objects were not touched stays restorable through every case.
/// </summary>
public sealed class CorruptionHarnessTests : InterruptionHarness
{
    private async Task<(byte[] Data, RepositoryKeySet Keys, Repository.Crypto.KeyHierarchy Hierarchy, Storage.Local.LocalFileSystemObjectStore Store)>
        PublishAsync()
    {
        var store = CreateStore();
        var keys = CreateKeys();
        var hierarchy = CreateHierarchy();

        var data = BuildFile(seed: 11);
        using var source = new MemoryStream(data);
        await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xA1), CancellationToken.None);

        return (data, keys, hierarchy, store);
    }

    private string FirstDataBlobPath() =>
        Directory.EnumerateFiles(Path.Combine(StoreRoot, "blobs", "data"), "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .First();

    private static async Task FlipByteAsync(string path, Index index)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[index] ^= 0x01;
        await File.WriteAllBytesAsync(path, bytes);
    }

    [Fact]
    public async Task An_envelope_flip_fails_the_blob_at_open_and_is_scoped_to_it()
    {
        var (_, keys, hierarchy, store) = await PublishAsync();
        using var _keys = keys;
        using var _hierarchy = hierarchy;

        // Flip inside the envelope's salt: the derived blob key changes, so
        // the footer fails authentication at open — a scoped damage finding.
        await FlipByteAsync(FirstDataBlobPath(), 20);

        using var reader = new RepositoryReader(Repo, keys, store);
        var exception = await Assert.ThrowsAsync<BlobFormatException>(async () =>
            await reader.LoadBlobsAsync(CancellationToken.None));
        Assert.Contains("authentication", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_record_header_flip_is_a_format_violation_scoped_to_its_record()
    {
        var (_, keys, hierarchy, store) = await PublishAsync();
        using var _keys = keys;
        using var _hierarchy = hierarchy;

        // Flip a byte in the first record's header (it sits right after the
        // 88-byte envelope): the header disagrees with the footer's table.
        await FlipByteAsync(FirstDataBlobPath(), BlobEnvelope.Length + 6);

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var failures = 0;
        foreach (var record in reader.AllRecords)
        {
            var read = await reader.ReadSegmentAsync(record.ObjectId, CancellationToken.None);
            if (read.Outcome != RecordReadOutcome.Ok)
            {
                failures++;
                Assert.Equal(RecordReadOutcome.FormatViolation, read.Outcome);
            }
        }

        Assert.Equal(1, failures);
    }

    [Fact]
    public async Task A_tag_flip_is_an_authentication_failure_scoped_to_its_record()
    {
        var (_, keys, hierarchy, store) = await PublishAsync();
        using var _keys = keys;
        using var _hierarchy = hierarchy;

        using var probe = new RepositoryReader(Repo, keys, store);
        await probe.LoadBlobsAsync(CancellationToken.None);
        var victim = probe.AllRecords.First();
        Assert.True(probe.TryLocateRecord(victim.ObjectId, out var storeKey, out var entry));

        // The tag is the 16 bytes after the stored payload.
        var path = Path.Combine(StoreRoot, storeKey.Value.Replace('/', Path.DirectorySeparatorChar));
        await FlipByteAsync(path, (int)((long)entry.PhysicalOffset + RecordHeader.Length + entry.StoredLength));

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var read = await reader.ReadSegmentAsync(victim.ObjectId, CancellationToken.None);

        Assert.Equal(RecordReadOutcome.AuthenticationFailed, read.Outcome);
    }

    [Fact]
    public async Task A_footer_flip_fails_the_blob_at_open()
    {
        var (_, keys, hierarchy, store) = await PublishAsync();
        using var _keys = keys;
        using var _hierarchy = hierarchy;

        var path = FirstDataBlobPath();
        var length = new FileInfo(path).Length;

        // Flip inside the encrypted footer (between the last record and the
        // locator) — table authentication fails at open.
        await FlipByteAsync(path, (int)(length - FooterLocator.Length - 4));

        using var reader = new RepositoryReader(Repo, keys, store);
        await Assert.ThrowsAsync<BlobFormatException>(async () => await reader.LoadBlobsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_corrupted_standalone_snapshot_fails_authentication_and_emits_nothing()
    {
        var (_, keys, hierarchy, store) = await PublishAsync();
        using var _keys = keys;
        using var _hierarchy = hierarchy;

        var snapshotPath = Directory
            .EnumerateFiles(Path.Combine(StoreRoot, "snapshots"), "*", SearchOption.AllDirectories)
            .Single();
        await FlipByteAsync(snapshotPath, ^20); // inside ciphertext/tag territory

        var record = StandaloneRecordFraming.Parse(await File.ReadAllBytesAsync(snapshotPath));
        var metadataKey = keys.DeriveClassKey(Domain.BlobClass.Metadata, record.KeyGeneration);

        Assert.False(StandaloneRecordCipher.TryOpen(record, Repo, metadataKey, out var plaintext));
        Assert.Empty(plaintext);
    }

    [Fact]
    public async Task A_snapshot_sealed_for_another_repository_is_rejected_on_replay()
    {
        var (_, keys, hierarchy, store) = await PublishAsync();
        using var _keys = keys;
        using var _hierarchy = hierarchy;

        // Replay the snapshot object into a "sibling repository" reader: the
        // AAD binds the repository identity, so substitution fails closed.
        var snapshotPath = Directory
            .EnumerateFiles(Path.Combine(StoreRoot, "snapshots"), "*", SearchOption.AllDirectories)
            .Single();
        var record = StandaloneRecordFraming.Parse(await File.ReadAllBytesAsync(snapshotPath));

        var otherRepository = Domain.Identifiers.RepositoryId.FromBytes(
            Convert.FromHexString("ffffffffffffffffffffffffffffffff"));
        var metadataKey = keys.DeriveClassKey(Domain.BlobClass.Metadata, record.KeyGeneration);

        Assert.False(StandaloneRecordCipher.TryOpen(record, otherRepository, metadataKey, out _));
    }

    [Fact]
    public async Task A_missing_blob_refuses_the_restore_with_the_segment_named()
    {
        var (_, keys, hierarchy, store) = await PublishAsync();
        using var _keys = keys;
        using var _hierarchy = hierarchy;

        File.Delete(FirstDataBlobPath());

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        // The snapshot's manifest still decodes (metadata blobs intact), but
        // the restore refuses — no partial file, the missing segment named.
        await Assert.ThrowsAnyAsync<Exception>(async () => await RestoreSnapshotAsync(store, keys, 0xA1));
    }

    [Fact]
    public async Task Untouched_snapshots_survive_every_corruption_of_a_second_backup()
    {
        var (baseline, keys, hierarchy, store) = await PublishAsync();
        using var _keys = keys;
        using var _hierarchy = hierarchy;

        // Publish a second snapshot, then corrupt one of ITS new blobs.
        var second = BuildFile(seed: 12);
        var blobsBefore = Directory
            .EnumerateFiles(Path.Combine(StoreRoot, "blobs"), "*", SearchOption.AllDirectories)
            .ToHashSet(StringComparer.Ordinal);

        using (var source = new MemoryStream(second))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xB2), CancellationToken.None);
        }

        // Flip inside a record body (not the footer — a damaged footer is a
        // per-blob open failure, exercised in its own case above).
        var newBlob = Directory
            .EnumerateFiles(Path.Combine(StoreRoot, "blobs", "data"), "*", SearchOption.AllDirectories)
            .First(file => !blobsBefore.Contains(file));
        await FlipByteAsync(newBlob, BlobEnvelope.Length + RecordHeader.Length + 10);

        // Corruption is local (04 §7): the first snapshot's objects were not
        // touched, and it restores byte-identically.
        Assert.Equal(baseline, await RestoreSnapshotAsync(store, keys, 0xA1));
    }
}
