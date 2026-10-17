using System.Security.Cryptography;
using System.Text;
using FallbackPlan.Repository;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// One peer holding two owners' archives of the same files (NFR-SEC-001,
/// NFR-SEC-004, FR-REP-001): every stored identifier is keyed per repository,
/// so the peer holds two replicas that share no object name but the fixed
/// descriptor's and no object's bytes, and cannot tell from either that the
/// owners hold the same files.
/// Deduplication stops at the repository. Each replica opens under its
/// owner's passphrase and under no other.
/// </summary>
[TestClass]
public sealed class PeerHubTests : IDisposable
{
    private readonly HostHarness _owner = new();
    private readonly HostHarness _neighbour = new();
    private readonly HostHarness _hub = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));

    private CancellationToken Timeout => _timeout.Token;

    public PeerHubTests()
    {
        // Two households with two passphrases. The harness gives every
        // installation the same one, which would let either owner open the
        // other's replica and prove nothing about whose it is.
        Environment.SetEnvironmentVariable(
            _neighbour.PassphraseVariable, "A different Passphrase 77 for the household next door!");
    }

    public void Dispose()
    {
        _timeout.Dispose();
        _owner.Dispose();
        _neighbour.Dispose();
        _hub.Dispose();
    }

    [TestMethod]
    public async Task Hub_TwoOwnersBackingUpTheSameFiles_HoldsTwoReplicasThatShareNoBlob()
    {
        var photo = PeerSite.Noise(1_500_000, seed: 7);
        var recipe = Encoding.UTF8.GetBytes("the family recipe, which both households keep");

        await using var hub = await PeerSite.StartAsync(_hub, Timeout);
        await using var owner = await PeerSite.StartAsync(_owner, Timeout);
        await using var neighbour = await PeerSite.StartAsync(_neighbour, Timeout);
        foreach (var site in new[] { owner, neighbour })
        {
            site.WriteSourceBytes("shared/photo.bin", photo);
            site.WriteSourceBytes("shared/recipe.txt", recipe);
            await site.PairToStoreAtAsync(hub, "hub");
            await site.DeclareDocsAsync("hub");
        }

        await owner.WaitForFirstBackupAsync();
        await neighbour.WaitForFirstBackupAsync();
        await owner.WaitForProvenAsync("hub");
        await neighbour.WaitForProvenAsync("hub");

        // ---- Two replicas, each its owner's archive byte for byte.
        var ownersCopy = hub.ReplicaOf(owner);
        var neighboursCopy = hub.ReplicaOf(neighbour);
        Assert.HasCount(2, hub.Replicas());
        Assert.AreNotEqual(ownersCopy, neighboursCopy);
        await PeerSite.AssertReplicaMatchesAsync(_owner.RepositoryPath, ownersCopy, Timeout);
        await PeerSite.AssertReplicaMatchesAsync(_neighbour.RepositoryPath, neighboursCopy, Timeout);

        // ---- Identical files, and yet nothing the hub holds is the same in
        // both: no object's name but the descriptor's fixed one, and no
        // object's bytes. Blob names would differ even unkeyed, being
        // allocated at random; snapshot, delta, journal and hint names are
        // where a raw identifier would show.
        var ownersObjects = await PeerSite.ReadReplicableAsync(ownersCopy, Timeout);
        var neighboursObjects = await PeerSite.ReadReplicableAsync(neighboursCopy, Timeout);
        Assert.IsNotEmpty(PeerSite.DataBlobs(ownersObjects));
        Assert.IsNotEmpty(PeerSite.DataBlobs(neighboursObjects));

        var sharedNames = ownersObjects.Keys
            .Intersect(neighboursObjects.Keys, StringComparer.Ordinal)
            .Where(key => key != RepositoryLifecycle.DescriptorKey.Value)
            .ToList();
        Assert.IsEmpty(sharedNames, "a name both owners' replicas hold tells the hub they hold the same thing");

        var neighboursBytes = neighboursObjects.Values.Select(Digest).ToHashSet(StringComparer.Ordinal);
        var sharedBytes = ownersObjects.Where(pair => neighboursBytes.Contains(Digest(pair.Value))).Select(pair => pair.Key).ToList();
        Assert.IsEmpty(sharedBytes, "an object both owners' replicas hold byte for byte tells the hub they hold the same thing");

        // ---- Each replica is its owner's: it opens under that passphrase
        // and refuses the other household's.
        var refused = await neighbour.ListSnapshotsInAsync(ownersCopy);
        Assert.AreNotEqual(0, refused.ExitCode, "the neighbour's passphrase opened the owner's replica");

        foreach (var (site, copy, name) in new[] { (owner, ownersCopy, "owner"), (neighbour, neighboursCopy, "neighbour") })
        {
            var (exitCode, snapshots, error) = await site.ListSnapshotsInAsync(copy);
            Assert.AreEqual(0, exitCode, error);
            var restored = await site.RecoverFromAsync(copy, Assert.ContainsSingle(snapshots), $"{name}-from-hub");
            await PeerSite.AssertFileRestoredAsync(restored, "photo.bin", photo);
            await PeerSite.AssertFileRestoredAsync(restored, "recipe.txt", recipe);
        }
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
