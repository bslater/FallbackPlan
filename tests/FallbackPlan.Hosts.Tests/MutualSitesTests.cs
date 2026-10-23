using System.Text;
using FallbackPlan.Api;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Two households, each the other's alternative site (FR-REP-001, FR-VER-001;
/// ADR-0030 Amendment 4): each service backs up and stores on the one
/// listener, and pairing in one direction neither stands in for pairing in
/// the other nor gets in its way. Each site's files come back from the
/// replica the other holds, with its own passphrase alone.
/// </summary>
[TestClass]
public sealed class MutualSitesTests : IDisposable
{
    private readonly HostHarness _harnessA = new();
    private readonly HostHarness _harnessB = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private CancellationToken Timeout => _timeout.Token;

    public void Dispose()
    {
        _timeout.Dispose();
        _harnessA.Dispose();
        _harnessB.Dispose();
    }

    [TestMethod]
    public async Task MutualSites_EachBacksUpToTheOther_AndEachRecoversFromTheReplicaTheOtherHolds()
    {
        _harnessA.WriteSourceFile("notes.txt", "site A's notes");
        _harnessB.WriteSourceFile("notes.txt", "site B's notes, which are not site A's");
        _harnessB.WriteSourceFile("accounts/2026.csv", "date,amount\n2026-10-01,42\n");

        await using var siteA = await PeerSite.StartAsync(_harnessA, Timeout);
        await using var siteB = await PeerSite.StartAsync(_harnessB, Timeout);

        await siteA.PairToStoreAtAsync(siteB, "site-b");
        await siteB.PairToStoreAtAsync(siteA, "site-a");
        await siteA.DeclareDocsAsync("site-b");
        await siteB.DeclareDocsAsync("site-a");

        await siteA.WaitForFirstBackupAsync();
        await siteB.WaitForFirstBackupAsync();
        await siteA.WaitForProvenAsync("site-b");
        await siteB.WaitForProvenAsync("site-a");

        // ---- Each site holds the other's archive, byte for byte, and
        // nothing else.
        var heldAtB = siteB.ReplicaOf(siteA);
        var heldAtA = siteA.ReplicaOf(siteB);
        Assert.AreEqual(heldAtB, Assert.ContainsSingle(siteB.Replicas()));
        Assert.AreEqual(heldAtA, Assert.ContainsSingle(siteA.Replicas()));
        await PeerSite.AssertReplicaMatchesAsync(_harnessA.RepositoryPath, heldAtB, Timeout);
        await PeerSite.AssertReplicaMatchesAsync(_harnessB.RepositoryPath, heldAtA, Timeout);

        // ---- Each site's status reads its own copy as proven.
        await AssertProvenAsync(siteA, "site-b");
        await AssertProvenAsync(siteB, "site-a");

        // ---- Each site's files come back from the other's copy.
        var fromB = await RecoverOnlySnapshotAsync(siteA, heldAtB, "a-from-b");
        await PeerSite.AssertFileRestoredAsync(fromB, "notes.txt", Encoding.UTF8.GetBytes("site A's notes"));
        Assert.IsNull(PeerSite.FindFile(fromB, "2026.csv"), "site A's copy must not hold site B's files");

        var fromA = await RecoverOnlySnapshotAsync(siteB, heldAtA, "b-from-a");
        await PeerSite.AssertFileRestoredAsync(
            fromA, "notes.txt", Encoding.UTF8.GetBytes("site B's notes, which are not site A's"));
        await PeerSite.AssertFileRestoredAsync(
            fromA, "2026.csv", Encoding.UTF8.GetBytes("date,amount\n2026-10-01,42\n"));
    }

    private async Task AssertProvenAsync(PeerSite site, string destination)
    {
        Assert.IsInstanceOfType<StatusResult>(
            await site.Operator.ExecuteAsync(new GetStatusCommand(), Timeout), out var status);
        var row = Assert.ContainsSingle(Assert.ContainsSingle(status.Sets).Destinations);
        Assert.AreEqual(destination, row.Name);
        Assert.AreEqual("in-sync", row.State);
        Assert.AreEqual("proven", row.Verification);
    }

    private static async Task<string> RecoverOnlySnapshotAsync(PeerSite owner, string replicaPath, string name)
    {
        var (exitCode, snapshots, error) = await owner.ListSnapshotsInAsync(replicaPath);
        Assert.AreEqual(0, exitCode, error);
        return await owner.RecoverFromAsync(replicaPath, Assert.ContainsSingle(snapshots), name);
    }
}
