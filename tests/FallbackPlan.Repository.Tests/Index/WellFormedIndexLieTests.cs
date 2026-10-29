using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Repository.Tests.EndToEnd;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Index;

using Catalogue = FallbackPlan.Repository.Catalogue.Catalogue;
using CatalogueRebuilder = FallbackPlan.Repository.Catalogue.CatalogueRebuilder;

/// <summary>
/// An index that is well formed, correctly signed and wrong (FR-MAN-009,
/// NFR-SEC-005): the lie arrives through a member's signed delta, survives the
/// load and the catalogue's rebuild intact, and is caught where the bytes are
/// read, never served. A segment's entry naming another real record is
/// refused by the reader's cross-check of the record's own header, and the
/// footer serves the record asked for. An entry naming a blob the store does
/// not hold is a damage finding that names the object, once the rebuild knows
/// which blobs exist. And an object listed twice, once truly and once at
/// another record, resolves to one location in both resolvers whichever order
/// the delta lists them in, and restores byte for byte whichever that is.
/// </summary>
[TestClass]
public sealed class WellFormedIndexLieTests : ArchiveTestHarness
{
    [TestMethod]
    public async Task IndexDelta_PointsASegmentAtAnotherValidRecord_IsCaughtBeforeTheBytesAreUsed()
    {
        var (data, archived, store, keys, credential) = await ArchiveAsync(0x7E51);
        using var _k = keys;
        using var _c = credential;
        var entries = await IndexEntriesOfAsync(store, keys, archived);

        // The lie: one segment's entry names another segment's record, a real
        // and valid one, and a member signs it into a delta.
        var (victim, other) = TwoRecords(entries);
        await PublishAsync(store, credential, entries.Select(entry => entry.ObjectId == victim.ObjectId ? At(entry, other) : entry));
        using var catalogue = await RebuildAsync(store, credential);
        Assert.AreEqual(other.PhysicalOffset, catalogue.ResolveLocation(victim.ObjectId)!.PhysicalOffset, "the lie arrived intact");

        // Caught by the layer that reads the bytes: the record's own header
        // names another object, so it is refused, and the footer serves the
        // record asked for.
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        reader.UseLocationSource(catalogue.ResolveLocation);
        var read = await reader.ReadSegmentAsync(victim.ObjectId, CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome, read.Detail);
        Assert.AreEqual(1, reader.LocationFallbacks, "the lying location was refused once, and the footer answered");
        var reference = archived.SegmentReferences.First(segment => segment.ObjectId == victim.ObjectId);
        SequenceAssert.AreEqual(data.AsSpan((int)reference.LogicalOffset, (int)reference.LogicalLength).ToArray(), read.Plaintext);

        using var restorer = new RepositoryReader(Repo, keys, store, Authority);
        restorer.UseLocationSource(catalogue.ResolveLocation);
        using var restored = new MemoryStream();
        var restore = await restorer.RestoreAsync(archived.SegmentReferences, restored, CancellationToken.None);
        Assert.IsTrue(restore.Success, restore.FailureDetail);
        SequenceAssert.AreEqual(data, restored.ToArray());
    }

    [TestMethod]
    public async Task IndexDelta_ClaimsASegmentThatNoBlobHolds_SurfacesAMissingSegmentFindingNamingIt()
    {
        var (_, archived, store, keys, credential) = await ArchiveAsync(0x7E52);
        using var _k = keys;
        using var _c = credential;
        var entries = await IndexEntriesOfAsync(store, keys, archived);

        // A signed entry for an object at a blob the store has never held.
        var claimed = ObjectId.FromBytes([.. Enumerable.Repeat((byte)0xC1, 32)]);
        var nowhere = BlobId.FromBytes([.. Enumerable.Repeat((byte)0xC2, 16)]);
        await PublishAsync(store, credential, entries.Append(new IndexEntry(claimed, nowhere, 64, 128, 1, 1, IndexEntryType.Insertion)));

        // A rebuild that knows every blob the store holds names the object,
        // and the blob, and serves no location for it.
        using var inventory = new RepositoryReader(Repo, keys, store, Authority);
        await inventory.LoadBlobsAsync(CancellationToken.None);
        Assert.IsEmpty(inventory.SkippedBlobs);

        using var catalogue = await RebuildAsync(
            store, credential, CatalogueRebuilder.KnownBlobs(inventory.Blobs.Select(blob => blob.BlobId), inventoryComplete: true));

        var finding = Assert.ContainsSingle(catalogue.Findings(), string.Join(Environment.NewLine, catalogue.Findings()));
        Assert.AreEqual(DamageKind.MissingBlob, finding.Kind);
        Assert.Contains(claimed.ToString(), finding.Detail, StringComparison.Ordinal);
        Assert.Contains(nowhere.ToString(), finding.Detail, StringComparison.Ordinal);
        Assert.IsNull(catalogue.ResolveLocation(claimed));

        // The true entries beside it are untouched by it.
        Assert.IsTrue(entries.All(entry => catalogue.ResolveLocation(entry.ObjectId) is not null));
    }

