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
    /// <summary>
    /// The plan's lines, and whether the run would restore every file and fit;
    /// the plan kept as JSON too, where the request names a file for it.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="request">The restore it plans, as it was asked for.</param>
    public static OperationReport Describe(RestorePlanResult plan, RestoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(request);
        if (request.AsPlanned is { } kept)
        {
            return HeldTo(plan, kept);
        }

        var outputDirectory = request.OutputDirectory;

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
        if (request.PlanExport is { } file)
        {
            RestorePlanExport.Write(file, request, plan);
            lines.Add($"plan kept in {file}");
        }

        lines.Add("nothing was restored: this was a plan");

        return new OperationReport(
            fits && plan.MissingObjects.Count == 0 && plan.ArchivedObjects is not { Count: > 0 },
            lines);
    }

    /// <summary>
    /// A plan held to a kept one (ADR-0114): a success only where nothing a
    /// person approved has changed — the files, what is missing, what is held
    /// archived, the conflicts — so the run that follows is the run that was
    /// approved. What the kept plan already said would fail was approved too,
    /// and is not news.
    /// </summary>
    /// <remarks>
    /// Room is left to the run, which refuses a restore that will not fit
    /// unless told otherwise, whatever the kept plan said of it.
    /// </remarks>
    /// <param name="now">The plan made now.</param>
    /// <param name="kept">The plan kept, and its file.</param>
    private static OperationReport HeldTo(RestorePlanResult now, KeptPlan kept)
    {
        var then = kept.Plan.Plan;
        var planned = kept.Plan.PlannedAt is { } at
            ? string.Create(CultureInfo.InvariantCulture, $", planned {at.UtcDateTime:yyyy-MM-dd HH:mm} UTC")
            : string.Empty;

        List<string> changed = [];
        if (now.Files != then.Files)
        {
            changed.Add(string.Create(CultureInfo.InvariantCulture, $"  files: {then.Files} kept, {now.Files} now"));
        }

        Differ(changed, "missing", then.MissingObjects, now.MissingObjects);
        Differ(changed, "archived", then.ArchivedObjects ?? [], now.ArchivedObjects ?? []);
        if (now.Conflicts != then.Conflicts)
        {
            changed.Add(string.Create(CultureInfo.InvariantCulture, $"  conflicts: {then.Conflicts} kept, {now.Conflicts} now"));
        }

        if (changed.Count == 0)
        {
            return new OperationReport(
                true,
                [
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"plan: as kept in {kept.File}{planned}: {now.Files} file(s), {now.MissingObjects.Count} missing, "
                        + $"{now.ArchivedObjects?.Count ?? 0} archived, {now.Conflicts} conflict(s)"),
                ]);
        }

        return new OperationReport(
            false,
            [
                $"plan: no longer as kept in {kept.File}{planned}:",
                .. changed,
                "nothing was restored: the plan has changed since it was kept; plan the restore again",
            ]);
    }

    /// <summary>The names one plan says and another does not, each way.</summary>
    private static void Differ(List<string> changed, string what, IReadOnlyList<string> then, IReadOnlyList<string> now)
    {
        changed.AddRange(now.Except(then, StringComparer.Ordinal).Select(path => $"  {what} now: {path}"));
        changed.AddRange(then.Except(now, StringComparer.Ordinal).Select(path => $"  {what} no longer: {path}"));
    }
}
