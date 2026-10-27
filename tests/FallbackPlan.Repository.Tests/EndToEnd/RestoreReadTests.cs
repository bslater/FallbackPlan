using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Restore;
using FallbackPlan.Storage.Local;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A plan says what the run will read from where it restores from, beside
/// what it will write (FR-RST-003; architecture 08 §2; ADR-0083 Amendment 2).
/// The two differ: a store holds content compressed, once however many
/// files repeat it, and not at all where a sparse file keeps a hole, and it
/// holds every record sealed. On a store that bills for what is read, the
/// read figure is the one a person is paying for.
/// </summary>
/// <remarks>
/// The figure is what the run asks the store for: each record the run needs,
/// once, at the length the reader sizes a record's read by, and each blob's
/// envelope once. A whole tree read through the reader a run uses takes no
/// more than that, and not much less.
/// </remarks>
[TestClass]
public sealed class RestoreReadTests : ArchiveTestHarness
{
    private const int MiB = 1024 * 1024;

    [TestMethod]
    public async Task Plan_ContentThatCompresses_IsReadInFewerBytesThanItWrites()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/notes.txt", Repeating(MiB));

        var (plan, probed) = await PlanAsync(source, 0xC1);

        Assert.AreEqual((ulong)MiB, plan.SpaceEstimateBytes);
        Assert.IsGreaterThan(0UL, probed.ReadBytes);
        Assert.IsLessThan(plan.SpaceEstimateBytes / 4, probed.ReadBytes, "a megabyte of one repeated pattern is stored in far less");
    }

    [TestMethod]
    public async Task Plan_ContentThatDoesNotCompress_IsReadInAtLeastItsOwnBytes()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/photo.raw", Random(MiB, 1));

        var (_, probed) = await PlanAsync(source, 0xC2);

        Assert.IsGreaterThanOrEqualTo((ulong)MiB, probed.ReadBytes, "every byte of it is stored, sealed");
        Assert.IsLessThan((ulong)MiB + (64 * 1024), probed.ReadBytes);
    }

    [TestMethod]
    public async Task Plan_AFileTwice_IsReadOnce()
    {
        // The store holds a repeated file's content once, and so does what
        // the run reads: the second name costs only its place in a folder.
        var content = Random(MiB, 2);
        var once = new FakeFileSystemSource();
        once.AddFile("docs/a.bin", content);
        var twice = new FakeFileSystemSource();
        twice.AddFile("docs/a.bin", content);
        twice.AddFile("copy/a.bin", content);

        var (_, single) = await PlanAsync(once, 0xC3);
        var (plan, both) = await PlanAsync(twice, 0xC4);

        Assert.AreEqual(2UL * MiB, plan.SpaceEstimateBytes);
        Assert.IsLessThan(single.ReadBytes + 4096, both.ReadBytes, "the second copy's content is not read again");
    }

    [TestMethod]
    public async Task Plan_ASparseFile_IsReadWithoutItsHoles()
    {
        var content = new byte[8 * MiB];
        new Random(5).NextBytes(content.AsSpan(3 * MiB, MiB));
        var source = new FakeFileSystemSource();
        var node = source.AddFile("disk.img", content);
        source.AddNode(node with { SparseExtents = [new SparseExtent(0, 3 * MiB), new SparseExtent(4 * MiB, 4 * MiB)] });

        var (plan, probed) = await PlanAsync(source, 0xC5);

        Assert.AreEqual(8UL * MiB, plan.SpaceEstimateBytes);
        Assert.IsGreaterThanOrEqualTo((ulong)MiB, probed.ReadBytes);
        Assert.IsLessThan((ulong)MiB + (64 * 1024), probed.ReadBytes, "seven of its eight megabytes are holes, which nothing stores");
    }

    [TestMethod]
    public async Task Run_ReadsNoMoreThanThePlanSaid_AndNotMuchLess()
    {
        var source = new FakeFileSystemSource();
        for (var index = 0; index < 6; index++)
        {
            source.AddFile($"docs/{index}.bin", Random(200 * 1024, (byte)(10 + index)));
        }

        source.AddFile("docs/deep/notes.txt", Repeating(50_000));

        var (planned, read) = await RunAndCountAsync(source, 0xC6);

        Assert.IsGreaterThan(0L, read);
        Assert.IsLessThanOrEqualTo(planned, read, "the run read more than its plan said");
        Assert.IsGreaterThan((long)(planned * 0.99), read, "the plan said far more than the run read");
    }

    [TestMethod]
    public async Task Run_OfAFileAcrossManyBlobs_ReadsEachBlobsEnvelopeOnce_AsThePlanSaid()
    {
        // One file, few records of the tree's own, and many blobs: what is
        // read beyond the content is the blobs' envelopes, once each.
        var source = new FakeFileSystemSource();
        source.AddFile("disk.img", Random(3 * MiB, 20));

        var (planned, read) = await RunAndCountAsync(source, 0xC8);

        Assert.IsLessThanOrEqualTo(planned, read, "the run read more than its plan said");
        Assert.IsGreaterThan((long)(planned * 0.997), read, "the plan said more than the run read");
    }

    [TestMethod]
    public async Task Plan_ReadBytes_IsEachRecordOnce_AsTheReaderSizesIt_AndEachBlobsEnvelopeOnce()
    {
        // The figure's definition, held exactly: the runs above show it
        // agrees with what a run reads, and this shows it is the figure the
        // record and the contract name, with no part left out or counted
        // twice where a run's own slack would hide it.
        var content = Random(300_000, 30);
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.bin", content);
        source.AddFile("docs/copy/a.bin", content);
        source.AddFile("docs/notes.txt", Repeating(70_000));

        var (store, keys, catalogue) = await PublishAsync(source, 0xC9);
        using (keys)
        using (catalogue)
        {
            var plan = RestorePlanner.Plan(catalogue, SnapshotOf(0xC9), string.Empty, RestoreTargetProfile.ForLocalPlatform());
            var probed = await RestoreBlobSet.ResolveAsync(catalogue, plan, store, Repo, keys, CancellationToken.None);

            using var reader = new RepositoryReader(Repo, keys, store, Authority);
            reader.UseLocationSource(catalogue.ResolveLocation);
            var records = new HashSet<ObjectId>();
            foreach (var item in plan.Items)
            {
                records.Add(item.ObjectId);
                if (item.Kind != EntryKind.File)
                {
                    continue;
                }

                var manifest = FileVersionManifestCodec.Decode(
                    (await reader.ReadSegmentAsync(item.ObjectId, CancellationToken.None)).Plaintext);
                records.UnionWith(manifest.SegmentReferences.Select(reference => reference.ObjectId));
                records.UnionWith(manifest.Metadata.AlternateStreams.Select(stream => stream.ObjectId));
            }

            var expected = 0UL;
            var blobs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in records)
            {
                var location = catalogue.ResolveLocation(record)!;
                expected += (ulong)RecordFraming.MaxRecordLength(location.StoredLength);
                foreach (var blobClass in new[] { BlobClass.Metadata, BlobClass.Data })
                {
                    var key = BlobStoreKeys.ForBlob(blobClass, location.StoreBlobKey!.Value);
                    var held = await store.GetMetadataAsync(key, CancellationToken.None);
                    if (held.Found && blobs.Add(key.Value))
                    {
                        expected += (ulong)BlobReader.EnvelopeReadLength(held.Metadata!.Length);
                    }
                }
            }

            Assert.IsGreaterThan(1, blobs.Count, "the tree spans several blobs, so an envelope counted per record would show");
            Assert.IsLessThan(records.Count, blobs.Count);
            Assert.AreEqual(expected, probed.ReadBytes);
        }
    }

    /// <summary>
    /// Plans a whole snapshot, then restores it as the service's run does,
    /// from the catalogue's locations and opening no blob, through a store
    /// that counts: what the plan said it would read, and what it read.
    /// </summary>
    private async Task<(long Planned, long Read)> RunAndCountAsync(FakeFileSystemSource source, byte seed)
    {
        var (store, keys, catalogue) = await PublishAsync(source, seed);
        using (keys)
        using (catalogue)
        {
            var plan = RestorePlanner.Plan(catalogue, SnapshotOf(seed), string.Empty, RestoreTargetProfile.ForLocalPlatform());
            var probed = await RestoreBlobSet.ResolveAsync(catalogue, plan, store, Repo, keys, CancellationToken.None);

            var counted = new CountingObjectStore(store);
            using var reader = new RepositoryReader(Repo, keys, counted, Authority);
            reader.UseLocationSource(catalogue.ResolveLocation);
            var receipt = await new RestoreExecutor(reader, RestoreTargetProfile.ForLocalPlatform()).ExecuteAsync(
                plan, Path.Combine(SpoolDirectory, $"restored-{seed:x2}"),
                new RestoreExecutionOptions
                {
                    DestinationMode = RestoreDestinationMode.InPlace,
                    RunId = "reads",
                    NowUnixMilliseconds = 1_722_700_000_000,
                },
                CancellationToken.None);

            Assert.AreEqual(RestoreOutcome.Complete, receipt.Outcome);
            return ((long)probed.ReadBytes, counted.BytesRead);
        }
    }

    [TestMethod]
    public async Task Plan_ContentTheStoreDoesNotHold_IsNotCountedAsRead()
    {
        // A file whose content is gone fails; the run reads nothing of it,
        // so nothing of it is counted.
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.bin", Random(300_000, 3));
        source.AddFile("docs/b.bin", Random(300_000, 4));

        var (store, keys, catalogue) = await PublishAsync(source, 0xC7);
        using (keys)
        using (catalogue)
        {
            var plan = RestorePlanner.Plan(catalogue, SnapshotOf(0xC7), string.Empty, RestoreTargetProfile.ForLocalPlatform());
            var whole = await RestoreBlobSet.ResolveAsync(catalogue, plan, store, Repo, keys, CancellationToken.None);
            Directory.Delete(Path.Combine(StoreRoot, "blobs", "data"), recursive: true);

            var lost = await RestoreBlobSet.ResolveAsync(catalogue, plan, store, Repo, keys, CancellationToken.None);

            Assert.HasCount(2, lost.Missing);
            Assert.IsGreaterThan(600_000UL, whole.ReadBytes);
            Assert.IsLessThan(16_384UL, lost.ReadBytes, "what is left to read is the folders' and files' own records");
        }
    }

    private async Task<(RestorePlan Plan, RestoreBlobSetResult Probed)> PlanAsync(FakeFileSystemSource source, byte seed)
    {
        var (store, keys, catalogue) = await PublishAsync(source, seed);
        using (keys)
        using (catalogue)
        {
            var plan = RestorePlanner.Plan(catalogue, SnapshotOf(seed), string.Empty, RestoreTargetProfile.ForLocalPlatform());
            return (plan, await RestoreBlobSet.ResolveAsync(catalogue, plan, store, Repo, keys, CancellationToken.None));
        }
    }

    private async Task<(LocalFileSystemObjectStore Store, RepositoryKeySet Keys, CatalogueDb Catalogue)> PublishAsync(
        FakeFileSystemSource source, byte seed)
    {
        var store = CreateStore();
        var keys = CreateKeys();
        using var credential = CreateCredential();
        var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, $"reads-{seed:x2}.db"), Repo);

        await new PublicationOrchestrator(
                SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
                new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, "sequence.txt"))),
                SpoolDirectory, FormatVersions.SealedDataPlane, observer: null, catalogue)
            .PublishAsync(Job(source, seed), CancellationToken.None);

        return (store, keys, catalogue);
    }

    private static byte[] SnapshotOf(byte seed) => Enumerable.Repeat(seed, 16).ToArray();

    private static SnapshotJob Job(FakeFileSystemSource source, byte seed) => new()
    {
        Source = source,
        Roots = [new ScanRoot("/")],
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = SnapshotOf(seed),
        NowUnixMilliseconds = 1_722_600_000_000,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "restore-read-tests/1.0",
    };

    /// <summary>A pattern of 256 bytes over and over: content any compressor shrinks.</summary>
    private static byte[] Repeating(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(i * 31);
        }

        return data;
    }

    private static byte[] Random(int length, byte seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }
}
