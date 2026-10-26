using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// Replacing a replica object the deep sweep found damaged: a copy is proven
/// sound where it sits before anything at the replica is touched — its whole
/// blob still hashes to its sealed digest, and its envelope names the blob
/// this key derives from — and a replica with no sound copy anywhere keeps
/// even its damaged object. The store-level half of FR-VER-007, and of
/// FR-VER-008's repair at a peer, where the copy goes through a target that
/// may refuse it and is proven again where the target says it landed.
/// </summary>
/// <remarks>
/// A damaged blob is still worth keeping when nothing can replace it. Its
/// records are authenticated one at a time, so a flipped byte costs the one
/// record it lands in, and the rest still restore. Deleting it on the way to
/// a replacement that then turns out not to exist would turn one lost record
/// into a lost blob, which is why the proof comes first.
/// </remarks>
[TestClass]
public sealed class ReplicaRepairTests : ArchiveTestHarness
{
    private string ReplicaRoot => Path.Combine(StoreRoot, "..", "replica");

    private string SpareRoot => Path.Combine(StoreRoot, "..", "spare");

    [TestMethod]
    public async Task Repair_ADamagedBlob_IsReplacedByTheSourcesSoundCopy()
    {
        var (source, replica, _, keys) = await SeedAsync();
        using var held = keys;
        var victim = (await BlobKeysAsync(replica))[0];
        await RotAsync(ReplicaRoot, victim);

        var outcome = await ReplicaRepair.RepairAsync(
            Repo, keys, replica, victim, [Serve("the source", source)], CancellationToken.None);

        Assert.IsTrue(outcome.Repaired, outcome.Detail);
        Assert.AreEqual("the source", outcome.RepairedFrom);
        CollectionAssert.AreEqual(
            await ReadAsync(StoreRoot, victim), await ReadAsync(ReplicaRoot, victim),
            "the replica must hold the source's bytes now");

        var proof = await ReplicaRepair.ProveAsync(Repo, keys, replica, victim, CancellationToken.None);
        Assert.IsTrue(proof.Sound, proof.Detail);
    }

    [TestMethod]
    public async Task Repair_ASourceWhoseCopyIsDamagedToo_IsPassedOverForTheNext()
    {
        var (source, replica, spare, keys) = await SeedAsync();
        using var held = keys;
        var victim = (await BlobKeysAsync(replica))[0];
        await RotAsync(ReplicaRoot, victim);
        await RotAsync(SpareRoot, victim);

        var outcome = await ReplicaRepair.RepairAsync(
            Repo, keys, replica, victim,
            [Serve("destination 'spare'", spare), Serve("the source", source)], CancellationToken.None);

        Assert.AreEqual("the source", outcome.RepairedFrom, outcome.Detail);
        CollectionAssert.AreEqual(await ReadAsync(StoreRoot, victim), await ReadAsync(ReplicaRoot, victim));
    }

    [TestMethod]
    public async Task Repair_ASourceHoldingAnotherBlobUnderThisKey_IsNotTrusted()
    {
        // The case the sweep binds each blob to its key for, met here at the
        // other end. A well-formed, correctly sealed blob stored under the
        // wrong key passes every check of its own bytes. Only its envelope
        // says it is some other blob, and copying it over the damaged one
        // would replace a lost record with a whole wrong object.
        var (_, replica, spare, keys) = await SeedAsync();
        using var held = keys;
        var blobs = await BlobKeysAsync(replica);
        var victim = blobs[0];
        var other = blobs[1];
        await RotAsync(ReplicaRoot, victim);
        var damaged = await ReadAsync(ReplicaRoot, victim);
        await File.WriteAllBytesAsync(PathOf(SpareRoot, victim), await ReadAsync(SpareRoot, other));

        var outcome = await ReplicaRepair.RepairAsync(
            Repo, keys, replica, victim, [Serve("destination 'spare'", spare)], CancellationToken.None);

        Assert.IsFalse(outcome.Repaired, "another blob's bytes are not a copy of this one");
        Assert.Contains("destination 'spare'", outcome.Detail, StringComparison.Ordinal);
        CollectionAssert.AreEqual(damaged, await ReadAsync(ReplicaRoot, victim), "the replica must be left as it was");
    }

