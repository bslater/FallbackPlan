using FallbackPlan.Application;
using FallbackPlan.Repository.Catalogue;

namespace FallbackPlan.Agent;

/// <summary>
/// How a notice or a line says what damaged objects reach (FR-VER-005,
/// specification 04 §7): the snapshots and the files a restore of which
/// needs them, counted, so the damage has a scope a person can act on.
/// </summary>
/// <remarks>
/// <para>
/// Counts in the words, names behind the passphrase: the files are a bounded
/// sample a notice keeps beside its message, for a caller who unlocks the set
/// (FR-WOR-007, ADR-0089 Amendment 1), because anyone signed in reads the
/// words.
/// </para>
/// <para>
/// What could not be traced is said as such, with what it costs: every
/// snapshot there is counted as needing it, because not knowing is not
/// evidence that none does.
/// </para>
/// </remarks>
internal static class DamageReachText
{
    /// <summary>The sentences a notice carries after naming the damaged objects.</summary>
    public static string Sentences(DamageReach reach)
    {
        List<string> said = [];
        if (NeededBy(reach) is { } neededBy)
        {
            said.Add($"They are {neededBy}.");
        }

        if (Untraced(reach) is { } untraced)
        {
            said.Add($"{untraced}.");
        }

        if (reach.FileSample.Count > 0)
        {
            said.Add(WithheldSentence);
        }

        return said.Count == 0 ? "No snapshot this installation lists needs them." : string.Join(" ", said);
    }

    /// <summary>
    /// The files a notice keeps beside its message: the bounded sample the
    /// trace named, of <paramref name="setId"/>'s backup, or null when it
    /// named none.
    /// </summary>
    public static NoticeNames? Names(string setId, DamageReach reach) =>
        reach.FileSample.Count == 0 ? null : new NoticeNames(setId, reach.FileSample);

    /// <summary>What a message says in place of the names it leaves out.</summary>
    public const string WithheldSentence =
        "Which files they are is shown only to someone who unlocks the set with its passphrase.";

    /// <summary>The same, as a clause for verify-destination's one line.</summary>
    public static string Clause(DamageReach reach)
    {
        List<string> said = [];
        if (NeededBy(reach) is { } neededBy)
        {
            said.Add(neededBy);
        }

        if (Untraced(reach) is { } untraced)
        {
            said.Add(untraced);
        }

        return said.Count == 0 ? "needed by no snapshot this installation lists" : string.Join("; ", said);
    }

    private static string? NeededBy(DamageReach reach)
    {
        if (reach.Snapshots.Count == 0)
        {
            return null;
        }

        List<string> what = [];
        if (reach.Files > 0)
        {
            what.Add($"for {reach.Files} file(s)");
        }

        if (reach.Structures > 0)
        {
            what.Add($"for the directory structure of {reach.Structures} of them");
        }

        return $"needed by {reach.Snapshots.Count} snapshot(s), {string.Join(", and ", what)}";
    }

    private static string? Untraced(DamageReach reach) => reach.Complete
        ? null
        : $"{reach.Untraced} of them this installation cannot trace to any snapshot, so every snapshot held there "
            + "is counted as needing them";
}
