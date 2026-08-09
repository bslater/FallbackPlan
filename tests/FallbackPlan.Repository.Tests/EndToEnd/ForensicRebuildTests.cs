using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Catalogue.Forensic;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository.Tests.EndToEnd;

using Catalogue = FallbackPlan.Repository.Catalogue.Catalogue;
using FallbackPlan.TestSupport;

/// <summary>
/// E2/E3/E4 (specification 07 §10, 05 §8, 06 §4.2; FR-MAN-009/010/011/012/014,
/// FR-RST-002; NFR-PERF-015, NFR-REL-003/006): forensic rebuild succeeds with every index
/// object deleted, a targeted recovery stops without a whole-repository
/// scan, the scan leaves every store byte identical, and the whole-file
/// hash catches assembly corruption that per-record verification cannot.
/// </summary>
[TestClass]
public sealed class ForensicRebuildTests : ArchiveTestHarness
{
    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    private async Task<(byte[] Data, PublishedSnapshot Published, RepositoryKeySet Keys, KeyHierarchy Hierarchy, Storage.Local.LocalFileSystemObjectStore Store)>
        PublishAsync(int regions = 6)
    {
        var data = BuildTestFile(regions: regions);
        var store = CreateStore();
        var keys = CreateKeys();
        var hierarchy = new KeyHierarchy(MasterKey);

        var orchestrator = new PublicationOrchestrator(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, hierarchy, store,
            new FallbackPlan.Repository.Index.WriterSequence(
                new FallbackPlan.Repository.Index.FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
            SpoolDirectory);

        using var source = new MemoryStream(data);
        var published = await orchestrator.PublishAsync(
            new BackupJob(
                source,
                "report.bin"u8.ToArray(),
                Enumerable.Repeat((byte)0x22, 16).ToArray(),
                Enumerable.Repeat((byte)0x33, 16).ToArray(),
                Enumerable.Repeat((byte)0x11, 16).ToArray(),
                1_722_600_000_000, 3_600_000, 5, "tests/1.0"),
            CancellationToken.None);

        return (data, published, keys, hierarchy, store);
    }

    private string[] AllStoreFiles() =>
        [.. Directory.EnumerateFiles(StoreRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];

    private byte[] StoreFingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in AllStoreFiles())
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(file));
            hash.AppendData(File.ReadAllBytes(file));
        }

        var digest = new byte[32];
        hash.GetHashAndReset(digest);
        return digest;
    }

    [TestMethod]
    public async Task ForensicRebuild_EveryIndexObjectDeleted_StillRestoresTheSnapshot()
    {
        var (data, _, keys, hierarchy, store) = await PublishAsync();
        using var _keys = keys;
        using var _hierarchy = hierarchy;

        // The E2 premise: EVERY index object is gone.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(StoreRoot, "index"), "*", SearchOption.AllDirectories).ToList())
        {
            File.Delete(file);
        }

        var fingerprintBefore = StoreFingerprint();

        using var rebuilder = new ForensicRebuilder(store, Repo, hierarchy);
        using var catalogue = Catalogue.Open(Path.Combine(SpoolDirectory, "forensic.db"), Repo);

        var report = await rebuilder.RebuildAsync(
            catalogue,
            new ForensicTarget.Snapshot(Enumerable.Repeat((byte)0x11, 16).ToArray()),
            CancellationToken.None);

        Assert.IsTrue(report.TargetSatisfied, string.Join("; ", report.Findings.Select(finding => finding.Detail)));

        // E3: the scan is read-only — every store byte identical.
        SequenceAssert.AreEqual(fingerprintBefore, StoreFingerprint());

        // Restore through the graph the rebuild located.
        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var snapshotBytes = await ReadSnapshotStandaloneAsync(store, keys);
        var decoded = SnapshotManifestCodec.Decode(snapshotBytes);
        var treeRead = await reader.ReadSegmentAsync(decoded.Manifest.RootTree, CancellationToken.None);
        var tree = TreeManifestCodec.Decode(treeRead.Plaintext!);
        var manifestRead = await reader.ReadSegmentAsync(tree.Entries[0].ObjectId, CancellationToken.None);
        var manifest = FileVersionManifestCodec.Decode(manifestRead.Plaintext!);

        using var restored = new MemoryStream();
        var restore = await new RestoreEngine(reader).RestoreFileAsync(manifest, restored, CancellationToken.None);

        Assert.IsTrue(restore.Success, restore.FailureDetail);
        SequenceAssert.AreEqual(data, restored.ToArray());
    }

    [TestMethod]
    public async Task ForensicRebuild_TargetedAtOneSnapshot_StopsBeforeScanningEveryDataBlob()
    {
        var (_, published, keys, hierarchy, store) = await PublishAsync(regions: 16);
        using var _keys = keys;
        using var _hierarchy = hierarchy;

        var totalDataBlobs = Directory
            .EnumerateFiles(Path.Combine(StoreRoot, "blobs", "data"), "*", SearchOption.AllDirectories)
            .Count();
        Assert.IsTrue(totalDataBlobs > 2, "the scenario needs several data blobs to make targeting observable");

        // Target only the FIRST segment's object — its records live in one
        // blob, so the scan must stop early (NFR-PERF-015's direction: one
        // named object without a whole-repository scan).
        var target = published.Archive.SegmentReferences[0].ObjectId;

        using var rebuilder = new ForensicRebuilder(store, Repo, hierarchy);
        using var catalogue = Catalogue.Open(Path.Combine(SpoolDirectory, "targeted.db"), Repo);

        var report = await rebuilder.RebuildAsync(
            catalogue,
            new ForensicTarget.Objects(new HashSet<Domain.Identifiers.ObjectId> { target }),
            CancellationToken.None);

        Assert.IsTrue(report.TargetSatisfied);
        Assert.IsTrue(report.DataBlobsScanned < totalDataBlobs,
            $"targeting must stop early: scanned {report.DataBlobsScanned} of {totalDataBlobs} data blobs");
        Assert.IsNotNull(catalogue.ResolveLocation(target));
    }

    [TestMethod]
    public async Task ForensicRebuild_ADataBlobIsDeleted_SurfacesAMissingBlobFinding()
    {
        var (_, _, keys, hierarchy, store) = await PublishAsync();
        using var _keys = keys;
        using var _hierarchy = hierarchy;

        foreach (var file in Directory.EnumerateFiles(Path.Combine(StoreRoot, "blobs", "data"), "*", SearchOption.AllDirectories).ToList())
        {
            File.Delete(file);
        }

        using var rebuilder = new ForensicRebuilder(store, Repo, hierarchy);
        using var catalogue = Catalogue.Open(Path.Combine(SpoolDirectory, "missing.db"), Repo);

        var report = await rebuilder.RebuildAsync(
            catalogue, new ForensicTarget.Everything(), CancellationToken.None);

        // Every segment the snapshot references is now missing — each is a
        // distinctly named MissingBlob finding, not a generic failure.
        Assert.IsFalse(report.TargetSatisfied);
        Assert.Contains(finding => finding.Kind == DamageKind.MissingBlob, report.Findings);
        Assert.Contains(finding => finding.Kind == DamageKind.MissingBlob, catalogue.Findings());
    }

    [TestMethod]
    public async Task ForensicRebuild_AFooterIsCorrupted_ScopesTheFindingAndContinuesScanning()
    {
        var (_, _, keys, hierarchy, store) = await PublishAsync();
        using var _keys = keys;
        using var _hierarchy = hierarchy;

        // Corrupt one data blob's footer region (truncate its locator).
        var victim = Directory
            .EnumerateFiles(Path.Combine(StoreRoot, "blobs", "data"), "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .First();
        var bytes = await File.ReadAllBytesAsync(victim);
        await File.WriteAllBytesAsync(victim, bytes[..^16]);

        using var rebuilder = new ForensicRebuilder(store, Repo, hierarchy);
        using var catalogue = Catalogue.Open(Path.Combine(SpoolDirectory, "corrupt.db"), Repo);

        var report = await rebuilder.RebuildAsync(catalogue, new ForensicTarget.Everything(), CancellationToken.None);

        Assert.Contains(finding => finding.Kind == DamageKind.CorruptRecord, report.Findings);

        // Corruption is local (04 §7): the other blobs' records still indexed.
        Assert.IsTrue(report.RecordsIndexed > 0);
    }

    [TestMethod]
    public async Task Restore_SegmentsAreAssembledWrongThoughEveryTagPasses_IsCaughtByTheWholeFileHash()
    {
        var (data, published, keys, hierarchy, store) = await PublishAsync();
        using var _keys = keys;
        using var _hierarchy = hierarchy;

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        // Build a manifest whose references SWAP the object ids of two
        // same-length segments: coverage validates, every AEAD tag passes,
        // every record's content identifier is truthful — and the file is
        // still wrong. Only the whole-file hash can say so (06 §4.2's "the
        // two check different things and both are required").
        var references = published.Archive.SegmentReferences.ToArray();
        var first = Array.FindIndex(references, r => r.LogicalLength == references[0].LogicalLength);
        var second = Array.FindIndex(
            references, first + 1,
            r => r.LogicalLength == references[first].LogicalLength && r.ObjectId != references[first].ObjectId);
        Assert.IsTrue(second > first, "the scenario needs two same-length segments with different content");

        (references[first], references[second]) = (
            references[first] with { ObjectId = references[second].ObjectId },
            references[second] with { ObjectId = references[first].ObjectId });

        var forged = new FileVersionManifest
        {
            EntryKind = EntryKind.File,
            Name = "forged.bin"u8.ToArray(),
            NameNormalisation = NameNormalisation.Unknown,
            LogicalLength = (ulong)data.Length,
            SegmentReferences = references,
            WholeFileHash = SHA256.HashData(data),
            SegmentationProfile = 0x0001,
        };

        using var verify = new VerifyEngine(Repo, keys, store);
        var result = await verify.VerifyFileAsync(forged, reader, CancellationToken.None);

        Assert.IsFalse(result.Ok);
        Assert.IsNotNull(result.Detail);
        Assert.Contains("whole_file_hash", result.Detail, StringComparison.Ordinal);

        // The restore engine refuses the same forgery before emitting a byte.
        using var destination = new MemoryStream();
        var restore = await new RestoreEngine(reader).RestoreFileAsync(forged, destination, CancellationToken.None);

        Assert.IsFalse(restore.Success);
        Assert.AreEqual(0, destination.Length);
    }

    [TestMethod]
    public async Task Verify_AnIntactBlobAndASilentlyAlteredOne_PassesTheFirstAndCatchesTheSecondAtLevelTwo()
    {
        var (_, published, keys, hierarchy, store) = await PublishAsync();
        using var _keys = keys;
        using var _hierarchy = hierarchy;

        var blob = published.Archive.Blobs[0];
        using var verify = new VerifyEngine(Repo, keys, store);

        foreach (var level in new[] { VerifyLevel.LocatorAndFooter, VerifyLevel.FooterAndDigest, VerifyLevel.EveryRecord })
        {
            var result = await verify.VerifyBlobAsync(blob.StoreKey, blob.Length, level, CancellationToken.None);
            Assert.IsTrue(result.Ok, $"{level}: {result.Detail}");
        }

        // Flip one ciphertext byte: level 1 still passes (the footer is
        // intact), level 2 catches the alteration without any decryption.
        var path = Path.Combine(StoreRoot, blob.StoreKey.Value.Replace('/', Path.DirectorySeparatorChar));
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[100] ^= 0x01;
        await File.WriteAllBytesAsync(path, bytes);

        var level1 = await verify.VerifyBlobAsync(blob.StoreKey, blob.Length, VerifyLevel.LocatorAndFooter, CancellationToken.None);
        var level2 = await verify.VerifyBlobAsync(blob.StoreKey, blob.Length, VerifyLevel.FooterAndDigest, CancellationToken.None);

        Assert.IsTrue(level1.Ok, "structural checks cannot see a body flip — that is what level 2 is for");
        Assert.IsFalse(level2.Ok);
    }

    [TestMethod]
    public async Task ForensicRebuild_ATreeSnapshot_RepopulatesThePathTables()
    {
        // A multi-directory tree snapshot, then a forensic rebuild into a
        // fresh catalogue: `ls` must work from footers alone (wave T2 —
        // the v2 tables are part of what "rebuilt" means).
        var treeSource = new FakeFileSystemSource();
        treeSource.AddFile("docs/inner/deep.bin", [1, 2, 3, 4]);
        treeSource.AddFile("docs/top.bin", [5, 6, 7]);
        treeSource.AddFile("root.bin", [8, 9]);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy([.. Enumerable.Range(0, 32).Select(value => (byte)value)]);

        var orchestrator = new PublicationOrchestrator(
            SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, hierarchy, store,
            new Repository.Index.WriterSequence(
                new Repository.Index.FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence-tree.txt"))),
            SpoolDirectory);

        await orchestrator.PublishAsync(
            new SnapshotJob
            {
                Source = treeSource,
                RootPath = "/",
                DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
                BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
                SnapshotId = Enumerable.Repeat((byte)0x77, 16).ToArray(),
                NowUnixMilliseconds = 1_722_600_000_000,
                DeclaredMaxDurationMs = 3_600_000,
                ExpiryGeneration = 5,
                ClientVersion = "forensic-tests/1.0",
            },
            CancellationToken.None);

        using var rebuilder = new ForensicRebuilder(store, Repo, hierarchy);
        using var catalogue = Catalogue.Open(Path.Combine(SpoolDirectory, "forensic-tree.db"), Repo);
        var report = await rebuilder.RebuildAsync(catalogue, new ForensicTarget.Everything(), CancellationToken.None);

        Assert.IsTrue(report.TargetSatisfied);

        var snapshotId = Enumerable.Repeat((byte)0x77, 16).ToArray();
        var snapshot = Assert.ContainsSingle(row => row.SnapshotId.Span.SequenceEqual(snapshotId), catalogue.EnumerateSnapshots());
        Assert.AreEqual(1, snapshot.SignatureState);
        Assert.AreEqual(1_722_600_000_000ul, snapshot.CapturedAt);

        SequenceAssert.AreEqual(
            ["docs", "root.bin"],
            catalogue.ListDirectory(snapshotId, string.Empty).Select(entry => entry.Path));
        SequenceAssert.AreEqual(
            ["docs/inner", "docs/top.bin"],
            catalogue.ListDirectory(snapshotId, "docs").Select(entry => entry.Path));

        var deep = catalogue.LookupPath(snapshotId, "docs/inner/deep.bin");
        Assert.IsNotNull(deep);
        Assert.AreEqual(4ul, deep!.LogicalLength);
        Assert.IsNotNull(deep.ModifiedAt);
        Assert.IsNull(deep.IdentityDevice); // identity is never durable (02 §2)
    }

    private static async Task<byte[]> ReadSnapshotStandaloneAsync(Storage.Local.LocalFileSystemObjectStore store, RepositoryKeySet keys)
    {
        await foreach (var entry in store.ListAsync(ObjectPrefix.Parse("snapshots/"), ListOptions.Default, CancellationToken.None))
        {
            using var read = await store.OpenReadAsync(entry.Key, range: null, CancellationToken.None);
            using var memory = new MemoryStream();
            await read.Content!.CopyToAsync(memory);

            var record = Repository.Format.Records.StandaloneRecordFraming.Parse(memory.ToArray());
            var metadataKey = keys.DeriveClassKey(BlobClass.Metadata, record.KeyGeneration);
            Assert.IsTrue(Repository.Packing.StandaloneRecordCipher.TryOpen(
                record, Repo, metadataKey, out var plaintext));
            return plaintext;
        }

        throw new InvalidOperationException("No snapshot object found.");
    }
}
