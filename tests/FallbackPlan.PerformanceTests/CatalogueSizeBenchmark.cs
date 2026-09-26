using System.Globalization;
using FallbackPlan.TestSupport;

namespace FallbackPlan.PerformanceTests;

/// <summary>
/// NFR-PERF-011 catalogue bytes per file version, at the shape reference
/// scale <b>M</b> itself states: 10 M file versions against 50 M segment
/// references, so five references per version, each named by one path in
/// one snapshot. Seeded by <see cref="CatalogueSeeding"/> through the
/// catalogue's own recording calls, so the number is the schema's and not
/// a model's. Figures land in docs/phase-0-benchmarks.md.
/// </summary>
/// <remarks>
/// The total is reported per plane as well as whole, because a total on
/// its own says a budget is missed without saying where the bytes are —
/// and where they are decides what could be done about it.
/// </remarks>
public static class CatalogueSizeBenchmark
{
    /// <summary>The requirement's target, for the report to state alongside what it measured.</summary>
    private const int BudgetBytesPerVersion = 400;

    /// <summary>What one file version costs, whole and by plane.</summary>
    public static int Run(int versions)
    {
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Catalogue size (NFR-PERF-011) — {versions:N0} file versions, "
            + $"{CatalogueSeeding.SegmentsPerVersionAtScaleM} segment references each"));
        Console.WriteLine();
        Console.WriteLine("| Planes held | Bytes | B/version |");
        Console.WriteLine("|---|---:|---:|");

        Report("`file_versions` and its two indexes", versions, CataloguePlanes.FileVersions);
        Report("`tree_entries` and its two indexes", versions, CataloguePlanes.TreeEntries);
        Report("`object_locations` and `ix_locations_blob`", versions, CataloguePlanes.Locations);
        Report("`segment_dedup`", versions, CataloguePlanes.Dedup);
        Report("**all four, scale M's shape**", versions, CataloguePlanes.All);
        Report("**the floor — no segments at all**", versions, CataloguePlanes.All, segmentsPerVersion: 0);

        Console.WriteLine();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Budget: {BudgetBytesPerVersion} B/version at scale M — 10 M versions in 4 GB, 100 M in 40 GB."));
        return 0;
    }

    private static void Report(
        string label,
        int versions,
        CataloguePlanes planes,
        int segmentsPerVersion = CatalogueSeeding.SegmentsPerVersionAtScaleM)
    {
        var bytes = CatalogueSeeding.MeasureBytes(versions, segmentsPerVersion, planes);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"| {label} | {bytes:N0} | {bytes / (double)versions:F0} |"));
    }
}
