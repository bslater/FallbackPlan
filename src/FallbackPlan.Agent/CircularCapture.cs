using System.Text;
using FallbackPlan.Application;
using FallbackPlan.Domain;

namespace FallbackPlan.Agent;

/// <summary>
/// The circular-capture guard (FR-DEST-011): a local-path destination — or
/// the service's own state — inside a backup set's captured sources would
/// back the backup up into itself, growing without bound. Judged here, at
/// the command boundaries, and never on the configuration load path: an
/// installation already carrying the layout keeps loading, and the edit that
/// would keep it is what gets refused.
/// </summary>
/// <remarks>
/// Containment is <see cref="PathContainment"/>'s lexical fence. The
/// carve-out is the owner's: a folder the set's own exclude rules provably
/// fence off is not captured, so the layout is allowed — judged with the
/// same compiled rules the scanner walks under
/// (<see cref="PathRuleSet.IsExcluded"/>), in the same coordinates
/// (label-prefixed for a multi-root set, ADR-0040), so "provably excluded"
/// means exactly what the next backup will do. The edit that stops
/// excluding the folder re-enters this guard and is refused then.
/// </remarks>
internal static class CircularCapture
{
    /// <summary>
    /// Every way these sets capture this configuration's own storage, or
    /// empty when they do not.
    /// </summary>
    /// <param name="sets">The sets whose roots and rules to judge — the one being edited, or a draft.</param>
    /// <param name="destinations">Every declared destination; only local paths carry a path to judge.</param>
    /// <param name="serviceStorage">
    /// The service's own directories (state, archives root), as
    /// (description, path) pairs — only the agent knows where those are.
    /// </param>
    /// <param name="named">Whether findings are prefixed with the owning set's name.</param>
    internal static List<string> Defects(
        IEnumerable<BackupSetConfiguration> sets,
        IEnumerable<DestinationConfiguration> destinations,
        IReadOnlyList<(string Description, string Path)> serviceStorage,
        bool named = true)
    {
        var defects = new List<string>();
        var contained = new List<(string Description, string Path, string Remedy)>();

        foreach (var destination in destinations)
        {
            if (destination is { Kind: DestinationKind.LocalPath, Path.Length: > 0 })
            {
                contained.Add((
                    $"destination '{destination.Name}' at '{destination.Path}'",
                    destination.Path,
                    "the backup would capture its own archive"));
            }
        }

        foreach (var (description, path) in serviceStorage)
        {
            if (!string.IsNullOrEmpty(path))
            {
                contained.Add(($"this service's {description} at '{path}'", path, $"the backup would capture the service's {description}"));
            }
        }

        foreach (var set in sets)
        {
            // The scanner's own rule compilation; validity is judged
            // case-independently elsewhere, so caseSensitive here is the same
            // placeholder it is in draft validation. Rules that do not
            // compile exclude nothing — the rule-set defect is its own
            // refusal, and a guard that trusted a broken fence would not be
            // a guard.
            _ = PathRuleSet.TryCreate(set.IncludeRules, set.ExcludeRules, caseSensitive: true, out var rules, out _);

            foreach (var root in set.Roots)
            {
                if (string.IsNullOrEmpty(root.Path) || !Path.IsPathRooted(root.Path))
                {
                    continue;
                }

                foreach (var candidate in contained)
                {
                    if (!PathContainment.IsAtOrUnder(root.Path, candidate.Path)
                        || IsExcluded(set, root, rules, candidate.Path))
                    {
                        continue;
                    }

                    var finding =
                        $"{candidate.Description} lies inside root '{root.Path}' and is not excluded — "
                        + $"{candidate.Remedy}. Exclude that folder from the set, or move it outside the sources.";
                    defects.Add(named ? $"backup set '{set.Name}': {finding}" : finding);
                }
            }
        }

        return defects;
    }

    /// <summary>
    /// Whether the set's exclude rules provably fence off
    /// <paramref name="candidatePath"/> under <paramref name="root"/> — the
    /// scanner would not descend into it (06 §7.1: an excluded directory
    /// prunes its subtree).
    /// </summary>
    private static bool IsExcluded(
        BackupSetConfiguration set,
        BackupRootConfiguration root,
        PathRuleSet? rules,
        string candidatePath)
    {
        if (rules is null)
        {
            return false;
        }

        var relative = Path.GetRelativePath(Path.GetFullPath(root.Path), Path.GetFullPath(candidatePath));
        if (relative == "." || relative.Length == 0 || relative.StartsWith("..", StringComparison.Ordinal))
        {
            // The root itself, or a case-folded containment the platform's
            // relative-path arithmetic does not reproduce: nothing a rule
            // could provably fence off.
            return false;
        }

        // The rules' coordinates: '/'-separated, NFC, label-prefixed when the
        // set walks more than one root (ADR-0040).
        var rulePath = relative.Replace(Path.DirectorySeparatorChar, '/').Normalize(NormalizationForm.FormC);
        if (set.Roots.Count > 1)
        {
            if (string.IsNullOrEmpty(root.Label))
            {
                return false;
            }

            rulePath = root.Label + "/" + rulePath;
        }

        return rules.IsExcluded(rulePath);
    }
}
