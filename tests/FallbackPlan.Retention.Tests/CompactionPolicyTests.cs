using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Retention;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Retention.Tests;

/// <summary>
/// Which partly live blobs are worth rewriting (ADR-0067; FR-GC-011,
/// NFR-OPS-008). The planner hands over every blob it kept whole for a live
/// minority; the policy decides which of them a pass touches, and the two
/// bounds exist for different reasons — a fraction, because rewriting a blob
/// that is mostly live moves bytes to reclaim almost nothing, and a floor in
/// bytes, because rewriting a small blob costs more than the few kilobytes
/// it frees whatever the fraction says.
/// </summary>
/// <remarks>
/// Pure over the plan's own rows, so no archive is built here: what an
/// archive proves is that the rows are right, which
/// <see cref="SharedRecordRetentionTests"/> already holds.
/// </remarks>
[TestClass]
public sealed class CompactionPolicyTests
{
    private static readonly CompactionPolicy Default = CompactionPolicy.Default;

    [TestMethod]
    public void ABlobHalfDead_AndPastTheFloor_IsACandidate()
    {
        var blob = Blob(liveBytes: 8L * 1024 * 1024, deadBytes: 8L * 1024 * 1024);

        var candidates = Default.SelectCandidates([blob]);

        Assert.AreEqual(blob.BlobId, Assert.ContainsSingle(candidates).BlobId);
    }

    [TestMethod]
    public void ABlobMostlyLive_IsLeftAlone()
    {
        // A tenth dead: rewriting it moves nine times the bytes it frees.
        var blob = Blob(liveBytes: 90L * 1024 * 1024, deadBytes: 10L * 1024 * 1024);

        Assert.IsEmpty(Default.SelectCandidates([blob]));
    }

    [TestMethod]
    public void ABlobMostlyDead_ButSmall_IsLeftAlone()
    {
        // Nine tenths dead and still not worth the write: the floor is in
        // bytes because the cost of a rewrite is in bytes.
        var blob = Blob(liveBytes: 64 * 1024, deadBytes: 576 * 1024);

        Assert.IsEmpty(Default.SelectCandidates([blob]));
    }

    [TestMethod]
    public void TheBudget_StopsThePass_AndTakesTheLargestReclaimFirst()
    {
        // Three candidates, each individually eligible, whose cost sums past
        // one pass's budget. The budget is over bytes READ — the whole
        // candidate, live and dead alike, because the rewrite copies the live
        // records out of a blob it has had to open — so twenty and twelve fit
        // in thirty-four and eight more do not. A pass takes what it can and
        // the rest wait: a long-neglected archive converges over several runs
        // rather than monopolising one.
        var policy = Default with { ByteBudget = 34L * 1024 * 1024 };
        var small = Blob(liveBytes: 1024, deadBytes: 8L * 1024 * 1024);
        var large = Blob(liveBytes: 1024, deadBytes: 20L * 1024 * 1024);
        var middling = Blob(liveBytes: 1024, deadBytes: 12L * 1024 * 1024);

        var candidates = policy.SelectCandidates([small, large, middling]);

        Assert.HasCount(2, candidates);
        Assert.AreEqual(large.BlobId, candidates[0].BlobId);
        Assert.AreEqual(middling.BlobId, candidates[1].BlobId);
    }

    [TestMethod]
    public void AnEmptyBacklog_SelectsNothing()
    {
        Assert.IsEmpty(Default.SelectCandidates([]));
    }

    [TestMethod]
    public void TheDescription_SaysWhatWouldBeReclaimed_AndWhatIsWaiting()
    {
        var taken = Blob(liveBytes: 1024, deadBytes: 20L * 1024 * 1024);
        var waiting = Blob(liveBytes: 90L * 1024 * 1024, deadBytes: 10L * 1024 * 1024);

        var lines = string.Join("\n", Default.Describe([taken, waiting], Default.SelectCandidates([taken, waiting])));

        Assert.Contains("1 blob(s)", lines, StringComparison.Ordinal);
        Assert.Contains("20,971,520", lines, StringComparison.Ordinal);
        Assert.Contains("below the threshold", lines, StringComparison.Ordinal);
    }

    /// <summary>
    /// The format gate, and the whole of what it says: below format 3 a
    /// rewrite means decrypt-and-reseal, which needs a content key this
    /// service does not hold, so nothing is selected and the remedy is
    /// named.
    /// </summary>
    [TestMethod]
    public void AFormatTwoSet_SelectsNothing_AndNamesTheRemedy()
    {
        var blob = Blob(liveBytes: 8L * 1024 * 1024, deadBytes: 8L * 1024 * 1024);

        var selection = Default.Select(FormatVersions.SealedDataPlane, [blob]);

        Assert.IsEmpty(selection.Candidates);
        Assert.Contains(
            "upgrade_set_format",
            string.Join("\n", selection.Lines),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// And it is quiet when there is nothing to be quiet about. ADR-0066
    /// decision 6 supports both formats and pushes neither, and the
    /// <c>format-upgradable</c> notice is silenced for good by acknowledging
    /// it; a retention line repeating the offer every pass would re-open what
    /// that acknowledgement closed. So the refusal is printed only when a
    /// backlog would otherwise have produced work.
    /// </summary>
    [TestMethod]
    public void AFormatTwoSet_WithNothingWorthRewriting_SaysNothingAtAll()
    {
        // A backlog, but every blob below the threshold: at format 3 this
        // pass would also have done nothing, so there is no offer to make.
        var trivial = Blob(liveBytes: 64 * 1024, deadBytes: 576 * 1024);

        Assert.IsEmpty(Default.Select(FormatVersions.SealedDataPlane, [trivial]).Lines
            .Where(line => line.Contains("upgrade_set_format", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AFormatThreeSet_SelectsAndDescribesAsThePolicyAlwaysDid()
    {
        var blob = Blob(liveBytes: 8L * 1024 * 1024, deadBytes: 8L * 1024 * 1024);

        var selection = Default.Select(FormatVersions.RelocatableRecords, [blob]);

        Assert.AreEqual(blob.BlobId, Assert.ContainsSingle(selection.Candidates).BlobId);
        Assert.Contains(
            "compaction would rewrite",
            string.Join("\n", selection.Lines),
            StringComparison.Ordinal);
    }

    private static int _next;

    private static CompactableBlob Blob(long liveBytes, long deadBytes)
    {
        var n = Interlocked.Increment(ref _next);
        var bytes = new byte[BlobId.Size];
        BitConverter.TryWriteBytes(bytes, n);
        var id = BlobId.FromBytes(bytes);
        return new CompactableBlob(
            ObjectKey.Parse($"blobs/data/abcd/blob{n}"),
            id,
            [new RecordTableEntry(
                ObjectId.FromBytes([.. Enumerable.Repeat((byte)7, 32)]),
                Ordinal: 0,
                PhysicalOffset: 0,
                StoredLength: (uint)liveBytes,
                LogicalLength: (ulong)liveBytes,
                CompressionProfileValue: 0,
                EncryptionProfileValue: 1,
                ObjectType.SegmentRecord)],
            liveBytes,
            deadBytes,
            DeadRecords: 1);
    }
}
