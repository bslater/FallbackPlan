using System.Security.Cryptography;
using System.Text;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.TestSupport;

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

    private PublicationOrchestrator CreateOrchestrator(IObjectStore store, RepositoryKeySet keys, KeyHierarchy hierarchy) =>
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
            observer: null);

    private static SnapshotJob Job(FakeFileSystemSource source) => new()
    {
        Source = source,
        RootPath = "/",
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
    public async Task A_multi_file_tree_publishes_and_restores_from_a_cold_reader()
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
    public async Task The_snapshot_records_probe_parents_policy_and_complete_status()
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
    public async Task Failures_produce_an_error_manifest_and_partial_status_and_the_rest_still_captures()
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
    public async Task Excluded_paths_are_pruned_and_are_not_failures()
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
    public async Task Invalid_rules_are_refused_before_any_byte_is_written()
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
    public async Task Hardlinked_files_share_a_group_and_singletons_carry_none()
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
    public async Task Symlinks_and_specials_are_zero_content_versions_with_their_shapes()
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
    public async Task Sparse_files_store_only_data_and_restore_with_zeroes()
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
    public async Task Alternate_streams_become_single_segment_records_and_oversize_is_error_reason_6()
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
    public async Task A_file_changing_mid_read_is_reread_and_diagnosed_when_it_never_settles()
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
    public async Task A_name_that_comes_to_mean_a_different_object_is_recorded_as_a_substitution()
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
    public async Task Blob_continuity_spans_files_instead_of_sealing_per_file()
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
    public async Task A_directory_too_wide_for_one_manifest_shards_into_a_valid_chain()
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
    public void Source_filesystem_limits_round_trip_through_the_snapshot_codec()
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
}
