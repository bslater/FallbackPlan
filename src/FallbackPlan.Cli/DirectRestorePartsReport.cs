using System.Globalization;
using FallbackPlan.Cli.Resources;
using FallbackPlan.Restore;

namespace FallbackPlan.Cli;

/// <summary>
/// What <c>restore-parts</c> prints (FR-RST-008, ADR-0142): each destination
/// a restore in direct mode left unfinished under a state directory, by the
/// name of the folder its parts are kept in, with what its run restores,
/// where, and what the parts hold, or one destination's parts let go by that
/// name, or every destination's at once but where a run is under way
/// (ADR-0158). It names none of the backup's files, as the service's listing
/// of its runs does not.
/// </summary>
internal static class DirectRestorePartsReport
{
    /// <summary>Writes each destination left unfinished under <paramref name="stateDirectory"/>, newest first.</summary>
    public static void WriteKept(TextWriter output, string stateDirectory)
    {
        var root = DirectRestoreParts.RootUnder(stateDirectory);
        var kept = DirectRestoreParts.ReadKept(stateDirectory);
        if (kept.Count == 0)
        {
            output.WriteLine($"No restore in direct mode left parts under '{root}'.");
            return;
        }

        foreach (var entry in kept)
        {
            output.WriteLine(Describe(entry));
        }

        output.WriteLine(kept.Count == 1
            ? $"1 destination a restore in direct mode left unfinished keeps parts under '{root}'."
            : string.Create(CultureInfo.InvariantCulture, $"{kept.Count} destinations a restore in direct mode left unfinished keep parts under '{root}'."));
        output.WriteLine(
            $"To restore what is left of one, run its restore again with --resume. To let one's parts go, run restore-parts --state '{stateDirectory}' --forget <name>, or --forget-all to let every one's go.");
    }

    /// <summary>
    /// Lets the parts the folder <paramref name="name"/> keeps under
    /// <paramref name="stateDirectory"/> go, and says so.
    /// </summary>
    /// <exception cref="CliFailureException">When the name is not a folder's, none of that name is kept, or it is not all removed.</exception>
    public static void LetGo(TextWriter output, string stateDirectory, string name)
    {
        // Checked before it is joined to a path: what is not a folder's name
        // could name any file the state directory holds, or one outside it.
        if (!DirectRestoreParts.IsName(name))
        {
            throw new CliFailureException(Strings.FormatDirectRestorePartsReport_NotAPartsName(name));
        }

        long? held;
        bool underWay;
        try
        {
            held = DirectRestoreParts.Forget(stateDirectory, name, out underWay);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CliFailureException(Strings.FormatDirectRestorePartsReport_PartsNotLetGo(name, exception.Message));
        }

        // A run there would lose what it is assembling (ADR-0143).
        if (underWay)
        {
            throw new CliFailureException(Strings.FormatDirectRestorePartsReport_RunUnderWay(name));
        }

        var root = DirectRestoreParts.RootUnder(stateDirectory);
        if (held is not { } bytes)
        {
            throw new CliFailureException(Strings.FormatDirectRestorePartsReport_NoPartsKept(name, root));
        }

        output.WriteLine(LetGoLine(Path.Combine(root, name), bytes));
    }

    /// <summary>
    /// Lets every destination's parts under <paramref name="stateDirectory"/>
    /// go at once (ADR-0158), but one with a run under way, and says what
    /// became of each.
    /// </summary>
    /// <exception cref="CliFailureException">When none could be let go, or one was not all removed.</exception>
    public static void LetGoAll(TextWriter output, string stateDirectory)
    {
        var root = DirectRestoreParts.RootUnder(stateDirectory);
        IReadOnlyList<DirectRestoreLetGo> outcomes;
        try
        {
            outcomes = DirectRestoreParts.ForgetAll(stateDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CliFailureException(Strings.FormatDirectRestorePartsReport_NoneLetGo(root, exception.Message));
        }

        if (outcomes.Count == 0)
        {
            output.WriteLine($"No restore in direct mode left parts under '{root}'.");
            return;
        }

        foreach (var outcome in outcomes)
        {
            var parts = Path.Combine(root, outcome.Name);
            if (outcome.UnderWay)
            {
                output.WriteLine($"kept the parts under '{parts}': a restore in direct mode is under way there");
            }
            else if (outcome.Problem is null)
            {
                output.WriteLine(LetGoLine(parts, outcome.Held));
            }
        }

        var letGo = outcomes.Where(outcome => !outcome.UnderWay && outcome.Problem is null).ToList();
        var kept = outcomes.Count(outcome => outcome.UnderWay);
        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"let go the parts of {letGo.Count} {(letGo.Count == 1 ? "destination" : "destinations")}, {RestoreSpace.Bytes((ulong)letGo.Sum(outcome => outcome.Held))} in all");
        output.WriteLine(kept == 0
            ? summary
            : string.Create(CultureInfo.InvariantCulture, $"{summary}; {kept} with a run under way {(kept == 1 ? "is" : "are")} kept"));

        var failed = outcomes.Where(outcome => outcome.Problem is not null).ToList();
        if (failed.Count > 0)
        {
            throw new CliFailureException(Strings.FormatDirectRestorePartsReport_NotAllLetGo(
                failed.Count, string.Join("; ", failed.Select(outcome => $"'{Path.Combine(root, outcome.Name)}': {outcome.Problem}"))));
        }
    }

    private static string LetGoLine(string parts, long held) =>
        $"let go the parts kept under '{parts}', {RestoreSpace.Bytes((ulong)held)} of the files a restore in direct mode had assembled";

    /// <summary>A destination left unfinished, as a person chooses whether to resume it or let it go.</summary>
    private static string Describe(KeptDirectRestoreParts kept)
    {
        var holding = kept.Held > 0 ? $"holding {RestoreSpace.Bytes((ulong)kept.Held)} it had assembled" : null;
        var underWay = kept.UnderWay ? "; a run there is under way" : string.Empty;
        if (kept.Run is not { } run)
        {
            return $"{kept.Name}  {holding ?? "holding nothing"}; what its run restored was not kept{underWay}";
        }

        var began = DateTimeOffset.FromUnixTimeMilliseconds((long)run.BeganAt).UtcDateTime;
        var of = run.Set is { } set ? $" of set '{set}'" : string.Empty;
        var to = run.OutputDirectory ?? "where its files came from";
        var how = run.Existing is { } existing ? $", {existing} where a file is in the way" : string.Empty;
        var held = holding is null ? string.Empty : $", {holding}";
        return string.Create(
            CultureInfo.InvariantCulture, $"{kept.Name}  snapshot {run.SnapshotId}{of} to {to}{how}, begun {began:yyyy-MM-dd HH:mm} UTC{held}{underWay}");
    }
}