    [TestMethod]
    public async Task Repair_NoSoundSourceAnywhere_LeavesTheDamagedObjectWhereItIs()
    {
        var (_, replica, spare, keys) = await SeedAsync();
        using var held = keys;
        var victim = (await BlobKeysAsync(replica))[0];
        await RotAsync(ReplicaRoot, victim);
        var damaged = await ReadAsync(ReplicaRoot, victim);
        await spare.DeleteAsync(victim, DeleteConditions.None, CancellationToken.None);

        var outcome = await ReplicaRepair.RepairAsync(
            Repo, keys, replica, victim, [Serve("destination 'spare'", spare)], CancellationToken.None);

        Assert.IsFalse(outcome.Repaired);
        Assert.IsNull(outcome.RepairedFrom);
        Assert.Contains("destination 'spare'", outcome.Detail, StringComparison.Ordinal);
        Assert.IsTrue(File.Exists(PathOf(ReplicaRoot, victim)), "a damaged blob with no replacement must not be deleted");
        CollectionAssert.AreEqual(damaged, await ReadAsync(ReplicaRoot, victim));
    }

    [TestMethod]
    public async Task Repair_ASourceThatCannotBeReached_IsSkippedForTheNext()
    {
        var (source, replica, _, keys) = await SeedAsync();
        using var held = keys;
        var victim = (await BlobKeysAsync(replica))[0];
        await RotAsync(ReplicaRoot, victim);

        var outcome = await ReplicaRepair.RepairAsync(
            Repo, keys, replica, victim,
            [
                new CopySource("destination 'gone'", _ => throw new IOException("the drive is not mounted")),
                new CopySource("destination 'away'", _ => ValueTask.FromResult<IObjectStore?>(null)),
                Serve("the source", source),
            ],
            CancellationToken.None);

        Assert.AreEqual("the source", outcome.RepairedFrom, outcome.Detail);
    }

    [TestMethod]
    public async Task Repair_ASourceNeverOpened_WhenAnEarlierOneServed()
    {
        // A source is opened only when every earlier one failed to serve,
        // because opening a peer's means dialling it — somebody else's link,
        // spent on a question already answered.
        var (source, replica, _, keys) = await SeedAsync();
        using var held = keys;
        var victim = (await BlobKeysAsync(replica))[0];
        await RotAsync(ReplicaRoot, victim);
        var opened = false;

        var outcome = await ReplicaRepair.RepairAsync(
            Repo, keys, replica, victim,
            [
                Serve("the source", source),
                new CopySource("destination 'friend'", _ =>
                {
                    opened = true;
                    return ValueTask.FromResult<IObjectStore?>(null);
                }),
            ],
            CancellationToken.None);

        Assert.IsTrue(outcome.Repaired, outcome.Detail);
        Assert.IsFalse(opened, "a later source must not be opened once an earlier one served");
    }

    [TestMethod]
    public async Task Repair_ThroughATarget_InstallsTheProvenCopy_AndProvesItWhereTheTargetSaysItLanded()
    {
        // A peer's replica is written through a session and read back through
        // another, so the repair hands the proven copy to the target and
        // proves it again where the target says it landed (ADR-0100).
        var (source, replica, _, keys) = await SeedAsync();
        using var held = keys;
        var victim = (await BlobKeysAsync(replica))[0];
        await RotAsync(ReplicaRoot, victim);
        var target = new RecordingTarget(replica);

        var outcome = await ReplicaRepair.RepairAsync(
            Repo, keys, target, victim, [Serve("the source", source)], scratch: null, CancellationToken.None);

        Assert.IsTrue(outcome.Repaired, outcome.Detail);
        Assert.AreEqual("the source", outcome.RepairedFrom);
        Assert.AreEqual(new FileInfo(PathOf(StoreRoot, victim)).Length, Assert.ContainsSingle(target.Installed).Length);
        CollectionAssert.AreEqual(await ReadAsync(StoreRoot, victim), await ReadAsync(ReplicaRoot, victim));
    }

    [TestMethod]
    public async Task Repair_ATargetOfferedOnlyACopyThatWillNotProve_IsNeverAsked()
    {
        var (_, replica, spare, keys) = await SeedAsync();
        using var held = keys;
        var victim = (await BlobKeysAsync(replica))[0];
        await RotAsync(ReplicaRoot, victim);
        await RotAsync(SpareRoot, victim);
        var target = new RecordingTarget(replica);

        var outcome = await ReplicaRepair.RepairAsync(
            Repo, keys, target, victim, [Serve("destination 'spare'", spare)], scratch: null, CancellationToken.None);

        Assert.IsFalse(outcome.Repaired);
        Assert.IsEmpty(target.Installed, "nothing unproven is offered");
    }

