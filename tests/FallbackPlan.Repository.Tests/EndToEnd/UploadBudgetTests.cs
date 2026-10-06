using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;
using FallbackPlan.Filesystem;
using Counting = FallbackPlan.TestSupport.CountingObjectStore;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// NFR-PERF-008's request budget, measured: at most 10 PUTs per GB written for
/// data blobs and 20 requests per GB in all, at the object-store blob profile.
/// Every request a backup makes is counted by the kind of object it was for,
/// for a first backup and for an incremental, and the per-GiB figure is built
/// from what was measured. What keeps the figure down is NFR-PERF-006's
/// packing: segments into blobs, and a backup's hints into one pack.
/// </summary>
/// <remarks>
/// <para>
/// A test cannot write a gibibyte, and a small backup's per-GiB figure would be
/// all fixed cost, so the terms are measured apart and then added up. Writing
/// G for one GiB, s for a backup's average file size and T for the profile's
/// blob target:
/// </para>
/// <code>
///   data blobs       ⌈G / T⌉                         the profile; sealing at T is the blob writer's
///   metadata blobs   the larger of the bytes and the records the measured manifests need, over T
///   blob covers      one journal extension per blob   measured: 08 §4's intent extension
///   per backup       measured: the intent, its retirement, the index delta, the snapshot, the hints
///   per file         measured: what a backup of more, smaller files costs beyond the same bytes in fewer
/// </code>
/// <para>
/// The per-file term is what this suite exists to hold at zero. One
/// source-identity hint per created file version (06 §11 before ADR-0090) was
/// a request per file, which put the 4.48 GB, 9 190-file first backup that
/// showed the cost (ADR-0088) at about 2 000 requests per GB from hints alone.
/// One pack per backup makes it a per-backup term.
/// </para>
/// <para>
/// The blob covers are named rather than absorbed. Every blob is preceded by
/// the intent extension that covers it, each its own journal record, so blobs
/// cost two requests each: at the object-store target that is 8 data blobs
/// and their 8 covers before anything else, and the total stays over 20 until
/// a cover stops costing a request of its own. That is GC-safety machinery
/// (ADR-0009, 08 §4) and is owed separately; this suite pins the term — one
/// cover a blob — and holds everything beside it to the 20.
/// </para>
/// </remarks>
[TestClass]
public sealed class UploadBudgetTests : ArchiveTestHarness
{
    private const int KiB = 1024;
    private const long GiB = 1024L * 1024 * 1024;

    /// <summary>The 4.48 GB first backup in 9 190 files the per-file cost was found in.</summary>
    private const long MeasuredAverageFileBytes = 4_480_000_000L / 9_190;

    /// <summary>A tree of many small files: documents, source trees, mail stores.</summary>
    private const long SmallAverageFileBytes = 16 * KiB;

    private static CapturePolicy ObjectStorePolicy { get; } = CapturePolicy.Default with
    {
        BlobWriteProfile = BlobWriteProfile.ObjectStoreDefault,
    };

    private sealed record Measured(
        int Files,
        long ContentBytes,
        long Requests,
        long BlobPuts,
        long JournalPuts,
        long HintPuts,
        long MetadataBytes,
        long MetadataRecords)
    {
        /// <summary>Every request that was not a blob's own upload.</summary>
        public long BesideBlobs => Requests - BlobPuts;
    }

    private static byte[] Incompressible(int seed, int length)
    {
        var content = new byte[length];
        new Random(seed).NextBytes(content);
        return content;
    }

    private static SnapshotJob Job(
        FakeFileSystemSource source, byte snapshotSeed, ulong now, ReadOnlyMemory<byte>? prior = null) => new()
    {
        Source = source,
        Roots = [new ScanRoot("/")],
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = Enumerable.Repeat(snapshotSeed, 16).ToArray(),
        PriorSnapshotId = prior,
        NowUnixMilliseconds = now,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "fallbackplan-tests/1.0",
    };

