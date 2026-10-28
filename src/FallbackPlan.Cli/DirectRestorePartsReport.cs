using System.Globalization;
using FallbackPlan.Cli.Resources;
using FallbackPlan.Restore;

namespace FallbackPlan.Cli;

/// <summary>
/// What <c>restore-parts</c> prints (FR-RST-008, ADR-0142): each destination
/// a restore in direct mode left unfinished under a state directory, by the
/// name of the folder its parts are kept in, with what its run restores,
/// where, and what the parts hold, or one destination's parts let go by that
/// name. It names none of the backup's files, as the service's listing of
/// its runs does not.
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
            $"To restore what is left of one, run its restore again with --resume. To let one's parts go, run restore-parts --state '{stateDirectory}' --forget <name>.");
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
        try
        {
            held = DirectRestoreParts.Forget(stateDirectory, name);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CliFailureException(Strings.FormatDirectRestorePartsReport_PartsNotLetGo(name, exception.Message));
        }

        var root = DirectRestoreParts.RootUnder(stateDirectory);
        if (held is not { } bytes)
        {
            throw new CliFailureException(Strings.FormatDirectRestorePartsReport_NoPartsKept(name, root));
        }

        output.WriteLine(
            $"let go the parts kept under '{Path.Combine(root, name)}', {RestoreSpace.Bytes((ulong)bytes)} of the files a restore in direct mode had assembled");
    }

    /// <summary>A destination left unfinished, as a person chooses whether to resume it or let it go.</summary>
    private static string Describe(KeptDirectRestoreParts kept)
    {
        var holding = kept.Held > 0 ? $"holding {RestoreSpace.Bytes((ulong)kept.Held)} it had assembled" : null;
        if (kept.Run is not { } run)
        {
            return $"{kept.Name}  {holding ?? "holding nothing"}; what its run restored was not kept";
        }

        var began = DateTimeOffset.FromUnixTimeMilliseconds((long)run.BeganAt).UtcDateTime;
        var of = run.Set is { } set ? $" of set '{set}'" : string.Empty;
        var to = run.OutputDirectory ?? "where its files came from";
        var how = run.Existing is { } existing ? $", {existing} where a file is in the way" : string.Empty;
        var held = holding is null ? string.Empty : $", {holding}";
        return string.Create(
            CultureInfo.InvariantCulture, $"{kept.Name}  snapshot {run.SnapshotId}{of} to {to}{how}, begun {began:yyyy-MM-dd HH:mm} UTC{held}");
    }
}
