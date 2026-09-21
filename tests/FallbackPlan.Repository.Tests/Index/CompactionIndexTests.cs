using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Profiles;
using FallbackPlan.Repository.Catalogue;
using FallbackPlan.Repository.Catalogue.Forensic;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.Index;

/// <summary>
/// What a compaction pass publishes
/// ([ADR-0067](../../../docs/adr/0067-the-keyless-compactor.md); 07 §2–§3):
/// the supersessions that move readers onto the rewritten blobs, the covered
/// commitments that keep them verifiable, and the bound that keeps the delta
/// readable. Every case names the exit criterion of
/// [ADR-0025 Amendment 2](../../../docs/adr/0025-compaction-reseals-records.md)
/// it discharges. Establishes FR-GC-004, FR-MAN-019 and FR-MAN-015: what a
/// pass publishes is index entries and nothing else, so no manifest, tree or
/// snapshot is touched by a rewrite.
/// </summary>
/// <remarks>
/// Does not establish FR-GC-011: nothing here decides which blobs are worth
/// rewriting, and nothing here deletes a source — the tombstone, the intent
/// and their order are the pass's.
/// </remarks>
[TestClass]
public sealed class CompactionIndexTests : IDisposable
{
    private static readonly RepositoryId Repo =
        RepositoryId.FromBytes(Convert.FromHexString("4142434445464748494a4b4c4d4e4f50"));

    private static readonly WriterId Writer =
        WriterId.FromBytes(Convert.FromHexString("e0e1e2e3e4e5e6e7e8e9eaebecedeeef"));

    private static readonly Argon2Parameters TinyParameters =
        new() { MemoryKiB = 64, Iterations = 1, Parallelism = 1 };

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fbp-compaction-index", Guid.NewGuid().ToString("n"));

    private string SpoolDirectory => Path.Combine(_root, "spool");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Criteria 1, 2 and 3: every produced blob is covered, once, with every record it holds.</summary>
    [TestMethod]
    public async Task APublishedCompaction_CoversEveryProducedBlobOnce_AndNamesEveryRecordInIt()
    {
        using var fixture = await Fixture.CreateAsync(_root, SpoolDirectory);
        var source = await fixture.WriteBlobAsync(Payloads(4));

        var produced = await fixture.CompactAsync([Candidate(source, 0, 2)]);
        var published = await fixture.PublishAsync(produced);

        var delta = await fixture.ReadDeltaAsync(Assert.ContainsSingle(published.DeltaIds));

        // Criterion 1: the produced blob reaches the index, with the
        // commitments that make it checkable by someone other than the device
        // that sealed it (07 §2.2, §2.3).
        var blobId = Assert.ContainsSingle(published.Published);
        Assert.AreEqual(blobId, Assert.ContainsSingle(delta.CoveredBlobIds));
        Assert.HasCount(1, delta.CoveredBlobDigests);
        Assert.HasCount(1, delta.CoveredBlobMerkleRoots);
        Assert.IsFalse(delta.CoveredBlobDigests[0].Span.SequenceEqual(new byte[32]));

        // Criterion 2: nothing appears twice — not a covered blob, not an
        // object.
        Assert.HasCount(delta.CoveredBlobIds.Distinct().Count(), delta.CoveredBlobIds);
        Assert.HasCount(
            delta.Entries.Select(entry => entry.ObjectId).Distinct().Count(), delta.Entries);

        // Criterion 3: every record the blob holds has an entry, so a restore
        // resolves from the index alone and never re-reads a footer to find
        // one.
        var carried = produced.SelectMany(blob => blob.Sealed.RecordTable.Select(record => record.ObjectId));
        CollectionAssert.AreEquivalent(
            carried.ToList(), delta.Entries.Select(entry => entry.ObjectId).ToList());

        // And every one of them is a supersession (07 §2 key 5): compaction
        // is that type's only producer. Precedence does not read it — 07 §3
        // orders by generation and (writer_id, sequence) — so this assertion
        // holds a declaration rather than a mechanism, which is exactly why
        // it is worth pinning: nothing else would notice it going.
        Assert.ContainsSingle(delta.Entries.Select(entry => entry.EntryType).Distinct().ToList());
        Assert.AreEqual(IndexEntryType.Supersession, delta.Entries[0].EntryType);
    }

