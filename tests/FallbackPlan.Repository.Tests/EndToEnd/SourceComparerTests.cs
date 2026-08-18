using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Filesystem;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.TestSupport;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The source comparer (FR-SVC-009): a dry scan against a published
/// snapshot classifies every file the way the next backup would decide it —
/// new, updated, metadata-only, moved, deleted — and keeps "the disk lost
/// it" apart from "the rules stopped capturing it", because a filter edit
/// flagged as deletion would be a false alarm and a deletion flagged as a
/// filter edit would be a missed one.
/// </summary>
[TestClass]
public sealed class SourceComparerTests : ArchiveTestHarness
{
    private static readonly byte[] MasterKey = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    private CatalogueDb OpenCatalogue() =>
        CatalogueDb.Open(Path.Combine(SpoolDirectory, "catalogue.db"), Repo);

    private PublicationOrchestrator CreateOrchestrator(
        Storage.Abstractions.IObjectStore store, RepositoryKeySet keys, KeyHierarchy hierarchy, CatalogueDb catalogue) =>
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
            catalogue);

    private static SnapshotJob Job(FakeFileSystemSource source) => new()
    {
        Source = source,
        RootPath = "/",
        DeviceId = Enumerable.Repeat((byte)0x22, 16).ToArray(),
        BackupSetId = Enumerable.Repeat((byte)0x33, 16).ToArray(),
        SnapshotId = Enumerable.Repeat((byte)0xB1, 16).ToArray(),
        NowUnixMilliseconds = 1_722_600_000_000,
        DeclaredMaxDurationMs = 3_600_000,
        ExpiryGeneration = 5,
        ClientVersion = "fallbackplan-tests/1.0",
    };

    private static byte[] Bytes(int length, byte seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + i * 31);
        }

        return data;
    }

    /// <summary>Publishes the fake tree and answers the baseline snapshot id.</summary>
    private async Task<ReadOnlyMemory<byte>> PublishBaselineAsync(FakeFileSystemSource source, CatalogueDb catalogue)
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = new KeyHierarchy(MasterKey);
        await CreateOrchestrator(store, keys, hierarchy, catalogue).PublishAsync(Job(source), CancellationToken.None);
        return Assert.ContainsSingle(catalogue.EnumerateSnapshots()).SnapshotId;
    }

    private static ValueTask<SourceComparison> CompareAsync(
        FakeFileSystemSource source,
        CatalogueDb? catalogue,
        ReadOnlyMemory<byte>? baseline,
        IReadOnlyList<string>? includes = null,
        IReadOnlyList<string>? excludes = null,
        int sampleLimit = 20) =>
        SourceComparer.CompareAsync(
            source, "/", includes ?? [], excludes ?? [], catalogue, baseline, sampleLimit, CancellationToken.None);

    [TestMethod]
    public async Task Compare_EveryKindOfChangeAtOnce_LandsEachInItsOwnBucket()
    {
        var source = new FakeFileSystemSource();
        var untouched = source.AddFile("docs/kept.txt", Bytes(400, 1));
        var toEdit = source.AddFile("docs/edited.txt", Bytes(500, 2));
        var toRestate = source.AddFile("docs/restated.txt", Bytes(600, 3));
        var toMove = source.AddFile("docs/old-name.txt", Bytes(700, 4));
        source.AddFile("docs/removed.txt", Bytes(800, 5));

        using var catalogue = OpenCatalogue();
        var baseline = await PublishBaselineAsync(source, catalogue);

        // The user's week of activity: an edit, a chmod, a rename, a delete,
        // and one brand-new file.
        toEdit.Content = Bytes(501, 9);
        toEdit.Metadata = toEdit.Metadata with { ModifiedAt = toEdit.Metadata.ModifiedAt + 1_000 };
        toRestate.Metadata = toRestate.Metadata with { PosixMode = 0x1FF };
        Assert.IsTrue(source.Remove("docs/old-name.txt"));
        source.AddNode(toMove with { RelativePath = "docs/new-name.txt" });
        Assert.IsTrue(source.Remove("docs/removed.txt"));
        source.AddFile("docs/brand-new.txt", Bytes(900, 6));
        _ = untouched;

        var comparison = await CompareAsync(source, catalogue, baseline);

        Assert.AreEqual(1, comparison.Unchanged);
        Assert.AreEqual(0, comparison.Failures);
        Assert.AreEqual("docs/brand-new.txt", Assert.ContainsSingle(comparison.New.Sample));
        Assert.AreEqual("docs/edited.txt", Assert.ContainsSingle(comparison.Updated.Sample));
        Assert.AreEqual("docs/restated.txt", Assert.ContainsSingle(comparison.MetadataOnly.Sample));
        Assert.AreEqual("docs/new-name.txt", Assert.ContainsSingle(comparison.Moved.Sample));
        Assert.AreEqual("docs/removed.txt", Assert.ContainsSingle(comparison.Deleted.Sample));
        Assert.AreEqual(0, comparison.NoLongerIncluded.Count);
    }

    [TestMethod]
    public async Task Compare_AMovedFile_IsNotAlsoCountedDeleted()
    {
        var source = new FakeFileSystemSource();
        var file = source.AddFile("a/original.bin", Bytes(300, 1));

        using var catalogue = OpenCatalogue();
        var baseline = await PublishBaselineAsync(source, catalogue);

        Assert.IsTrue(source.Remove("a/original.bin"));
        source.AddNode(file with { RelativePath = "b/relocated.bin" });

        var comparison = await CompareAsync(source, catalogue, baseline);

        Assert.AreEqual(1, comparison.Moved.Count);
        Assert.AreEqual(0, comparison.Deleted.Count);
        Assert.AreEqual(0, comparison.New.Count);
    }

    [TestMethod]
    public async Task Compare_ARuleNowExcludesACapturedFile_ReportsItAsNoLongerIncludedNotDeleted()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/report.txt", Bytes(400, 1));
        source.AddFile("photos/beach.jpg", Bytes(500, 2));
        source.AddFile("photos/sunset.jpg", Bytes(600, 3));

        using var catalogue = OpenCatalogue();
        var baseline = await PublishBaselineAsync(source, catalogue);

        // The operator excludes the photos — every file is still on disk.
        var comparison = await CompareAsync(source, catalogue, baseline, excludes: ["photos"]);

        Assert.AreEqual(1, comparison.Unchanged);
        Assert.AreEqual(0, comparison.Deleted.Count, "nothing left the disk");
        Assert.AreEqual(2, comparison.NoLongerIncluded.Count);
        SequenceAssert.AreEqual(
            ["photos/beach.jpg", "photos/sunset.jpg"],
            comparison.NoLongerIncluded.Sample.Order(StringComparer.Ordinal));

        // And the two findings compose: one photo also deleted from disk
        // still reads "no longer included" — the rules answer first, because
        // under the new configuration its absence is not a loss the next
        // backup would even see.
        Assert.IsTrue(source.Remove("photos/beach.jpg"));
        var again = await CompareAsync(source, catalogue, baseline, excludes: ["photos"]);
        Assert.AreEqual(2, again.NoLongerIncluded.Count);
        Assert.AreEqual(0, again.Deleted.Count);
    }

    [TestMethod]
    public async Task Compare_AnIncludeRuleNarrows_EverythingOutsideItReadsNoLongerIncluded()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/keep.txt", Bytes(400, 1));
        source.AddFile("music/song.mp3", Bytes(500, 2));

        using var catalogue = OpenCatalogue();
        var baseline = await PublishBaselineAsync(source, catalogue);

        var comparison = await CompareAsync(source, catalogue, baseline, includes: ["docs/**"]);

        Assert.AreEqual(1, comparison.Unchanged);
        Assert.AreEqual("music/song.mp3", Assert.ContainsSingle(comparison.NoLongerIncluded.Sample));
        Assert.AreEqual(0, comparison.Deleted.Count);
    }

    [TestMethod]
    public async Task Compare_NoBaselineSnapshot_CountsEverythingNew()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("one.txt", Bytes(100, 1));
        source.AddFile("two.txt", Bytes(200, 2));

        var comparison = await CompareAsync(source, catalogue: null, baseline: null);

        Assert.AreEqual(2, comparison.New.Count);
        Assert.AreEqual(0, comparison.Unchanged);
        Assert.AreEqual(0, comparison.Deleted.Count);
    }

    [TestMethod]
    public async Task Compare_MoreChangesThanTheSampleCap_CountsExactlyAndSamplesTheCap()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("kept.txt", Bytes(100, 1));

        using var catalogue = OpenCatalogue();
        var baseline = await PublishBaselineAsync(source, catalogue);

        for (var i = 0; i < 5; i++)
        {
            source.AddFile($"new-{i}.txt", Bytes(100 + i, (byte)(10 + i)));
        }

        var comparison = await CompareAsync(source, catalogue, baseline, sampleLimit: 2);

        Assert.AreEqual(5, comparison.New.Count, "the count is exact past the cap");
        Assert.HasCount(2, comparison.New.Sample);
    }

    [TestMethod]
    public async Task Compare_TheRulesAreInvalid_IsRefusedLikeAWriterWouldRefuseThem()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("file.txt", Bytes(100, 1));

        var refusal = await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await CompareAsync(source, catalogue: null, baseline: null, includes: ["a**b"]));
        Assert.Contains("a**b", refusal.Message);
    }

    [TestMethod]
    public async Task Compare_TheWalkReportsFailures_CountsThemWithoutClassifyingThem()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("readable.txt", Bytes(100, 1));
        source.InjectedFailures.Add(new ScanFailure(
            "secret/locked.txt", CaptureFailureReason.Permission, "permission denied"));

        using var catalogue = OpenCatalogue();
        var baseline = await PublishBaselineAsync(source, catalogue);

        var comparison = await CompareAsync(source, catalogue, baseline);

        Assert.AreEqual(1, comparison.Failures);
        Assert.AreEqual(1, comparison.Unchanged);
    }

    [TestMethod]
    public async Task Compare_ACaseInsensitiveSource_MatchesARecasedPathAsTheSameFile()
    {
        var source = new FakeFileSystemSource { Info = new(
            CaseSensitive: false, SupportsSparse: true, Name: "fakefs",
            MaxPathBytes: 4096, MaxComponentBytes: 255, ReservedNames: false) };
        var file = source.AddFile("Documents/Readme.md", Bytes(300, 1));

        using var catalogue = OpenCatalogue();
        var baseline = await PublishBaselineAsync(source, catalogue);

        // The same file, the same identity, a re-cased spelling — on a
        // case-insensitive filesystem that is nothing at all.
        Assert.IsTrue(source.Remove("Documents/Readme.md"));
        source.AddNode(file with { RelativePath = "documents/readme.md" });

        var comparison = await CompareAsync(source, catalogue, baseline);

        Assert.AreEqual(1, comparison.Unchanged);
        Assert.AreEqual(0, comparison.New.Count);
        Assert.AreEqual(0, comparison.Deleted.Count);
        Assert.AreEqual(0, comparison.Moved.Count);
    }

    [TestMethod]
    public async Task Compare_ADryScan_OpensNoContent()
    {
        var source = new FakeFileSystemSource();
        source.AddFile("docs/a.txt", Bytes(400, 1));
        source.AddFile("docs/b.txt", Bytes(500, 2));

        using var catalogue = OpenCatalogue();
        var baseline = await PublishBaselineAsync(source, catalogue);
        source.OpenedPaths.Clear();

        await CompareAsync(source, catalogue, baseline);

        Assert.IsEmpty(source.OpenedPaths, "a preview reads names and stats, never bytes");
    }
}
