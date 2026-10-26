using FallbackPlan.Api;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A peer lets go of the deltas its own checkpoints carry when a person's
/// reclaim grant is held (FR-MAN-017; ADR-0098 Amendment 1). The hub judges
/// the peer's replica over retrieval by the rule every store retires on —
/// a settled checkpoint there whose copy opens and is the very object at the
/// delta's key — and names what passes in the drop list it signs under the
/// grant, which the peer checks against the reclaim key.
/// </summary>
/// <remarks>
/// Nothing is dropped at a peer on the plane's word: the plane letting a
/// delta go is what admits it, and the peer's own copies are what retire it.
/// An unattended sync holds no authority to delete and names nothing.
/// </remarks>
[TestClass]
public sealed class PeerDeltaRetirementTests : IDisposable
{
    private readonly HostHarness _source = new();
    private readonly HostHarness _peer = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(4));

    private CancellationToken Timeout => _timeout.Token;

    public void Dispose()
    {
        _timeout.Dispose();
        _source.Dispose();
        _peer.Dispose();
    }

    [TestMethod]
    public async Task APersonsRetentionRun_RetiresAtThePeerWhatItsCheckpointsCarry_AndThePeerAloneRestoresWhatTheyHeld()
    {
        var firstBytes = PeerSite.Noise(4096, seed: 1);
        await using var peer = await PeerSite.StartAsync(_peer, Timeout);
        await using var source = await PeerSite.StartAsync(_source, Timeout, checkpointThreshold: 2);
        var (replica, first) = await ThreeBackupsAsync(source, peer, firstBytes);

        // The plane has let go of what its checkpoint carries. The peer, told
        // nothing by the unattended syncs, still holds every one of them.
        var carried = CarriedBy(await LoadAsync(replica));
        Assert.IsNotEmpty(carried, "the second run checkpoints the first two runs' deltas");
        Assert.IsFalse(
            (await DeltasAtTheirKeysAsync(_source.RepositoryPath)).Overlaps(carried),
            "the plane retires what its settled checkpoint carries");
        Assert.IsTrue(carried.IsSubsetOf(await DeltasAtTheirKeysAsync(replica)), "a sync holds no authority to delete");
        var checkpoints = await CheckpointKeysAsync(replica);

        var grant = await _source.ReclaimGrantAsync(source.Operator, Timeout);
        Assert.IsInstanceOfType<RetentionResult>(
            await source.Operator.ExecuteAsync(new RetentionCommand(Apply: true, ReclaimGrant: grant), Timeout),
            out var ran);

        Assert.IsFalse(
            (await DeltasAtTheirKeysAsync(replica)).Overlaps(carried),
            $"the peer still holds a carried delta after the run: {string.Join(" | ", ran.Lines)}");
        Assert.IsTrue(
            checkpoints.SetEquals(await CheckpointKeysAsync(replica)), "a checkpoint is never retired, at a peer as anywhere");

        // What the retired deltas indexed still restores from the peer alone,
        // read from the checkpoint the peer holds.
        var restored = await source.RecoverFromAsync(replica, first, "recovered-first");
        await PeerSite.AssertFileRestoredAsync(restored, "first.bin", firstBytes);
    }

    [TestMethod]
    public async Task ADeltaWhoseCarrierThePeerDoesNotHold_IsKeptThere_ThoughThePlaneLetItGo()
    {
        // The facts are the peer's. A hub that went on the plane's word would
        // tell the peer to drop a delta no checkpoint there carries, and the
        // peer would be left with a missing index object until the next push.
        await using var peer = await PeerSite.StartAsync(_peer, Timeout);
        await using var source = await PeerSite.StartAsync(_source, Timeout, checkpointThreshold: 2);
        var (replica, _) = await ThreeBackupsAsync(source, peer, PeerSite.Noise(4096, seed: 1));
        var carried = CarriedBy(await LoadAsync(replica));
        Assert.IsNotEmpty(carried);
        foreach (var checkpoint in Directory.GetFiles(
            Path.Combine(replica, "index", "checkpoint"), "*", SearchOption.AllDirectories))
        {
            File.Delete(checkpoint);
        }

        var grant = await _source.ReclaimGrantAsync(source.Operator, Timeout);
        Assert.IsInstanceOfType<RetentionResult>(
            await source.Operator.ExecuteAsync(new RetentionCommand(Apply: true, ReclaimGrant: grant), Timeout));

        Assert.IsTrue(
            carried.IsSubsetOf(await DeltasAtTheirKeysAsync(replica)),
            "a delta is retired only on a carrier at the store it is retired from");
    }

    /// <summary>
    /// Three backups to the peer, each proven there: the second checkpoints
    /// the first two runs' deltas, and the third settles that checkpoint, so
    /// the plane retires what it carries.
    /// </summary>
    private static async Task<(string Replica, string FirstSnapshot)> ThreeBackupsAsync(
        PeerSite source, PeerSite peer, byte[] firstBytes)
    {
        source.WriteSourceBytes("first.bin", firstBytes);
        await source.PairToStoreAtAsync(peer, "site-b");
        await source.DeclareDocsAsync("site-b");
        await source.WaitForFirstBackupAsync();
        var synced = await source.WaitForProvenAsync("site-b");
        var replica = peer.ReplicaOf(source);
        var (exitCode, snapshots, error) = await source.ListSnapshotsInAsync(replica);
        Assert.AreEqual(0, exitCode, error);
        var first = Assert.ContainsSingle(snapshots);

        foreach (var (name, seed) in new[] { ("second.bin", 2), ("third.bin", 3) })
        {
            source.WriteSourceBytes(name, PeerSite.Noise(4096, seed));
            await source.RunBackupAsync();
            synced = await source.WaitForProvenAsync("site-b", after: synced.SyncedSequence);
        }

        return (replica, first);
    }

    private static HashSet<DeltaId> CarriedBy(IndexState state) =>
        state.Checkpoints.SelectMany(checkpoint => checkpoint.Checkpoint.SubsumedDeltaIds).ToHashSet();

    private static async Task<HashSet<DeltaId>> DeltasAtTheirKeysAsync(string root)
    {
        var present = new HashSet<DeltaId>();
        await foreach (var entry in new LocalFileSystemObjectStore(root)
            .ListAsync(ObjectPrefix.Parse("index/delta/"), ListOptions.Default, CancellationToken.None))
        {
            Assert.IsTrue(MetadataStoreKeys.TryParseIndexDelta(entry.Key, out var deltaId));
            present.Add(deltaId);
        }

        return present;
    }

    private static async Task<HashSet<string>> CheckpointKeysAsync(string root)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var entry in new LocalFileSystemObjectStore(root)
            .ListAsync(ObjectPrefix.Parse("index/checkpoint/"), ListOptions.Default, CancellationToken.None))
        {
            keys.Add(entry.Key.Value);
        }

        return keys;
    }

    private async Task<IndexState> LoadAsync(string root)
    {
        var store = new LocalFileSystemObjectStore(root);
        using var passphrase = Passphrase.Create(Environment.GetEnvironmentVariable(_source.PassphraseVariable)!);
        var (repository, authority) = await RepositoryLifecycle.OpenForReadAsync(store, passphrase, Timeout);
        using (repository)
        using (authority)
        using (var loader = new IndexLoader(store, repository.RepositoryId, repository.Credential))
        {
            var state = await loader.LoadAsync(
                currentGeneration: 0, IndexLoader.GapPatienceGenerations, isSequenceAccountedAsync: null,
                blobState: null, Timeout);
            Assert.IsEmpty(state.Findings, string.Join(" | ", state.Findings.Select(finding => finding.Detail)));
            return state;
        }
    }
}
