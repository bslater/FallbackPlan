using FallbackPlan.Api;

namespace FallbackPlan.Cli;

/// <summary>
/// A restore's plan kept as a file (ADR-0108): the plan in the contract's own
/// words, beside the run it was made for, so a person can keep it, send it,
/// or read it with a script after the shell that printed it has gone. Its
/// shape is <see cref="RestorePlanFile"/>'s, which the console keeps too.
/// </summary>
/// <remarks>
/// It names the backup's files, as the printed plan does, and is written only
/// where the person who unlocked the plan asked for it. A file already at that
/// path is somebody's, so it is left alone: the file is created new or not at
/// all.
/// </remarks>
internal static class RestorePlanExport
{
    /// <summary>Whether <paramref name="file"/> may be written: false where anything is already there.</summary>
    /// <param name="file">The path asked for.</param>
    public static bool IsFree(string file) => !File.Exists(file) && !Directory.Exists(file);

    /// <summary>Writes the plan to <paramref name="file"/>, which must not exist yet.</summary>
    /// <param name="file">Where to keep it.</param>
    /// <param name="request">The restore it plans.</param>
    /// <param name="plan">The plan.</param>
    /// <exception cref="CliFailureException">Something is already at <paramref name="file"/>.</exception>
    public static void Write(string file, RestoreRequest request, RestorePlanResult plan)
    {
        // The run this command plans, in the words the service is asked in,
        // so the file is the one the console keeps too (ADR-0113).
        var asked = new PlanRestoreCommand(
            request.SnapshotId, request.Path, Paths: request.Paths, OutputDirectory: request.OutputDirectory,
            Target: request.Target, Existing: request.Existing, InPlace: request.InPlace);
        try
        {
            using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write);
            RestorePlanFile.Write(stream, asked, plan, DateTimeOffset.UtcNow);
        }
        catch (IOException) when (!IsFree(file))
        {
            throw new CliFailureException(Refusal(file));
        }
    }

    /// <summary>What a person is told when something is already where they asked the plan kept.</summary>
    /// <param name="file">The path asked for.</param>
    public static string Refusal(string file) =>
        $"'{file}' already exists. A plan is kept in a new file and leaves that one alone; name another.";
}