    /// <summary>Criterion 5: the supersession wins, so nothing is left resolving into the drained blob.</summary>
    [TestMethod]
    public async Task AfterTheSourceIsDeleted_EveryMovedObjectResolvesToTheNewBlob_WithNoDamageFinding()
    {
        using var fixture = await Fixture.CreateAsync(_root, SpoolDirectory);
        var payloads = Payloads(4);
        var source = await fixture.WriteBlobAsync(payloads);

        // The index as it stood before compaction: the source blob, named by
        // an insertion, exactly as a capture would have published it.
        await fixture.PublishOriginalAsync(source);

        var produced = await fixture.CompactAsync([Candidate(source, 0, 2)]);
        var published = await fixture.PublishAsync(produced);
        var moved = produced[0].Sealed.RecordTable.Select(record => record.ObjectId).ToList();

        fixture.Catalogue.SetBlobState(source.BlobId, BlobState.Deleted);

        foreach (var objectId in moved)
        {
            var location = fixture.Catalogue.ResolveLocation(objectId);
            Assert.IsNotNull(location, $"object {objectId} resolved nowhere after compaction");
            Assert.AreEqual(Assert.ContainsSingle(published.Published), location.BlobId);
        }

        // And no rule-3 finding: the winner was already the new blob, so the
        // drained one was superseded rather than merely unreachable. A pass
        // that deleted without superseding would leave the old entry winning
        // and report damage here — the "leftover index object" criterion 5
        // names.
        Assert.IsEmpty(fixture.Catalogue.Findings());
    }

    /// <summary>Criterion 6: records whose identifiers share a shard compact without a broken index.</summary>
    [TestMethod]
    public async Task RecordsSharingAShard_AreAllPublished_AndAllResolve()
    {
        using var fixture = await Fixture.CreateAsync(_root, SpoolDirectory);

        // Near-identical inputs, in the sense the index cares about: 07 §8's
        // shard is the top sixteen bits of the object identifier, so the
        // payloads are searched for rather than hoped for — twenty-four
        // arbitrary records share a shard about once in two hundred runs,
        // which is a test that passes by not proving anything.
        var colliding = SharingAShard(fixture.Keys.ContentIdKey);
        var source = await fixture.WriteBlobAsync([.. colliding, .. Payloads(2, seed: 0x70)]);
        var live = source.Table
            .Where(entry => Shard(entry.ObjectId) == Shard(source.Table[0].ObjectId))
            .ToList();
        Assert.HasCount(2, live);

        var produced = await fixture.CompactAsync([
            new CompactionSource(source.Key, source.BlobId, live)
        ]);
        var published = await fixture.PublishAsync(produced);

        var delta = await fixture.ReadDeltaAsync(Assert.ContainsSingle(published.DeltaIds));
        Assert.HasCount(live.Count, delta.Entries);
        Assert.ContainsSingle(delta.Entries.Select(entry => entry.Shard).Distinct().ToList());
        foreach (var entry in live)
        {
            Assert.IsNotNull(fixture.Catalogue.ResolveLocation(entry.ObjectId));
        }
    }

    /// <summary>
    /// Two payloads whose object identifiers land in one index shard, found by
    /// search: the identifier is an HMAC of the content, so a caller cannot
    /// choose a shard, only look for one.
    /// </summary>
    private static IReadOnlyList<byte[]> SharingAShard(ReadOnlySpan<byte> contentIdKey)
    {
        using var deriver = new ObjectIdDeriver(contentIdKey);
        var seen = new Dictionary<ushort, byte[]>();
        for (var index = 0; index < 1_000_000; index++)
        {
            var payload = new byte[64];
            BitConverter.TryWriteBytes(payload, index);
            var shard = Shard(deriver.Derive(ObjectType.SegmentRecord, ContentHasher.Hash(payload)));
            if (seen.TryGetValue(shard, out var first))
            {
                return [first, payload];
            }

            seen[shard] = payload;
        }

        throw new InvalidOperationException("no two of a million payloads shared an index shard");
    }

