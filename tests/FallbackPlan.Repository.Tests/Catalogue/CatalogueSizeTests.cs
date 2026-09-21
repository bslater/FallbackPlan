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
/// The ceilings are measured figures, not the requirement's target. As of
/// this suite the target is <b>missed by the schema itself</b>: the floor
/// — a version, the one path that reaches it and its own manifest's
/// location, with no segment anywhere — is already several times 400 B,
/// so no corpus and no dedup ratio can bring a real catalogue inside the
/// budget. <c>AFileVersionWithNoSegmentsAtAll_AlreadyCostsMoreThanTheWholeBudget</c>
/// pins that as the fact it is; the requirement row carries the corrected
/// target and what it would take to reach the old one.
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

        // Measured 2 520 B/version here, and 2 476-2 537 between 2 000 and
        // 40 000 versions. This is the headline figure rather than the
        // regression guard: a whole added column moves it by only about 4%,
        // which is inside any band a different page size or fill factor
        // could also move it by. What guards the schema is the per-plane
        // case below, where the same change shows up five times as large.
        Assert.IsGreaterThan(2_200, perVersion, $"{perVersion:F0} B/version");
        Assert.IsLessThan(2_850, perVersion, $"{perVersion:F0} B/version");
    }

    [TestMethod]
    [DataRow(CataloguePlanes.FileVersions, 330, 420, DisplayName = "file_versions and its two indexes")]
    [DataRow(CataloguePlanes.TreeEntries, 565, 720, DisplayName = "tree_entries and its two indexes")]
    [DataRow(CataloguePlanes.Locations, 1_035, 1_315, DisplayName = "object_locations and ix_locations_blob")]
    [DataRow(CataloguePlanes.Dedup, 376, 478, DisplayName = "segment_dedup")]
    public void EachPlaneCostsWhatItsRowsAndIndexesCost(CataloguePlanes plane, int floor, int ceiling)
    {
        // Where the bytes are, which is what makes the headline actionable
        // and what a schema change has to get past. A column added to
        // file_versions with an index on it moves that plane by about a
        // third while moving the total by a twentieth, so this is the case
        // that notices.
        var perVersion = CatalogueSeeding.MeasureBytes(Versions, planes: plane) / (double)Versions;

        Assert.IsGreaterThan(floor, perVersion, $"{plane}: {perVersion:F0} B/version");
        Assert.IsLessThan(ceiling, perVersion, $"{plane}: {perVersion:F0} B/version");
    }

    [TestMethod]
    public void AFileVersionWithNoSegmentsAtAll_AlreadyCostsMoreThanTheWholeBudget()
    {
        // The floor: one file_versions row, the one tree_entries row that
        // makes the version reachable, and the one object_locations row for
        // its own manifest. A version no snapshot names is not restorable
        // and is not one of scale M's ten million, so nothing below this is
        // a catalogue anybody could restore from.
        var floor = CatalogueSeeding.MeasureBytes(Versions, segmentsPerVersion: 0) / (double)Versions;

        Assert.IsGreaterThan(1_030, floor, $"{floor:F0} B/version");
        Assert.IsLessThan(1_320, floor, $"{floor:F0} B/version");

        // Stated as an assertion rather than left in prose: the budget is
        // unreachable before a single segment exists, so it is the target
        // that is wrong and not the corpus that is unlucky. This goes red
        // the day the schema genuinely gets there, which is when the
        // requirement row wants revisiting.
        Assert.IsGreaterThan(
            BudgetBytesPerVersion,
            floor,
            $"the floor is {floor:F0} B/version against a {BudgetBytesPerVersion} B budget");
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
