using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Diagnostics;
using FallbackPlan.Domain.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Agent;

/// <summary>
/// Builds the diagnostic bundle (architecture 10 §4, ADR-0081): one zip a
/// person can send to whoever is helping them.
/// </summary>
/// <remarks>
/// <para>
/// Every field is classified where it is declared here, by the same types the
/// log uses, and rendered by the same rule (<see cref="LogRecordRenderer.RenderValue"/>):
/// a path is a <see cref="LogPath"/>, an identifier a <see cref="LogId"/>, a
/// name a person chose or a word the code chose a <see cref="LogLabel"/>, and
/// text nobody can vouch for — an error, a notice's message — stays a string,
/// which the rule withholds unless the person opted in to paths. A field added
/// to the configuration later is absent from the bundle until somebody
/// classifies it here, which is the direction a privacy rule should fail.
/// </para>
/// <para>
/// What is never here is not a matter of rendering: the bundle reads no
/// credential store, no session, no pairing secret and no environment
/// variable, and the installation's salt and keys, which describe_service
/// hands any client, are left out because each one is a durable handle that
/// would tie this bundle to every archive the installation wrote.
/// </para>
/// </remarks>
internal static class DiagnosticBundle
{
    /// <summary>The bundle layout's own version, for a tool reading <c>manifest.json</c>.</summary>
    private const int Format = 1;

    /// <summary>The most log records a bundle carries before the size budget is even consulted.</summary>
    private const int MaximumLogRecords = 10_000;

    /// <summary>The most recent jobs a bundle lists.</summary>
    private const int MaximumJobs = 50;

    private static readonly JsonSerializerOptions Json = new()
    {
        // A person reads these files, and nothing embeds them in a page: an
        // apostrophe in a notice should read as one, not as \u0027.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>The files a bundle holds, in the order they are written.</summary>
    internal static readonly string[] Entries =
    [
        "README.txt", "manifest.json", "environment.json", "configuration.json",
        "status.json", "notices.json", "jobs.json", "log.txt",
    ];

    /// <summary>What the service hands the builder: facts already in hand, read once.</summary>
    internal sealed record Inputs(
        DateTimeOffset CreatedAt,
        bool IncludePaths,
        string ServiceVersion,
        string? SetupState,
        int ActiveJobs,
        bool RemoteBindingEnabled,
        string StateDirectory,
        string? ArchivesRoot,
        ClientConfiguration? Configuration,
        string? ConfigurationProblem,
        StatusResult? Status,
        string? StatusProblem,
        IReadOnlyList<Notice> Notices,
        IReadOnlyList<JobRecord> Jobs,
        LoggingComposition? Logging);

    /// <summary>Builds the bundle, dropping the log's oldest records until it fits.</summary>
    internal static DiagnosticBundleResult Build(Inputs inputs)
    {
        var mode = inputs.IncludePaths ? RenderMode.RedactedWithPaths : RenderMode.Redacted;
        var records = ReadRing(inputs.Logging);
        var carried = Math.Min(records.Count, MaximumLogRecords);

        while (true)
        {
            var content = Zip(inputs, mode, records, carried);
            if (content.Length <= DiagnosticBundleResult.MaximumContentBytes || carried == 0)
            {
                return new DiagnosticBundleResult(
                    FileName(inputs.CreatedAt), Convert.ToBase64String(content), inputs.IncludePaths, Entries,
                    carried, records.Count - carried);
            }

            carried /= 2;
        }
    }

    /// <summary>The product and the moment, and nothing that names the machine or its owner.</summary>
    private static string FileName(DateTimeOffset createdAt) =>
        string.Create(CultureInfo.InvariantCulture, $"fallbackplan-diagnostics-{createdAt.UtcDateTime:yyyyMMdd-HHmmss}Z.zip");

    private static List<LogRecord> ReadRing(LoggingComposition? logging)
    {
        var records = new List<LogRecord>();
        if (logging is null)
        {
            return records;
        }

        var since = 0L;
        while (true)
        {
            var page = logging.Ring.Read(since, 4_096, LogLevel.Trace);
            records.AddRange(page.Records);
            if (page.Records.Count == 0 || page.NextSequence == since)
            {
                return records;
            }

            since = page.NextSequence;
        }
    }

    private static byte[] Zip(Inputs inputs, RenderMode mode, List<LogRecord> records, int carried)
    {
        var log = records.Skip(records.Count - carried).ToList();
        var leftOut = records.Count - carried;

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "README.txt", Readme(inputs, carried, leftOut), inputs.CreatedAt);
            Write(archive, "manifest.json", JsonSerializer.Serialize(Manifest(inputs, carried, leftOut), Json), inputs.CreatedAt);
            Write(archive, "environment.json", JsonSerializer.Serialize(Environment(inputs, mode), Json), inputs.CreatedAt);
            Write(archive, "configuration.json", JsonSerializer.Serialize(Configuration(inputs, mode), Json), inputs.CreatedAt);
            Write(archive, "status.json", JsonSerializer.Serialize(Status(inputs, mode), Json), inputs.CreatedAt);
            Write(archive, "notices.json", JsonSerializer.Serialize(Notices(inputs, mode), Json), inputs.CreatedAt);
            Write(archive, "jobs.json", JsonSerializer.Serialize(Jobs(inputs, mode), Json), inputs.CreatedAt);
            Write(archive, "log.txt", Log(inputs, mode, log, leftOut), inputs.CreatedAt);
        }

