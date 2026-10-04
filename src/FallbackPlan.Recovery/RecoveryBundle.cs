using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FallbackPlan.Domain.Diagnostics;

namespace FallbackPlan.Recovery;

/// <summary>
/// The recovery tool's diagnostic bundle (architecture 08 §5; ADR-0082): one
/// zip describing one run, for whoever is helping the person who ran it.
/// </summary>
/// <remarks>
/// <para>
/// The tool has no service, no ring and no state, so its bundle is a report of
/// the run rather than a log: what was asked, what the archive's descriptor
/// said, whether the passphrase reproduced the keys, which blobs did not open,
/// what the snapshots are, what the restore did, and why the run stopped.
/// </para>
/// <para>
/// Every field is rendered through <see cref="RedactedRendering"/>, the rule
/// the service's log uses, by the type it is declared as here: a path as a
/// <see cref="LogPath"/>, an identifier through its own redacted form, the
/// code's vocabulary as a <see cref="LogLabel"/>, and a reader's or the
/// platform's words as text no type cleared. So a field added later is
/// withheld until somebody declares what it is.
/// </para>
/// </remarks>
internal static class RecoveryBundle
{
    /// <summary>The tool's name and version, in the bundle and nowhere it would identify anybody.</summary>
    internal const string ToolVersion = "fallbackplan-recover/0.1";

    /// <summary>
    /// The most notes of one kind of list a bundle carries. A restore of a
    /// badly damaged archive can fail on every file it holds, and a bundle
    /// nobody can send helps nobody; the count of the rest is kept.
    /// </summary>
    internal const int MaximumNotes = 10_000;

    /// <summary>What the opt-in to paths releases, said before the run.</summary>
    internal const string PathsConsequence =
        "This bundle will include plaintext paths: the archive's location, the output folder, the names of "
        + "the files that could not be restored, and the text of errors, which often repeats a path. Paths can "
        + "say more about a person than their files do. Identifiers are still shortened.";

    /// <summary>The bundle's entries, in the order they are written.</summary>
    internal static readonly string[] Entries =
    [
        "README.txt", "manifest.json", "environment.json", "run.json",
        "archive.json", "snapshots.json", "restore.json",
    ];

    private static readonly string[] Verbs = ["open", "snapshots", "restore"];

    private static readonly JsonSerializerOptions Json = new()
    {
        // A person reads these files, and nothing embeds them in a page.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Builds the bundle for <paramref name="run"/>.</summary>
    /// <param name="run">What the run did and found.</param>
    /// <param name="createdAt">When the bundle is made.</param>
    /// <returns>The zip's bytes.</returns>
    public static byte[] Build(RecoveryRun run, DateTimeOffset createdAt)
    {
        var mode = run.IncludePaths ? RenderMode.RedactedWithPaths : RenderMode.Redacted;

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "README.txt", Readme(run, createdAt), createdAt);
            Write(archive, "manifest.json", Serialize(new ManifestEntry(
                1, "FallbackPlan", "fallbackplan-recover", ToolVersion, Instant(createdAt), run.IncludePaths, Entries)), createdAt);
            Write(archive, "environment.json", Serialize(new EnvironmentEntry(
                "FallbackPlan",
                ToolVersion,
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription,
                RuntimeInformation.OSArchitecture.ToString(),
                RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount)), createdAt);
            Write(archive, "run.json", Serialize(RunOf(run, mode)), createdAt);
            Write(archive, "archive.json", Serialize(ArchiveOf(run, mode)), createdAt);
            Write(archive, "snapshots.json", Serialize(SnapshotsOf(run, mode)), createdAt);
            Write(archive, "restore.json", Serialize(RestoreOf(run, mode)), createdAt);
        }

