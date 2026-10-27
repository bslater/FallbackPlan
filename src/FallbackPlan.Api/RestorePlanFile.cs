using System.Globalization;
using System.Text.Json;
using FallbackPlan.Api.Transport;

namespace FallbackPlan.Api;

/// <summary>
/// A restore's plan kept as a file (ADR-0108 Amendment 1, ADR-0113): the plan
/// in the contract's own words, beside the run it was made for, in one shape
/// whichever client keeps it, the command line or the console.
/// </summary>
/// <remarks>
/// <para>
/// It names the backup's files, as the plan on screen does, and is kept only
/// where the person who unlocked the plan asked for it.
/// </para>
/// <para>
/// Version 2 keeps every field version 1 had, with the meaning it had, and
/// adds what a run asks beside one path. The restore source a plan was asked
/// through is not kept: it is an open handle that means nothing once it
/// closes.
/// </para>
/// </remarks>
public static class RestorePlanFile
{
    /// <summary>What a kept plan says it is.</summary>
    public const string Kind = "fallbackplan-restore-plan";

    /// <summary>The shape of the file this build writes.</summary>
    public const int Version = 2;

    private static readonly JsonSerializerOptions Indented = new(FrameCodec.SerializerOptions) { WriteIndented = true };

    /// <summary>The kept plan, as text.</summary>
    /// <param name="request">The plan as it was asked for.</param>
    /// <param name="plan">The plan.</param>
    /// <param name="plannedAt">When it was planned.</param>
    public static string Serialize(PlanRestoreCommand request, RestorePlanResult plan, DateTimeOffset plannedAt) =>
        JsonSerializer.Serialize(DocumentOf(request, plan, plannedAt), Indented);

    /// <summary>Writes the kept plan to <paramref name="stream"/>.</summary>
    /// <param name="stream">Where to write it.</param>
    /// <param name="request">The plan as it was asked for.</param>
    /// <param name="plan">The plan.</param>
    /// <param name="plannedAt">When it was planned.</param>
    public static void Write(Stream stream, PlanRestoreCommand request, RestorePlanResult plan, DateTimeOffset plannedAt) =>
        JsonSerializer.Serialize(stream, DocumentOf(request, plan, plannedAt), Indented);

    /// <summary>
    /// Reads a kept plan back, of this version or an earlier one, as the run
    /// it was kept for and the plan (ADR-0114).
    /// </summary>
    /// <param name="text">The file's text.</param>
    /// <returns>The kept plan.</returns>
    /// <exception cref="InvalidDataException">
    /// The text is not JSON, not a kept restore plan, of a version this build
    /// does not read, or holds no plan; the message says which.
    /// </exception>
    public static Kept Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        Document? document;
        try
        {
            document = JsonSerializer.Deserialize<Document>(text, FrameCodec.SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"it is not JSON: {exception.Message}", exception);
        }

        if (document is null || !string.Equals(document.Kind, Kind, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"it is not a kept restore plan: it says it is '{document?.Kind}'");
        }

        if (document.Version is < 1 or > Version)
        {
            throw new InvalidDataException(string.Create(
                CultureInfo.InvariantCulture,
                $"it is a kept restore plan of version {document.Version}, and this build reads versions 1 to {Version}"));
        }

        if (document.Plan is null || string.IsNullOrEmpty(document.SnapshotId))
        {
            throw new InvalidDataException("it is a kept restore plan with no plan or no snapshot in it");
        }

        return new Kept(
            document.Version,
            document.Contract,
            new PlanRestoreCommand(
                document.SnapshotId, document.Path, Paths: document.Paths, OutputDirectory: document.OutputDirectory,
                Target: document.Target, Existing: document.Existing, InPlace: document.InPlace),
            document.Plan,
            DateTimeOffset.TryParse(document.PlannedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                ? at
                : null);
    }

    /// <summary>A plan read back from its file.</summary>
    /// <param name="Version">The shape's version the file was written under.</param>
    /// <param name="Contract">The contract the plan was answered under, as the file says it.</param>
    /// <param name="Request">The run it was kept for, as the service is asked it; never naming a source.</param>
    /// <param name="Plan">The plan, as it was answered.</param>
    /// <param name="PlannedAt">When it was planned; null where the file says no time this build reads.</param>
    public sealed record Kept(int Version, string? Contract, PlanRestoreCommand Request, RestorePlanResult Plan, DateTimeOffset? PlannedAt);

    private static Document DocumentOf(PlanRestoreCommand request, RestorePlanResult plan, DateTimeOffset plannedAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(plan);

        return new Document(
            Kind,
            Version,
            ContractVersion.Current.ToString(),
            request.SnapshotId,
            request.Path,
            request.Paths,
            request.OutputDirectory,
            request.Target,
            request.Existing,
            request.InPlace,
            plannedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            plan);
    }

    /// <summary>The file's shape.</summary>
    /// <param name="Kind">What the file is.</param>
    /// <param name="Version">The shape's version.</param>
    /// <param name="Contract">The contract the plan was answered under.</param>
    /// <param name="SnapshotId">The snapshot planned.</param>
    /// <param name="Path">The one path within it, as version 1 kept it; null for all of it or for several.</param>
    /// <param name="Paths">The paths within it, where the run asked for several; null where it asked one or none.</param>
    /// <param name="OutputDirectory">Where the run would write; null or empty where it lands where the files came from.</param>
    /// <param name="Target">The run's target, as the contract takes it; null where none was asked.</param>
    /// <param name="Existing">The run's existing-file policy; null for the default.</param>
    /// <param name="InPlace">Whether the run would land in place rather than under quarantine.</param>
    /// <param name="PlannedAt">When, ISO-8601 UTC.</param>
    /// <param name="Plan">The plan, in the contract's words.</param>
    private sealed record Document(
        string? Kind,
        int Version,
        string? Contract,
        string? SnapshotId,
        string? Path,
        IReadOnlyList<string>? Paths,
        string? OutputDirectory,
        string? Target,
        string? Existing,
        bool InPlace,
        string? PlannedAt,
        RestorePlanResult? Plan);
}
