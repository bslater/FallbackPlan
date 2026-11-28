using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Domain.Profiles;
using FallbackPlan.Repository.Catalogue.Forensic;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Index;
using FallbackPlan.Retention;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A large file's reference runs are part of its version wherever its content
/// is (ADR-0202 decision 4; FR-ARCH-010): the collector's mark keeps every run
/// a kept version lists and every segment the runs name, and stops the sweep
/// when a run will not read (FR-GC-002), and a catalogue projected from the
/// repository or rebuilt from blobs alone traces a segment in a run, and the
/// run itself, back to the file and its snapshot (FR-VER-005).
/// </summary>
/// <remarks>
/// The snapshot is <see cref="ReferenceRunHarness"/>'s, built by hand: no
/// writer in this build writes runs.
/// </remarks>
[TestClass]
public sealed class ReferenceRunReachTests : ReferenceRunHarness
{
    private static SurveyedSnapshot Surveyed(Built built) => new(
        new SnapshotFact(Convert.ToHexStringLower(SnapshotId), built.Snapshot.CaptureCompletedAt),
        built.Snapshot,
        ObjectKey.Parse("snapshots/hand-built"),
        ObjectId.FromBytes(Enumerable.Repeat((byte)0x77, 32).ToArray()));

    private CatalogueDb OpenCatalogue(string name) =>
        CatalogueDb.Open(Path.Combine(SpoolDirectory, name + ".db"), Repo);

    /// <summary>
    /// Locates every record the snapshot's blobs hold in <paramref name="catalogue"/>,
    /// as the index of a capture would: a projection walks manifests and leaves
    /// where each object lives to the index, and this snapshot has none.
    /// </summary>
    private static void Locate(CatalogueDb catalogue, RepositoryReader reader, Built built, params ObjectId[] objects)
    {
        List<IndexEntry> entries = [];
        foreach (var objectId in objects)
        {
            Assert.IsTrue(reader.TryLocateRecord(objectId, out var key, out var record), $"{objectId} is in no blob");
            var blob = built.Blobs.Single(candidate => candidate.StoreKey.Equals(key));
            entries.Add(new IndexEntry(
                objectId, blob.BlobId, record.PhysicalOffset, record.StoredLength,
                record.CompressionProfileValue, record.EncryptionProfileValue, IndexEntryType.Insertion));
        }

        catalogue.ApplyDelta(
            DeltaId.FromBytes(Enumerable.Repeat((byte)0x44, 16).ToArray()),
            new IndexDelta { WriterId = Writer, Sequence = 1, Generation = 0, Entries = entries });
    }

    private static void AssertReaches(CatalogueDb catalogue, RepositoryKeySet keys, ArchivedBlob damaged, string what)
    {
        var reach = DamageScope.Trace(catalogue, keys, [damaged.StoreKey.Value]);

        Assert.AreEqual(Convert.ToHexStringLower(SnapshotId), Assert.ContainsSingle(reach.Snapshots), what);
        Assert.AreEqual("huge.bin", Assert.ContainsSingle(reach.FileSample), what);
    }

    [TestMethod]
    public async Task TheCollectorsMark_KeepsEveryRunAKeptVersionLists_AndEverySegmentTheRunsName()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        var built = await BuildAsync(store, keys);
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var (reachable, unwalkable) = await StagingMark.MarkAsync(reader, [Surveyed(built)], CancellationToken.None);

