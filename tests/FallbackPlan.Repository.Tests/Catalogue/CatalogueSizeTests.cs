using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Catalogue;

/// <summary>
/// What the catalogue costs per file version (NFR-PERF-011): the
/// assertion half of the budget, beside the figure
/// <c>PerformanceTests/CatalogueSizeBenchmark</c> publishes. Both seed
/// through <see cref="CatalogueSeeding"/>, so the pinned ceiling and the
/// published number describe one catalogue.
/// </summary>
/// <remarks>
/// <para>
/// A benchmark is not an assertion ([traceability
/// matrix](../../../docs/requirements/traceability.md)), and a stopwatch
/// in a unit suite is a flake. What is asserted here is instead the thing
/// the budget is actually about: <b>bytes per row, which is a property of
/// the schema rather than of the scale</b>. The scale-invariance case is
/// what lets a measurement at a few thousand versions speak to scale M's
/// ten million — the ratio is flat, and falls slightly as the B-trees
/// fill, so the small measurement is the conservative one.
/// </para>
/// <para>
/// The ceilings are measured figures, not the requirement's target, which
/// the catalogue still misses. Until ADR-0148 it was <b>missed by the schema
/// itself</b>: the floor — a version, the one path that reaches it and its
/// own manifest's location, with no segment anywhere — cost more than 400 B,
/// so no corpus and no dedup ratio could bring a real catalogue inside the
/// budget. The floor now costs about the budget, so what a catalogue is over
/// it by is what its segment references cost.
/// <c>AFileVersionWithNoSegmentsAtAll_CostsAboutTheWholeBudget</c> pins that;
/// the requirement row carries the figures.
/// </para>
/// <para>
/// The headline's shape names each version by one path in one snapshot,
/// which is the set whose every file changes in every snapshot. A set where
/// fewer change names each version in more snapshots, and
/// <c>ASnapshotListsEveryFile_SoAFileItLeftUnchangedCostsItATreeEntry</c>
/// pins what each of those costs.
/// </para>
/// </remarks>
[TestClass]
public sealed class CatalogueSizeTests
{
    /// <summary>NFR-PERF-011's target, which the measurements below are read against.</summary>
    private const int BudgetBytesPerVersion = 400;

    /// <summary>
    /// Enough versions for the B-trees to reach several levels and for one
    /// 4 KiB page to be a fifth of a byte per version, and few enough that
    /// seeding stays inside a unit suite's patience.
    /// </summary>
    private const int Versions = 3_000;

    [TestMethod]
    public void AFileVersionAtScaleMsOwnShape_CostsWhatTheSchemaCosts()
    {
        var perVersion = CatalogueSeeding.MeasureBytes(Versions) / (double)Versions;

        // Measured 965 B/version here, and 942 at 20 000 versions, since a
        // tree entry is keyed by its folder and name (ADR-0148); 2 520 and
        // 2 477 before the catalogue's schema was first measured against its
        // budget (ADR-0144 to ADR-0147). This is the headline figure rather
        // than the regression guard: a whole added column moves it by only
        // about 5%, which is inside any band a different page size or fill
        // factor could also move it by. What guards the schema is the
        // per-plane case below, where the same change shows up five times as
        // large.
        Assert.IsGreaterThan(850, perVersion, $"{perVersion:F0} B/version");
        Assert.IsLessThan(1_080, perVersion, $"{perVersion:F0} B/version");
    }

