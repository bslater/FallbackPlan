using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Index;

namespace FallbackPlan.Repository.Tests.Catalogue;

using Catalogue = FallbackPlan.Repository.Catalogue.Catalogue;

/// <summary>
/// The catalogue's contract (architecture 02 §7; FR-MAN-002, FR-MAN-005;
/// NFR-PERF-004, NFR-PERF-010): a disposable cache whose SQL location
/// resolver agrees with <see cref="IndexPrecedence"/> on every input — two
/// implementations of 07 §3 that must never diverge. Also (FR-VER-001) that
/// the signed blob digests every delta publishes survive a rebuild from the
/// index plane alone, which is what the digest tier of verification reads.
/// And that each commit is atomic but not flushed, the protection
/// ADR-0010 Amendment 2 gives a cache.
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
    public void ApplyDelta_TheCoveredBlobDigests_SurviveARebuildFromTheDeltaAlone()
    {
        // A rebuilt catalogue has only the index plane to go on: no
        // RecordBlob ever ran, so the physical row is a placeholder. The
        // digest the writer signed into the delta is still there — and the
        // placeholder's store key is NOT, since it would be the blob id
        // dressed as a key, and a restore from a rebuilt catalogue derives
        // the real one.
        using var catalogue = Open();
        var digest = System.Security.Cryptography.SHA256.HashData("the sealed bytes"u8);

        catalogue.ApplyDelta(Delta(1), new IndexDelta
        {
            WriterId = Writer(1),
            Sequence = 1,
            Generation = 0,
            CoveredBlobIds = [Blob(1)],
            CoveredBlobDigests = [digest],
            Entries = [new IndexEntry(Object(1), Blob(1), 88, 100, 1, 1, IndexEntryType.Insertion)],
        });

        Assert.IsTrue(digest.AsSpan().SequenceEqual(catalogue.SignedDigestOf(Blob(1))!.Value.Span));
        Assert.IsFalse(catalogue.SignedDigestOf(Blob(2)).HasValue, "no delta named blob 2");

        var resolved = catalogue.ResolveLocation(Object(1))!;
        Assert.AreEqual(Blob(1), resolved.BlobId);
        Assert.IsFalse(resolved.StoreBlobKey.HasValue, "a placeholder row must not answer with the blob id as a store key");
    }

    [TestMethod]
    public void ApplyDelta_ARowTheWriterRecorded_KeepsItsStoreKeyAndGainsTheDigest()
    {
        // The live path: RecordBlob first with the real store key, the delta
        // after it. The delta's digest lands and nothing physical moves.
        using var catalogue = Open();
        var storeKey = StoreBlobKey.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));
        var digest = System.Security.Cryptography.SHA256.HashData("the sealed bytes"u8);

        catalogue.RecordBlob(Blob(1), storeKey, BlobClass.Data, KeyGeneration.Zero, 1, 4096, digest: default);
        catalogue.ApplyDelta(Delta(1), new IndexDelta
        {
            WriterId = Writer(1),
            Sequence = 1,
            Generation = 0,
            CoveredBlobIds = [Blob(1)],
            CoveredBlobDigests = [digest],
            Entries = [new IndexEntry(Object(1), Blob(1), 88, 100, 1, 1, IndexEntryType.Insertion)],
        });

        Assert.AreEqual(storeKey, catalogue.ResolveLocation(Object(1))!.StoreBlobKey);
        Assert.IsTrue(digest.AsSpan().SequenceEqual(catalogue.SignedDigestOf(Blob(1))!.Value.Span));
    }

    [TestMethod]
    public void ApplyDelta_ADeltaWithoutDigests_RecordsNothingAboutItsBlobs()
    {
        // The digests are optional in the delta (07 §2.2); a writer that
        // published none leaves the tier with nothing to read, and the
        // catalogue must not invent a row that says otherwise.
        using var catalogue = Open();

        catalogue.ApplyDelta(Delta(1), new IndexDelta
        {
            WriterId = Writer(1),
            Sequence = 1,
            Generation = 0,
            CoveredBlobIds = [Blob(1)],
            Entries = [new IndexEntry(Object(1), Blob(1), 88, 100, 1, 1, IndexEntryType.Insertion)],
        });

        Assert.IsFalse(catalogue.SignedDigestOf(Blob(1)).HasValue, "the delta carried no digest");
        Assert.IsFalse(catalogue.ResolveLocation(Object(1))!.StoreBlobKey.HasValue);
    }

    [TestMethod]
    public void ApplyDelta_TheCoveredBlobMerkleRoots_SurviveARebuildAndNeverErasedByALaterDelta()
    {
        // The root rides the same placeholder-aware path as the digest
        // (07 §2.3), and its own statement rather than a second column on
        // the digest's: a later delta that carries digests and no roots —
        // which is every delta a format-2 writer publishes — must leave a
        // root already on record alone rather than clearing it.
        using var catalogue = Open();
        var digest = System.Security.Cryptography.SHA256.HashData("the sealed bytes"u8);
        var root = System.Security.Cryptography.SHA256.HashData("the tree over the sealed bytes"u8);

        catalogue.ApplyDelta(Delta(1), new IndexDelta
        {
            WriterId = Writer(1),
            Sequence = 1,
            Generation = 0,
            CoveredBlobIds = [Blob(1)],
            CoveredBlobDigests = [digest],
            CoveredBlobMerkleRoots = [root],
            Entries = [new IndexEntry(Object(1), Blob(1), 88, 100, 1, 1, IndexEntryType.Insertion)],
        });

        Assert.IsTrue(root.AsSpan().SequenceEqual(catalogue.SignedMerkleRootOf(Blob(1))!.Value.Span));
        Assert.IsFalse(catalogue.SignedMerkleRootOf(Blob(2)).HasValue, "no delta named blob 2");
        Assert.IsFalse(
            catalogue.ResolveLocation(Object(1))!.StoreBlobKey.HasValue,
            "a placeholder row must not answer with the blob id as a store key");

        catalogue.ApplyDelta(Delta(2), new IndexDelta
        {
            WriterId = Writer(1),
            Sequence = 2,
            Generation = 0,
            CoveredBlobIds = [Blob(1)],
            CoveredBlobDigests = [digest],
            Entries = [new IndexEntry(Object(2), Blob(1), 188, 100, 1, 1, IndexEntryType.Insertion)],
        });

        Assert.IsTrue(root.AsSpan().SequenceEqual(catalogue.SignedMerkleRootOf(Blob(1))!.Value.Span));
    }

    [TestMethod]
    public void ApplyDelta_ARowTheWriterRecorded_KeepsItsStoreKeyAndGainsTheMerkleRoot()
    {
        using var catalogue = Open();
        var storeKey = StoreBlobKey.FromBytes(Convert.FromHexString("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"));
        var digest = System.Security.Cryptography.SHA256.HashData("the sealed bytes"u8);
        var root = System.Security.Cryptography.SHA256.HashData("the tree over the sealed bytes"u8);

        catalogue.RecordBlob(Blob(1), storeKey, BlobClass.Data, KeyGeneration.Zero, 1, 4096, digest: default);
        catalogue.ApplyDelta(Delta(1), new IndexDelta
        {
            WriterId = Writer(1),
            Sequence = 1,
            Generation = 0,
            CoveredBlobIds = [Blob(1)],
            CoveredBlobDigests = [digest],
            CoveredBlobMerkleRoots = [root],
            Entries = [new IndexEntry(Object(1), Blob(1), 88, 100, 1, 1, IndexEntryType.Insertion)],
        });

        Assert.AreEqual(storeKey, catalogue.ResolveLocation(Object(1))!.StoreBlobKey);
        Assert.IsTrue(root.AsSpan().SequenceEqual(catalogue.SignedMerkleRootOf(Blob(1))!.Value.Span));
    }

    [TestMethod]
    public void ApplyDelta_ADeltaWithoutMerkleRoots_RecordsNoRoot()
    {
        // Every format-2 delta, and the case the tier reads as "this blob
        // cannot be challenged by chunk" rather than as damage.
        using var catalogue = Open();

        catalogue.ApplyDelta(Delta(1), new IndexDelta
        {
            WriterId = Writer(1),
            Sequence = 1,
            Generation = 0,
            CoveredBlobIds = [Blob(1)],
            CoveredBlobDigests = [System.Security.Cryptography.SHA256.HashData("the sealed bytes"u8)],
            Entries = [new IndexEntry(Object(1), Blob(1), 88, 100, 1, 1, IndexEntryType.Insertion)],
        });

        Assert.IsTrue(catalogue.SignedDigestOf(Blob(1)).HasValue);
        Assert.IsFalse(catalogue.SignedMerkleRootOf(Blob(1)).HasValue, "the delta carried no Merkle root");
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
    public void Open_EachCommit_IsAtomicButNotFlushed_BecauseTheCatalogueIsACache()
    {
        using var catalogue = Open();

        var (journalMode, synchronous) = catalogue.Durability();

        // WAL keeps every commit atomic and the file consistent through a
        // crash or a power loss. NORMAL (1) is what spares each commit a disk
        // flush: a power loss can then take the newest commits, leaving the
        // catalogue behind the store, the direction StaleCatalogueTests shows
        // costs only a rewrite. FULL (2) is a flush for every row a
        // publication records, and on Windows each flush costs milliseconds.
        Assert.AreEqual("wal", journalMode);
        Assert.AreEqual(1L, synchronous);
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