    [TestMethod]
    public async Task Repair_ATargetThatWillNotTakeTheCopy_KeepsWhatItHolds_AndIsOfferedNoOther()
    {
        // The target's refusal is about its own copy — a peer judges that on
        // its own check (ADR-0100) — so another source's copy, which proves
        // to be the same bytes, would be refused for the same reason. It is
        // not opened, and the target's reason is the outcome's.
        var (source, replica, spare, keys) = await SeedAsync();
        using var held = keys;
        var victim = (await BlobKeysAsync(replica))[0];
        await RotAsync(ReplicaRoot, victim);
        var damaged = await ReadAsync(ReplicaRoot, victim);
        var target = new RecordingTarget(replica) { Refusal = "its own check of its copy passes" };
        var opened = false;

        var outcome = await ReplicaRepair.RepairAsync(
            Repo, keys, target, victim,
            [
                Serve("the source", source),
                new CopySource("destination 'spare'", _ =>
                {
                    opened = true;
                    return ValueTask.FromResult<IObjectStore?>(spare);
                }),
            ],
            scratch: null, CancellationToken.None);

        Assert.IsFalse(outcome.Repaired);
        Assert.IsFalse(outcome.Gone);
        Assert.Contains("its own check of its copy passes", outcome.Detail, StringComparison.Ordinal);
        Assert.IsFalse(opened, "a refusal about the target's copy is not answered with another source");
        CollectionAssert.AreEqual(damaged, await ReadAsync(ReplicaRoot, victim));
    }

    [TestMethod]
    public async Task Repair_ATargetThatNoLongerHoldsTheKey_SaysTheDamageHasGone_AndRepairsNothing()
    {
        // A peer never creates an object under a repair; the push does, if
        // the peer is still owed it. What the repair learns is that the
        // damage is no longer there to hold against it.
        var (source, replica, _, keys) = await SeedAsync();
        using var held = keys;
        var victim = (await BlobKeysAsync(replica))[0];
        await replica.DeleteAsync(victim, DeleteConditions.None, CancellationToken.None);
        var target = new RecordingTarget(replica) { Gone = true };

        var outcome = await ReplicaRepair.RepairAsync(
            Repo, keys, target, victim, [Serve("the source", source)], scratch: null, CancellationToken.None);

        Assert.IsTrue(outcome.Gone);
        Assert.IsFalse(outcome.Repaired);
        Assert.IsFalse(File.Exists(PathOf(ReplicaRoot, victim)), "a repair creates nothing");
    }

