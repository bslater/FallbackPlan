using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Index;

namespace FallbackPlan.Repository.Tests.Catalogue;

using Catalogue = FallbackPlan.Repository.Catalogue.Catalogue;

/// <summary>
/// The catalogue's contract (architecture 02 §7; FR-MAN-002, FR-MAN-005;
/// NFR-PERF-004, NFR-PERF-010): a disposable cache whose SQL location
/// resolver agrees with <see cref="IndexPrecedence"/> on every input — two
/// implementations of 07 §3 that must never diverge.
/// </summary>
[TestClass]
public sealed class CatalogueTests : IDisposable
{
    private static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-catalogue-tests", Guid.NewGuid().ToString("n"));

    private string CataloguePath => Path.Combine(_root, "catalogue.db");

    private Catalogue Open() => Catalogue.Open(CataloguePath, Repo);

    private static ObjectId Object(byte seed)
    {
        var bytes = new byte[32];
        Array.Fill(bytes, seed);
        return ObjectId.FromBytes(bytes);
    }

    private static BlobId Blob(byte seed)
    {
        var bytes = new byte[16];
        Array.Fill(bytes, seed);
        return BlobId.FromBytes(bytes);
    }

    private static WriterId Writer(byte seed)
    {
        var bytes = new byte[16];
        Array.Fill(bytes, seed);
        return WriterId.FromBytes(bytes);
    }

    private static DeltaId Delta(byte seed)
    {
        var bytes = new byte[16];
        Array.Fill(bytes, seed);
        return DeltaId.FromBytes(bytes);
    }

    [TestMethod]
    public void ApplyDelta_TheSameDeltaTwice_IsANoOp()
    {
        using var catalogue = Open();

        var delta = new IndexDelta
        {
            WriterId = Writer(1),
            Sequence = 1,
            Generation = 0,
            Entries = [new IndexEntry(Object(1), Blob(1), 88, 100, 1, 1, IndexEntryType.Insertion)],
        };

        catalogue.ApplyDelta(Delta(1), delta);
        catalogue.ApplyDelta(Delta(1), delta);

        Assert.AreEqual(1, catalogue.AppliedDeltaCount());
        Assert.IsNotNull(catalogue.ResolveLocation(Object(1)));
    }

    [TestMethod]
    public void ResolveLocation_AnyRandomisedEntrySet_AgreesWithIndexPrecedence()
    {
        // Two implementations of 07 §3 — the in-memory resolver and the SQL
        // ORDER BY — must never diverge. Randomized parity over many objects
        // is the strongest cheap check.
        using var catalogue = Open();
        var random = new Random(20260803);
        var byObject = new Dictionary<ObjectId, List<ProvenancedEntry>>();
        var sequence = 0UL;

        for (var i = 0; i < 200; i++)
        {
            var objectId = Object((byte)random.Next(1, 20));
            var entry = new IndexEntry(
                objectId,
                Blob((byte)random.Next(1, 10)),
                (ulong)random.Next(0, 100_000),
                (uint)random.Next(1, 10_000),
                0x0001,
                0x0001,
                random.Next(2) == 0 ? IndexEntryType.Insertion : IndexEntryType.Supersession);

            var generation = (ulong)random.Next(0, 4);
            var writer = Writer((byte)random.Next(1, 5));
            sequence++;

            catalogue.ApplyDelta(
                DeltaId.FromBytes(System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(sequence)).AsSpan(0, 16)),
                new IndexDelta { WriterId = writer, Sequence = sequence, Generation = generation, Entries = [entry] });

            byObject.TryAdd(objectId, []);
            byObject[objectId].Add(new ProvenancedEntry(entry, generation, writer, sequence));
        }

        foreach (var (objectId, candidates) in byObject)
        {
            var expected = IndexPrecedence.Resolve(candidates, _ => BlobState.Live, [])!;
            var actual = catalogue.ResolveLocation(objectId)!;

            Assert.AreEqual(expected.Entry.BlobId, actual.BlobId);
            Assert.AreEqual(expected.Entry.PhysicalOffset, actual.PhysicalOffset);
            Assert.AreEqual(expected.Generation, actual.Generation);
            Assert.AreEqual(expected.WriterId, actual.WriterId);
            Assert.AreEqual(expected.Sequence, actual.Sequence);
        }
    }

    [TestMethod]
    public void ResolveLocation_TheWinningEntryNamesADeletedBlob_ExcludesItAndReportsAFinding()
    {
        using var catalogue = Open();

        catalogue.ApplyDelta(Delta(1), new IndexDelta
        {
            WriterId = Writer(1),
            Sequence = 1,
            Generation = 1,
            Entries = [new IndexEntry(Object(1), Blob(1), 88, 100, 1, 1, IndexEntryType.Insertion)],
        });
        catalogue.ApplyDelta(Delta(2), new IndexDelta
        {
            WriterId = Writer(1),
            Sequence = 2,
            Generation = 2,
            Entries = [new IndexEntry(Object(1), Blob(2), 999, 100, 1, 1, IndexEntryType.Supersession)],
        });

        catalogue.SetBlobState(Blob(2), BlobState.Deleted);

        var resolved = catalogue.ResolveLocation(Object(1));

        // The generation-2 winner names a deleted blob: superseded (rule 3),
        // the generation-1 location serves, and the anomaly is recorded.
        Assert.AreEqual(Blob(1), resolved!.BlobId);
        Assert.Contains(finding => finding.Kind == DamageKind.MissingBlob, catalogue.Findings());
    }

    [TestMethod]
    public void Open_RepositoryIdentityDiffers_DropsAndRebuildsTheCache()
    {
        using (var catalogue = Open())
        {
            catalogue.ApplyDelta(Delta(1), new IndexDelta
            {
                WriterId = Writer(1),
                Sequence = 1,
                Generation = 0,
                Entries = [new IndexEntry(Object(1), Blob(1), 88, 100, 1, 1, IndexEntryType.Insertion)],
            });
        }

        var otherRepository = RepositoryId.FromBytes(Convert.FromHexString("ffffffffffffffffffffffffffffffff"));

        using var reopened = Catalogue.Open(CataloguePath, otherRepository);

        // The cache belonged to a different repository: dropped, not merged
        // (FR-MAN-002 — the catalogue is disposable, never authoritative).
        Assert.AreEqual(0, reopened.AppliedDeltaCount());
        Assert.IsNull(reopened.ResolveLocation(Object(1)));
    }

    [TestMethod]
    public void LookupByContent_ARecordedSegment_RoundTripsByContentIdentifier()
    {
        using var catalogue = Open();

        var contentId = ContentId.FromBytes(System.Security.Cryptography.SHA256.HashData("segment"u8));
        catalogue.RecordSegmentDedup(contentId, Object(7));

        Assert.AreEqual(Object(7), catalogue.LookupByContent(contentId));
        Assert.IsNull(catalogue.LookupByContent(ContentId.FromBytes(System.Security.Cryptography.SHA256.HashData("other"u8))));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
