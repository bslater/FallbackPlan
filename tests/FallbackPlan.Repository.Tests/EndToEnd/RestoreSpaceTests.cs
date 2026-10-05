using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Restore;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A restore knows whether it fits before it writes (FR-RST-003; ADR-0083).
/// What a run needs on each volume it writes to: every file's written bytes in
/// whole clusters, a cluster for each directory it creates, and room for the
/// largest file where the engine holds it while its hash verifies. That is
/// measured against what the platform says is free there.
/// </summary>
/// <remarks>
/// <para>
/// The bytes a file writes are its logical length less its holes, because a
/// sparse file restores sparse (FR-ARCH-013). The catalogue knows only logical
/// lengths, so a run measures from them first and reads manifests only when
/// that says it is short. A run that fits by logical length has its answer
/// without a read.
/// </para>
/// <para>
/// A restore in place over files already there is credited only what the
/// existing-file policy frees. Overwriting frees each file once its
/// replacement lands, so the largest one is in flight on top. Moving aside on
/// the same volume, or keeping both, frees nothing. A file the policy fails
/// is never written.
/// </para>
/// <para>
/// The refusal itself, before anything is written, belongs to the run: the
/// service's and the CLI's tests hold it there.
/// </para>
/// </remarks>
[TestClass]
public sealed class RestoreSpaceTests : ArchiveTestHarness
{
    private const ulong Cluster = 4096;
    private const int MiB = 1024 * 1024;
    private const long Plenty = 1L << 40;

    [TestMethod]
    public async Task ATreeThatFits_NeedsItsFilesInWholeClusters_AClusterPerDirectory_AndRoomForItsLargestFile()
    {
        using var published = await PublishAsync(ThreeFiles(), 0xB1);
        var output = Path.Combine(SpoolDirectory, "out");

        var report = await RestoreSpace.MeasureAsync(
            [new RestoreSlice(published.Plan, output)],
            RestoreDestinationMode.Quarantine,
            ExistingDestinationPolicy.Preserve,
            Probe(Plenty),
            writtenBytes: null,
            CancellationToken.None);

        var need = Assert.ContainsSingle(report.Needs);
        Assert.AreEqual(output, need.Directory);
        Assert.IsFalse(need.Working);

        // 5 000 bytes take two clusters, 100 bytes one, 4 096 bytes one; the
        // directories data and data/sub one each; and the engine's working
        // copy of the largest file shares this volume.
        Assert.AreEqual((2 + 1 + 1) * Cluster + 2 * Cluster + 2 * Cluster, need.NeededBytes);
        Assert.AreEqual(Plenty, need.AvailableBytes);
        Assert.IsFalse(need.IsShort);
        Assert.IsFalse(report.IsShort);
        Assert.IsEmpty(report.Shortfalls);

        // What the files write, unrounded: logical lengths, because nothing
        // here was measured from a manifest.
        Assert.AreEqual(5_000ul + 100ul + 4_096ul, report.WriteBytes);
        Assert.IsFalse(report.Exact);
    }

    [TestMethod]
    public async Task TheEnginesWorkingCopyOnAnotherVolume_IsANeedOfItsOwn_ForTheLargestFile()
    {
        using var published = await PublishAsync(ThreeFiles(), 0xB2);
        var output = Path.Combine(SpoolDirectory, "out");
        var working = Path.Combine(SpoolDirectory, "working");
        Directory.CreateDirectory(working);

        var report = await RestoreSpace.MeasureAsync(
            [new RestoreSlice(published.Plan, output)],
            RestoreDestinationMode.Quarantine,
            ExistingDestinationPolicy.Preserve,
            Probe(Plenty, volumeOf: path => path.StartsWith(working, StringComparison.Ordinal) ? 2ul : 1ul, working),
            writtenBytes: null,
            CancellationToken.None);

        Assert.HasCount(2, report.Needs);
        var target = report.Needs.Single(need => !need.Working);
        Assert.AreEqual(output, target.Directory);
        Assert.AreEqual(4 * Cluster + 2 * Cluster, target.NeededBytes);

        var spool = report.Needs.Single(need => need.Working);
        Assert.AreEqual(working, spool.Directory);
        Assert.AreEqual(2 * Cluster, spool.NeededBytes, "the engine holds one file at a time: the largest");
    }