    private PublicationOrchestrator CreateOrchestrator(
        IObjectStore store, RepositoryKeySet keys, RepositoryWriteCredential credential, CatalogueDb catalogue, string name) =>
        new(
            ObjectStorePolicy,
            Repo,
            Writer,
            KeyGeneration.Zero,
            keys,
            credential,
            store,
            new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, $"{name}-sequence.txt"))),
            SpoolDirectory,
            FormatVersions.SealedDataPlane,
            observer: null,
            catalogue);

    private static Measured Tally(Counting counting, PublishedTreeSnapshot published, int files, long contentBytes)
    {
        var puts = counting.PutKeys;
        return new Measured(
            files,
            contentBytes,
            Requests: counting.Puts + counting.Reads + counting.Listings.Count + counting.MetadataReads + counting.Deletes,
            BlobPuts: puts.Count(key => key.StartsWith("blobs/", StringComparison.Ordinal)),
            JournalPuts: puts.Count(key => key.StartsWith("journal/", StringComparison.Ordinal)),
            HintPuts: puts.Count(key => key.StartsWith("hints/", StringComparison.Ordinal)),
            MetadataBytes: published.MetadataBlobs.Sum(blob => blob.Length),
            MetadataRecords: published.MetadataBlobs.Sum(blob => (long)blob.RecordCount));
    }

    private static FakeFileSystemSource Tree(int files, int fileBytes, int seed)
    {
        var source = new FakeFileSystemSource();
        for (var index = 0; index < files; index++)
        {
            source.AddFile($"data/{index:d4}.bin", Incompressible(seed + index, fileBytes), fileId: (ulong)(10_000 + index));
        }

        return source;
    }

    private async Task<Measured> MeasureFirstBackupAsync(string name, int files, int fileBytes)
    {
        var counting = new Counting(new LocalFileSystemObjectStore(Path.Combine(StoreRoot, name)));
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, $"{name}.db"), Repo);

        var published = await CreateOrchestrator(counting, keys, credential, catalogue, name)
            .PublishAsync(Job(Tree(files, fileBytes, seed: files), 0xA1, now: 1_722_600_000_000), CancellationToken.None);

        return Tally(counting, published, files, (long)files * fileBytes);
    }

    /// <summary>
    /// The requests per GiB of content written, in files of
    /// <paramref name="averageFileBytes"/>, built from the measured terms (see
    /// the class remarks).
    /// </summary>
    private static (double Data, double Covers, double Total) PerGiB(
        Measured few, Measured many, long averageFileBytes)
    {
        var profile = ObjectStorePolicy.BlobWriteProfile;
        var files = (double)GiB / averageFileBytes;

        // Per file: what more, smaller files cost beyond the same bytes in
        // fewer. Per backup: everything beside the blobs, less their covers —
        // the journal holds the intent, its retirement and an extension a
        // blob — and less the per-file term.
        var perFile = (double)(many.BesideBlobs - few.BesideBlobs) / (many.Files - few.Files);
        var perBackup = few.BesideBlobs - (few.JournalPuts - 2) - (perFile * few.Files);

        var data = Math.Ceiling((double)GiB / profile.TargetSizeBytes);
        var metadata = Math.Max(
            1,
            Math.Max(
                Math.Ceiling(files * many.MetadataBytes / many.Files / profile.TargetSizeBytes),
                Math.Ceiling(files * many.MetadataRecords / many.Files / profile.MaximumRecordCount)));
        var covers = data + metadata;

        return (data, covers, data + metadata + covers + perBackup + (perFile * files));
    }

    [TestMethod]
    public async Task AFirstBackup_RequestsBesideItsBlobs_DoNotGrowWithItsFiles_AndItsGiBFitsTheBudget()
    {
        // The same 4 MiB as 64 files and as 1 024: one data blob and one
        // metadata blob either way, so every difference is per file.
        var few = await MeasureFirstBackupAsync("few", files: 64, fileBytes: 64 * KiB);
        var many = await MeasureFirstBackupAsync("many", files: 1_024, fileBytes: 4 * KiB);

        Assert.AreEqual(few.BlobPuts, many.BlobPuts, "the fixture's premise: the same bytes fill the same blobs");
        Assert.AreEqual(
            few.BesideBlobs,
            many.BesideBlobs,
            $"64 files cost {few.BesideBlobs} requests beside their blobs and 1 024 cost {many.BesideBlobs}; "
            + $"hints {few.HintPuts} and {many.HintPuts}");

        // The term that remains: one journal extension covers each blob.
        Assert.AreEqual(few.BlobPuts + 2, few.JournalPuts, "a blob is covered by one intent extension of its own");

        foreach (var averageFileBytes in new[] { MeasuredAverageFileBytes, SmallAverageFileBytes })
        {
            var (data, covers, total) = PerGiB(few, many, averageFileBytes);
            Assert.IsLessThanOrEqualTo(10, data, $"data PUTs per GiB at {averageFileBytes} bytes a file");
            Assert.IsLessThanOrEqualTo(
                20,
                total - covers,
                $"{total:F1} requests per GiB at {averageFileBytes} bytes a file, {covers} of them blob covers");
        }
    }

    [TestMethod]
    public async Task AnIncremental_RequestsBesideItsBlobs_DoNotGrowWithTheFilesItChanges()
    {
        var inner = new LocalFileSystemObjectStore(Path.Combine(StoreRoot, "incremental"));
        var counting = new Counting(inner);
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, "incremental.db"), Repo);
        var orchestrator = CreateOrchestrator(counting, keys, credential, catalogue, "incremental");

        var source = Tree(1_024, 4 * KiB, seed: 1);
        await orchestrator.PublishAsync(Job(source, 0xB1, now: 1_722_600_000_000), CancellationToken.None);

        async Task<Measured> ChangeAsync(int files, byte snapshot, byte prior, int seed)
        {
            for (var index = 0; index < files; index++)
            {
                source.AddFile($"data/{index:d4}.bin", Incompressible(seed + index, 4 * KiB), fileId: (ulong)(10_000 + index))
                    .Metadata = EntryMetadata.Empty with { ModifiedAt = 1_722_600_000_000ul + snapshot };
            }

            counting.Reset();
            var published = await orchestrator.PublishAsync(
                Job(source, snapshot, now: 1_722_600_000_000ul + snapshot, prior: Enumerable.Repeat(prior, 16).ToArray()),
                CancellationToken.None);
            return Tally(counting, published, files, files * 4L * KiB);
        }

        // Sixteen changed files, then two hundred and fifty-six: each
        // incremental's requests beside its blobs are the same fixed handful.
        var few = await ChangeAsync(16, snapshot: 0xB2, prior: 0xB1, seed: 5_000);
        var many = await ChangeAsync(256, snapshot: 0xB3, prior: 0xB2, seed: 7_000);

        Assert.AreEqual(few.BlobPuts, many.BlobPuts, "the fixture's premise: both incrementals fill one blob of each class");
        Assert.AreEqual(
            few.BesideBlobs,
            many.BesideBlobs,
            $"16 changed files cost {few.BesideBlobs} requests beside their blobs and 256 cost {many.BesideBlobs}; "
            + $"hints {few.HintPuts} and {many.HintPuts}");
        Assert.AreEqual(few.BlobPuts + 2, few.JournalPuts, "a blob is covered by one intent extension of its own");

        foreach (var averageFileBytes in new[] { MeasuredAverageFileBytes, SmallAverageFileBytes })
        {
            var (data, covers, total) = PerGiB(few, many, averageFileBytes);
            Assert.IsLessThanOrEqualTo(10, data, $"data PUTs per GiB changed at {averageFileBytes} bytes a file");
            Assert.IsLessThanOrEqualTo(
                20,
                total - covers,
                $"{total:F1} requests per GiB changed at {averageFileBytes} bytes a file, {covers} of them blob covers");
        }
    }
}