        Assert.IsEmpty(unwalkable, string.Join("; ", unwalkable));
        foreach (var kept in new[] { built.Version, built.FirstRun, built.SecondRun, built.FirstSegment, built.SecondSegment })
        {
            Assert.Contains(kept, reachable);
        }
    }

    [TestMethod]
    public async Task TheCollectorsMark_OverARunThatWillNotRead_IsUnwalkable_SoTheSweepDeletesNothing()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        var built = await BuildAsync(store, keys, writeSecondRun: false);
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var (reachable, unwalkable) = await StagingMark.MarkAsync(reader, [Surveyed(built)], CancellationToken.None);

        var reason = Assert.ContainsSingle(unwalkable);
        Assert.Contains(built.SecondRun.ToString(), reason, StringComparison.Ordinal);
        Assert.Contains(built.FirstSegment, reachable, "what the runs before it name is still kept");
    }

    [TestMethod]
    public async Task TheCollectorsMark_OverARunThatDoesNotFollowTheOneBefore_IsUnwalkable()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        var built = await BuildAsync(store, keys, gapBeforeSecondRun: true);
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);

        var (_, unwalkable) = await StagingMark.MarkAsync(reader, [Surveyed(built)], CancellationToken.None);

        var reason = Assert.ContainsSingle(unwalkable);
        Assert.StartsWith(built.Version.ToString(), reason, StringComparison.Ordinal);
        Assert.Contains("run 1", reason, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ACatalogueProjectedFromTheRepository_TracesARunsSegments_AndTheRun_ToTheFile()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        var built = await BuildAsync(store, keys);
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        using var catalogue = OpenCatalogue("projected");
        Locate(catalogue, reader, built, built.FirstSegment, built.SecondSegment, built.FirstRun, built.SecondRun);

        var report = await CatalogueProjector.ProjectAsync(catalogue, reader, store, Repo, keys, credential, CancellationToken.None);

        Assert.AreEqual(1, report.FileVersions);
        Assert.AreEqual(0, report.Missing);
        AssertReaches(catalogue, keys, built.SecondData, "the segment the second run names");
        AssertReaches(catalogue, keys, built.FirstData, "the segment the first run names");
        AssertReaches(catalogue, keys, built.Runs, "the runs themselves");
    }

    [TestMethod]
    public async Task ACatalogueProjectedOverARunThatWillNotRead_RecordsItMissing_AndStillTracesTheRunsBeforeIt()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        var built = await BuildAsync(store, keys, writeSecondRun: false);
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        using var catalogue = OpenCatalogue("projected-missing");
        Locate(catalogue, reader, built, built.FirstSegment, built.FirstRun);

        var report = await CatalogueProjector.ProjectAsync(catalogue, reader, store, Repo, keys, credential, CancellationToken.None);

        Assert.AreEqual(1, report.Missing, "the run no blob carries");
        var finding = Assert.ContainsSingle(catalogue.Findings().Where(finding => finding.Kind == DamageKind.MissingIndexObject));
        Assert.Contains(built.SecondRun.ToString(), finding.Detail, StringComparison.Ordinal);
        AssertReaches(catalogue, keys, built.FirstData, "the segment the run before the missing one names");
    }

    [TestMethod]
    public async Task ACatalogueProjectedOverARunThatDoesNotFollowTheOneBefore_RecordsTheVersionDamaged()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        var built = await BuildAsync(store, keys, gapBeforeSecondRun: true);
        using var reader = new RepositoryReader(Repo, keys, store, Authority);
        await reader.LoadBlobsAsync(CancellationToken.None);
        using var catalogue = OpenCatalogue("projected-gap");

        var report = await CatalogueProjector.ProjectAsync(catalogue, reader, store, Repo, keys, credential, CancellationToken.None);

        Assert.AreEqual(0, report.Missing, "every run is in a blob");
        var finding = Assert.ContainsSingle(catalogue.Findings());
        Assert.AreEqual(DamageKind.CorruptRecord, finding.Kind);
        Assert.Contains(built.Version.ToString(), finding.Detail, StringComparison.Ordinal);
        Assert.Contains("run 1", finding.Detail, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ACatalogueRebuiltFromBlobsAlone_TracesARunsSegments_AndTheRun_ToTheFile()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        var built = await BuildAsync(store, keys);
        using var rebuilder = new ForensicRebuilder(store, Repo, credential);
        using var catalogue = OpenCatalogue("forensic");

        var report = await rebuilder.RebuildAsync(catalogue, new ForensicTarget.Everything(), CancellationToken.None);

        Assert.IsTrue(report.TargetSatisfied, string.Join("; ", report.Findings.Select(finding => finding.Detail)));
        AssertReaches(catalogue, keys, built.SecondData, "the segment the second run names");
        AssertReaches(catalogue, keys, built.FirstData, "the segment the first run names");
        AssertReaches(catalogue, keys, built.Runs, "the runs themselves");
    }

    [TestMethod]
    public async Task AForensicRebuild_NeedsTheSegmentsARunNames_SoOneNoBlobCarries_IsMissing()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        var built = await BuildAsync(store, keys);
        File.Delete(Path.Combine(StoreRoot, built.SecondData.StoreKey.Value.Replace('/', Path.DirectorySeparatorChar)));
        using var rebuilder = new ForensicRebuilder(store, Repo, credential);
        using var catalogue = OpenCatalogue("forensic-lost-segment");

        var report = await rebuilder.RebuildAsync(
            catalogue, new ForensicTarget.Snapshot(SnapshotId), CancellationToken.None);

        Assert.IsFalse(report.TargetSatisfied);
        var finding = Assert.ContainsSingle(report.Findings);
        Assert.Contains(built.SecondSegment.ToString(), finding.Detail, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AForensicRebuildOverARunThatWillNotRead_RecordsItMissing()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        var built = await BuildAsync(store, keys, writeSecondRun: false);
        using var rebuilder = new ForensicRebuilder(store, Repo, credential);
        using var catalogue = OpenCatalogue("forensic-missing-run");

        var report = await rebuilder.RebuildAsync(catalogue, new ForensicTarget.Everything(), CancellationToken.None);

        var finding = Assert.ContainsSingle(report.Findings.Where(finding => finding.Kind == DamageKind.MissingIndexObject));
        Assert.Contains(built.SecondRun.ToString(), finding.Detail, StringComparison.Ordinal);
        Assert.Contains(finding, catalogue.Findings().ToList(), "the rebuilt catalogue keeps the finding as the report states it");
        AssertReaches(catalogue, keys, built.FirstData, "the segment the run before the missing one names");
    }

    [TestMethod]
    public async Task AForensicRebuildOverARunThatDoesNotFollowTheOneBefore_RecordsTheVersionDamaged()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var credential = CreateCredential();
        var built = await BuildAsync(store, keys, gapBeforeSecondRun: true);
        using var rebuilder = new ForensicRebuilder(store, Repo, credential);
        using var catalogue = OpenCatalogue("forensic-gap");

        var report = await rebuilder.RebuildAsync(catalogue, new ForensicTarget.Everything(), CancellationToken.None);

        var finding = Assert.ContainsSingle(report.Findings);
        Assert.AreEqual(DamageKind.CorruptRecord, finding.Kind);
        Assert.Contains(built.Version.ToString(), finding.Detail, StringComparison.Ordinal);
        Assert.Contains("run 1", finding.Detail, StringComparison.Ordinal);
        Assert.Contains(finding, catalogue.Findings().ToList(), "the rebuilt catalogue keeps the finding as the report states it");
    }
}
