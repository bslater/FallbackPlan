using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.TestSupport;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// Multi-file publication end to end (phase-1 wave T1; FR-MAN-004): the scanner event
/// stream becomes a full manifest graph — bottom-up trees, per-kind file
/// versions with the ADR-0026 shapes, populated policy and error manifests,
/// the probed source filesystem — and everything restores from a cold
/// reader.
/// </summary>
[TestClass]
public sealed class SnapshotPublicationTests : ArchiveTestHarness
{
    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];
    private static readonly byte[] DeviceId = [.. Enumerable.Repeat((byte)0x22, 16)];

    private PublicationOrchestrator CreateOrchestrator(
        IObjectStore store, RepositoryKeySet keys, KeyHierarchy hierarchy, ILogger? logger = null,
        IJobProgressReporter? progress = null) =>
        new(
            SmallBlobPolicy,
            Repo,
            Writer,
            KeyGeneration.Zero,
            keys,
            hierarchy,
            store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
            SpoolDirectory,
            observer: null,
            progress: progress,
            logger: logger);

    private static SnapshotJob Job(FakeFileSystemSource source) => new()
    {
        Source = source,
        Roots = [new ScanRoot("/")],
        DeviceId = DeviceId,
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = Enumerable.Repeat((byte)0x11, 16).ToArray(),
        ParentSnapshots = [Enumerable.Repeat((byte)0x44, 16).ToArray()],
        NowUnixMilliseconds = 1_722_600_000_000,
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

    /// <summary>Reads a manifest record and decodes it as a tree chain, following continuations.</summary>
    private static async Task<(TreeManifest Head, IReadOnlyList<TreeEntry> Entries)> ReadTreeAsync(
        RepositoryReader reader, ObjectId headId)
    {
        var chain = new List<TreeManifest>();
        ObjectId? next = headId;
        while (next is { } id)
        {
            var read = await reader.ReadSegmentAsync(id, CancellationToken.None);
            Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome);
            var manifest = TreeManifestCodec.Decode(read.Plaintext!);
            chain.Add(manifest);
            next = manifest.Continuation;
        }

        return (chain[0], TreeChain.ValidateAndFlatten(chain));
    }

    private static async Task<FileVersionManifest> ReadFileVersionAsync(RepositoryReader reader, ObjectId id)
    {
        var read = await reader.ReadSegmentAsync(id, CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome);
        return FileVersionManifestCodec.Decode(read.Plaintext!);
    }

    [TestMethod]
    public async Task TreePublication_AMultiFileTree_PublishesAndRestoresFromAColdReader()
    {
        var source = new FakeFileSystemSource();
        var alpha = Deterministic(200_000, 3);
        var beta = Deterministic(70_000, 7);
        var nested = Deterministic(130_000, 11);
        source.AddFile("docs/alpha.bin", alpha);
        source.AddFile("docs/deep/nested.bin", nested);
        source.AddFile("beta.bin", beta);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        var published = await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source), CancellationToken.None);

        Assert.AreEqual(3, published.Files.Count);
        Assert.IsEmpty(published.Failures);
        Assert.IsNull(published.ErrorManifestObjectId);

        // Cold reader: footers only, no index, no catalogue.
        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var (rootHead, rootEntries) = await ReadTreeAsync(reader, published.RootTreeObjectId);
        SequenceAssert.AreEqual("/"u8.ToArray(), rootHead.Name!.Value.ToArray());

        // Root entries in byte order: beta.bin, docs.
        SequenceAssert.AreEqual(["beta.bin", "docs"], rootEntries.Select(entry => Encoding.UTF8.GetString(entry.Name.Span)));
        Assert.AreEqual(EntryKind.File, rootEntries[0].EntryKind);
        Assert.AreEqual(EntryKind.DirectoryPlaceholder, rootEntries[1].EntryKind);

        var (docsHead, docsEntries) = await ReadTreeAsync(reader, rootEntries[1].ObjectId);
        SequenceAssert.AreEqual("docs"u8.ToArray(), docsHead.Name!.Value.ToArray());
        SequenceAssert.AreEqual(["alpha.bin", "deep"], docsEntries.Select(entry => Encoding.UTF8.GetString(entry.Name.Span)));

        var (_, deepEntries) = await ReadTreeAsync(reader, docsEntries[1].ObjectId);
        var nestedEntry = Assert.ContainsSingle(deepEntries);

        // Every file restores byte-identical.
        foreach (var (entryId, expected) in new[]
                 {
                     (rootEntries[0].ObjectId, beta),
                     (docsEntries[0].ObjectId, alpha),
                     (nestedEntry.ObjectId, nested),
                 })
        {
            var manifest = await ReadFileVersionAsync(reader, entryId);
            using var restored = new MemoryStream();
            var restore = await reader.RestoreAsync(manifest.SegmentReferences, restored, CancellationToken.None);
            Assert.IsTrue(restore.Success, restore.FailureDetail);
            SequenceAssert.AreEqual(expected, restored.ToArray());
        }
    }

    [TestMethod]
    public async Task SnapshotManifest_AClockIsSupplied_StampsCaptureCompletionWhenItHappened()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("a.bin", Deterministic(1000, 1));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        // Retention is the one wall-clock consumer and it reads capture
        // times (00-conventions §7), so a multi-hour capture stamped as
        // zero-duration misstates the very field retention decides on. The
        // engine takes no clock of its own; the job carries one.
        var job = Job(source) with { Clock = () => 1_722_600_005_000 };
        await CreateOrchestrator(store, keys, hierarchy).PublishAsync(job, CancellationToken.None);

        var snapshotKeys = new List<ObjectKey>();
        await foreach (var entry in store.ListAsync(ObjectPrefix.Parse("snapshots/"), ListOptions.Default, CancellationToken.None))
        {
            snapshotKeys.Add(entry.Key);
        }

        byte[] snapshotBytes;
        using (var read = await store.OpenReadAsync(Assert.ContainsSingle(snapshotKeys), range: null, CancellationToken.None))
        {
            using var memory = new MemoryStream();
            await read.Content!.CopyToAsync(memory);
            snapshotBytes = memory.ToArray();
        }

        var record = StandaloneRecordFraming.Parse(snapshotBytes);
        var metadataKey = keys.DeriveClassKey(BlobClass.Metadata, KeyGeneration.Zero);
        Assert.IsTrue(StandaloneRecordCipher.TryOpen(record, Repo, metadataKey, out var plain));
        var manifest = SnapshotManifestCodec.Decode(plain).Manifest;

        Assert.AreEqual(1_722_600_000_000UL, manifest.CaptureStartedAt);
        Assert.AreEqual(1_722_600_005_000UL, manifest.CaptureCompletedAt);
    }

    [TestMethod]
    public async Task SnapshotManifest_APublishedSnapshot_RecordsProbeParentsPolicyAndStatus()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("a.bin", Deterministic(1000, 1));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        var job = Job(source) with { IncludeRules = ["**/*.bin"], ExcludeRules = ["skip"] };
        var published = await CreateOrchestrator(store, keys, hierarchy).PublishAsync(job, CancellationToken.None);

        // The discoverable snapshot, verified and decoded.
        var snapshotKeys = new List<ObjectKey>();
        await foreach (var entry in store.ListAsync(ObjectPrefix.Parse("snapshots/"), ListOptions.Default, CancellationToken.None))
        {
            snapshotKeys.Add(entry.Key);
        }

        byte[] snapshotBytes;
        using (var read = await store.OpenReadAsync(Assert.ContainsSingle(snapshotKeys), range: null, CancellationToken.None))
        {
            using var memory = new MemoryStream();
            await read.Content!.CopyToAsync(memory);
            snapshotBytes = memory.ToArray();
        }

        var record = StandaloneRecordFraming.Parse(snapshotBytes);
        var metadataKey = keys.DeriveClassKey(BlobClass.Metadata, KeyGeneration.Zero);
        Assert.IsTrue(StandaloneRecordCipher.TryOpen(record, Repo, metadataKey, out var plain));
        var decoded = SnapshotManifestCodec.Decode(plain);

        using (var signer = RepositorySigner.Create(hierarchy, KeyGeneration.Zero))
        {
            Assert.IsTrue(signer.Verify(decoded.SignedBytes.Span, decoded.Signature.Span));
        }

        var manifest = decoded.Manifest;
        Assert.AreEqual(1, manifest.CaptureStatus);
        Assert.IsNull(manifest.ErrorManifest);
        Assert.AreEqual(1, manifest.ConsistencyMethod);

        // The probed filesystem, ADR-0026 §Decision 7 keys included.
        Assert.AreEqual("fakefs", manifest.SourceFilesystem.Name);
        Assert.IsTrue(manifest.SourceFilesystem.CaseSensitive);
        Assert.AreEqual(4096u, manifest.SourceFilesystem.MaxPathBytes);
        Assert.AreEqual(255u, manifest.SourceFilesystem.MaxComponentBytes);
        Assert.IsFalse(manifest.SourceFilesystem.ReservedNames);

        // Lineage.
        var parent = Assert.ContainsSingle(manifest.ParentSnapshots);
        SequenceAssert.AreEqual(Enumerable.Repeat((byte)0x44, 16).ToArray(), parent.ToArray());

        // The policy manifest carries the rule strings verbatim (06 §7.1).
        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var policyRead = await reader.ReadSegmentAsync(published.PolicyObjectId, CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, policyRead.Outcome);
        var policy = PolicyManifestCodec.Decode(policyRead.Plaintext!);
        SequenceAssert.AreEqual(["**/*.bin"], policy.IncludeRules);
        SequenceAssert.AreEqual(["skip"], policy.ExcludeRules);
    }

    [TestMethod]
    public async Task TreePublication_IncludeRules_CaptureOnlyTheIncludedSubtree()
    {
        // Include rules are 06 §7.1 semantics, not decoration: a set that says
        // "capture photos/**" and receives everything has silently backed up
        // what the operator chose to leave out. The rules travel to the policy
        // manifest either way; this holds the capture itself to them.
        var source = new FakeFileSystemSource();
        source.AddFile("photos/a.bin", Deterministic(9_000, 3));
        source.AddFile("photos/deep/b.bin", Deterministic(7_000, 5));
        source.AddFile("docs/c.bin", Deterministic(5_000, 7));
        source.AddFile("d.bin", Deterministic(3_000, 9));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        var job = Job(source) with { IncludeRules = ["photos/**"] };
        var published = await CreateOrchestrator(store, keys, hierarchy).PublishAsync(job, CancellationToken.None);

        SequenceAssert.AreEqual(
            ["photos/a.bin", "photos/deep/b.bin"],
            [.. published.Files.Select(file => file.RelativePath).Order(StringComparer.Ordinal)]);
        Assert.IsEmpty(published.Failures);

        // The tree graph carries no skeleton for what was not captured: the
        // root names photos alone — no docs directory, no d.bin.
        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);
        var (_, rootEntries) = await ReadTreeAsync(reader, published.RootTreeObjectId);
        var rootNames = rootEntries.Select(entry => Encoding.UTF8.GetString(entry.Name.Span)).ToList();
        SequenceAssert.AreEqual(["photos"], rootNames);

        // And the captured subtree is whole: photos holds its file and its
        // child directory, which holds its own.
        var (_, photosEntries) = await ReadTreeAsync(
            reader, Assert.ContainsSingle(rootEntries).ObjectId);
        SequenceAssert.AreEqual(
            ["a.bin", "deep"],
            [.. photosEntries.Select(entry => Encoding.UTF8.GetString(entry.Name.Span)).Order(StringComparer.Ordinal)]);
    }

    [TestMethod]
    public async Task TreePublication_IncludeAndExcludeTogether_ExcludeStillWins()
    {
        // 06 §7.1: exclusion beats inclusion at any depth. The include names
        // the whole subtree; the exclude carves one child back out.
        var source = new FakeFileSystemSource();
        source.AddFile("work/keep.bin", Deterministic(4_000, 3));
        source.AddFile("work/secret.bin", Deterministic(4_000, 5));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        var job = Job(source) with { IncludeRules = ["work/**"], ExcludeRules = ["work/secret.bin"] };
        var published = await CreateOrchestrator(store, keys, hierarchy).PublishAsync(job, CancellationToken.None);

        var file = Assert.ContainsSingle(published.Files);
        Assert.AreEqual("work/keep.bin", file.RelativePath);
    }

    [TestMethod]
    public async Task TreePublication_SomeFilesFail_ProducesAnErrorManifestAndCapturesTheRest()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("good.bin", Deterministic(5000, 2));
        source.AddFile("bad.bin", Deterministic(5000, 3)).OpenFailure = new UnauthorizedAccessException("denied");
        source.InjectedFailures.Add(new ScanFailure("vanished", CaptureFailureReason.NotFound, "listing failed"));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        var published = await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source), CancellationToken.None);

        Assert.AreEqual(2, published.Failures.Count);
        Assert.IsNotNull(published.ErrorManifestObjectId);
        Assert.ContainsSingle(published.Files); // good.bin captured

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var errorRead = await reader.ReadSegmentAsync(published.ErrorManifestObjectId!.Value, CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, errorRead.Outcome);
        var errors = ErrorManifestCodec.Decode(errorRead.Plaintext!);

        Assert.Contains(failure =>
            failure.Reason == CaptureFailureReason.Permission &&
            Encoding.UTF8.GetString(failure.PathComponents[0].Span) == "bad.bin", errors.Failures);
        Assert.Contains(failure => failure.Reason == CaptureFailureReason.NotFound, errors.Failures);

        // capture_status = 2 iff a non-empty error manifest is referenced
        // (ADR-0026 §Decision 3).
        var snapshotRead = await reader.ReadSegmentAsync(published.SnapshotObjectId, CancellationToken.None);
        var snapshot = SnapshotManifestCodec.Decode(snapshotRead.Plaintext!);
        Assert.AreEqual(2, snapshot.Manifest.CaptureStatus);
        Assert.AreEqual(published.ErrorManifestObjectId, snapshot.Manifest.ErrorManifest);

        // The failed file appears in no tree.
        var (_, rootEntries) = await ReadTreeAsync(reader, published.RootTreeObjectId);
        Assert.DoesNotContain(entry => Encoding.UTF8.GetString(entry.Name.Span) == "bad.bin", rootEntries);
    }

    [TestMethod]
    public async Task TreePublication_AnExcludeRuleMatches_PrunesThePathWithoutRecordingAFailure()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("keep/data.bin", Deterministic(1000, 4));
        source.AddFile("skip/cache.bin", Deterministic(1000, 5));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        var job = Job(source) with { ExcludeRules = ["skip"] };
        var published = await CreateOrchestrator(store, keys, hierarchy).PublishAsync(job, CancellationToken.None);

        Assert.ContainsSingle(published.Files);
        Assert.IsEmpty(published.Failures);
        Assert.IsNull(published.ErrorManifestObjectId);
        Assert.DoesNotContain(file => file.RelativePath.StartsWith("skip", StringComparison.Ordinal), published.Files);
    }

    [TestMethod]
    public async Task TreePublication_TheRulesAreInvalid_IsRefusedBeforeAnyByteIsWritten()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("a.bin", [1, 2, 3]);
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        var job = Job(source) with { ExcludeRules = ["a**b"] };

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(job, CancellationToken.None));

        var blobs = 0;
        await foreach (var _ in store.ListAsync(ObjectPrefix.Parse("blobs/"), ListOptions.Default, CancellationToken.None))
        {
            blobs++;
        }

        Assert.AreEqual(0, blobs);
    }

    [TestMethod]
    public async Task TreePublication_HardlinkedFiles_ShareAGroupWhileSingletonsCarryNone()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("one.bin", Deterministic(2000, 6), linkCount: 2, fileId: 42);
        source.AddFile("two.bin", Deterministic(2000, 6), linkCount: 2, fileId: 42);
        source.AddFile("solo.bin", Deterministic(2000, 8));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        var published = await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source), CancellationToken.None);

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var byPath = published.Files.ToDictionary(file => file.RelativePath);
        var one = await ReadFileVersionAsync(reader, byPath["one.bin"].ObjectId);
        var two = await ReadFileVersionAsync(reader, byPath["two.bin"].ObjectId);
        var solo = await ReadFileVersionAsync(reader, byPath["solo.bin"].ObjectId);

        Assert.IsNotNull(one.HardlinkGroup);
        SequenceAssert.AreEqual(one.HardlinkGroup!.Value.ToArray(), two.HardlinkGroup!.Value.ToArray());
        Assert.IsNull(solo.HardlinkGroup);

        // The expected derivation, computed independently (ADR-0026 §Decision 1).
        var message = "fbp/hardlink/v1"u8.ToArray()
            .Concat(DeviceId)
            .Concat(new byte[] { 0, 0, 0, 0, 0, 0, 0, 42 })
            .ToArray();
        var expected = HMACSHA256.HashData(keys.ContentIdKey.ToArray(), message)[..16];
        SequenceAssert.AreEqual(expected, one.HardlinkGroup.Value.ToArray());
    }

    [TestMethod]
    public async Task TreePublication_SymlinksAndSpecialFiles_BecomeZeroContentVersionsCarryingTheirShape()
    {
        var source = new FakeFileSystemSource();
        source.AddNode(new FakeFileSystemSource.Node
        {
            RelativePath = "link",
            Kind = ScanEntryKind.Symlink,
            LinkTarget = "target/elsewhere"u8.ToArray(),
            Metadata = new EntryMetadata { ModifiedAt = 1_722_000_000_000 },
        });
        source.AddNode(new FakeFileSystemSource.Node
        {
            RelativePath = "pipe",
            Kind = ScanEntryKind.Special,
            Diagnostics = ["special-kind: fifo"],
            Metadata = new EntryMetadata { ModifiedAt = 1_722_000_000_000, PosixMode = 0x1A4 },
        });

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        var published = await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source), CancellationToken.None);

        Assert.IsEmpty(published.Failures);

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var byPath = published.Files.ToDictionary(file => file.RelativePath);
        var link = await ReadFileVersionAsync(reader, byPath["link"].ObjectId);
        var pipe = await ReadFileVersionAsync(reader, byPath["pipe"].ObjectId);

        Assert.AreEqual(EntryKind.Symlink, link.EntryKind);
        SequenceAssert.AreEqual("target/elsewhere"u8.ToArray(), link.LinkTarget!.Value.ToArray());
        Assert.AreEqual(0ul, link.LogicalLength);
        Assert.IsEmpty(link.SegmentReferences);

        Assert.AreEqual(EntryKind.Special, pipe.EntryKind);
        Assert.Contains("special-kind: fifo", pipe.CaptureDiagnostics);
        Assert.AreEqual(0ul, pipe.LogicalLength);
        SequenceAssert.AreEqual(SHA256.HashData([]), pipe.WholeFileHash.ToArray());
    }

    [TestMethod]
    public async Task TreePublication_ASparseFile_StoresOnlyItsDataAndRestoresTheZeroes()
    {
        // 64 KiB data ‖ 128 KiB hole ‖ 64 KiB data. The backing content
        // materialises the hole as zeroes; the scanner reports the extent.
        var head = Deterministic(64 * 1024, 9);
        var tail = Deterministic(64 * 1024, 13);
        var content = head.Concat(new byte[128 * 1024]).Concat(tail).ToArray();

        var source = new FakeFileSystemSource();
        var node = source.AddFile("sparse.bin", content);
        source.AddNode(node with { SparseExtents = [new SparseExtent(64 * 1024, 128 * 1024)] });

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        var published = await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source), CancellationToken.None);

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var manifest = await ReadFileVersionAsync(
            reader, Assert.ContainsSingle(published.Files).ObjectId);

        // The hole is an extent, not stored bytes: references cover exactly
        // the data runs (06 §3.2 tiling).
        var extent = Assert.ContainsSingle(manifest.SparseExtents);
        Assert.AreEqual(64ul * 1024, extent.Offset);
        Assert.AreEqual(128ul * 1024, extent.Length);
        Assert.AreEqual(content.Length, (long)manifest.LogicalLength);
        Assert.AreEqual(128L * 1024, manifest.SegmentReferences.Sum(reference => reference.LogicalLength));

        // Restore materialises the zeroes and the whole-file hash verifies.
        var engine = new RestoreEngine(reader);
        using var restored = new MemoryStream();
        var restore = await engine.RestoreFileAsync(manifest, restored, CancellationToken.None);
        Assert.IsTrue(restore.Success, restore.FailureDetail);
        SequenceAssert.AreEqual(content, restored.ToArray());
    }

    [TestMethod]
    public async Task TreePublication_AlternateStreams_BecomeSingleSegmentRecordsAndOversizeIsAnErrorManifestEntry()
    {
        var source = new FakeFileSystemSource();
        var withStream = source.AddFile("carrier.bin", Deterministic(3000, 15));
        withStream.AlternateStreams["Zone.Identifier"] = "[ZoneTransfer]\nZoneId=3"u8.ToArray();

        // SmallBlobPolicy segments are 64 KiB — this stream cannot fit one.
        var oversize = source.AddFile("big-stream.bin", Deterministic(100, 16));
        oversize.AlternateStreams["huge"] = Deterministic(80 * 1024, 17);

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        var published = await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source), CancellationToken.None);

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var byPath = published.Files.ToDictionary(file => file.RelativePath);
        var carrier = await ReadFileVersionAsync(reader, byPath["carrier.bin"].ObjectId);

        var stream = Assert.ContainsSingle(carrier.Metadata.AlternateStreams);
        SequenceAssert.AreEqual("Zone.Identifier"u8.ToArray(), stream.Name.ToArray());

        // The stream's object id names a segment record holding the bytes.
        var streamRead = await reader.ReadSegmentAsync(stream.ObjectId, CancellationToken.None);
        Assert.AreEqual(RecordReadOutcome.Ok, streamRead.Outcome);
        SequenceAssert.AreEqual("[ZoneTransfer]\nZoneId=3"u8.ToArray(), streamRead.Plaintext);

        // The oversize stream is error reason 6; its file still captured.
        Assert.Contains(failure => failure.Reason == CaptureFailureReason.TooLarge, published.Failures);
        var big = await ReadFileVersionAsync(reader, byPath["big-stream.bin"].ObjectId);
        Assert.IsEmpty(big.Metadata.AlternateStreams);
        Assert.AreEqual(2, snapshotStatusOf(published));

        int snapshotStatusOf(PublishedTreeSnapshot snapshot) => snapshot.ErrorManifestObjectId is null ? 1 : 2;
    }

    [TestMethod]
    public async Task TreePublication_AFileChangesMidRead_IsRereadAndDiagnosedWhenItNeverSettles()
    {
        var source = new FakeFileSystemSource();
        var restless = source.AddFile("restless.bin", Deterministic(4000, 20));
        restless.RevalidationChangesRemaining = 10; // never settles within the attempt bound

        var stable = source.AddFile("stable.bin", Deterministic(4000, 21));
        stable.RevalidationChangesRemaining = 1; // settles on the second read

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        var published = await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source), CancellationToken.None);

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var byPath = published.Files.ToDictionary(file => file.RelativePath);
        var diagnosed = await ReadFileVersionAsync(reader, byPath["restless.bin"].ObjectId);
        var settled = await ReadFileVersionAsync(reader, byPath["stable.bin"].ObjectId);

        // ADR-0026 §Decision 2: content is the last complete read; the
        // diagnostic records the attempt count.
        Assert.Contains("captured-inconsistent: 2", diagnosed.CaptureDiagnostics);
        Assert.DoesNotContain(diagnostic =>
            diagnostic.StartsWith("captured-inconsistent", StringComparison.Ordinal), settled.CaptureDiagnostics);
    }

    [TestMethod]
    public async Task TreePublication_ANameComesToMeanADifferentObject_IsRecordedAsASubstitution()
    {
        var source = new FakeFileSystemSource();
        var swapped = source.AddFile("swapped.bin", Deterministic(4000, 22), fileId: 900);

        // Revalidation sees a different inode at the same name. That is not an
        // edit: re-reading the name would read the substitute, so the attempt
        // loop must stop rather than spend its budget confirming the swap.
        swapped.SubstitutedIdentity = new ScanIdentity(Device: 7, FileId: 901, LinkCount: 1);

        var settled = source.AddFile("settled.bin", Deterministic(4000, 23));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        var published = await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source), CancellationToken.None);

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var byPath = published.Files.ToDictionary(file => file.RelativePath);
        var diagnosed = await ReadFileVersionAsync(reader, byPath["swapped.bin"].ObjectId);
        var ordinary = await ReadFileVersionAsync(reader, byPath[settled.RelativePath].ObjectId);

        Assert.Contains("captured-identity-changed", diagnosed.CaptureDiagnostics);
        Assert.DoesNotContain(diagnostic =>
            diagnostic.StartsWith("captured-inconsistent", StringComparison.Ordinal), diagnosed.CaptureDiagnostics);

        // An unchanged file's identity matches its own, so nothing is claimed
        // about it — the check must not fire on the ordinary case.
        Assert.DoesNotContain("captured-identity-changed", ordinary.CaptureDiagnostics);
    }

    [TestMethod]
    public async Task TreePublication_ManySmallFiles_KeepsBlobContinuityInsteadOfSealingPerFile()
    {
        // 40 files of 1 KiB under a 256 KiB blob target: continuity means a
        // handful of blobs, one-per-file would mean 40.
        var source = new FakeFileSystemSource();
        for (var i = 0; i < 40; i++)
        {
            source.AddFile($"files/f{i:d3}.bin", Deterministic(1024, (byte)i));
        }

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);

        var published = await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source), CancellationToken.None);

        Assert.AreEqual(40, published.Files.Count);
        Assert.IsTrue(published.ContentBlobs.Count <= 2,
            $"40 small files must share blobs (specification 05 §5); got {published.ContentBlobs.Count}.");
    }

    [TestMethod]
    public async Task TreePublication_ADirectoryTooWideForOneManifest_ShardsIntoAValidChain()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("seed.bin", Deterministic(100, 1));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        var published = await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source), CancellationToken.None);

        // Drive the chain writer directly at a tiny shard budget: the same
        // code path publication uses, forced to shard.
        var sequence = new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence2.txt")));
        var builder = new ManifestBuilder(
            Repo, Writer, KeyGeneration.Zero, keys, store, sequence, SpoolDirectory, SmallBlobPolicy.BlobWriteProfile);

        var fileVersionId = published.Files[0].ObjectId;
        var entries = Enumerable.Range(0, 100)
            .Select(i => new TreeEntry(Encoding.UTF8.GetBytes($"entry-{i:d4}"), fileVersionId, EntryKind.File))
            .ToList();

        ObjectId headId;
        await using (builder)
        {
            headId = await TreeChainWriter.WriteAsync(
                builder, entries, "wide"u8.ToArray(), NameNormalisation.Nfc, EntryMetadata.Empty,
                CancellationToken.None, shardBudget: 256);
            await builder.FlushAsync(CancellationToken.None);
        }

        using var reader = new RepositoryReader(Repo, keys, store);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var chain = new List<TreeManifest>();
        ObjectId? next = headId;
        while (next is { } id)
        {
            var read = await reader.ReadSegmentAsync(id, CancellationToken.None);
            Assert.AreEqual(RecordReadOutcome.Ok, read.Outcome);
            var manifest = TreeManifestCodec.Decode(read.Plaintext!);
            chain.Add(manifest);
            next = manifest.Continuation;
        }

        Assert.IsTrue(chain.Count > 1, "a 256-byte budget over 100 entries must shard");

        // The chain satisfies every 06 §9 rule and flattens to the input.
        var flattened = TreeChain.ValidateAndFlatten(chain);
        Assert.AreEqual(entries.Count, flattened.Count);
        SequenceAssert.AreEqual(
            entries.Select(entry => Encoding.UTF8.GetString(entry.Name.Span)),
            flattened.Select(entry => Encoding.UTF8.GetString(entry.Name.Span)));
    }

    [TestMethod]
    public void SnapshotManifest_SourceFilesystemLimits_RoundTripThroughTheCodec()
    {
        var manifest = new SnapshotManifest
        {
            SnapshotId = new byte[16],
            DeviceId = new byte[16],
            BackupSetId = new byte[16],
            CaptureStartedAt = 1,
            CaptureCompletedAt = 2,
            RootTree = ObjectId.FromBytes(new byte[32]),
            PolicyManifest = ObjectId.FromBytes(new byte[32]),
            ConsistencyMethod = 1,
            CaptureStatus = 1,
            SourceFilesystem = new SourceFilesystem(
                CaseSensitive: false, SupportsSparse: true, Name: "ntfs",
                MaxPathBytes: 65534, MaxComponentBytes: 510, ReservedNames: true),
            PublicationGeneration = 0,
            ClientVersion = "t",
        };

        var decoded = SnapshotManifestCodec.Decode(SnapshotManifestCodec.Encode(manifest, new byte[64])).Manifest;

        Assert.AreEqual(65534u, decoded.SourceFilesystem.MaxPathBytes);
        Assert.AreEqual(510u, decoded.SourceFilesystem.MaxComponentBytes);
        Assert.IsTrue(decoded.SourceFilesystem.ReservedNames);

        // And a three-key map still round-trips to nulls — the phase-0
        // form is untouched (ADR-0026 §Decision 7 keeps the fixture frozen).
        var legacy = manifest with { SourceFilesystem = new SourceFilesystem(true, false, "stream") };
        var legacyDecoded = SnapshotManifestCodec.Decode(SnapshotManifestCodec.Encode(legacy, new byte[64])).Manifest;
        Assert.IsNull(legacyDecoded.SourceFilesystem.MaxPathBytes);
        Assert.IsNull(legacyDecoded.SourceFilesystem.MaxComponentBytes);
        Assert.IsNull(legacyDecoded.SourceFilesystem.ReservedNames);
    }

    [TestMethod]
    public async Task TreePublication_AFailureBeforeTheWriteIntent_ReportsNoCompletedStep()
    {
        // The failure log names the last COMPLETED step, and it is what a
        // person debugs from — a crash in the pre-intent window (the spool
        // hygiene sweep, the probe, the rule check) must not be reported as
        // "after step PublishIntent" when no intent was ever published.
        var source = new FakeFileSystemSource();
        source.AddFile("docs/alpha.bin", Deterministic(4_000, 3));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        var logger = new RecordingLogger();

        var orchestrator = CreateOrchestrator(store, keys, hierarchy, logger);
        var job = Job(source) with
        {
            Source = new FaultInjectingSource(source, probeFailure: new IOException("the volume vanished before the probe")),
        };
        await Assert.ThrowsExactlyAsync<IOException>(
            async () => await orchestrator.PublishAsync(job, CancellationToken.None));

        var failure = Assert.ContainsSingle(logger.Records.Where(record => record.EventId == 2003).ToList());
        Assert.AreEqual(PublicationStep.Preparing, failure.Value("Step"));
    }

    [TestMethod]
    public async Task TreePublication_AFailureDuringTheCapture_ReportsTheIntentAsTheLastCompletedStep()
    {
        // Steps 2–4 interleave by design, so a mid-capture failure's last
        // completed step is the intent — and the tree path must actually
        // record it: before this pin it never did, and every capture failure
        // wore the initializer's label whether the intent had happened or not.
        var source = new FakeFileSystemSource();
        source.AddFile("docs/alpha.bin", Deterministic(4_000, 3));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        var logger = new RecordingLogger();

        var orchestrator = CreateOrchestrator(store, keys, hierarchy, logger);
        var job = Job(source) with
        {
            Source = new FaultInjectingSource(source, midScanFailure: new IOException("the disk died mid-walk")),
        };
        await Assert.ThrowsExactlyAsync<IOException>(
            async () => await orchestrator.PublishAsync(job, CancellationToken.None));

        var failure = Assert.ContainsSingle(logger.Records.Where(record => record.EventId == 2003).ToList());
        Assert.AreEqual(PublicationStep.PublishIntent, failure.Value("Step"));
    }

    [TestMethod]
    public async Task TreePublication_TheCountingPass_FixesThePlanBeforeArchivingBegins()
    {
        // FR-SVC-006's determinate half: a run first counts what it will
        // process, so every later report carries a fixed denominator a
        // client can honestly divide by. 300 files crosses the counting
        // pass's report interval, so the feed also shows the count growing
        // while the plan is still open.
        var source = new FakeFileSystemSource();
        for (var i = 0; i < 300; i++)
        {
            source.AddFile($"docs/file-{i:d3}.bin", Deterministic(10, (byte)i));
        }

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        var reporter = new RecordingReporter();

        var published = await CreateOrchestrator(store, keys, hierarchy, progress: reporter)
            .PublishAsync(Job(source), CancellationToken.None);
        Assert.HasCount(300, published.Files);

        var reports = reporter.Reports;
        Assert.Contains(
            report => report.TotalFiles is null && report.State == JobState.Scanning && report.FilesSeen > 0,
            reports,
            "the counting pass must report its running tally before the plan is fixed");

        var final = reports[^1];
        Assert.AreEqual(300L, final.TotalFiles);
        Assert.AreEqual(3_000L, final.TotalBytes);
        Assert.AreEqual(300L, final.FilesDone);

        // Once fixed, the plan never wavers: every report after the first
        // carrying totals carries the same totals.
        var planned = reports.SkipWhile(report => report.TotalFiles is null).ToList();
        Assert.IsNotEmpty(planned);
        Assert.IsTrue(
            planned.All(report => report.TotalFiles == 300L && report.TotalBytes == 3_000L),
            "the counted plan must be identical on every report that carries it");
    }

    [TestMethod]
    public async Task TreePublication_ARuleExcludedFile_IsAbsentFromThePlan()
    {
        // The count applies the run's own rules — a plan that counted files
        // the capture then skips would leave the meter finishing at 60%.
        var source = new FakeFileSystemSource();
        source.AddFile("docs/keep.bin", Deterministic(100, 5));
        source.AddFile("docs/skip.tmp", Deterministic(50, 7));

        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        var reporter = new RecordingReporter();

        var job = Job(source) with { ExcludeRules = ["*.tmp"] };
        var published = await CreateOrchestrator(store, keys, hierarchy, progress: reporter)
            .PublishAsync(job, CancellationToken.None);

        Assert.ContainsSingle(published.Files);
        var final = reporter.Reports[^1];
        Assert.AreEqual(1L, final.TotalFiles);
        Assert.AreEqual(100L, final.TotalBytes);
    }

    /// <summary>Keeps every report, in order — the feed the console would see.</summary>
    private sealed class RecordingReporter : IJobProgressReporter
    {
        private readonly List<JobProgress> _reports = [];

        public IReadOnlyList<JobProgress> Reports
        {
            get
            {
                lock (_reports)
                {
                    return [.. _reports];
                }
            }
        }

        public void Report(JobProgress progress)
        {
            lock (_reports)
            {
                _reports.Add(progress);
            }
        }
    }

    /// <summary>
    /// Delegates to a real source and throws where told: from the probe
    /// (before anything is durable) or from the capture's scan after its
    /// last event (mid-capture, the intent already published). The counting
    /// pass walks first and is allowed to finish — the mid-capture case
    /// needs a plan to exist and the intent to be durable before the fault.
    /// </summary>
    private sealed class FaultInjectingSource(
        IFileSystemSource inner, Exception? probeFailure = null, Exception? midScanFailure = null) : IFileSystemSource
    {
        private int _scans;

        public SourceFilesystemInfo Probe(string rootPath) =>
            probeFailure is null ? inner.Probe(rootPath) : throw probeFailure;

        public RevalidationProbe? Revalidate(ScanEntry entry) => inner.Revalidate(entry);

        public async IAsyncEnumerable<ScanEvent> ScanAsync(
            string rootPath, ScanOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var walk = Interlocked.Increment(ref _scans);
            await foreach (var scanEvent in inner.ScanAsync(rootPath, options, cancellationToken).ConfigureAwait(false))
            {
                yield return scanEvent;
            }

            if (midScanFailure is not null && walk > 1)
            {
                throw midScanFailure;
            }
        }

        public Stream OpenRead(ScanEntry entry) => inner.OpenRead(entry);

        public Stream OpenAlternateStream(ScanEntry entry, string streamName) => inner.OpenAlternateStream(entry, streamName);
    }
}