    [TestMethod]
    public async Task AVolumeWithTooLittleFree_IsShort_AndTheRefusalSaysWhereAndHowMuch()
    {
        using var published = await PublishAsync(ThreeFiles(), 0xB3);
        var output = Path.Combine(SpoolDirectory, "out");

        var report = await RestoreSpace.MeasureAsync(
            [new RestoreSlice(published.Plan, output)],
            RestoreDestinationMode.Quarantine,
            ExistingDestinationPolicy.Preserve,
            Probe(1_000),
            writtenBytes: null,
            CancellationToken.None);

        Assert.IsTrue(report.IsShort);
        var shortfall = Assert.ContainsSingle(report.Shortfalls);
        Assert.IsTrue(shortfall.IsShort);
        Assert.AreEqual(1_000L, shortfall.AvailableBytes);

        // The three facts a person needs to act on it: the space needed, the
        // space free, and which disk.
        var refusal = report.Refusal();
        Assert.Contains(output, refusal, StringComparison.Ordinal);
        Assert.Contains("32768", refusal, StringComparison.Ordinal);
        Assert.Contains("1000", refusal, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task APlatformThatWillNotSayWhatIsFree_IsNeverShort()
    {
        // The check exists to stop a restore filling a disk, never to stop a
        // restore because a platform would not answer — the posture the
        // destination floor took first (FR-DEST-010).
        using var published = await PublishAsync(ThreeFiles(), 0xB4);

        var report = await RestoreSpace.MeasureAsync(
            [new RestoreSlice(published.Plan, Path.Combine(SpoolDirectory, "out"))],
            RestoreDestinationMode.Quarantine,
            ExistingDestinationPolicy.Preserve,
            Probe(null),
            writtenBytes: null,
            CancellationToken.None);

        var need = Assert.ContainsSingle(report.Needs);
        Assert.IsNull(need.AvailableBytes);
        Assert.IsFalse(need.IsShort);
        Assert.IsFalse(report.IsShort);
    }

    [TestMethod]
    public async Task ASparseFile_MeasuredFromItsManifest_NeedsOnlyTheBytesItWrites()
    {
        using var published = await PublishAsync(SparseDisk(), 0xB5);
        var output = Path.Combine(SpoolDirectory, "out");
        RestoreSlice[] slices = [new(published.Plan, output)];

        var byLength = await RestoreSpace.MeasureAsync(
            slices, RestoreDestinationMode.InPlace, ExistingDestinationPolicy.Preserve,
            Probe(Plenty), writtenBytes: null, CancellationToken.None);

        // Eight logical MiB, and the working copy of the same file.
        Assert.AreEqual(8ul * MiB + 8ul * MiB, Assert.ContainsSingle(byLength.Needs).NeededBytes);

        var byManifest = await RestoreSpace.MeasureAsync(
            slices, RestoreDestinationMode.InPlace, ExistingDestinationPolicy.Preserve,
            Probe(Plenty), RestoreSpace.WrittenBytesThrough(published.Reader), CancellationToken.None);

        // One MiB of data between the holes, and its working copy, which
        // keeps the holes too.
        Assert.AreEqual(1ul * MiB + 1ul * MiB, Assert.ContainsSingle(byManifest.Needs).NeededBytes);
        Assert.AreEqual((ulong)MiB, byManifest.WriteBytes);
        Assert.IsTrue(byManifest.Exact);
    }

    [TestMethod]
    public async Task ARunShortByLogicalLength_IsMeasuredAgainFromItsManifests_AndFitsWhenItsBytesDo()
    {
        using var published = await PublishAsync(SparseDisk(), 0xB6);

        var report = await RestoreSpace.MeasureRunAsync(
            [new RestoreSlice(published.Plan, Path.Combine(SpoolDirectory, "out"))],
            RestoreDestinationMode.InPlace,
            ExistingDestinationPolicy.Preserve,
            Probe(4L * MiB),
            published.Reader,
            CancellationToken.None);

        Assert.IsTrue(report.Exact, "sixteen logical MiB against four free must be looked at again");
        Assert.IsFalse(report.IsShort);
    }

    [TestMethod]
    public async Task ARunShortByItsWrittenBytesToo_IsShort()
    {
        using var published = await PublishAsync(SparseDisk(), 0xB7);

        var report = await RestoreSpace.MeasureRunAsync(
            [new RestoreSlice(published.Plan, Path.Combine(SpoolDirectory, "out"))],
            RestoreDestinationMode.InPlace,
            ExistingDestinationPolicy.Preserve,
            Probe(MiB),
            published.Reader,
            CancellationToken.None);

        Assert.IsTrue(report.Exact);
        Assert.IsTrue(report.IsShort);
    }

    [TestMethod]
    public async Task ARunThatFitsByLogicalLength_IsDecidedWithoutReadingAManifest()
    {
        using var published = await PublishAsync(SparseDisk(), 0xB8);

        var report = await RestoreSpace.MeasureRunAsync(
            [new RestoreSlice(published.Plan, Path.Combine(SpoolDirectory, "out"))],
            RestoreDestinationMode.InPlace,
            ExistingDestinationPolicy.Preserve,
            Probe(Plenty),
            published.Reader,
            CancellationToken.None);

        Assert.IsFalse(report.Exact);
        Assert.IsFalse(report.IsShort);
    }

    [TestMethod]
    public async Task InPlace_OverFilesAlreadyThere_IsCreditedOnlyWhatTheExistingFilePolicyFrees()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("one.bin", Deterministic(MiB, 1));
        source.AddFile("two.bin", Deterministic(MiB, 2));
        source.AddFile("three.bin", Deterministic(MiB, 3));
        source.AddFile("small.bin", Deterministic(100, 4));
        using var published = await PublishAsync(source, 0xB9);

        var live = Path.Combine(SpoolDirectory, "live");
        Directory.CreateDirectory(live);
        foreach (var name in new[] { "one.bin", "two.bin", "three.bin" })
        {
            await File.WriteAllBytesAsync(Path.Combine(live, name), new byte[MiB]);
        }

        async Task<ulong> NeedAsync(RestoreDestinationMode mode, ExistingDestinationPolicy policy) =>
            Assert.ContainsSingle((await RestoreSpace.MeasureAsync(
                [new RestoreSlice(published.Plan, live)], mode, policy,
                Probe(Plenty), writtenBytes: null, CancellationToken.None)).Needs).NeededBytes;

        // Moved aside on the same volume, or kept beside: every restored byte
        // is new, plus the working copy of the largest file.
        Assert.AreEqual(3ul * MiB + Cluster + MiB, await NeedAsync(RestoreDestinationMode.InPlace, ExistingDestinationPolicy.Preserve));
        Assert.AreEqual(3ul * MiB + Cluster + MiB, await NeedAsync(RestoreDestinationMode.InPlace, ExistingDestinationPolicy.WriteBeside));

        // Overwritten: the three replace files their own size, so only the
        // small one grows the volume — but each new file lands before its old
        // one goes, so the largest is in flight on top of the working copy.
        Assert.AreEqual(Cluster + MiB + MiB, await NeedAsync(RestoreDestinationMode.InPlace, ExistingDestinationPolicy.Replace));

        // Failed: the three are never written, so the largest file written is
        // the small one.
        Assert.AreEqual(Cluster + Cluster, await NeedAsync(RestoreDestinationMode.InPlace, ExistingDestinationPolicy.Fail));

        // Quarantined: the run lands in a directory of its own, so nothing
        // there is replaced whatever the policy says.
        Assert.AreEqual(3ul * MiB + Cluster + MiB, await NeedAsync(RestoreDestinationMode.Quarantine, ExistingDestinationPolicy.Replace));
    }

    [TestMethod]
    public async Task TwoSlices_OnOneVolume_AddUp_AndOnTwoVolumes_AreMeasuredApart()
    {
        // An original-location restore of a set with two roots runs one slice
        // per root. On one volume they share its free space, so they are
        // measured together, or each would fit and both would not.
        using var published = await PublishAsync(ThreeFiles(), 0xBA);
        var first = Path.Combine(SpoolDirectory, "root-a");
        var second = Path.Combine(SpoolDirectory, "root-b");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        RestoreSlice[] slices = [new(published.Plan, first), new(published.Plan, second)];

        var shared = await RestoreSpace.MeasureAsync(
            slices, RestoreDestinationMode.Quarantine, ExistingDestinationPolicy.Preserve,
            Probe(Plenty), writtenBytes: null, CancellationToken.None);

        var together = Assert.ContainsSingle(shared.Needs);
        Assert.AreEqual(first, together.Directory);
        Assert.AreEqual(2 * (6 * Cluster) + 2 * Cluster, together.NeededBytes);

        var apart = await RestoreSpace.MeasureAsync(
            slices, RestoreDestinationMode.Quarantine, ExistingDestinationPolicy.Preserve,
            Probe(Plenty, volumeOf: path => path.StartsWith(second, StringComparison.Ordinal) ? 2ul : 1ul),
            writtenBytes: null, CancellationToken.None);

        Assert.HasCount(2, apart.Needs);
        Assert.AreEqual(6 * Cluster + 2 * Cluster, apart.Needs.Single(need => need.Directory == first).NeededBytes);
        Assert.AreEqual(6 * Cluster, apart.Needs.Single(need => need.Directory == second).NeededBytes);
    }

    private static RestoreSpaceProbe Probe(long? available, Func<string, ulong?>? volumeOf = null, string? working = null) => new()
    {
        AvailableBytes = _ => available,
        VolumeOf = volumeOf ?? (_ => 1),
        WorkingDirectory = working ?? Path.GetTempPath(),
    };

    private static FakeFileSystemSource ThreeFiles()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("data/a.bin", Deterministic(5_000, 1));
        source.AddFile("data/b.bin", Deterministic(100, 2));
        source.AddFile("data/sub/c.bin", Deterministic(4_096, 3));
        return source;
    }

