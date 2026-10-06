using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Catalogue;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using FallbackPlan.Filesystem;
using Counting = FallbackPlan.TestSupport.CountingObjectStore;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// One hint pack per publication (specification 06 §11.5, ADR-0090). A backup
/// writes the source-identity hints of the versions it created as one object
/// rather than one object each — the per-file request that put a first backup
/// a hundred times over NFR-PERF-008's budget. After a catalogue rebuild a
/// renamed file finds its ancestor through the packs, through the per-file
/// hints an older writer left, or through both, so the parent-version
/// reference FR-MAN-003 keeps is still the one the file's history had. A pack
/// names only the versions its backup created, so NFR-PERF-005's incremental
/// metadata holds as it did.
/// </summary>
[TestClass]
public sealed class HintPackTests : ArchiveTestHarness
{
    private static readonly byte[] DeviceId = [.. Enumerable.Repeat((byte)0x22, 16)];
    private static readonly byte[] OtherDeviceId = [.. Enumerable.Repeat((byte)0x44, 16)];

    private const ulong FirstCapture = 1_722_600_000_000;

    private CatalogueDb OpenCatalogue() =>
        CatalogueDb.Open(Path.Combine(SpoolDirectory, "catalogue.db"), Repo);

    private PublicationOrchestrator CreateOrchestrator(
        IObjectStore store, RepositoryKeySet keys, RepositoryWriteCredential credential, CatalogueDb? catalogue) =>
        new(
            SmallBlobPolicy,
            Repo,
            Writer,
            KeyGeneration.Zero,
            keys,
            credential,
            store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
            SpoolDirectory,
            FormatVersions.SealedDataPlane,
            observer: null,
            catalogue);

