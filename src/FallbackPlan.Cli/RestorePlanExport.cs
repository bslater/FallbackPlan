using System.Globalization;
using System.Text.Json;
using FallbackPlan.Api;
using FallbackPlan.Api.Transport;

namespace FallbackPlan.Cli;

/// <summary>
/// A restore's plan kept as a file (ADR-0108): the plan in the contract's own
/// words, beside the run it was made for, so a person can keep it, send it,
/// or read it with a script after the shell that printed it has gone.
/// </summary>
/// <remarks>
/// It names the backup's files, as the printed plan does, and is written only
/// where the person who unlocked the plan asked for it. A file already at that
/// path is somebody's, so it is left alone: the file is created new or not at
/// all.
/// </remarks>
internal static class RestorePlanExport
{
    /// <summary>What a kept plan says it is.</summary>
    public const string Kind = "fallbackplan-restore-plan";

    /// <summary>The shape of the file this build writes.</summary>
    public const int Version = 1;

    private static readonly JsonSerializerOptions Indented = new(FrameCodec.SerializerOptions) { WriteIndented = true };

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
        var document = new Document(
            Kind,
            Version,
            ContractVersion.Current.ToString(),
            request.SnapshotId,
            request.Path,
            request.OutputDirectory,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            plan);
        try
        {
            using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write);
            JsonSerializer.Serialize(stream, document, Indented);
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

    /// <summary>The file's shape.</summary>
    /// <param name="Kind">What the file is.</param>
    /// <param name="Version">The shape's version.</param>
    /// <param name="Contract">The contract the plan was answered under.</param>
    /// <param name="SnapshotId">The snapshot planned.</param>
    /// <param name="Path">The path within it; null for all of it.</param>
    /// <param name="OutputDirectory">Where the run would write.</param>
    /// <param name="PlannedAt">When, ISO-8601 UTC.</param>
    /// <param name="Plan">The plan, in the contract's words.</param>
    private sealed record Document(
        string Kind,
        int Version,
        string Contract,
        string SnapshotId,
        string? Path,
        string OutputDirectory,
        string PlannedAt,
        RestorePlanResult Plan);
}