    /// <summary>Eight logical MiB holding one MiB of data, between two holes.</summary>
    private static FakeFileSystemSource SparseDisk()
    {
        var content = new byte[8 * MiB];
        new Random(5).NextBytes(content.AsSpan(3 * MiB, MiB));

        var source = new FakeFileSystemSource();
        var node = source.AddFile("disk.img", content);
        source.AddNode(node with { SparseExtents = [new SparseExtent(0, 3 * MiB), new SparseExtent(4 * MiB, 4 * MiB)] });
        return source;
    }

    private static byte[] Deterministic(int length, byte seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + i * 31);
        }

        return data;
    }

    private static SnapshotJob Job(FakeFileSystemSource source, byte seed) => new()
    {
        Source = source,
        Roots = [new ScanRoot("/")],
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = Enumerable.Repeat(seed, 16).ToArray(),
        NowUnixMilliseconds = 1_722_600_000_000,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "restore-space-tests/1.0",
    };

    private async Task<Published> PublishAsync(FakeFileSystemSource source, byte seed)
    {
        var store = CreateStore();
        var keys = CreateKeys();
        using var credential = CreateCredential();
        using var catalogue = CatalogueDb.Open(Path.Combine(SpoolDirectory, $"space-{seed:x2}.db"), Repo);

        await new PublicationOrchestrator(
                SmallBlobPolicy, Repo, Writer, KeyGeneration.Zero, keys, credential, store,
                new WriterSequence(new FileSequenceStateStore(Path.Combine(SpoolDirectory, $"sequence-{seed:x2}.txt"))),
                SpoolDirectory, FormatVersions.SealedDataPlane, observer: null, catalogue)
            .PublishAsync(Job(source, seed), CancellationToken.None);

        var plan = RestorePlanner.Plan(
            catalogue, Enumerable.Repeat(seed, 16).ToArray(), string.Empty, RestoreTargetProfile.ForLocalPlatform());

        var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        return new Published(plan, reader, keys);
    }

    private sealed record Published(RestorePlan Plan, RepositoryReader Reader, RepositoryKeySet Keys) : IDisposable
    {
        public void Dispose()
        {
            Reader.Dispose();
            Keys.Dispose();
        }
    }
}