    private ManifestBuilder CreateBuilder(IObjectStore store, RepositoryKeySet keys, string sequenceName = "builder-sequence.txt") =>
        new(
            Repo, Writer, KeyGeneration.Zero, keys, store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, sequenceName))),
            SpoolDirectory, SmallBlobPolicy.BlobWriteProfile, FormatVersions.SealedDataPlane);

    private static byte[] SnapshotId(byte seed) => [.. Enumerable.Repeat(seed, 16)];

    private static SnapshotJob Job(FakeFileSystemSource source, byte snapshotSeed, ulong now = FirstCapture) => new()
    {
        Source = source,
        Roots = [new ScanRoot("/")],
        DeviceId = DeviceId,
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = SnapshotId(snapshotSeed),
        NowUnixMilliseconds = now,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "fallbackplan-tests/1.0",
    };

    private static byte[] Deterministic(int length, byte seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + i * 31);
        }

        return data;
    }

    private static ObjectId TestObjectId(byte seed)
    {
        var bytes = new byte[ObjectId.Size];
        Array.Fill(bytes, seed);
        return ObjectId.FromBytes(bytes);
    }

    private static async Task<List<string>> KeysUnderAsync(LocalFileSystemObjectStore store, string prefix)
    {
        var found = new List<string>();
        await foreach (var entry in store.ListAsync(ObjectPrefix.Parse(prefix), ListOptions.Default, CancellationToken.None))
        {
            found.Add(entry.Key.Value);
        }

        return found;
    }

    private static async Task<byte[]> ReadObjectAsync(IObjectStore store, string key)
    {
        using var read = await store.OpenReadAsync(ObjectKey.Parse(key), range: null, CancellationToken.None);
        Assert.AreEqual(OpenReadOutcome.Found, read.Outcome, key);
        using var buffer = new MemoryStream();
        await read.Content!.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static async Task<SourceIdentityPack> ReadPackAsync(IObjectStore store, RepositoryKeySet keys, string key)
    {
        var record = StandaloneRecordFraming.Parse(await ReadObjectAsync(store, key));
        Assert.AreEqual(ObjectType.SourceIdentityPack, record.Header.ObjectType);

        var metadataKey = keys.DeriveClassKey(BlobClass.Metadata, record.KeyGeneration);
        try
        {
            Assert.IsTrue(StandaloneRecordCipher.TryOpen(record, Repo, metadataKey, out var plaintext));
            return SourceIdentityPackCodec.Decode(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(metadataKey);
        }
    }

    /// <summary>
    /// Turns every pack into the per-file hints a writer from before
    /// ADR-0090 published for the same versions — the same record framing,
    /// type, key and body that writer used — so a test can stand on a
    /// repository an older installation left.
    /// </summary>
    private static async Task RewriteAsPerFileHintsAsync(LocalFileSystemObjectStore store, RepositoryKeySet keys)
    {
        using var deriver = new ObjectIdDeriver(keys.ContentIdKey);
        var metadataKey = keys.DeriveClassKey(BlobClass.Metadata, KeyGeneration.Zero);
        try
        {
            foreach (var key in await KeysUnderAsync(store, "hints/identity-pack/"))
            {
                var pack = await ReadPackAsync(store, keys, key);
                foreach (var entry in pack.Entries)
                {
                    var hint = new SourceIdentityHint
                    {
                        SourceKey = entry.SourceKey,
                        SnapshotId = pack.SnapshotId,
                        ObjectId = entry.ObjectId,
                        CapturedAt = pack.CapturedAt,
                    };

                    var encoded = SourceIdentityHintCodec.Encode(hint);
                    var sealedHint = StandaloneRecordCipher.Seal(
                        Repo, metadataKey, KeyGeneration.Zero, Writer, counter: 1,
                        ObjectType.SourceIdentityHint,
                        deriver.Derive(ObjectType.SourceIdentityHint, ContentHasher.Hash(encoded)),
                        encoded);

                    await store.PutAsync(
                        MetadataStoreKeys.SourceIdentityHint(hint.SourceKey.Span, hint.CapturedAt, hint.SnapshotId.Span),
                        _ => ValueTask.FromResult<Stream>(new MemoryStream(sealedHint, writable: false)),
                        PutConditions.IfNotExists,
                        CancellationToken.None);
                }

                await store.DeleteAsync(ObjectKey.Parse(key), DeleteConditions.None, CancellationToken.None);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(metadataKey);
        }
    }

    /// <summary>The catalogue is a cache: lose it, and rebuild what the repository holds.</summary>
    private async Task<CatalogueDb> RebuildCatalogueAsync(
        IObjectStore store, RepositoryKeySet keys, RepositoryWriteCredential credential)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(Path.Combine(SpoolDirectory, "catalogue.db"));

        var rebuilt = OpenCatalogue();
        await new CatalogueRebuilder(new IndexLoader(store, Repo, credential)).RebuildAsync(
            rebuilt, currentGeneration: 0, gapPatienceGenerations: 2, isSequenceAccountedAsync: null,
            CancellationToken.None);

        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        await CatalogueProjector.ProjectAsync(rebuilt, reader, store, Repo, keys, credential, CancellationToken.None);
        return rebuilt;
    }

    private static async Task<FileVersionManifest> ReadManifestAsync(IObjectStore store, RepositoryKeySet keys, ObjectId objectId)
    {
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var read = await reader.ReadSegmentAsync(objectId, CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome);
        return FileVersionManifestCodec.Decode(read.Plaintext!);
    }

    private static FakeFileSystemSource SourceOf(int files, int fileIdBase = 5_000)
    {
        var source = new FakeFileSystemSource();
        for (var index = 0; index < files; index++)
        {
            source.AddFile($"docs/file-{index:d3}.bin", Deterministic(2_000, (byte)index), fileId: (ulong)(fileIdBase + index));
        }

        return source;
    }

    [TestMethod]
    public async Task AFirstBackup_WritesItsHintsAsOnePack_AndNoHintPerFile()
    {
        var source = SourceOf(48);
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        var published = await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xA1), CancellationToken.None);

        Assert.IsEmpty(await KeysUnderAsync(store, "hints/identity/"), "a hint was written per file");
        var packKey = Assert.ContainsSingle(await KeysUnderAsync(store, "hints/identity-pack/"));
        Assert.AreEqual(MetadataStoreKeys.SourceIdentityPack(DeviceId, FirstCapture, SnapshotId(0xA1), part: 0).Value, packKey);

        // The pack names every version this backup created, each under the
        // source key its file's identity derives, and nothing else.
        var pack = await ReadPackAsync(store, keys, packKey);
        SequenceAssert.AreEqual(DeviceId, pack.DeviceId.ToArray());
        SequenceAssert.AreEqual(SnapshotId(0xA1), pack.SnapshotId.ToArray());
        Assert.AreEqual(FirstCapture, pack.CapturedAt);

        using var deriver = new SourceIdentityKeyDeriver(keys.ContentIdKey);
        var expected = published.Files
            .Select(file => (Key: Convert.ToHexString(deriver.Derive(DeviceId, file.IdentityFileId!.Value)), file.ObjectId))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToList();
        SequenceAssert.AreEqual(
            expected,
            pack.Entries.Select(entry => (Convert.ToHexString(entry.SourceKey.Span), entry.ObjectId)));
    }

    [TestMethod]
    public async Task ABackupThatCreatesNoVersion_WritesNoPack()
    {
        var source = SourceOf(8);
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = OpenCatalogue();

        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(Job(source, 0xB1), CancellationToken.None);
        await CreateOrchestrator(store, keys, credential, catalogue)
            .PublishAsync(
                Job(source, 0xB2, now: FirstCapture + 1) with { PriorSnapshotId = SnapshotId(0xB1) },
                CancellationToken.None);

        // Every file short-circuited, so the second backup created nothing
        // and owes no hint: the first backup's pack still names them all.
        Assert.ContainsSingle(await KeysUnderAsync(store, "hints/identity-pack/"));
    }

    [TestMethod]
    public async Task MoreHintsThanOnePackHolds_AreWrittenAsNumberedParts()
    {
        var store = CreateStore();
        using var keys = CreateKeys();

        // Distinct keys, deliberately not in order: the writer sorts.
        var entries = Enumerable.Range(0, SourceIdentityPack.MaxEntries + 5)
            .Select(index =>
            {
                var key = SHA256.HashData(BitConverter.GetBytes(index))[..SourceIdentityHint.SourceKeyLength];
                return new SourceIdentityPackEntry { SourceKey = key, ObjectId = TestObjectId((byte)index) };
            })
            .ToList();

        var written = 0;
        await using (var builder = CreateBuilder(store, keys))
        {
            await builder.WriteSourceIdentityPacksAsync(
                DeviceId, SnapshotId(0xC1), FirstCapture, entries, intentSequence: 1,
                count => written += count, CancellationToken.None);
        }

        Assert.AreEqual(entries.Count, written);
        var parts = await KeysUnderAsync(store, "hints/identity-pack/");
        SequenceAssert.AreEqual(
            [
                MetadataStoreKeys.SourceIdentityPack(DeviceId, FirstCapture, SnapshotId(0xC1), part: 0).Value,
                MetadataStoreKeys.SourceIdentityPack(DeviceId, FirstCapture, SnapshotId(0xC1), part: 1).Value,
            ],
            parts);

        var first = await ReadPackAsync(store, keys, parts[0]);
        var second = await ReadPackAsync(store, keys, parts[1]);
        Assert.HasCount(SourceIdentityPack.MaxEntries, first.Entries);
        Assert.HasCount(5, second.Entries);
        Assert.AreEqual(1u, second.Part);

        // Together they are every entry once, in source-key order across the
        // parts as within each.
        SequenceAssert.AreEqual(
            entries.Select(entry => Convert.ToHexString(entry.SourceKey.Span)).Order(StringComparer.Ordinal),
            first.Entries.Concat(second.Entries).Select(entry => Convert.ToHexString(entry.SourceKey.Span)));
    }

    [TestMethod]
    public async Task ThePackIndex_AnswersWithTheNewestVersionAtOrBeforeTheBound()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        var sourceKey = Enumerable.Repeat((byte)0x5C, 16).ToArray();

        await using (var builder = CreateBuilder(store, keys))
        {
            for (byte step = 1; step <= 3; step++)
            {
                await builder.WriteSourceIdentityPacksAsync(
                    DeviceId, SnapshotId(step), FirstCapture + step * 1_000ul,
                    [new SourceIdentityPackEntry { SourceKey = sourceKey, ObjectId = TestObjectId(step) }],
                    intentSequence: step, written: null, CancellationToken.None);
            }
        }

        async Task<ObjectId?> AnswerAt(ulong bound) =>
            (await SourceIdentityPackIndex.LoadAsync(store, Repo, keys, DeviceId, bound, CancellationToken.None))
                .Find(sourceKey);

        // Packs after the bound describe versions the prior snapshot did not
        // contain, so they are not its ancestor (06 §11.3).
        Assert.IsNull(await AnswerAt(FirstCapture + 999));
        Assert.AreEqual(TestObjectId(1), await AnswerAt(FirstCapture + 1_000));
        Assert.AreEqual(TestObjectId(2), await AnswerAt(FirstCapture + 2_999));
        Assert.AreEqual(TestObjectId(3), await AnswerAt(FirstCapture + 3_000));
    }

    [TestMethod]
    public async Task ThePackIndex_ReadsOnlyThisDevicesPacks()
    {
        var inner = CreateStore();
        using var keys = CreateKeys();
        var mine = Enumerable.Repeat((byte)0x01, 16).ToArray();
        var theirs = Enumerable.Repeat((byte)0x02, 16).ToArray();

        await using (var builder = CreateBuilder(inner, keys))
        {
            await builder.WriteSourceIdentityPacksAsync(
                DeviceId, SnapshotId(0xD1), FirstCapture,
                [new SourceIdentityPackEntry { SourceKey = mine, ObjectId = TestObjectId(0x01) }],
                intentSequence: 1, written: null, CancellationToken.None);
            await builder.WriteSourceIdentityPacksAsync(
                OtherDeviceId, SnapshotId(0xD2), FirstCapture,
                [new SourceIdentityPackEntry { SourceKey = theirs, ObjectId = TestObjectId(0x02) }],
                intentSequence: 2, written: null, CancellationToken.None);
        }

        // A source key is derived from its device, so another device's packs
        // can never answer this one's questions: they are not even read.
        var counting = new Counting(inner);
        var index = await SourceIdentityPackIndex.LoadAsync(
            counting, Repo, keys, DeviceId, FirstCapture, CancellationToken.None);

        Assert.AreEqual(TestObjectId(0x01), index.Find(mine));
        Assert.IsNull(index.Find(theirs));
        Assert.AreEqual(1, index.PacksRead);
        Assert.AreEqual(1L, counting.Reads);
    }

    [TestMethod]
    public async Task APackFiledUnderAnotherKey_IsNotBelieved()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        var sourceKey = Enumerable.Repeat((byte)0x6D, 16).ToArray();

        await using (var builder = CreateBuilder(store, keys))
        {
            await builder.WriteSourceIdentityPacksAsync(
                DeviceId, SnapshotId(0xE1), FirstCapture + 5_000,
                [new SourceIdentityPackEntry { SourceKey = sourceKey, ObjectId = TestObjectId(0x0E) }],
                intentSequence: 1, written: null, CancellationToken.None);
        }

        // The store key is not covered by the AEAD. Moved to an earlier
        // capture time it would answer a bound the pack's own body says it
        // postdates — so it must not answer at all.
        var original = Assert.ContainsSingle(await KeysUnderAsync(store, "hints/identity-pack/"));
        var bytes = await ReadObjectAsync(store, original);
        var moved = MetadataStoreKeys.SourceIdentityPack(DeviceId, FirstCapture, SnapshotId(0xE1), part: 0);
        await store.PutAsync(
            moved, _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false)),
            PutConditions.IfNotExists, CancellationToken.None);
        await store.DeleteAsync(ObjectKey.Parse(original), DeleteConditions.None, CancellationToken.None);

        var index = await SourceIdentityPackIndex.LoadAsync(
            store, Repo, keys, DeviceId, FirstCapture + 10_000, CancellationToken.None);

        Assert.IsNull(index.Find(sourceKey));
        Assert.AreEqual(0, index.PacksRead);
    }

    [TestMethod]
    public async Task RenamedFilesAfterARebuild_FindTheirAncestors_InTheBackupsPack_WithOneRead()
    {
        var source = SourceOf(20);
        var inner = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();

        PublishedTreeSnapshot first;
        using (var live = OpenCatalogue())
        {
            first = await CreateOrchestrator(inner, keys, credential, live)
                .PublishAsync(Job(source, 0xF1), CancellationToken.None);
        }

        using var rebuilt = await RebuildCatalogueAsync(inner, keys, credential);

        // Every file moves, and five new ones arrive: twenty-five questions
        // for the hints, which the one pack answers or rules out.
        for (var index = 0; index < 20; index++)
        {
            Assert.IsTrue(source.Remove($"docs/file-{index:d3}.bin"));
            source.AddFile($"moved/file-{index:d3}.bin", Deterministic(2_000, (byte)index), fileId: (ulong)(5_000 + index));
        }

        for (var index = 0; index < 5; index++)
        {
            source.AddFile($"new/file-{index:d3}.bin", Deterministic(1_500, (byte)(100 + index)), fileId: (ulong)(9_000 + index));
        }

        var counting = new Counting(inner);
        var second = await CreateOrchestrator(counting, keys, credential, rebuilt)
            .PublishAsync(Job(source, 0xF2, now: FirstCapture + 1) with { PriorSnapshotId = SnapshotId(0xF1) }, CancellationToken.None);

        for (var index = 0; index < 20; index++)
        {
            var moved = second.Files.Single(file => file.RelativePath == $"moved/file-{index:d3}.bin");
            var original = first.Files.Single(file => file.RelativePath == $"docs/file-{index:d3}.bin");
            var manifest = await ReadManifestAsync(inner, keys, moved.ObjectId);
            Assert.AreEqual(original.ObjectId, manifest.ParentVersion, moved.RelativePath);
        }

        // One listing of this device's packs and one read of the pack, however
        // many files asked; no per-file listing, because no per-file hint
        // exists to find.
        Assert.ContainsSingle(counting.ReadKeys.Where(key => key.StartsWith("hints/", StringComparison.Ordinal)));
        Assert.ContainsSingle(counting.Listings.Where(prefix => prefix.StartsWith("hints/identity-pack/", StringComparison.Ordinal)));
        Assert.IsFalse(
            counting.Listings.Any(prefix => prefix.StartsWith("hints/identity/", StringComparison.Ordinal) && prefix.Length > "hints/identity/".Length),
            "a source key's per-file prefix was listed: " + string.Join(", ", counting.Listings));
    }

    [TestMethod]
    public async Task PerFileHintsAnOlderWriterLeft_StillAnswerARename_AfterARebuild()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/before.bin", Deterministic(4_000, 11), fileId: 4_242);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();

        PublishedFileVersion original;
        using (var live = OpenCatalogue())
        {
            var first = await CreateOrchestrator(store, keys, credential, live)
                .PublishAsync(Job(source, 0xA5), CancellationToken.None);
            original = first.Files.Single();
        }

        // The repository as an installation from before ADR-0090 left it: a
        // hint per version and no pack.
        await RewriteAsPerFileHintsAsync(store, keys);
        Assert.IsEmpty(await KeysUnderAsync(store, "hints/identity-pack/"));
        Assert.ContainsSingle(await KeysUnderAsync(store, "hints/identity/"));

        using var rebuilt = await RebuildCatalogueAsync(store, keys, credential);

        Assert.IsTrue(source.Remove("docs/before.bin"));
        source.AddFile("archive/after.bin", Deterministic(4_000, 11), fileId: 4_242);

        var second = await CreateOrchestrator(store, keys, credential, rebuilt)
            .PublishAsync(Job(source, 0xA6, now: FirstCapture + 1) with { PriorSnapshotId = SnapshotId(0xA5) }, CancellationToken.None);

        var after = second.Files.Single(file => file.RelativePath == "archive/after.bin");
        Assert.AreEqual(original.ObjectId, (await ReadManifestAsync(store, keys, after.ObjectId)).ParentVersion);
    }

    [TestMethod]
    public async Task ARepositoryOfBothShapes_AnswersFromItsPacks_AndFromPerFileHintsForWhatNoPackNames()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/changing.bin", Deterministic(3_000, 21), fileId: 6_001);
        source.AddFile("docs/quiet.bin", Deterministic(3_000, 22), fileId: 6_002);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();

        ObjectId quiet;
        ObjectId changed;
        using (var live = OpenCatalogue())
        {
            var first = await CreateOrchestrator(store, keys, credential, live)
                .PublishAsync(Job(source, 0xB5), CancellationToken.None);
            quiet = first.Files.Single(file => file.RelativePath == "docs/quiet.bin").ObjectId;

            // The older installation's hints, then this one's first backup:
            // the changed file's new version is named in a pack, the quiet
            // file only in the per-file hint its first version left.
            await RewriteAsPerFileHintsAsync(store, keys);

            source.AddFile("docs/changing.bin", Deterministic(3_000, 23), fileId: 6_001)
                .Metadata = EntryMetadata.Empty with { ModifiedAt = FirstCapture + 50 };
            var second = await CreateOrchestrator(store, keys, credential, live)
                .PublishAsync(Job(source, 0xB6, now: FirstCapture + 100) with { PriorSnapshotId = SnapshotId(0xB5) }, CancellationToken.None);
            changed = second.Files.Single(file => file.RelativePath == "docs/changing.bin").ObjectId;
        }

        Assert.ContainsSingle(await KeysUnderAsync(store, "hints/identity-pack/"));

        using var rebuilt = await RebuildCatalogueAsync(store, keys, credential);

        Assert.IsTrue(source.Remove("docs/changing.bin"));
        Assert.IsTrue(source.Remove("docs/quiet.bin"));
        source.AddFile("moved/changing.bin", Deterministic(3_000, 23), fileId: 6_001)
            .Metadata = EntryMetadata.Empty with { ModifiedAt = FirstCapture + 50 };
        source.AddFile("moved/quiet.bin", Deterministic(3_000, 22), fileId: 6_002);

        var third = await CreateOrchestrator(store, keys, credential, rebuilt)
            .PublishAsync(Job(source, 0xB7, now: FirstCapture + 200) with { PriorSnapshotId = SnapshotId(0xB6) }, CancellationToken.None);

        var movedChanging = third.Files.Single(file => file.RelativePath == "moved/changing.bin");
        var movedQuiet = third.Files.Single(file => file.RelativePath == "moved/quiet.bin");
        Assert.AreEqual(changed, (await ReadManifestAsync(store, keys, movedChanging.ObjectId)).ParentVersion);
        Assert.AreEqual(quiet, (await ReadManifestAsync(store, keys, movedQuiet.ObjectId)).ParentVersion);
    }
}
