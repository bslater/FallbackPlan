using FallbackPlan.Repository.Packing;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// Corruption probes against the stored bytes (specification 04 §7, 05 §4–§5;
/// NFR-REL-004, FR-RST-005): damage is detected, scoped to the record it
/// touched, and never turns into a partial restore.
/// </summary>
[TestClass]
public sealed class ArchiveCorruptionTests : ArchiveTestHarness
{
    private async Task<(ArchiveResult Result, RepositoryKeySet Keys, Storage.Local.LocalFileSystemObjectStore Store)> ArchiveAsync()
    {
        var store = CreateStore();
        var keys = CreateKeys();
        var archiver = CreateArchiver(store, keys);

        using var source = new MemoryStream(BuildTestFile(regions: 6));
        var result = await archiver.ArchiveAsync(source, CancellationToken.None);

        return (result, keys, store);
    }

    private string[] StoredBlobFiles() =>
        [.. Directory.EnumerateFiles(StoreRoot, "*", SearchOption.AllDirectories).Where(f => !f.Contains(".fbp-tmp", StringComparison.Ordinal)).Order()];

    [TestMethod]
    public async Task A_flipped_ciphertext_byte_fails_exactly_its_record_and_refuses_the_restore()
    {
        var (result, keys, store) = await ArchiveAsync();
        using var _ = keys;

        // The compressible regions repeat, so several references legitimately
        // share one object identifier. Pick a victim referenced exactly once
        // so "exactly one failure" is well-defined, locate its record, and
        // flip a single ciphertext byte beneath the store.
        var victimReference = result.SegmentReferences
            .GroupBy(reference => reference.ObjectId)
            .First(group => group.Count() == 1)
            .Single();

        RecordTableEntry victim;
        string victimFile;
        using (var probe = new RepositoryReader(Repo, keys, store))
        {
            await probe.LoadBlobsAsync(CancellationToken.None);
            Assert.IsTrue(probe.TryLocateRecord(victimReference.ObjectId, out var blobStoreKey, out victim));
            victimFile = Path.Combine(StoreRoot, blobStoreKey.Value.Replace('/', Path.DirectorySeparatorChar));
        }

        var stored = await File.ReadAllBytesAsync(victimFile, CancellationToken.None);
        stored[(long)victim.PhysicalOffset + 54 + 3] ^= 0x01;
        await File.WriteAllBytesAsync(victimFile, stored, CancellationToken.None);

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        // Exactly the damaged record fails; every other segment still reads.
        var failures = 0;
        foreach (var reference in result.SegmentReferences)
        {
            var read = await reader.ReadSegmentAsync(reference.ObjectId, CancellationToken.None);
            if (read.Outcome != RecordReadOutcome.Ok)
            {
                failures++;
                Assert.AreEqual(reference.ObjectId, victimReference.ObjectId);
                Assert.AreEqual(RecordReadOutcome.AuthenticationFailed, read.Outcome);
            }
        }

        Assert.AreEqual(1, failures);

        // The whole-file restore refuses rather than emitting partial output.
        using var destination = new MemoryStream();
        var restore = await reader.RestoreAsync(result.SegmentReferences, destination, CancellationToken.None);

        Assert.IsFalse(restore.Success);
        Assert.IsNotNull(restore.FailureDetail);
        Assert.Contains("AuthenticationFailed", restore.FailureDetail, StringComparison.Ordinal);
        Assert.AreEqual(0, destination.Length);
    }

    [TestMethod]
    public async Task A_truncated_locator_is_refused_at_open()
    {
        var (_, keys, store) = await ArchiveAsync();
        using var _ = keys;

        var file = StoredBlobFiles()[0];
        var bytes = await File.ReadAllBytesAsync(file, CancellationToken.None);
        await File.WriteAllBytesAsync(file, bytes[..^16], CancellationToken.None);

        using var reader = new RepositoryReader(Repo, keys, store);

        await Assert.ThrowsExactlyAsync<BlobFormatException>(async () =>
            await reader.LoadBlobsAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task Trailing_garbage_after_the_locator_is_a_damage_finding()
    {
        var (_, keys, store) = await ArchiveAsync();
        using var _ = keys;

        var file = StoredBlobFiles()[0];
        var bytes = await File.ReadAllBytesAsync(file, CancellationToken.None);
        await File.WriteAllBytesAsync(file, [.. bytes, 0xDE, 0xAD], CancellationToken.None);

        using var reader = new RepositoryReader(Repo, keys, store);

        await Assert.ThrowsExactlyAsync<BlobFormatException>(async () =>
            await reader.LoadBlobsAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task A_blob_from_a_different_repository_fails_footer_authentication()
    {
        var (_, keys, store) = await ArchiveAsync();
        using var _ = keys;

        var otherRepository = FallbackPlan.Domain.Identifiers.RepositoryId.FromBytes(
            Convert.FromHexString("ffffffffffffffffffffffffffffffff"));

        using var reader = new RepositoryReader(otherRepository, keys, store);

        var exception = await Assert.ThrowsExactlyAsync<BlobFormatException>(async () =>
            await reader.LoadBlobsAsync(CancellationToken.None));
        Assert.Contains("authentication", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
