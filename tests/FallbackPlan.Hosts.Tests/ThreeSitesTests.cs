using System.Text;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// One source and two alternative sites (FR-DEST-002, FR-DEST-003,
/// FR-DEST-004, FR-REP-001): a capture is packaged once and both peers hold
/// the same archive byte for byte; one peer going away neither delays the
/// capture nor holds the other peer back; the gap is recorded against the
/// absent peer alone, and closes when it returns.
/// </summary>
[TestClass]
public sealed class ThreeSitesTests : IDisposable
{
    private readonly HostHarness _source = new();
    private readonly HostHarness _harnessB = new();
    private readonly HostHarness _harnessC = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private CancellationToken Timeout => _timeout.Token;

    public void Dispose()
    {
        _timeout.Dispose();
        _source.Dispose();
        _harnessB.Dispose();
        _harnessC.Dispose();
    }

    [TestMethod]
    public async Task ThreeSites_OneSourceFansOutToTwoPeers_AndAnOutageAtOneLeavesTheOtherCurrent()
    {
        _source.WriteSourceFile("notes.txt", "before the outage");

        await using var siteB = await PeerSite.StartAsync(_harnessB, Timeout);
        await using var siteC = await PeerSite.StartAsync(_harnessC, Timeout);
        await using var source = await PeerSite.StartAsync(_source, Timeout);

        await source.PairToStoreAtAsync(siteB, "site-b");
        await source.PairToStoreAtAsync(siteC, "site-c");
        await source.DeclareDocsAsync("site-b", "site-c");

        // ---- The first capture lands at both, and both prove it.
        await source.WaitForFirstBackupAsync();
        await source.WaitForProvenAsync("site-b");
        var firstAtC = await source.WaitForProvenAsync("site-c");

        var atB = siteB.ReplicaOf(source);
        var atC = siteC.ReplicaOf(source);
        await PeerSite.AssertReplicaMatchesAsync(_source.RepositoryPath, atB, Timeout);
        await PeerSite.AssertReplicaMatchesAsync(_source.RepositoryPath, atC, Timeout);

        // ---- Site C goes away. The next capture still completes, site B
        // still receives and proves it, and only site C's row says otherwise.
        var syncedAtB = source.Runtime.DestinationSync.Find(_source.DocsSetId, "site-b")!.SyncedSequence;
        await siteC.GoOfflineAsync();

        _source.WriteSourceFile("during-outage.txt", "written while site C was down");
        await source.RunBackupAsync();
        await source.WaitForProvenAsync("site-b", after: syncedAtB);
        await source.WaitForAsync(() =>
            source.Runtime.DestinationSync.Find(_source.DocsSetId, "site-c") is { } row
            && row.State != DestinationSyncState.InSync);

        await PeerSite.AssertReplicaMatchesAsync(_source.RepositoryPath, atB, Timeout);
        var offline = source.Runtime.DestinationSync.Find(_source.DocsSetId, "site-c")!;
        Assert.IsNotNull(offline.LastError, "an outage is a named failure, not a silent gap");
        Assert.AreEqual(firstAtC.SyncedSequence, offline.SyncedSequence, "site C cannot hold what it never received");

        Assert.IsInstanceOfType<StatusResult>(
            await source.Operator.ExecuteAsync(new GetStatusCommand(), Timeout), out var status);
        var rows = Assert.ContainsSingle(status.Sets).Destinations.ToDictionary(row => row.Name, StringComparer.Ordinal);
        Assert.AreEqual("in-sync", rows["site-b"].State);
        Assert.AreNotEqual("in-sync", rows["site-c"].State);

        // ---- Site C returns where it was. The failed sync records its
        // outcome a moment before its job leaves the queue, and an on-demand
        // sync arriving in that tail would be coalesced away; wait until the
        // pair's job identity is free.
        await source.WaitForAsync(() => !source.Runtime.Queue.IsActive(FanOut.JobIdFor(_source.DocsSetId, "site-c")));
        siteC.ComeBackOnline();

        Assert.IsInstanceOfType<SyncResult>(
            await source.Operator.ExecuteAsync(new SyncCommand("docs", "site-c"), Timeout), out var synced);
        Assert.Contains(line => line.Contains("in sync", StringComparison.Ordinal), synced.Lines, string.Join(" | ", synced.Lines));
        await PeerSite.AssertReplicaMatchesAsync(_source.RepositoryPath, atC, Timeout);

        // The catch-up carried the outage's capture: the file written while
        // site C was down comes back from site C.
        var (exitCode, snapshots, error) = await source.ListSnapshotsInAsync(atC);
        Assert.AreEqual(0, exitCode, error);
        Assert.HasCount(2, snapshots);
        var restored = await source.RecoverFromAsync(atC, snapshots[0], "from-c-a");
        if (PeerSite.FindFile(restored, "during-outage.txt") is null)
        {
            restored = await source.RecoverFromAsync(atC, snapshots[1], "from-c-b");
        }

        await PeerSite.AssertFileRestoredAsync(
            restored, "during-outage.txt", Encoding.UTF8.GetBytes("written while site C was down"));
    }
}
