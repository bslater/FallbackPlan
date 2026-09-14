using FallbackPlan.Replication;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Replication.Tests;

/// <summary>
/// What a replication pass costs to decide it has nothing to do
/// (NFR-PERF-005, NFR-PERF-008, FR-REP-006). The outcome of a pass says
/// nothing about this: a pass that copies no objects can still have walked the
/// whole archive once for every dependency phase, and did.
/// </summary>
/// <remarks>
/// These assert on listings rather than on wall-clock, because the cost that
/// matters is per-object work at a scale no test can hold: a listing scoped to
/// one prefix and a listing of everything are indistinguishable in a fixture
/// of a dozen objects and are not remotely alike at a million. The counting
/// store records the prefix each listing was opened under, so the shape of the
/// walk is what gets pinned, not its duration.
/// </remarks>
[TestClass]
public sealed class CopierListingCostTests
{
    private string _root = null!;
    private string _sourcePath = null!;
    private string _replicaPath = null!;

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"fbp-copier-cost-{Guid.NewGuid():N}");
        _sourcePath = Path.Combine(_root, "source");
        _replicaPath = Path.Combine(_root, "replica");
        Directory.CreateDirectory(_sourcePath);
        Directory.CreateDirectory(_replicaPath);
    }

    [TestCleanup]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }

    [TestMethod]
    public async Task Copy_OverTheDependencyPhases_WalksEachObjectOnceRatherThanOncePerPhase()
    {
        // Eight phases and one archive: listing everything per phase means the
        // source is enumerated eight times to copy it once, so the cost of a
        // pass scales with the archive multiplied by the number of dependency
        // classes — a constant nobody chose and nobody can see.
        var inner = new LocalFileSystemObjectStore(_sourcePath);
        await SeedAsync(inner);
        var source = new CountingObjectStore(inner);
        var replica = new LocalFileSystemObjectStore(_replicaPath);

        var outcome = await StoreToStoreCopier.CopyAsync(source, replica, CancellationToken.None);

        Assert.AreEqual(9L, outcome.Copied, "every seeded object must have crossed");
        Assert.IsLessThanOrEqualTo(
            (long)(outcome.Copied * 2),
            source.EntriesYielded,
            $"the source was enumerated {source.EntriesYielded} times over for {outcome.Copied} objects; "
            + $"listings were [{string.Join(", ", source.Listings.Select(prefix => $"'{prefix}'"))}]");
    }

    [TestMethod]
    public async Task Copy_EachPhase_ListsUnderThatPhasesOwnPrefix()
    {
        // The shape, not just the total: a phase that lists everything and
        // filters in memory reads the same objects a scoped listing skips, and
        // on a store that charges per thousand keys that difference is the
        // whole bill. The catch-all phase is the one exception and says so.
        var inner = new LocalFileSystemObjectStore(_sourcePath);
        await SeedAsync(inner);
        var source = new CountingObjectStore(inner);
        var replica = new LocalFileSystemObjectStore(_replicaPath);

        await StoreToStoreCopier.CopyAsync(source, replica, CancellationToken.None);

        Assert.HasCount(
            1,
            source.Listings.Where(prefix => prefix.Length == 0),
            "only the catch-all phase may list the whole namespace: "
            + $"[{string.Join(", ", source.Listings.Select(prefix => $"'{prefix}'"))}]");
        Assert.Contains("blobs/", source.Listings, StringComparer.Ordinal);
        Assert.Contains("snapshots/", source.Listings, StringComparer.Ordinal);
    }

    [TestMethod]
    public async Task Copy_TheDestinationInventory_IsReadPhaseByPhaseRatherThanHeldWhole()
    {
        // The other full key set: the destination's whole inventory is held in
        // memory for the length of the pass so the diff can be answered, which
        // makes the pass's peak memory a function of the archive's object
        // count. Phase-scoped listings bound it by the largest phase instead,
        // and the phases are released as they finish.
        var inner = new LocalFileSystemObjectStore(_sourcePath);
        await SeedAsync(inner);
        var replicaInner = new LocalFileSystemObjectStore(_replicaPath);
        await StoreToStoreCopier.CopyAsync(inner, replicaInner, CancellationToken.None);

        var destination = new CountingObjectStore(replicaInner);
        await StoreToStoreCopier.CopyAsync(inner, destination, CancellationToken.None);

        Assert.IsGreaterThan(
            1,
            destination.Listings.Count,
            "the destination is listed once for the whole archive, which is the key set the pass then holds");
        Assert.IsEmpty(
            destination.Listings.Where(prefix => prefix.Length == 0),
            "a whole-namespace listing of the destination is the whole-archive key set by another name");
    }

    [TestMethod]
    public async Task Copy_Incrementally_MakesNoWholeNamespaceWalkAtAll()
    {
        // The catch-all phase cannot be scoped — "every key no named phase
        // claims" is not a prefix — so it is the one full walk left in a pass.
        // It exists for a repository written by a version this one has never
        // heard of, which is not a thing that changes between two polls a
        // minute apart, so an incremental pass leaves it to the reconciling
        // one and the stray it might find waits for that.
        var inner = new LocalFileSystemObjectStore(_sourcePath);
        await SeedAsync(inner);
        var source = new CountingObjectStore(inner);
        var replica = new LocalFileSystemObjectStore(_replicaPath);

        var outcome = await StoreToStoreCopier.CopyAsync(
            source, replica, CancellationToken.None, scope: CopyScope.Incremental);

        Assert.IsEmpty(
            source.Listings.Where(prefix => prefix.Length == 0),
            "an incremental pass must not walk the whole namespace");
        Assert.AreEqual(
            8L, outcome.Copied,
            "everything under a named phase still crosses; only the stray waits for the reconciling pass");

        var stray = await replica.GetMetadataAsync(
            ObjectKey.Parse("something-else/object-c"), CancellationToken.None);
        Assert.IsFalse(stray.Found, "the stray is the catch-all phase's to find");

        await StoreToStoreCopier.CopyAsync(source, replica, CancellationToken.None, scope: CopyScope.Reconcile);
        Assert.IsTrue(
            (await replica.GetMetadataAsync(
                ObjectKey.Parse("something-else/object-c"), CancellationToken.None)).Found,
            "and the reconciling pass finds it");
    }

    /// <summary>
    /// Nine objects spread over the phases the copier orders by: two blobs,
    /// the index plane, the journal, a snapshot, the descriptor, the keys, and
    /// one object under a prefix the phase list has never heard of.
    /// </summary>
    private static async Task SeedAsync(LocalFileSystemObjectStore source)
    {
        string[] keys =
        [
            "repository-format",
            "keys/master",
            "blobs/data/0000/object-a",
            "blobs/meta/0000/object-b",
            "journal/writer-a/00000001",
            "index/delta/00000001/delta-a",
            "index/checkpoint/00000001",
            "something-else/object-c",
            "snapshots/device-a/set-a/snapshot-a",
        ];

        foreach (var key in keys)
        {
            var payload = new byte[64];
            Random.Shared.NextBytes(payload);
            await source.PutAsync(
                ObjectKey.Parse(key),
                _ => ValueTask.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                PutConditions.None,
                CancellationToken.None);
        }
    }
}
