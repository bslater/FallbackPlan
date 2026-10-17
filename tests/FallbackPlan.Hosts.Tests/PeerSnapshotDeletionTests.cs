using FallbackPlan.Api;
using FallbackPlan.Domain;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A person's deletion reaching a live peer through content that snapshots
/// share (FR-GC-013; ADR-0080): the one command drops the snapshot
/// at the peer and the data only it held, and leaves the data it shared with
/// the snapshots that stay, so what remains still restores from the peer.
/// </summary>
/// <remarks>
/// Three captures make the shapes unambiguous. The first holds one file; the
/// second adds another, whose segments are the only new data and so sit in
/// blobs of their own; the third removes it again. The middle snapshot is then
/// the only one that references the second file's blobs, and deleting it must
/// leave the peer holding exactly the data the first capture sent.
/// </remarks>
[TestClass]
public sealed class PeerSnapshotDeletionTests : IDisposable
{
    private static readonly int Segment = SegmentSize.Default.Bytes;

    private readonly HostHarness _source = new();
    private readonly HostHarness _peer = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private CancellationToken Timeout => _timeout.Token;

    public void Dispose()
    {
        _timeout.Dispose();
        _source.Dispose();
        _peer.Dispose();
    }

    [TestMethod]
    public async Task DeleteSnapshots_TheOnlySnapshotHoldingAFile_LeavesThePeerWithTheSharedDataAndWithoutTheRest()
    {
        var kept = PeerSite.Noise(2 * Segment, seed: 11);
        var draft = PeerSite.Noise(2 * Segment, seed: 12);

        await using var peer = await PeerSite.StartAsync(_peer, Timeout);
        await using var source = await PeerSite.StartAsync(_source, Timeout);
        source.WriteSourceBytes("kept.bin", kept);

        await source.PairToStoreAtAsync(peer, "site-b");
        await source.DeclareDocsAsync("site-b");
        await source.WaitForFirstBackupAsync();
        var synced = await source.WaitForProvenAsync("site-b");
        var replicaPath = peer.ReplicaOf(source);
        var dataSentFirst = PeerSite.DataBlobs(await PeerSite.ReadReplicableAsync(replicaPath, Timeout)).Keys.ToHashSet(StringComparer.Ordinal);

        var draftPath = source.WriteSourceBytes("draft.bin", draft);
        await source.RunBackupAsync();
        synced = await source.WaitForProvenAsync("site-b", after: synced.SyncedSequence);

        File.Delete(draftPath);
        await source.RunBackupAsync();
        await source.WaitForProvenAsync("site-b", after: synced.SyncedSequence);

        // The draft's data crossed, and is still there while its snapshot is.
        var beforeDeletion = PeerSite.DataBlobs(await PeerSite.ReadReplicableAsync(replicaPath, Timeout));
        Assert.IsTrue(dataSentFirst.IsProperSubsetOf(beforeDeletion.Keys), "the draft's data never reached the peer");

        Assert.IsInstanceOfType<SnapshotsResult>(
            await source.Operator.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var listed);
        Assert.HasCount(3, listed.Snapshots);
        var middle = listed.Snapshots.OrderBy(snapshot => snapshot.CapturedAt).ElementAt(1).SnapshotId;

        // ---- The person's deletion, under the set's reclaim grant.
        var grant = await _source.ReclaimGrantAsync(source.Operator, Timeout);
        Assert.IsInstanceOfType<DeleteSnapshotsResult>(
            await source.Operator.ExecuteAsync(
                new DeleteSnapshotsCommand(_source.DocsSetId, [middle], Apply: true, ReclaimGrant: grant), Timeout),
            out var deleted);
        Assert.AreEqual("deleted", Assert.ContainsSingle(deleted.Snapshots).State, string.Join(" | ", deleted.Lines));

        // ---- The peer let go of the draft's data in the same command, and
        // of nothing the snapshots that stay still use.
        var afterDeletion = PeerSite.DataBlobs(await PeerSite.ReadReplicableAsync(replicaPath, Timeout));
        Assert.IsTrue(
            dataSentFirst.SetEquals(afterDeletion.Keys),
            $"the peer holds {afterDeletion.Count} data blob(s) after the deletion; the first capture sent {dataSentFirst.Count}");

        // The next pass carries the deletion's own records, and the peer is
        // the archive again.
        Assert.IsInstanceOfType<SyncResult>(
            await source.Operator.ExecuteAsync(new SyncCommand("docs", "site-b"), Timeout), out var resynced);
        Assert.Contains(line => line.Contains("in sync", StringComparison.Ordinal), resynced.Lines, string.Join(" | ", resynced.Lines));
        await PeerSite.AssertReplicaMatchesAsync(_source.RepositoryPath, replicaPath, Timeout);

        // ---- What stays restores from the peer, and the draft is in none
        // of it.
        var (exitCode, snapshots, error) = await source.ListSnapshotsInAsync(replicaPath);
        Assert.AreEqual(0, exitCode, error);
        Assert.HasCount(2, snapshots);
        foreach (var (snapshot, index) in snapshots.Select((snapshot, index) => (snapshot, index)))
        {
            var restored = await source.RecoverFromAsync(replicaPath, snapshot, $"recovered-{index}");
            await PeerSite.AssertFileRestoredAsync(restored, "kept.bin", kept);
            Assert.IsNull(PeerSite.FindFile(restored, "draft.bin"), "a snapshot that stays never held the draft");
        }
    }
}