    /// <summary>Criteria 8 and 9: a re-derived index reports the new state and the whole of it.</summary>
    [TestMethod]
    public async Task AForensicRebuildAfterCompaction_ResolvesToTheNewBlob_AndHoldsEveryLiveObject()
    {
        using var fixture = await Fixture.CreateAsync(_root, SpoolDirectory);
        var source = await fixture.WriteBlobAsync(Payloads(4));
        var untouched = await fixture.WriteBlobAsync(Payloads(2, seed: 0x90));

        var produced = await fixture.CompactAsync([Candidate(source, 0, 2)]);
        var published = await fixture.PublishAsync(produced);
        var moved = produced[0].Sealed.RecordTable.Select(record => record.ObjectId).ToList();

        // The drained blob goes, which is what the pass would do next.
        await fixture.Store.DeleteAsync(source.Key, DeleteConditions.None, CancellationToken.None);

        using var rebuilt = CatalogueDb.Open(Path.Combine(_root, "rebuilt.db"), Repo);
        using var rebuilder = new ForensicRebuilder(fixture.Store, Repo, fixture.Credential);
        await rebuilder.RebuildAsync(rebuilt, new ForensicTarget.Everything(), CancellationToken.None);

        // Criterion 8: it reports what the repository is now, not a union
        // that still names the blob that went.
        foreach (var objectId in moved)
        {
            var location = rebuilt.ResolveLocation(objectId);
            Assert.IsNotNull(location, $"object {objectId} was lost by the rebuild");
            Assert.AreEqual(Assert.ContainsSingle(published.Published), location.BlobId);
        }

        // Criterion 9: and it is complete — the blob compaction never touched
        // is there too.
        foreach (var entry in untouched.Table)
        {
            Assert.IsNotNull(
                rebuilt.ResolveLocation(entry.ObjectId),
                $"the untouched blob's object {entry.ObjectId} was lost by the rebuild");
        }
    }

    /// <summary>Criterion 10: a pass past the budget publishes several deltas, each inside the bound.</summary>
    [TestMethod]
    public async Task APassPastTheDeltaBudget_PublishesSeveralDeltas_EachWithinTheBound()
    {
        using var fixture = await Fixture.CreateAsync(_root, SpoolDirectory);
        var sources = new List<WrittenBlob>();
        for (var index = 0; index < 3; index++)
        {
            sources.Add(await fixture.WriteBlobAsync(
                Payloads(2, seed: (byte)(0x20 + (index * 8)))));
        }

        var produced = await fixture.CompactAsync(
            [.. sources.Select(blob => new CompactionSource(blob.Key, blob.BlobId, blob.Table))],
            CapturePolicy.Default with
            {
                BlobWriteProfile = BlobWriteProfile.LocalDefault with { MaximumRecordCount = 2 },
            });
        Assert.HasCount(3, produced);

        // A budget that fits one blob's entries and not two: the real bound
        // is sixteen mebibytes, and reaching it with real records would need
        // a blob larger than the format allows, so the budget is the seam.
        var published = await fixture.PublishAsync(produced, deltaByteBudget: 400);

        Assert.IsGreaterThan(1, published.DeltaIds.Count);
        foreach (var deltaId in published.DeltaIds)
        {
            var encoded = await fixture.ReadDeltaObjectAsync(deltaId);
            Assert.IsLessThanOrEqualTo(FormatLimits.MaxMetadataObjectSize, encoded.Length);
        }

        // Split or not, every object still resolves — the point of the bound
        // is that the index stays readable, not that it stays in one piece.
        foreach (var record in produced.SelectMany(blob => blob.Sealed.RecordTable))
        {
            Assert.IsNotNull(fixture.Catalogue.ResolveLocation(record.ObjectId));
        }
    }

