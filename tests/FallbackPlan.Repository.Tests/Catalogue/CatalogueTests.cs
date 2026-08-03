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

    [Fact]
    public void Applying_the_same_delta_twice_is_a_no_op()
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

        Assert.Equal(1, catalogue.AppliedDeltaCount());
        Assert.NotNull(catalogue.ResolveLocation(Object(1)));
    }

    [Fact]
    public void The_sql_resolver_agrees_with_IndexPrecedence_on_randomized_entry_sets()
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

            Assert.Equal(expected.Entry.BlobId, actual.BlobId);
            Assert.Equal(expected.Entry.PhysicalOffset, actual.PhysicalOffset);
            Assert.Equal(expected.Generation, actual.Generation);
            Assert.Equal(expected.WriterId, actual.WriterId);
            Assert.Equal(expected.Sequence, actual.Sequence);
        }
    }

    [Fact]
    public void A_deleted_blob_winner_is_excluded_and_reported()
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
        Assert.Equal(Blob(1), resolved!.BlobId);
        Assert.Contains(catalogue.Findings(), finding => finding.Kind == DamageKind.MissingBlob);
    }

    [Fact]
    public void A_repository_mismatch_drops_and_rebuilds_the_cache()
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
        Assert.Equal(0, reopened.AppliedDeltaCount());
        Assert.Null(reopened.ResolveLocation(Object(1)));
    }

    [Fact]
    public void Dedup_lookup_round_trips_by_content_identifier()
    {
        using var catalogue = Open();

        var contentId = ContentId.FromBytes(System.Security.Cryptography.SHA256.HashData("segment"u8));
        catalogue.RecordSegmentDedup(contentId, Object(7));

        Assert.Equal(Object(7), catalogue.LookupByContent(contentId));
        Assert.Null(catalogue.LookupByContent(ContentId.FromBytes(System.Security.Cryptography.SHA256.HashData("other"u8))));
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