        return buffer.ToArray();
    }

    private static RunEntry RunOf(RecoveryRun run, RenderMode mode) => new(
        run.Verb is null ? null : Verbs.Contains(run.Verb, StringComparer.Ordinal) ? Label(run.Verb) : Text(run.Verb, mode),
        new OptionsEntry(
            RenderPath(run.Repo, mode),
            run.PassphraseVariableNamed,
            run.PassphraseVariableSet,
            run.Snapshot is null ? null : Render(LogId.Snapshot(run.Snapshot), mode),
            RenderPath(run.Output, mode),
            ConsoleLogging.LevelNames[(int)run.LogLevel]),
        run.ExitCode,
        run.Failure is not { } failure
            ? null
            : new FailureEntry(
                Name(failure.Stage),
                Name(failure.Reason),
                failure.Option is null ? null : Label(failure.Option),
                Label(failure.ExceptionType),
                Text(failure.Message, mode)));

    private static ArchiveEntry ArchiveOf(RecoveryRun run, RenderMode mode) => new(
        RenderPath(run.Repo, mode),
        Name(run.Descriptor),
        run.FormatVersion,
        run.EffectiveFormatVersion,
        Features(run.RequiredFeatures),
        Features(run.OptionalFeatures),
        Features(run.UnsupportedFeatures),
        run.Kdf is { } kdf ? new KdfEntry(kdf.MemoryKiB, kdf.Iterations, kdf.Parallelism) : null,
        run.CreatedAt is { } created ? Instant(created) : null,
        run.UnstableFormat,
        run.Repository is { } repository ? Render(repository, mode) : null,
        Name(run.Passphrase),
        run.BlobNotes is not { } notes
            ? null
            : new BlobsEntry(
                run.ReadableBlobs ?? 0,
                notes.Count,
                [.. notes.Take(MaximumNotes).Select(note => NoteOf(note, mode))],
                Math.Max(0, notes.Count - MaximumNotes)));

    private static SnapshotsEntry SnapshotsOf(RecoveryRun run, RenderMode mode) => new(
        run.Snapshots is not null,
        [.. (run.Snapshots ?? []).Select(snapshot => new SnapshotEntry(
            Render(LogId.Snapshot(snapshot.Manifest.SnapshotId), mode),
            Instant(snapshot.Manifest.CaptureCompletedAt),
            snapshot.SignatureVerified ? "verified" : "failed",
            snapshot.Manifest.ObservedClockSkewMs))]);

    private static RestoreEntry RestoreOf(RecoveryRun run, RenderMode mode) => run.Restore is not { } report
        ? new RestoreEntry(false, null, null, null, null, [], 0)
        : new RestoreEntry(
            true,
            run.Snapshot is null ? null : Render(LogId.Snapshot(run.Snapshot), mode),
            report.Restored,
            report.Failed,
            report.Skipped,
            [.. report.Notes.Take(MaximumNotes).Select(note => NoteOf(note, mode))],
            Math.Max(0, report.Notes.Count - MaximumNotes));

    private static NoteEntry NoteOf(RecoveryNote note, RenderMode mode) => new(
        Name(note.Kind),
        RenderPath(note.Path, mode),
        note.Blob is { } blob ? Render(blob, mode) : note.Record is { } record ? Render(record, mode) : null,
        note.Outcome is null ? null : Render(note.Outcome, mode),
        note.Reason is null ? null : Label(note.Reason),
        Text(note.Detail, mode));

    private static string Readme(RecoveryRun run, DateTimeOffset createdAt)
    {
        var text = new StringBuilder();
        text.AppendLine("FallbackPlan recovery tool diagnostic bundle");
        text.AppendLine(CultureInfo.InvariantCulture, $"Made {createdAt.UtcDateTime:u} by {ToolVersion}.");
        text.AppendLine();
        text.AppendLine("This describes one run of the standalone recovery tool, so that whoever is helping can");
        text.AppendLine("see what the archive said and where the run stopped without being sent the archive:");
        text.AppendLine();
        text.AppendLine("  run.json        the verb, the options it was given, the exit code, and why it stopped");
        text.AppendLine("  archive.json    what the archive's descriptor said, whether the passphrase reproduced its");
        text.AppendLine("                  keys, and which blobs did not open");
        text.AppendLine("  snapshots.json  the snapshots the archive lists, when the run listed them");
        text.AppendLine("  restore.json    what a restore did and every file it could not restore, when one ran");
        text.AppendLine("  environment.json  the tool's version, the runtime and the operating system");
        text.AppendLine();
        text.AppendLine("It never holds the passphrase or the name of the variable holding it, any key, the");
        text.AppendLine("archive's salt or sealing public key (the same in every archive an installation writes),");
        text.AppendLine("the archive's recorded creator, or the machine's name. Identifiers are shortened to a");
        text.AppendLine("prefix: enough to tell two apart, not enough to follow one between stores.");
        text.AppendLine();

        if (run.IncludePaths)
        {
            text.AppendLine("Paths are INCLUDED, because the person who made it asked for them. The archive's");
            text.AppendLine("location, the output folder, the names of files that could not be restored, and the");
            text.AppendLine("text of errors appear as written. Paths can say more about a person than their files do.");
        }
        else
        {
            text.AppendLine("Paths are left out: each appears as a short digest, path#xxxxxxxx, which tells two");
            text.AppendLine("apart without saying either. The text of errors is withheld, because it often repeats a");
            text.AppendLine("path. The person who made it can make one with --include-paths if paths are needed.");
        }

        return text.ToString();
    }

    private static string? RenderPath(string? path, RenderMode mode) =>
        string.IsNullOrEmpty(path) ? null : RedactedRendering.Render(new LogPath(path), mode);

    private static string Render(object value, RenderMode mode) => RedactedRendering.Render(value, mode);

    private static string? Text(string? text, RenderMode mode) =>
        text is null ? null : RedactedRendering.Render(text, mode);

    private static string Label(string text) => RedactedRendering.Render(new LogLabel(text), RenderMode.Redacted);

    private static string Name<T>(T value)
        where T : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private static string[]? Features(IReadOnlyList<ushort>? features) =>
        features is null ? null : [.. features.Select(feature => string.Create(CultureInfo.InvariantCulture, $"0x{feature:x4}"))];

    private static string Instant(ulong unixMilliseconds) =>
        Instant(DateTimeOffset.FromUnixTimeMilliseconds((long)unixMilliseconds));

    private static string Instant(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    private static void Write(ZipArchive archive, string name, string text, DateTimeOffset at)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = at;
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(text);
    }

    private sealed record ManifestEntry(
        int BundleFormat,
        string Product,
        string Tool,
        string ToolVersion,
        string CreatedAt,
        bool IncludesPaths,
        IReadOnlyList<string> Entries);

    private sealed record EnvironmentEntry(
        string Product,
        string ToolVersion,
        string Runtime,
        string OperatingSystem,
        string OsArchitecture,
        string ProcessArchitecture,
        int ProcessorCount);

    private sealed record RunEntry(string? Verb, OptionsEntry Options, int? ExitCode, FailureEntry? Failure);

    private sealed record OptionsEntry(
        string? Repo,
        bool PassphraseVariableNamed,
        bool? PassphraseVariableSet,
        string? Snapshot,
        string? Output,
        string LogLevel);

    private sealed record FailureEntry(string Stage, string Reason, string? Option, string Exception, string? Message);

    private sealed record ArchiveEntry(
        string? Store,
        string Descriptor,
        int? FormatVersion,
        int? EffectiveFormatVersion,
        IReadOnlyList<string>? RequiredFeatures,
        IReadOnlyList<string>? OptionalFeatures,
        IReadOnlyList<string>? UnsupportedFeatures,
        KdfEntry? Kdf,
        string? CreatedAt,
        bool? UnstableFormat,
        string? Repository,
        string Passphrase,
        BlobsEntry? Blobs);

    private sealed record KdfEntry(uint MemoryKib, uint Iterations, byte Parallelism);

    private sealed record BlobsEntry(int Readable, int Skipped, IReadOnlyList<NoteEntry> Notes, int NotesLeftOut);

    private sealed record SnapshotsEntry(bool Listed, IReadOnlyList<SnapshotEntry> Snapshots);

    private sealed record SnapshotEntry(string Snapshot, string CapturedAt, string Signature, long? ObservedClockSkewMs);

    private sealed record RestoreEntry(
        bool Ran,
        string? Snapshot,
        int? Restored,
        int? Failed,
        int? Skipped,
        IReadOnlyList<NoteEntry> Notes,
        int NotesLeftOut);

    private sealed record NoteEntry(
        string Kind, string? Path, string? Subject, string? Outcome, string? Reason, string? Detail);
}