    [TestMethod]
    public async Task Repair_ACopyTheTargetTookButThatDoesNotProveWhereItLanded_IsNotClaimed()
    {
        var (source, replica, spare, keys) = await SeedAsync();
        using var held = keys;
        var victim = (await BlobKeysAsync(replica))[0];
        await RotAsync(ReplicaRoot, victim);
        await RotAsync(SpareRoot, victim);
        var target = new RecordingTarget(replica) { Landed = spare };

        var outcome = await ReplicaRepair.RepairAsync(
            Repo, keys, target, victim, [Serve("the source", source)], scratch: null, CancellationToken.None);

        Assert.IsFalse(outcome.Repaired, "taken is the target's word; landed is a proof");
        Assert.Contains("where it landed", outcome.Detail, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Prove_SaysWhetherACopyIsHeld_AndWhetherItIsSound()
    {
        var (_, replica, spare, keys) = await SeedAsync();
        using var held = keys;
        var victim = (await BlobKeysAsync(replica))[0];

        var intact = await ReplicaRepair.ProveAsync(Repo, keys, replica, victim, CancellationToken.None);
        Assert.IsTrue(intact.Held);
        Assert.IsTrue(intact.Sound, intact.Detail);

        await RotAsync(ReplicaRoot, victim);
        var rotted = await ReplicaRepair.ProveAsync(Repo, keys, replica, victim, CancellationToken.None);
        Assert.IsTrue(rotted.Held);
        Assert.IsFalse(rotted.Sound);
        Assert.IsFalse(string.IsNullOrWhiteSpace(rotted.Detail), "a copy found unsound must say why");

        await spare.DeleteAsync(victim, DeleteConditions.None, CancellationToken.None);
        var absent = await ReplicaRepair.ProveAsync(Repo, keys, spare, victim, CancellationToken.None);
        Assert.IsFalse(absent.Held);
        Assert.IsFalse(absent.Sound);
    }

    private static CopySource Serve(string name, IObjectStore store) =>
        new(name, _ => ValueTask.FromResult<IObjectStore?>(store));

    /// <summary>
    /// A target over a local store that installs as the store's own repair
    /// does, or answers as a peer may: that it will not take the copy, or
    /// that it no longer holds the key.
    /// </summary>
    private sealed class RecordingTarget(LocalFileSystemObjectStore replica) : IRepairTarget
    {
        public string? Refusal { get; init; }

        public bool Gone { get; init; }

        /// <summary>Where the copy is proven after it is taken; the replica itself when null.</summary>
        public IObjectStore? Landed { get; init; }

        public List<(ObjectKey Key, long Length)> Installed { get; } = [];

        public async ValueTask<RepairInstall> InstallAsync(
            ObjectKey key, IObjectStore candidate, long length, CancellationToken cancellationToken)
        {
            Installed.Add((key, length));
            if (Gone)
            {
                return RepairInstall.NoLongerHeld;
            }

            if (Refusal is { } refusal)
            {
                return RepairInstall.Refused(refusal);
            }

            await replica.DeleteAsync(key, DeleteConditions.None, cancellationToken);
            await replica.PutAsync(
                key,
                async token => (await candidate.OpenReadAsync(key, range: null, token)).Content!,
                PutConditions.None,
                cancellationToken);
            return RepairInstall.Done;
        }

        public ValueTask<IObjectStore> LandedAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Landed ?? replica);
    }

    /// <summary>Archives a test file into a store and mirrors it to a replica and a spare.</summary>
    private async Task<(LocalFileSystemObjectStore Source, LocalFileSystemObjectStore Replica,
        LocalFileSystemObjectStore Spare, RepositoryKeySet Keys)> SeedAsync()
    {
        var store = CreateStore();
        var keys = CreateKeys();
        var archiver = CreateArchiver(store, keys);
        using var file = new MemoryStream(BuildTestFile());
        await archiver.ArchiveAsync(file, CancellationToken.None);

        var replica = await MirrorAsync(store, ReplicaRoot);
        var spare = await MirrorAsync(store, SpareRoot);
        Assert.IsGreaterThanOrEqualTo(2, (await BlobKeysAsync(replica)).Count, "the small-blob policy must produce several blobs");
        return (store, replica, spare, keys);
    }

    private static async Task<LocalFileSystemObjectStore> MirrorAsync(LocalFileSystemObjectStore from, string root)
    {
        Directory.CreateDirectory(root);
        var mirror = new LocalFileSystemObjectStore(root);
        await foreach (var entry in from.ListAsync(ObjectPrefix.All, ListOptions.Default, CancellationToken.None))
        {
            using var read = await from.OpenReadAsync(entry.Key, range: null, CancellationToken.None);
            var bytes = new byte[entry.Length];
            await read.Content!.ReadExactlyAsync(bytes, CancellationToken.None);
            await mirror.PutAsync(
                entry.Key,
                _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false)),
                PutConditions.None,
                CancellationToken.None);
        }

        return mirror;
    }

    private static async Task<List<ObjectKey>> BlobKeysAsync(LocalFileSystemObjectStore store)
    {
        var keys = new List<ObjectKey>();
        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("blobs/"), ListOptions.Default, CancellationToken.None))
        {
            keys.Add(entry.Key);
        }

        return [.. keys.OrderBy(key => key.Value, StringComparer.Ordinal)];
    }

    private static string PathOf(string root, ObjectKey key) =>
        Path.Combine(root, key.Value.Replace('/', Path.DirectorySeparatorChar));

    private static Task<byte[]> ReadAsync(string root, ObjectKey key) => File.ReadAllBytesAsync(PathOf(root, key));

    private static async Task RotAsync(string root, ObjectKey key)
    {
        var path = PathOf(root, key);
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes);
    }
}
