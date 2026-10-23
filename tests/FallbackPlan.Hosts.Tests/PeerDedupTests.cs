using FallbackPlan.Domain;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Deduplication as the other site sees it (FR-MAN-006, FR-REP-005): content
/// the source already holds is stored there once, and a pass to a live peer
/// carries only what is new. A copy of a file costs the peer nothing, and an
/// edit costs it the segment the edit touched.
/// </summary>
/// <remarks>
/// <para>
/// Every measure is taken from what the stores hold and from what the ledger
/// says a pass copied, never from the product's own account of what it
/// skipped. The replica equals the archive after every pass, so the archive's
/// growth is the peer's. The ledger's count of objects copied is held to the
/// objects that actually changed at the peer, so a pass that re-sent what the
/// peer already held would be caught even though the end state is the same.
/// </para>
/// <para>
/// The content is noise, which no compressor shrinks, so a stored size says
/// how many segments are there. Segments are the default fixed size, and the
/// edit sits wholly inside one of them.
/// </para>
/// </remarks>
[TestClass]
public sealed class PeerDedupTests : IDisposable
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
    public async Task Dedup_ACopyAndAnInPlaceEdit_CarryOnlyTheEditedSegmentToThePeer()
    {
        await using var peer = await PeerSite.StartAsync(_peer, Timeout);
        await using var source = await PeerSite.StartAsync(_source, Timeout);

        // Four segments of content, twice over in the first capture.
        var original = PeerSite.Noise(4 * Segment, seed: 1);
        source.WriteSourceBytes("media/big.bin", original);
        source.WriteSourceBytes("media/twin.bin", original);

        await source.PairToStoreAtAsync(peer, "site-b");
        await source.DeclareDocsAsync("site-b");
        await source.WaitForFirstBackupAsync();
        var first = await source.WaitForProvenAsync("site-b");

        var replicaPath = peer.ReplicaOf(source);
        await PeerSite.AssertReplicaMatchesAsync(_source.RepositoryPath, replicaPath, Timeout);
        var before = await PeerSite.ReadReplicableAsync(replicaPath, Timeout);
        var dataBefore = PeerSite.DataBlobs(before);

        // ---- Within one capture, two identical files are stored once.
        var storedOnce = dataBefore.Values.Sum(blob => (long)blob.Length);
        Assert.IsGreaterThanOrEqualTo((long)original.Length, storedOnce, "the content itself must have crossed");
        Assert.IsLessThan((long)original.Length + Segment, storedOnce,
            $"two identical files of {original.Length} bytes stored {storedOnce} bytes of data at the peer");

        // ---- The second capture: a third copy, and a hundred bytes edited
        // inside big.bin's third segment. twin.bin keeps the original.
        source.WriteSourceBytes("media/copy-of-big.bin", original);
        var edited = (byte[])original.Clone();
        PeerSite.Noise(100, seed: 2).CopyTo(edited, (2 * Segment) + 4096);
        source.WriteSourceBytes("media/big.bin", edited);

        await source.RunBackupAsync();
        var second = await source.WaitForProvenAsync("site-b", after: first.SyncedSequence);
        await PeerSite.AssertReplicaMatchesAsync(_source.RepositoryPath, replicaPath, Timeout);

        var after = await PeerSite.ReadReplicableAsync(replicaPath, Timeout);
        var changed = after
            .Where(pair => !before.TryGetValue(pair.Key, out var was) || !was.AsSpan().SequenceEqual(pair.Value))
            .Select(pair => pair.Key)
            .ToList();
        var dataAdded = PeerSite.DataBlobs(after)
            .Where(pair => !dataBefore.ContainsKey(pair.Key))
            .Sum(pair => (long)pair.Value.Length);

        // The edited segment crossed. The copy's four and big.bin's three
        // unedited segments did not.
        Assert.IsGreaterThanOrEqualTo((long)Segment, dataAdded, "the edited segment must have crossed");
        Assert.IsLessThan(2L * Segment, dataAdded,
            $"the second pass carried {dataAdded} bytes of data for one edited segment and a copy");
        Assert.IsTrue(dataBefore.Keys.All(after.ContainsKey), "a data blob the first pass carried has gone");

        // And the pass copied what changed, not what the peer already held.
        Assert.AreEqual(changed.Count, second.Objects,
            $"the pass copied {second.Objects} object(s); {changed.Count} changed at the peer: {string.Join(", ", changed)}");

        // ---- All three files come back from the peer as they were captured.
        var (exitCode, snapshots, error) = await source.ListSnapshotsInAsync(replicaPath);
        Assert.AreEqual(0, exitCode, error);
        Assert.HasCount(2, snapshots);

        // The listing's order is the recovery tool's business; the content
        // says which snapshot is the later one.
        var restored = await source.RecoverFromAsync(replicaPath, snapshots[0], "recovered-a");
        if (PeerSite.FindFile(restored, "copy-of-big.bin") is null)
        {
            restored = await source.RecoverFromAsync(replicaPath, snapshots[1], "recovered-b");
        }

        await PeerSite.AssertFileRestoredAsync(restored, "big.bin", edited);
        await PeerSite.AssertFileRestoredAsync(restored, "twin.bin", original);
        await PeerSite.AssertFileRestoredAsync(restored, "copy-of-big.bin", original);
    }
}
