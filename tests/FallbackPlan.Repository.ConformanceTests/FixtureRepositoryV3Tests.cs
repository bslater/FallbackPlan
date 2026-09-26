using System.Runtime.CompilerServices;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Profiles;
using FallbackPlan.Repository.Catalogue;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Index.Journal;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Domain.Configuration;

using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// The committed <c>fixture-repository-v3</c> bytes
/// ([ADR-0052](../../../docs/adr/0052-relocatable-records-format-v3.md)
/// Amendment 1; NFR-COMP-004, FR-WOR-001, FR-WOR-003, FR-MAN-007): format 3
/// keeps every promise format 2 makes — structure to the write bundle alone,
/// content only to the derived authority, the wrong passphrase refused — and
/// adds the one only frozen bytes can establish, that a data record lifted
/// out of this blob opens in a blob written later by a different writer.
/// </summary>
[TestClass]
public sealed class FixtureRepositoryV3Tests : IDisposable
{
    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "fbp-fixture-v3-tests", Guid.NewGuid().ToString("n"));

    private static string CommittedFixturePath([CallerFilePath] string sourceFile = "")
    {
        var root = LocateRoot(AppContext.BaseDirectory) ?? LocateRoot(Path.GetDirectoryName(sourceFile));
        Assert.IsNotNull(root);
        return Path.Combine(
            root, "specifications", "repository-format", "conformance", "fixtures", "fixture-repository-v3");
    }

    private static string? LocateRoot(string? start)
    {
        if (string.IsNullOrEmpty(start))
        {
            return null;
        }

        var directory = new DirectoryInfo(start);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FallbackPlan.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName;
    }

    [TestMethod]
    public async Task FixtureRepositoryV3_TheCommittedBytes_HoldTheWriteOnlyReadContract()
    {
        var committed = CommittedFixturePath();
        if (!Directory.Exists(committed))
        {
            // Bootstrap: materialise the fixture so it can be committed, and
            // fail loudly — a green run must never silently create what it
            // was supposed to read.
            await FixtureRepositoryV3.GenerateAsync(committed, CancellationToken.None);
            Assert.Fail($"The committed v3 fixture was absent and has been generated at {committed} — review and commit it.");
        }

        var store = new LocalFileSystemObjectStore(committed);

        Assert.IsFalse(Directory.Exists(Path.Combine(committed, "keys")), "a write-only repository stores no key object");

        // The descriptor names format 3 and demands relocatable-records, so a
        // build that does not implement the feature refuses the repository by
        // name rather than misreading it (00 §5).
        var descriptor = await RepositoryLifecycle.ReadDescriptorAsync(store, CancellationToken.None);
        Assert.AreEqual(FormatVersions.RelocatableRecords, descriptor.FormatVersion);
        Assert.Contains(
            Format.Descriptor.RepositoryDescriptorCodec.FeatureRelocatableRecords, descriptor.RequiredFeatures);

        using (var wrong = Passphrase.Create("not the fixture passphrase"))
        {
            Assert.IsFalse(
                RepositoryLifecycle.TryDeriveReadAuthority(descriptor, wrong, out _),
                "the wrong passphrase must not reproduce the fixture's keys");
        }

        // The write bundle alone opens structure and refuses content — the
        // split is unchanged by the keys moving into the records.
        using (var authority = FixtureRepositoryV3.DeriveAuthority())
        {
            using var writeOnly = await RepositoryLifecycle.OpenAsync(
                store, authority.Credential, CancellationToken.None);
            using var structural = new RepositoryReader(writeOnly.RepositoryId, writeOnly.Keys, store);
            await structural.LoadBlobsAsync(CancellationToken.None);

            var manifestEntry = structural.AllRecords.Single(
                record => record.ObjectType == ObjectType.FileVersionManifest);
            var manifestRead = await structural.ReadSegmentAsync(manifestEntry.ObjectId, CancellationToken.None);
            Assert.AreEqual(RecordReadOutcome.Ok, manifestRead.Outcome, "the structure plane reads with the bundle alone");
            var manifest = FileVersionManifestCodec.Decode(manifestRead.Plaintext!);

            var sealedRead = await structural.ReadSegmentAsync(
                manifest.SegmentReferences[0].ObjectId, CancellationToken.None);
            Assert.AreEqual(
                RecordReadOutcome.ContentSealed, sealedRead.Outcome,
                "content must answer sealed, not readable and not damaged, without a grant");

            using var catalogue = Catalogue.Catalogue.Open(
                Path.Combine(_scratch, "catalogue.db"), writeOnly.RepositoryId);
            var report = await new CatalogueRebuilder(new IndexLoader(store, writeOnly.RepositoryId, writeOnly.Credential))
                .RebuildAsync(catalogue, currentGeneration: 0, gapPatienceGenerations: 2,
                    isSequenceAccountedAsync: null, CancellationToken.None);
            Assert.AreEqual(1, report.DeltasApplied);
            Assert.IsEmpty(report.Findings);

            using var journalReader = new JournalReader(store, writeOnly.RepositoryId, writeOnly.Credential);
            var (records, unparseable, _) = await journalReader.LoadAsync(maxGeneration: 0, CancellationToken.None);
            Assert.AreEqual(0, unparseable);
            Assert.AreEqual(2, records.Count);
        }

        using (var passphrase = FixtureRepositoryV3.CreatePassphrase())
        {
            var (repository, authority) = await RepositoryLifecycle.OpenForReadAsync(
                store, passphrase, CancellationToken.None);
            using (repository)
            using (authority)
            {
                Assert.AreEqual(FixtureRepositoryV3.Repo, repository.RepositoryId);
                Assert.IsTrue(repository.KdfBelowCreationMinimums, "the fixture's KDF parameters are deliberately small");

                using var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, store, authority);
                await reader.LoadBlobsAsync(CancellationToken.None);

                var manifestEntry = reader.AllRecords.Single(
                    record => record.ObjectType == ObjectType.FileVersionManifest);
                var manifestRead = await reader.ReadSegmentAsync(manifestEntry.ObjectId, CancellationToken.None);
                Assert.AreEqual(RecordReadOutcome.Ok, manifestRead.Outcome);

                var manifest = FileVersionManifestCodec.Decode(manifestRead.Plaintext!);
                using var restored = new MemoryStream();
                var restore = await new RestoreEngine(reader).RestoreFileAsync(manifest, restored, CancellationToken.None);
                Assert.IsTrue(restore.Success, restore.FailureDetail);
                SequenceAssert.AreEqual(FixtureRepositoryV3.FileContent(), restored.ToArray());
            }
        }
    }

    [TestMethod]
    public async Task FixtureRepositoryV3_ADataRecordCopiedOutOfTheFrozenBlob_OpensInABlobWrittenLater()
    {
        // The relocation property, proved against bytes nobody in this test
        // wrote: the committed record's key is its object's and its nonce
        // rides its prefix, so copying it verbatim into a blob with another
        // writer, another salt, another derived blob key and another ordinal
        // leaves it readable (ADR-0052 §4, 05 §2.2).
        var committed = CommittedFixturePath();
        Assert.IsTrue(Directory.Exists(committed), "the committed v3 fixture must exist");

        var store = new LocalFileSystemObjectStore(committed);
        using var authority = FixtureRepositoryV3.DeriveAuthority();
        using var keys = RepositoryKeySet.FromWriteCredential(authority.Credential);

        // Locate the fixture's sealed data blob and lift one record's sealed
        // bytes straight out of the object.
        var dataKey = await store.ListAsync(ObjectPrefix.Parse("blobs/data/"), ListOptions.Default, CancellationToken.None)
            .FirstAsync(CancellationToken.None);

        var blobBytes = new byte[dataKey.Length];
        using (var opened = await store.OpenReadAsync(dataKey.Key, null, CancellationToken.None))
        {
            Assert.AreEqual(OpenReadOutcome.Found, opened.Outcome);
            await opened.Content!.ReadExactlyAsync(blobBytes, CancellationToken.None);
        }

        var structureKey = keys.DeriveClassKey(BlobClass.Metadata, KeyGeneration.Zero);
        using var objectIds = new ObjectIdDeriver(authority.Credential.ContentIdKey.ToArray());
        using var grant = new SealedContentKeyOpener(authority.SealingPrivateKey, FixtureRepositoryV3.Repo);

        RecordTableEntry source;
        byte[] expected;
        var prefixLength = RecordFraming.PrefixLength(FormatVersions.RelocatableRecords, BlobClass.Data);
        using (var origin = await BlobReader.OpenAsync(
                   store, dataKey.Key, dataKey.Length, FixtureRepositoryV3.Repo,
                   (_, _) => keys.DeriveClassKey(BlobClass.Metadata, KeyGeneration.Zero), objectIds,
                   CancellationToken.None, grant))
        {
            source = origin.RecordTable[1];
            var read = await origin.ReadRecordAsync(source, CancellationToken.None);
            Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome);
            expected = read.Plaintext!;
        }

        var sealedRecord = blobBytes.AsSpan(
            (int)source.PhysicalOffset + RecordHeader.Length,
            prefixLength + (int)source.StoredLength + RecordCipher.TagLength).ToArray();

        // A fresh blob: different writer, different counter, different salt,
        // and therefore a different derived blob key at every level.
        var spool = Path.Combine(_scratch, "spool");
        var destinationRoot = Path.Combine(_scratch, "destination");
        var destinationStore = new LocalFileSystemObjectStore(destinationRoot);
        var otherWriter = WriterId.FromBytes(Convert.FromHexString("0f0e0d0c0b0a09080706050403020100"));

        ObjectKey key;
        long length;
        await using (var writer = BlobWriter.CreateSealed(
                         FixtureRepositoryV3.Repo, otherWriter, KeyGeneration.Zero, structureKey,
                         keys.SealingPublicKey, blobCounter: 77, EncryptionProfile.Aes256GcmV1,
                         BlobWriteProfile.LocalDefault, spool,
                         formatVersion: FormatVersions.RelocatableRecords))
        {
            await writer.AppendSealedRecordAsync(source, sealedRecord, CancellationToken.None);
            await using var sealedBlob = await writer.SealAsync(CancellationToken.None);

            using var storeKeys = new StoreBlobKeyDeriver(authority.Credential.KeyIdKey.ToArray());
            key = BlobStoreKeys.ForBlob(sealedBlob.BlobClass, storeKeys.Derive(sealedBlob.BlobId));
            length = sealedBlob.Length;
            var put = await destinationStore.PutAsync(
                key, sealedBlob.OpenContentAsync, PutConditions.IfNotExists, CancellationToken.None);
            Assert.AreEqual(PutOutcome.Created, put.Outcome);
        }

        using var relocatedReader = await BlobReader.OpenAsync(
            destinationStore, key, length, FixtureRepositoryV3.Repo,
            (_, _) => keys.DeriveClassKey(BlobClass.Metadata, KeyGeneration.Zero), objectIds,
            CancellationToken.None, grant);

        var relocated = await relocatedReader.ReadRecordAsync(relocatedReader.RecordTable[0], CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, relocated.Outcome);
        SequenceAssert.AreEqual(expected, relocated.Plaintext);
    }

    [TestMethod]
    public async Task FixtureRepositoryV3_ItsIndexDelta_CarriesAMerkleCommitmentPerCoveredBlob()
    {
        // The format-version gate, proved on frozen bytes: a repository at
        // format 3 publishes key 11 for every blob its delta covers
        // (07 §2.3), and the root is the tree over that blob's own sealed
        // bytes short of the locator — recomputed here from the object in
        // the store, never taken from the writer's word for it.
        var committed = CommittedFixturePath();
        Assert.IsTrue(Directory.Exists(committed), "the committed v3 fixture must exist");

        var store = new LocalFileSystemObjectStore(committed);
        using var authority = FixtureRepositoryV3.DeriveAuthority();

        using var loader = new IndexLoader(store, FixtureRepositoryV3.Repo, authority.Credential);
        var state = await loader.LoadAsync(currentGeneration: 0, gapPatienceGenerations: 2, isSequenceAccountedAsync: null, blobState: null, CancellationToken.None);
        Assert.IsEmpty(state.Findings);

        var delta = Assert.ContainsSingle(state.Deltas).Delta;
        Assert.IsNotEmpty(delta.CoveredBlobIds);
        Assert.HasCount(delta.CoveredBlobIds.Count, delta.CoveredBlobMerkleRoots);
        Assert.HasCount(delta.CoveredBlobIds.Count, delta.CoveredBlobDigests);

        using var storeKeys = new StoreBlobKeyDeriver(authority.Credential.KeyIdKey.ToArray());
        for (var i = 0; i < delta.CoveredBlobIds.Count; i++)
        {
            var storeKey = BlobStoreKeys.ForBlob(
                BlobClass.Data, storeKeys.Derive(delta.CoveredBlobIds[i]));
            var metadata = await store.GetMetadataAsync(storeKey, CancellationToken.None);
            if (metadata.Metadata is null)
            {
                storeKey = BlobStoreKeys.ForBlob(
                    BlobClass.Metadata, storeKeys.Derive(delta.CoveredBlobIds[i]));
                metadata = await store.GetMetadataAsync(storeKey, CancellationToken.None);
            }

            Assert.IsNotNull(metadata.Metadata);
            var bytes = new byte[metadata.Metadata.Length];
            using (var opened = await store.OpenReadAsync(storeKey, null, CancellationToken.None))
            {
                Assert.AreEqual(OpenReadOutcome.Found, opened.Outcome);
                await opened.Content!.ReadExactlyAsync(bytes, CancellationToken.None);
            }

            var preimage = bytes.AsSpan(0, bytes.Length - FooterLocator.Length);
            SequenceAssert.AreEqual(BlobMerkle.Root(preimage), delta.CoveredBlobMerkleRoots[i].ToArray());
            SequenceAssert.AreEqual(
                System.Security.Cryptography.SHA256.HashData(preimage), delta.CoveredBlobDigests[i].ToArray());
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }
}