        return buffer.ToArray();
    }

    private static void Write(ZipArchive archive, string name, string text, DateTimeOffset at)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = at;
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(text);
    }

    private static string? Render(object? value, RenderMode mode) =>
        value is null ? null : LogRecordRenderer.RenderValue(value, mode);

    /// <summary>
    /// A path the configuration may not have, as null when it has none. Not a
    /// conditional around <see cref="LogPath"/>: its conversion from a null
    /// string makes the null branch a path that renders as "(none)".
    /// </summary>
    private static string? RenderPath(string? path, RenderMode mode) =>
        string.IsNullOrEmpty(path) ? null : LogRecordRenderer.RenderValue(new LogPath(path), mode);

    private static string? Instant(ulong? unixMilliseconds) =>
        unixMilliseconds is { } ms
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
            : null;

    private static string Readme(Inputs inputs, int carried, int leftOut)
    {
        var text = new StringBuilder();
        text.AppendLine("FallbackPlan diagnostic bundle");
        text.AppendLine(CultureInfo.InvariantCulture, $"Made {inputs.CreatedAt.UtcDateTime:u} by {inputs.ServiceVersion} (contract {ContractVersion.Current}).");
        text.AppendLine();
        text.AppendLine("What is in it");
        text.AppendLine(CultureInfo.InvariantCulture, $"  log.txt             the service's recent log, oldest first: {carried} record(s){(leftOut > 0 ? string.Create(CultureInfo.InvariantCulture, $", and {leftOut} older one(s) left out to keep the file small enough to send") : string.Empty)}");
        text.AppendLine("  environment.json    versions, runtime and operating system, and how the service is logging");
        text.AppendLine("  configuration.json  the backup sets and destinations as configured");
        text.AppendLine("  status.json         each set's protection status and each destination's state");
        text.AppendLine("  notices.json        notices still waiting for somebody to acknowledge them");
        text.AppendLine("  jobs.json           the most recent jobs and how each one ended");
        text.AppendLine("  manifest.json       this summary, for a tool");
        text.AppendLine();
        text.AppendLine("What is never in it");
        text.AppendLine("  Passphrases, passwords, keys, recovery material, session tokens and pairing secrets. The bundle");
        text.AppendLine("  is built from the service's status, configuration and log, and reads none of the stores that");
        text.AppendLine("  hold them. The installation's salt and public keys are left out too: each one would tie this");
        text.AppendLine("  file to every archive the installation wrote.");
        text.AppendLine();
        text.AppendLine("How the rest is shown");
        text.AppendLine("  Identifiers that could link your stores to each other are shortened, like repo#1a2b3c4d.");
        text.AppendLine("  Names you gave your sets, destinations, paired devices and accounts are included as written.");
        text.AppendLine();

        if (inputs.IncludePaths)
        {
            text.AppendLine("Paths: included, because you asked for them");
            text.AppendLine("  This bundle shows the folders you back up, where your destinations are, the names of files in");
            text.AppendLine("  the log, and the text of errors and notices, which often names them. Path names can say more");
            text.AppendLine("  about a person than the files' contents do: share this file only with someone you would show");
            text.AppendLine("  those names to.");
        }
        else
        {
            text.AppendLine("Paths: left out");
            text.AppendLine("  The folders you back up, where your destinations are and the names of files appear as");
            text.AppendLine("  path#<code>, the same code for the same path, so the log still says when one file failed");
            text.AppendLine("  twice. The text of errors and notices, which often names files, shows as (withheld). A");
            text.AppendLine("  bundle with paths is something to ask for on purpose, for one bundle at a time.");
        }

        return text.ToString();
    }

    private static ManifestEntry Manifest(Inputs inputs, int carried, int leftOut) => new(
        Format,
        "FallbackPlan",
        inputs.CreatedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        inputs.ServiceVersion,
        ContractVersion.Current.ToString(),
        inputs.IncludePaths,
        Entries,
        carried,
        leftOut);

    private static EnvironmentEntry Environment(Inputs inputs, RenderMode mode)
    {
        LoggingEntry? logging = null;
        if (inputs.Logging is { } composition)
        {
            var levels = composition.Levels.Current;
            logging = new LoggingEntry(
                LogLevels.NameOf(levels.Default),
                levels.Categories.ToDictionary(pair => pair.Key, pair => LogLevels.NameOf(pair.Value), StringComparer.Ordinal),
                composition.DurableSink,
                levels.RetainFiles,
                levels.MaximumFileBytes,
                composition.Ring.Capacity,
                composition.Ring.OldestSequence,
                composition.Ring.NextSequence);
        }

        return new EnvironmentEntry(
            "FallbackPlan",
            inputs.ServiceVersion,
            ContractVersion.Current.ToString(),
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            System.Environment.ProcessorCount,
            ProcessStartedAt(),
            inputs.SetupState,
            inputs.ActiveJobs,
            inputs.RemoteBindingEnabled,
            Render(new LogPath(inputs.StateDirectory), mode),
            RenderPath(inputs.ArchivesRoot, mode),
            logging);
    }

    private static string? ProcessStartedAt()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException
            or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static ConfigurationEntry Configuration(Inputs inputs, RenderMode mode)
    {
        if (inputs.Configuration is not { } configuration)
        {
            return new ConfigurationEntry(null, [], [], null, null, null, null, null, Render(inputs.ConfigurationProblem, mode));
        }

        return new ConfigurationEntry(
            configuration.SchemaVersion,
            [.. configuration.Destinations.Select(destination => new DestinationEntry(
                Render(LogId.Destination(destination.Id), mode),
                Render(new LogLabel(destination.Name), mode),
                destination.Kind.ToString(),
                RenderPath(destination.Path, mode),
                Render(destination.Fingerprint is null ? null : LogId.Fingerprint(destination.Fingerprint), mode),
                Render(destination.Endpoint, mode),
                destination.FailureDomain?.ToString(),
                destination.Verification?.ToString(),
                destination.DeepVerifyIntervalDays,
                destination.DrillIntervalDays,
                destination.Priority,
                destination.TransferLimit))],
            [.. configuration.BackupSets.Select(set => new SetEntry(
                Render(LogId.BackupSet(set.Id), mode),
                Render(new LogLabel(set.Name), mode),
                [.. set.Roots.Select(root => new RootEntry(
                    Render(new LogPath(root.Path), mode),
                    root.Label is null ? null : Render(new LogLabel(root.Label), mode)))],
                RenderPath(set.Root, mode),
                [.. set.IncludeRules.Select(rule => Render(new LogPath(rule), mode)!)],
                [.. set.ExcludeRules.Select(rule => Render(new LogPath(rule), mode)!)],
                set.Schedule,
                Retention(set.Retention),
                set.Priority,
                set.DirectShip,
                [.. set.Destinations.Select(reference => new SetDestinationEntry(
                    Render(new LogLabel(reference.Ref), mode),
                    Retention(reference.Retention),
                    reference.Priority))]))],
            configuration.Logging is { } logging
                ? new ConfiguredLoggingEntry(logging.Level, logging.Categories, logging.RetainFiles, logging.MaxFileBytes, logging.RingCapacity)
                : null,
            configuration.MaxConcurrentBackups,
            configuration.BackgroundWindow,
            configuration.BackgroundReadLimit,
            configuration.ClockSkewMarginHours,
            null);
    }

    private static RetentionEntry? Retention(RetentionConfiguration? retention) =>
        retention is null
            ? null
            : new RetentionEntry(retention.KeepDaily, retention.KeepWeekly, retention.KeepMonthly, retention.MinGenerations, retention.DeferralDays);

    private static StatusEntry Status(Inputs inputs, RenderMode mode)
    {
        if (inputs.Status is not { } status)
        {
            return new StatusEntry(null, [], null, null, Render(inputs.StatusProblem, mode));
        }

        return new StatusEntry(
            Instant(status.ObservedAt),
            [.. status.Sets.Select(set => new SetStatusEntry(
                Render(new LogLabel(set.SetName), mode),
                set.Status.State.ToString(),
                set.Status.Verification is { } verification
                    ? new VerificationEntry(verification.Coverage, Instant(verification.VerifiedAtUnixMilliseconds))
                    : null,
                [.. set.Status.Warnings.Select(warning => Render(warning, mode)!)],
                set.NextRun,
                Instant(set.LastCompletedAt),
                [.. set.Destinations.Select(destination => new DestinationStatusEntry(
                    Render(new LogLabel(destination.Name), mode),
                    destination.Kind,
                    destination.State,
                    Instant(destination.LastSuccessAt),
                    Render(destination.Detail, mode),
                    destination.FailureDomain,
                    destination.Verification,
                    Instant(destination.BaselineCompletedAt),
                    destination.NeedsFull,
                    Render(destination.Reason is null ? null : new LogLabel(destination.Reason), mode),
                    destination.HeldBytes,
                    destination.OwedBytes,
                    Instant(destination.MeasuredAt),
                    Instant(destination.DrilledAt),
                    destination.DrillFiles,
                    Render(destination.DrillFailure, mode),
                    Render(destination.DrillLimit, mode),
                    destination.VerifiedSealed,
                    destination.VerifiedDigest,
                    destination.VerifiedChunk,
                    destination.DeepSweep is { } sweep
                        ? new DeepSweepEntry(
                            sweep.IntervalDays,
                            Instant(sweep.CircuitClosedAt),
                            sweep.ReadThisCircuit,
                            Instant(sweep.LastReadAt),
                            sweep.Stalls,
                            Render(sweep.StalledOn is null ? null : LogId.FromText("key", sweep.StalledOn), mode))
                        : null))]))],
            status.BackgroundWindow is { } window
                ? new WindowEntry(window.Text, window.Open, Instant(window.ChangesAt))
                : null,
            status.BackgroundLimits is { } limits
                ? new LimitsEntry(
                    limits.ReadLimit?.Text,
                    limits.TransferLimits.ToDictionary(
                        limit => Render(new LogLabel(limit.DestinationName), mode)!, limit => limit.Text, StringComparer.Ordinal))
                : null,
            null);
    }

    private static NoticeEntry[] Notices(Inputs inputs, RenderMode mode) =>
    [
        .. inputs.Notices
            .OrderBy(notice => notice.RaisedAt)
            .Select(notice => new NoticeEntry(
                Render(new LogLabel(notice.Kind), mode),
                Render(notice.Subject is null ? null : LogId.FromText("subject", notice.Subject), mode),
                Render(notice.Message, mode),
                Instant(notice.RaisedAt))),
    ];

    private static JobEntry[] Jobs(Inputs inputs, RenderMode mode)
    {
        var names = inputs.Configuration?.BackupSets.ToDictionary(set => set.Id, set => set.Name, StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);

        return
        [
            .. inputs.Jobs.TakeLast(MaximumJobs).Select(job => new JobEntry(
                Render(LogId.FromText("job", job.Id), mode),
                Render(LogId.BackupSet(job.BackupSetId), mode),
                names.TryGetValue(job.BackupSetId, out var name) ? Render(new LogLabel(name), mode) : null,
                job.State.ToString(),
                Instant(job.StartedAt),
                Instant(job.UpdatedAt),
                Render(job.SnapshotId is null ? null : LogId.Snapshot(job.SnapshotId), mode),
                Render(job.Detail, mode),
                job.Stats?.FilesSeen,
                job.Stats?.FilesDone,
                job.Stats?.FilesReused,
                job.Stats?.FilesFailed,
                job.Stats?.BytesSeen,
                job.Stats?.BytesStored)),
        ];
    }

    private static string Log(Inputs inputs, RenderMode mode, List<LogRecord> records, int leftOut)
    {
        if (inputs.Logging is null)
        {
            return "This service was started without a logging composition, so the bundle holds no log." + System.Environment.NewLine;
        }

        var text = new StringBuilder();
        if (leftOut > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"# {leftOut} older record(s) left out to keep the bundle small enough to send.");
        }

        foreach (var record in records)
        {
            text.AppendLine(LogRecordRenderer.RenderLine(record, mode));
        }

        return text.ToString();
    }

    private sealed record ManifestEntry(
        int BundleFormat,
        string Product,
        string CreatedAt,
        string ServiceVersion,
        string ContractVersion,
        bool IncludesPaths,
        IReadOnlyList<string> Entries,
        int LogRecords,
        int LogRecordsLeftOut);

    private sealed record EnvironmentEntry(
        string Product,
        string ServiceVersion,
        string ContractVersion,
        string Runtime,
        string OperatingSystem,
        string OsArchitecture,
        string ProcessArchitecture,
        int ProcessorCount,
        string? ProcessStartedAt,
        string? SetupState,
        int ActiveJobs,
        bool RemoteBindingEnabled,
        string? StateDirectory,
        string? ArchivesRoot,
        LoggingEntry? Logging);

    private sealed record LoggingEntry(
        string DefaultLevel,
        IReadOnlyDictionary<string, string> CategoryLevels,
        bool DurableSink,
        int RetainFiles,
        long MaximumFileBytes,
        int RingCapacity,
        long OldestSequence,
        long NextSequence);

    private sealed record ConfigurationEntry(
        int? SchemaVersion,
        IReadOnlyList<DestinationEntry> Destinations,
        IReadOnlyList<SetEntry> BackupSets,
        ConfiguredLoggingEntry? Logging,
        int? MaxConcurrentBackups,
        string? BackgroundWindow,
        string? BackgroundReadLimit,
        int? ClockSkewMarginHours,
        string? Unreadable);

    private sealed record DestinationEntry(
        string? Id,
        string? Name,
        string Kind,
        string? Path,
        string? Fingerprint,
        string? Endpoint,
        string? FailureDomain,
        string? Verification,
        int? DeepVerifyIntervalDays,
        int? DrillIntervalDays,
        int? Priority,
        string? TransferLimit);

    private sealed record SetEntry(
        string? Id,
        string? Name,
        IReadOnlyList<RootEntry> Roots,
        string? Root,
        IReadOnlyList<string> IncludeRules,
        IReadOnlyList<string> ExcludeRules,
        string? Schedule,
        RetentionEntry? Retention,
        int? Priority,
        bool DirectShip,
        IReadOnlyList<SetDestinationEntry> Destinations);

    private sealed record RootEntry(string? Path, string? Label);

    private sealed record SetDestinationEntry(string? Ref, RetentionEntry? Retention, int? Priority);

    private sealed record RetentionEntry(int? KeepDaily, int? KeepWeekly, int? KeepMonthly, int? MinGenerations, int? DeferralDays);

    private sealed record ConfiguredLoggingEntry(
        string? Level, IReadOnlyDictionary<string, string> Categories, int? RetainFiles, long? MaxFileBytes, int? RingCapacity);

    private sealed record StatusEntry(
        string? ObservedAt,
        IReadOnlyList<SetStatusEntry> Sets,
        WindowEntry? BackgroundWindow,
        LimitsEntry? BackgroundLimits,
        string? Unavailable);

    private sealed record SetStatusEntry(
        string? Name,
        string Status,
        VerificationEntry? Verification,
        IReadOnlyList<string> Warnings,
        string? NextRun,
        string? LastCompletedAt,
        IReadOnlyList<DestinationStatusEntry> Destinations);

    private sealed record VerificationEntry(double Coverage, string? VerifiedAt);

    private sealed record DestinationStatusEntry(
        string? Name,
        string Kind,
        string State,
        string? LastSuccessAt,
        string? Detail,
        string FailureDomain,
        string Verification,
        string? BaselineCompletedAt,
        bool NeedsFull,
        string? Reason,
        long HeldBytes,
        long OwedBytes,
        string? MeasuredAt,
        string? DrilledAt,
        int DrillFiles,
        string? DrillFailure,
        string? DrillLimit,
        int VerifiedSealed,
        int VerifiedDigest,
        int VerifiedChunk,
        DeepSweepEntry? DeepSweep);

    private sealed record DeepSweepEntry(
        int? IntervalDays, string? CircuitClosedAt, int ReadThisCircuit, string? LastReadAt, int Stalls, string? StalledOn);

    private sealed record WindowEntry(string Text, bool Open, string? ChangesAt);

    private sealed record LimitsEntry(string? ReadLimit, IReadOnlyDictionary<string, string> TransferLimits);

    private sealed record NoticeEntry(string? Kind, string? Subject, string? Message, string? RaisedAt);

    private sealed record JobEntry(
        string? Id,
        string? Set,
        string? SetName,
        string State,
        string? StartedAt,
        string? UpdatedAt,
        string? Snapshot,
        string? Detail,
        long? FilesSeen,
        long? FilesDone,
        long? FilesReused,
        long? FilesFailed,
        long? BytesSeen,
        long? BytesStored);
}
