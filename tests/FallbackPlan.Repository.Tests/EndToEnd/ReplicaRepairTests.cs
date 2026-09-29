using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// Replacing a replica object the deep sweep found damaged: a copy is proven
/// sound where it sits before anything at the replica is touched — its whole
/// blob still hashes to its sealed digest, and its envelope names the blob
/// this key derives from — and a replica with no sound copy anywhere keeps
/// even its damaged object. The store-level half of FR-VER-007.
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
        // The case the sweep's length comparison exists for, met here at the
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
                new RepairSource("destination 'gone'", _ => throw new IOException("the drive is not mounted")),
                new RepairSource("destination 'away'", _ => ValueTask.FromResult<IObjectStore?>(null)),
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
                new RepairSource("destination 'friend'", _ =>
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

    private static RepairSource Serve(string name, IObjectStore store) =>
        new(name, _ => ValueTask.FromResult<IObjectStore?>(store));

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