    [TestMethod]
    [DataRow(CataloguePlanes.FileVersions, 165, 210, DisplayName = "file_versions and its identity index")]
    [DataRow(CataloguePlanes.TreeEntries, 150, 190, DisplayName = "tree_entries")]
    [DataRow(CataloguePlanes.Locations, 605, 775, DisplayName = "object_locations and ix_locations_blob")]
    public void EachPlaneCostsWhatItsRowsAndIndexesCost(CataloguePlanes plane, int floor, int ceiling)
    {
        // Where the bytes are, which is what makes the headline actionable
        // and what a schema change has to get past. A hash's column added to
        // file_versions with an index on it moves that plane by more than
        // half while moving the total by a little over a tenth, so this is
        // the case that notices.
        var perVersion = CatalogueSeeding.MeasureBytes(Versions, planes: plane) / (double)Versions;

        Assert.IsGreaterThan(floor, perVersion, $"{plane}: {perVersion:F0} B/version");
        Assert.IsLessThan(ceiling, perVersion, $"{plane}: {perVersion:F0} B/version");
    }

    [TestMethod]
    public void AFileVersionWithNoSegmentsAtAll_CostsAboutTheWholeBudget()
    {
        // The floor: one file_versions row, the one tree_entries row that
        // makes the version reachable, and the one object_locations row for
        // its own manifest. A version no snapshot names is not restorable
        // and is not one of scale M's ten million, so nothing below this is
        // a catalogue anybody could restore from.
        var floor = CatalogueSeeding.MeasureBytes(Versions, segmentsPerVersion: 0) / (double)Versions;

        Assert.IsGreaterThan(370, floor, $"{floor:F0} B/version");
        Assert.IsLessThan(475, floor, $"{floor:F0} B/version");

        // Stated as an assertion rather than left in prose. Until ADR-0148
        // the floor alone was more than the budget, so it was the schema that
        // missed it before any segment existed. It measured 395 B/version at
        // 20 000 versions, inside the budget, and here, where the trees are
        // less full, within a tenth of it. What a catalogue is over the
        // budget by is its segment references.
        Assert.IsLessThan(
            BudgetBytesPerVersion * 1.1,
            floor,
            $"the floor is {floor:F0} B/version against a {BudgetBytesPerVersion} B budget");
    }

    [TestMethod]
    public void ASnapshotListsEveryFile_SoAFileItLeftUnchangedCostsItATreeEntry()
    {
        // Each snapshot lists every file of its set, changed or not, so a
        // catalogue grows with the snapshots it keeps as well as with its
        // versions. The headline's shape names each version by one path in
        // one snapshot, which is a set whose every file changes in every
        // snapshot. A set where fewer change pays this once per snapshot for
        // each file it left alone: 132 B measured here, which at a tenth of
        // the files changing per snapshot is about 2 000 B/version in all.
        var (one, _) = CatalogueSeeding.MeasureSnapshotBytes(Versions, snapshots: 1, changedPerSnapshot: 0);
        var (two, versions) = CatalogueSeeding.MeasureSnapshotBytes(Versions, snapshots: 2, changedPerSnapshot: 0);
        Assert.AreEqual(Versions, versions, "a snapshot that changes nothing adds no version");

        var perFile = (two - one) / (double)Versions;
        Assert.IsGreaterThan(115, perFile, $"{perFile:F0} B per unchanged file per snapshot");
        Assert.IsLessThan(150, perFile, $"{perFile:F0} B per unchanged file per snapshot");
    }

    [TestMethod]
    public void TheCostIsPerRowAndNotPerScale_SoASmallMeasurementSpeaksForALargeOne()
    {
        // The whole reason a measurement at a few thousand versions is
        // evidence about ten million. If this ever stopped holding, every
        // figure this suite and the harness publish would be a statement
        // about their own scale and nothing else.
        const int larger = Versions * 4;

        var small = CatalogueSeeding.MeasureBytes(Versions) / (double)Versions;
        var large = CatalogueSeeding.MeasureBytes(larger) / (double)larger;

        var drift = Math.Abs(large - small) / small;
        Assert.IsLessThan(
            0.05,
            drift,
            $"{small:F0} B/version at {Versions:N0} against {large:F0} at {larger:N0}");

        // And it falls rather than rises as the trees fill, so the cheap
        // measurement is the conservative one.
        Assert.IsLessThan(small * 1.02, large, $"{small:F0} -> {large:F0} B/version");
    }
}
