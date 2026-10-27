using Bodu;

namespace FallbackPlan.Restore;

/// <summary>A root a set captures, as a restore to the original location puts files back into it.</summary>
/// <param name="Path">The folder the root captures.</param>
/// <param name="Label">The root's name inside a snapshot of several roots (ADR-0040).</param>
public sealed record RestoreRoot(string Path, string? Label = null);

/// <summary>One part of a run to the original location: what goes back into one root.</summary>
/// <param name="Plan">What the root takes back, its paths without the root's label.</param>
/// <param name="OutputDirectory">The root's folder, where the slice lands.</param>
/// <param name="LabelPrefix">The label and a slash its paths shed, or empty where the set has one root.</param>
public sealed record RestoreRootSlice(RestorePlan Plan, string OutputDirectory, string LabelPrefix);

/// <summary>
/// Where a restore to the original location puts a plan back (ADR-0041), one
/// mapping for the service and the command line's direct mode (ADR-0119).
/// </summary>
/// <remarks>
/// A snapshot of several roots holds each root's files under its label, so
/// the label is what says which root a file goes back into. A label the set
/// no longer carries says nothing about where its files belong, and the
/// mapping is refused whole before anything is read or written rather than
/// put back in part.
/// </remarks>
public static class RestoreOriginalLocation
{
    /// <summary>The plan sliced over the set's roots.</summary>
    /// <param name="plan">The plan, its paths as the snapshot holds them.</param>
    /// <param name="roots">The set's roots, as its configuration has them now.</param>
    /// <param name="unmappedLabel">The first label no root carries, where the mapping is refused.</param>
    /// <returns>One slice a root, or null where a label is no root's.</returns>
    public static IReadOnlyList<RestoreRootSlice>? Slice(
        RestorePlan plan, IReadOnlyList<RestoreRoot> roots, out string? unmappedLabel)
    {
        ThrowHelper.ThrowIfNull(plan);
        ThrowHelper.ThrowIfNull(roots);
        unmappedLabel = null;

        if (roots.Count == 1)
        {
            return [new RestoreRootSlice(plan, roots[0].Path, string.Empty)];
        }

        var slices = new List<RestoreRootSlice>();
        foreach (var group in plan.Items.GroupBy(item => item.Path.Split('/')[0], StringComparer.Ordinal))
        {
            var root = roots.FirstOrDefault(candidate => string.Equals(candidate.Label, group.Key, StringComparison.Ordinal));
            if (root is null)
            {
                unmappedLabel = group.Key;
                return null;
            }

            // The label's own folder is the root, which is already there.
            var prefix = group.Key + "/";
            slices.Add(new RestoreRootSlice(
                plan with
                {
                    Items = [.. group
                        .Where(item => item.Path.Length > prefix.Length)
                        .Select(item => item with { Path = item.Path[prefix.Length..] })],
                },
                root.Path,
                prefix));
        }

        return slices;
    }

    /// <summary>The run's one receipt, whichever way it was sliced, its paths as they were asked.</summary>
    /// <param name="receipts">Each slice's receipt, with the label prefix its paths shed.</param>
    public static RestoreReceipt Merge(IReadOnlyList<(RestoreReceipt Receipt, string LabelPrefix)> receipts)
    {
        ThrowHelper.ThrowIfNull(receipts);
        if (receipts.Count == 1 && receipts[0].LabelPrefix.Length == 0)
        {
            return receipts[0].Receipt;
        }

        static string Prefixed(string prefix, string path) => prefix.Length == 0 ? path : prefix + path;
        return new RestoreReceipt
        {
            SchemaVersion = RestoreReceipt.CurrentSchemaVersion,
            SnapshotId = receipts[0].Receipt.SnapshotId,
            StartedAt = receipts.Min(entry => entry.Receipt.StartedAt),
            CompletedAt = receipts.Max(entry => entry.Receipt.CompletedAt),
            Items = [.. receipts.SelectMany(entry => entry.Receipt.Items.Select(item => item with
            {
                Path = Prefixed(entry.LabelPrefix, item.Path),
                WrittenAs = item.WrittenAs is null ? null : Prefixed(entry.LabelPrefix, item.WrittenAs),
            }))],
            Displaced = [.. receipts.SelectMany(entry =>
                entry.Receipt.Displaced.Select(path => Prefixed(entry.LabelPrefix, path)))],
            WrittenTo = string.Join("; ", receipts.Select(entry => entry.Receipt.WrittenTo)),
            Outcome = receipts.Max(entry => entry.Receipt.Outcome),
        };
    }
}