    /// <summary>Criterion 2, at scale: the split is at blob boundaries, so no blob is covered twice.</summary>
    [TestMethod]
    public async Task TheSplit_AcrossManyBlobs_CoversEachOneExactlyOnce()
    {
        using var fixture = await Fixture.CreateAsync(_root, SpoolDirectory);
        var sources = new List<CompactionSource>();
        for (var index = 0; index < 12; index++)
        {
            var written = await fixture.WriteBlobAsync(
                Payloads(1, seed: (byte)(0x10 + index)));
            sources.Add(new CompactionSource(written.Key, written.BlobId, written.Table));
        }

        // One record to a blob, so the pass produces twelve of them and the
        // split has something to divide.
        var produced = await fixture.CompactAsync(
            sources,
            CapturePolicy.Default with
            {
                BlobWriteProfile = BlobWriteProfile.LocalDefault with { MaximumRecordCount = 1 },
            });
        Assert.HasCount(12, produced);

        var deltas = CompactionPublication.Split(produced, byteBudget: 900);

        Assert.IsGreaterThan(1, deltas.Count);
        foreach (var delta in deltas)
        {
            Assert.IsGreaterThan(0, delta.CoveredBlobIds.Count);
            Assert.HasCount(delta.CoveredBlobIds.Count, delta.CoveredBlobDigests);
            Assert.HasCount(delta.CoveredBlobIds.Count, delta.CoveredBlobMerkleRoots);
        }

        // Each blob in exactly one delta, and every entry carried: the split
        // divides the pass, it does not drop or duplicate any of it.
        var covered = deltas.SelectMany(delta => delta.CoveredBlobIds).ToList();
        Assert.HasCount(12, covered);
        Assert.HasCount(12, covered.Distinct().ToList());
        CollectionAssert.AreEquivalent(
            produced.Select(blob => blob.Sealed.BlobId.ToString()).ToList(),
            covered.Select(id => id.ToString()).ToList());
        Assert.HasCount(12, deltas.SelectMany(delta => delta.Entries).ToList());
    }

    /// <summary>Criterion 11: two publications at once produce deltas that each decode and resolve.</summary>
    [TestMethod]
    public async Task TwoPublicationsAtOnce_EachProduceADeltaThatDecodesAndResolves()
    {
        using var first = await Fixture.CreateAsync(Path.Combine(_root, "a"), Path.Combine(_root, "a", "spool"));
        using var second = await Fixture.CreateAsync(Path.Combine(_root, "b"), Path.Combine(_root, "b", "spool"));

        var sourceA = await first.WriteBlobAsync(Payloads(4));
        var sourceB = await second.WriteBlobAsync(Payloads(4, seed: 0xA0));

        var producedA = await first.CompactAsync([Candidate(sourceA, 0, 2)]);
        var producedB = await second.CompactAsync([Candidate(sourceB, 0, 2)]);

        var publications = await Task.WhenAll(
            first.PublishAsync(producedA).AsTask(),
            second.PublishAsync(producedB).AsTask());

        foreach (var (fixture, published, produced) in new[]
        {
            (first, publications[0], producedA),
            (second, publications[1], producedB),
        })
        {
            var delta = await fixture.ReadDeltaAsync(Assert.ContainsSingle(published.DeltaIds));
            Assert.HasCount(2, delta.Entries);
            foreach (var record in produced.SelectMany(blob => blob.Sealed.RecordTable))
            {
                Assert.IsNotNull(fixture.Catalogue.ResolveLocation(record.ObjectId));
            }
        }
    }

    private static CompactionSource Candidate(WrittenBlob source, int from, int count) =>
        new(source.Key, source.BlobId, [.. source.Table.Skip(from).Take(count)]);

