using FallbackPlan.Domain;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The low-level restore's assembly check (architecture 08 §3; FR-RST-002).
/// Each segment individually authenticates and re-derives its own object
/// identifier, so a reference list that names real segments at the wrong
/// offsets passes every per-part check — two same-length segments swapped
/// produce a byte-for-byte plausible file that is not the file. Only the
/// whole-file hash catches assembly, and a caller that has it must be able
/// to make <see cref="RepositoryReader.RestoreAsync"/> enforce it.
/// </summary>
[TestClass]
public sealed class RestoreAssemblyTests : ArchiveTestHarness
{
    [TestMethod]
    public async Task Restore_SegmentsSwappedButContiguous_IsRefusedByTheExpectedHash()
    {
        // Two distinct, equal-length segments: swapping them keeps every
        // per-segment check green and changes only the assembly.
        var original = new byte[2 * 64 * 1024];
        new Random(7).NextBytes(original);

        var store = CreateStore();
        using var keys = CreateKeys();

        var archiver = CreateArchiver(store, keys);
        using var source = new MemoryStream(original);
        var result = await archiver.ArchiveAsync(source, CancellationToken.None);
        ReadOnlyMemory<byte> expectedHash = result.WholeFileHash.ToArray();
        Assert.AreEqual(2, result.SegmentReferences.Count);

        var straight = result.SegmentReferences;
        IReadOnlyList<SegmentReference> swapped =
        [
            straight[0] with { ObjectId = straight[1].ObjectId },
            straight[1] with { ObjectId = straight[0].ObjectId },
        ];

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        // The honest list with the expected hash still restores.
        using (var restored = new MemoryStream())
        {
            var restore = await reader.RestoreAsync(straight, restored, expectedHash, CancellationToken.None);
            Assert.IsTrue(restore.Success, restore.FailureDetail);
            SequenceAssert.AreEqual(original, restored.ToArray());
        }

        // The swapped list must be refused before a byte reaches the
        // destination: every tag passes, every length matches, and the
        // whole-file hash is the only thing left that can say no.
        using (var restored = new MemoryStream())
        {
            var restore = await reader.RestoreAsync(swapped, restored, expectedHash, CancellationToken.None);
            Assert.IsFalse(restore.Success, "segments assembled in the wrong order must not restore as success");
            Assert.AreEqual(0, restored.Length, "a refused restore must write nothing to the destination");
        }
    }
}
