using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Index;

namespace FallbackPlan.Repository.Tests.Index;

/// <summary>
/// Publication cuts a snapshot's index entries into deltas at blob boundaries
/// as it reads them (FR-MAN-008, ADR-0201): no more of the snapshot's blobs
/// is read than the delta being cut needs, so a snapshot's entries are never
/// all in memory at once.
/// </summary>
[TestClass]
public sealed class IndexDeltaBudgetTests
{
    private const int EntriesPerBlob = 100;

    [TestMethod]
    public void SplitAsItGoes_HasReadNoFurtherThanTheDeltaItHasCut()
    {
        var read = 0;
        IEnumerable<CoveredBlob> Blobs()
        {
            for (var index = 0; index < 1_000; index++)
            {
                read++;
                yield return Blob(index);
            }
        }

        // A budget that fits a few blobs and not many: the first delta closes
        // when the next blob would not fit, so the split has read that one
        // blob beyond it and no more.
        var budget = 10 * EntriesPerBlob * 64;
        using var parts = IndexDeltaBudget.SplitAsItGoes(Blobs(), budget).GetEnumerator();

        Assert.IsTrue(parts.MoveNext());
        var first = parts.Current;
        Assert.IsGreaterThan(1, first.CoveredBlobIds.Count);
        Assert.AreEqual(first.CoveredBlobIds.Count + 1, read);
    }

    [TestMethod]
    public void SplitAsItGoes_CutsWhatSplitCuts()
    {
        var blobs = Enumerable.Range(0, 37).Select(Blob).ToList();
        var budget = 7 * EntriesPerBlob * 64;

        var streamed = IndexDeltaBudget.SplitAsItGoes(blobs, budget).ToList();
        var whole = IndexDeltaBudget.Split(blobs, budget);

        Assert.HasCount(whole.Count, streamed);
        for (var index = 0; index < whole.Count; index++)
        {
            CollectionAssert.AreEqual(whole[index].CoveredBlobIds.ToArray(), streamed[index].CoveredBlobIds.ToArray());
            CollectionAssert.AreEqual(whole[index].Entries.ToArray(), streamed[index].Entries.ToArray());
        }
    }

    private static CoveredBlob Blob(int index)
    {
        var id = new byte[16];
        BitConverter.TryWriteBytes(id, index);
        var blobId = BlobId.FromBytes(id);
        var entries = Enumerable.Range(0, EntriesPerBlob).Select(record =>
        {
            var objectId = new byte[32];
            BitConverter.TryWriteBytes(objectId, (index * EntriesPerBlob) + record);
            return new IndexEntry(
                ObjectId.FromBytes(objectId), blobId, (ulong)(record * 4096), 4096, 0, 1, IndexEntryType.Insertion);
        }).ToList();
        return new CoveredBlob(blobId, new byte[32], null, entries);
    }
}