    /// <summary>The index shard of an object identifier: its top sixteen bits (07 §8).</summary>
    private static ushort Shard(ObjectId objectId)
    {
        Span<byte> bytes = stackalloc byte[32];
        objectId.CopyTo(bytes);
        return (ushort)((bytes[0] << 8) | bytes[1]);
    }

    private static IReadOnlyList<byte[]> Payloads(int count, byte seed = 0x40) =>
        [.. Enumerable.Range(0, count)
            .Select(index => Enumerable.Repeat((byte)(seed + index), 512 + index).ToArray())];

    internal sealed record WrittenBlob(
        ObjectKey Key, BlobId BlobId, IReadOnlyList<RecordTableEntry> Table);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly string _spool;

        private Fixture(
            string root,
            string spool,
            LocalFileSystemObjectStore store,
            RepositoryReadAuthority authority,
            RepositoryKeySet keys,
            CatalogueDb catalogue,
            WriterSequence sequence)
        {
            _root = root;
            _spool = spool;
            Store = store;
            Authority = authority;
            Keys = keys;
            Catalogue = catalogue;
            Sequence = sequence;
        }

        public LocalFileSystemObjectStore Store { get; }

        public RepositoryReadAuthority Authority { get; }

        public RepositoryKeySet Keys { get; }

        public CatalogueDb Catalogue { get; }

        public WriterSequence Sequence { get; }

        public RepositoryWriteCredential Credential => Authority.Credential;

        public static ValueTask<Fixture> CreateAsync(string root, string spool)
        {
            using var passphrase = Passphrase.Create("a compactable index!!!!!!!");
            var salt = Enumerable.Repeat((byte)0x1D, KekDerivation.SaltLength).ToArray();
            var authority = WriteOnlyDerivation.Derive(
                passphrase, TinyParameters, salt, KdfValidationMode.OpenRepository);

            return ValueTask.FromResult(new Fixture(
                root,
                spool,
                new LocalFileSystemObjectStore(Path.Combine(root, "store")),
                authority,
                RepositoryKeySet.FromWriteCredential(authority.Credential),
                CatalogueDb.Open(Path.Combine(root, "catalogue.db"), Repo),
                new WriterSequence(new FileSequenceStateStore(Path.Combine(root, "state", "sequence.txt")))));
        }

