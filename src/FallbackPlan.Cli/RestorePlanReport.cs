using System.Globalization;
using FallbackPlan.Api;

namespace FallbackPlan.Cli;

/// <summary>
/// What <c>restore --plan</c> prints (FR-RST-003, ADR-0107): the plan a run
/// would follow, with nothing restored. One rendering whichever gateway made
/// the plan, so a plan the service made and one made here read alike
/// (ADR-0028 §3).
/// </summary>
/// <remarks>
/// The report is a success only where the run would restore every file the
/// plan names and fit, so a script can plan before it restores. A conflict is
/// not a failure: the existing-file policy settles it.
/// </remarks>
internal static class RestorePlanReport
{
    /// <summary>The plan's lines, and whether the run would restore every file and fit.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="outputDirectory">Where the run would write, as it was asked for.</param>
    public static OperationReport Describe(RestorePlanResult plan, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var reading = plan.ReadBytes is { } read
            ? string.Create(CultureInfo.InvariantCulture, $", {read} byte(s) read")
            : string.Empty;
        List<string> lines =
        [
            string.Create(
                CultureInfo.InvariantCulture,
                $"plan: {plan.Files} file(s) to {outputDirectory}, {plan.WriteBytes ?? plan.Bytes} byte(s) written{reading}"),
        ];

        var fits = true;
        foreach (var need in plan.Space ?? [])
        {
            var room = need.AvailableBytes is not { } free || free >= need.NeededBytes;
            fits &= room;
            var working = need.Working ? " while files verify" : string.Empty;
            var available = need.AvailableBytes is { } known
                ? string.Create(CultureInfo.InvariantCulture, $", {known} free")
                : ", free space unknown";
            var shortfall = room ? string.Empty : " — the restore would not fit; --ignore-free-space restores anyway";
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"room: {need.Directory} needs {need.NeededBytes} byte(s){working}{available}{shortfall}"));
        }

        if (plan.MissingObjects.Count > 0)
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"missing: {plan.MissingObjects.Count} file(s) need content no copy holds, and would fail:"));
            lines.AddRange(plan.MissingObjects.Select(path => "  " + path));
        }

        if (plan.ArchivedObjects is { Count: > 0 } archived)
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"archived: {archived.Count} file(s) are held only in an archival tier, and would fail until the store rehydrates them:"));
            lines.AddRange(archived.Select(path => "  " + path));
        }

        if (plan.Conflicts > 0)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"conflicts: {plan.Conflicts}"));
            lines.AddRange((plan.ConflictSample ?? []).Select(conflict => "  " + conflict));
        }

        lines.AddRange(plan.Degradations ?? []);
        lines.Add("nothing was restored: this was a plan");

        return new OperationReport(
            fits && plan.MissingObjects.Count == 0 && plan.ArchivedObjects is not { Count: > 0 },
            lines);
    }
}