    [TestMethod]
    [DataRow(true, DisplayName = "the true location listed first")]
    [DataRow(false, DisplayName = "the true location listed second")]
    public async Task IndexDelta_ListsTheSameSegmentTwiceAtDifferentLocations_ResolvesDeterministicallyOrRefuses(bool trueFirst)
    {
        var (data, archived, store, keys, credential) = await ArchiveAsync(trueFirst ? 0x7E53 : 0x7E54);
        using var _k = keys;
        using var _c = credential;
        var entries = await IndexEntriesOfAsync(store, keys, archived);

        // One delta, one writer, one sequence: the two entries tie on every
        // rule of 07 §3, and nothing but their order tells them apart.
        var (victim, other) = TwoRecords(entries);
        var lie = At(victim, other);
        await PublishAsync(store, credential, entries.SelectMany(entry =>
            entry.ObjectId != victim.ObjectId ? new[] { entry } : trueFirst ? new[] { entry, lie } : new[] { lie, entry }));

        using var catalogue = await RebuildAsync(store, credential);
        using var loader = new IndexLoader(store, Repo, credential);
        var state = await loader.LoadAsync(0, 2, null, null, CancellationToken.None);

        // Both resolvers answer one of the two, and the same one.
        var loaded = state.Resolved[victim.ObjectId].Entry;
        var catalogued = catalogue.ResolveLocation(victim.ObjectId)!;
        Assert.AreEqual(loaded.BlobId, catalogued.BlobId);
        Assert.AreEqual(loaded.PhysicalOffset, catalogued.PhysicalOffset);

        // And whichever it is, the restore is the file.
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        reader.UseLocationSource(catalogue.ResolveLocation);
        using var restored = new MemoryStream();
        var restore = await reader.RestoreAsync(archived.SegmentReferences, restored, CancellationToken.None);
        Assert.IsTrue(restore.Success, restore.FailureDetail);
        SequenceAssert.AreEqual(data, restored.ToArray());
    }

    private async Task<(byte[] Data, ArchiveResult Archived, Storage.Local.LocalFileSystemObjectStore Store, RepositoryKeySet Keys, RepositoryWriteCredential Credential)> ArchiveAsync(int seed)
    {
        var data = BuildTestFile(seed, regions: 6);
        var store = CreateStore();
        var keys = CreateKeys();
        var credential = CreateCredential();
        using var source = new MemoryStream(data);
        var archived = await CreateArchiver(store, keys).ArchiveAsync(source, CancellationToken.None);
        return (data, archived, store, keys, credential);
    }

    /// <summary>Two records of distinct objects at distinct offsets: the one a lie is told about, and the one it is told with.</summary>
    private static (IndexEntry Victim, IndexEntry Other) TwoRecords(List<IndexEntry> entries)
    {
        var victim = entries[0];
        var other = entries.First(entry => entry.ObjectId != victim.ObjectId && entry.PhysicalOffset != victim.PhysicalOffset);
        return (victim, other);
    }

    /// <summary><paramref name="entry"/>'s object, at <paramref name="location"/>'s record.</summary>
    private static IndexEntry At(IndexEntry entry, IndexEntry location) => entry with
    {
        BlobId = location.BlobId,
        PhysicalOffset = location.PhysicalOffset,
        StoredLength = location.StoredLength,
    };

    private async Task PublishAsync(
        Storage.Local.LocalFileSystemObjectStore store, RepositoryWriteCredential credential, IEnumerable<IndexEntry> entries)
    {
        var listed = entries.ToList();
        var sequenceStore = new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"));
        using var publisher = new IndexPublisher(store, Repo, Writer, credential, new WriterSequence(sequenceStore));
        await publisher.PublishDeltaAsync(0, [.. listed.Select(entry => entry.BlobId).Distinct()], listed, CancellationToken.None);
    }

    private async Task<Catalogue> RebuildAsync(
        Storage.Local.LocalFileSystemObjectStore store,
        RepositoryWriteCredential credential,
        Func<BlobId, BlobState>? blobState = null)
    {
        var catalogue = Catalogue.Open(Path.Combine(SpoolDirectory, $"catalogue-{Guid.NewGuid():n}.db"), Repo);
        using var loader = new IndexLoader(store, Repo, credential);
        await new CatalogueRebuilder(loader).RebuildAsync(
            catalogue, currentGeneration: 0, gapPatienceGenerations: 2, isSequenceAccountedAsync: null, CancellationToken.None, blobState);
        return catalogue;
    }
}