        /// <summary>
        /// A blob as a capture would have left it. The counter comes from the
        /// same allocator the compactor draws from, because two blobs in one
        /// repository sharing a counter share an identifier — which the
        /// upload refuses, correctly, as a regressed sequence.
        /// </summary>
        public async ValueTask<WrittenBlob> WriteBlobAsync(IReadOnlyList<byte[]> payloads)
        {
            var counter = Sequence.AllocateNext();

            using var deriver = new ObjectIdDeriver(Keys.ContentIdKey);
            var structureKey = Credential.DeriveMetadataKey(KeyGeneration.Zero);
            SealedBlob sealedBlob;
            try
            {
                var writer = BlobWriter.CreateSealed(
                    Repo, Writer, KeyGeneration.Zero, structureKey, Credential.SealingPublicKey, counter,
                    EncryptionProfile.Aes256GcmV1, BlobWriteProfile.LocalDefault, _spool,
                    formatVersion: FormatVersions.RelocatableRecords);

                await using (writer.ConfigureAwait(false))
                {
                    foreach (var payload in payloads)
                    {
                        await writer.AppendRecordAsync(
                            ObjectType.SegmentRecord,
                            deriver.Derive(ObjectType.SegmentRecord, ContentHasher.Hash(payload)),
                            CompressionProfile.None, (ulong)payload.Length, payload, CancellationToken.None);
                    }

                    sealedBlob = await writer.SealAsync(CancellationToken.None);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(structureKey);
            }

            await using (sealedBlob.ConfigureAwait(false))
            {
                using var keyDeriver = new StoreBlobKeyDeriver(Keys.KeyIdKey);
                var key = BlobStoreKeys.ForBlob(sealedBlob.BlobClass, keyDeriver.Derive(sealedBlob.BlobId));
                var put = await Store.PutAsync(
                    key, sealedBlob.OpenContentAsync, PutConditions.IfNotExists, CancellationToken.None);
                Assert.AreEqual(PutOutcome.Created, put.Outcome);
                return new WrittenBlob(key, sealedBlob.BlobId, [.. sealedBlob.RecordTable]);
            }
        }

        public async ValueTask<IReadOnlyList<CompactedBlob>> CompactAsync(
            IReadOnlyList<CompactionSource> candidates, CapturePolicy? policy = null)
        {
            using var compactor = new BlobCompactor(
                Repo, Writer, KeyGeneration.Zero, Keys, policy ?? CapturePolicy.Default, Store,
                Sequence, _spool, FormatVersions.RelocatableRecords);

            return await compactor.CompactAsync(candidates, CancellationToken.None);
        }

        public async ValueTask<PublishedCompaction> PublishAsync(
            IReadOnlyList<CompactedBlob> produced, int? deltaByteBudget = null)
        {
            using var keyDeriver = new StoreBlobKeyDeriver(Keys.KeyIdKey);
            using var publisher = new IndexPublisher(Store, Repo, Writer, Credential, Sequence);
            return await CompactionPublication.PublishAsync(
                produced, Store, keyDeriver, publisher, Catalogue, generation: 0,
                CancellationToken.None, deltaByteBudget);
        }

        /// <summary>The index as a capture would have left it: insertions naming the source blob.</summary>
        public async ValueTask PublishOriginalAsync(WrittenBlob source)
        {
            using var publisher = new IndexPublisher(Store, Repo, Writer, Credential, Sequence);
            var entries = source.Table
                .Select(record => new IndexEntry(
                    record.ObjectId, source.BlobId, record.PhysicalOffset, record.StoredLength,
                    record.CompressionProfileValue, record.EncryptionProfileValue, IndexEntryType.Insertion))
                .ToList();

            var (deltaId, delta) = await publisher.PublishDeltaDetailedAsync(
                0, [source.BlobId], entries, [], [], CancellationToken.None);
            Catalogue.ApplyDelta(deltaId, delta);
        }

        /// <summary>
        /// The published delta, read back from its own object rather than
        /// through the loader: the loader does not say which delta it took a
        /// value from, and what these cases are about is what this
        /// publication wrote.
        /// </summary>
        public async ValueTask<IndexDelta> ReadDeltaAsync(DeltaId deltaId)
        {
            using var loader = new IndexLoader(Store, Repo, Credential);
            var state = await loader.LoadAsync(
                currentGeneration: 0, gapPatienceGenerations: 2, isSequenceAccountedAsync: null,
                blobState: null, CancellationToken.None);
            Assert.IsEmpty(state.Findings);

            var record = StandaloneRecordFraming.Parse(await ReadDeltaObjectAsync(deltaId));
            var metadataKey = Credential.DeriveMetadataKey(record.KeyGeneration);
            try
            {
                Assert.IsTrue(
                    StandaloneRecordCipher.TryOpen(record, Repo, metadataKey, out var plaintext),
                    $"delta {deltaId} failed authentication");
                return IndexDeltaCodec.Decode(plaintext).Delta;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(metadataKey);
            }
        }

        public async ValueTask<byte[]> ReadDeltaObjectAsync(DeltaId deltaId)
        {
            var key = MetadataStoreKeys.IndexDelta(generation: 0, deltaId);
            using var read = await Store.OpenReadAsync(key, range: null, CancellationToken.None);
            Assert.AreEqual(OpenReadOutcome.Found, read.Outcome, $"{key} was not in the store");
            using var buffer = new MemoryStream();
            await read.Content!.CopyToAsync(buffer, CancellationToken.None);
            return buffer.ToArray();
        }

        public void Dispose()
        {
            Catalogue.Dispose();
            Keys.Dispose();
            Authority.Dispose();
            _ = _root;
        }
    }
}
