using System.Security.Cryptography;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain;
using FallbackPlan.Protocol;

namespace FallbackPlan.Agent;

/// <summary>
/// The configuration surface (ADR-0037): destination CRUD, set deletion, the
/// folder browser, draft validation, and the pairing listing. Everything here
/// edits <c>config.json</c> through <see cref="ClientConfiguration.Save"/>, so
/// a refusal is the validator's own message and the file is untouched.
/// </summary>
public sealed partial class ServiceCommandHandler
{
    /// <summary>The vocabulary a destination declaration may use for its kind.</summary>
    private const string KindVocabulary = "local-path | peer | s3 | azure-blob | dropbox";

    /// <summary>The vocabulary a destination declaration may use for its failure domain.</summary>
    private const string DomainVocabulary = "same-volume | same-machine | same-site | independent";

    private ServiceResult DeleteBackupSet(DeleteBackupSetCommand command)
    {
        var configuration = runtime.Configuration;
        var set = configuration.FindSet(command.Name);
        if (set is null)
        {
            return new ServiceError(
                ServiceErrorReason.NotFound, $"No backup set named '{command.Name}' is configured.");
        }

        // A set with work in flight is not deletable out from under it: the
        // job would finish against configuration that no longer names it.
        var running = runtime.Jobs.Jobs.FirstOrDefault(job =>
            string.Equals(job.BackupSetId, set.Id, StringComparison.Ordinal) && !HasSettled(job.State));
        if (running is not null)
        {
            return new ServiceError(
                ServiceErrorReason.Refused,
                $"Backup set '{command.Name}' has job {running.Id} in progress; cancel it first.");
        }

        var sets = configuration.BackupSets
            .Where(candidate => !string.Equals(candidate.Id, set.Id, StringComparison.Ordinal))
            .ToList();

        try
        {
            (configuration with { BackupSets = sets }).Save(runtime.ConfigurationPath);
        }
        catch (ClientStateException exception)
        {
            return new ServiceError(ServiceErrorReason.InvalidArgument, exception.Message);
        }

        // Removal is a config edit, never an erasure — say what remains,
        // because "deleted the set" is the dangerous misreading (ADR-0037 §4).
        List<string> lines =
        [
            $"Backup set '{command.Name}' is no longer configured; no data was deleted.",
            $"Its staging archive remains at '{runtime.ArchivePath(set.Id)}'.",
        ];
        lines.AddRange(set.Destinations.Select(reference =>
            $"Destination '{reference.Ref}' keeps every copy it holds for this set."));

        return new ConfigurationChangeResult(lines);
    }

    private DestinationsResult ListDestinations() =>
        new([.. runtime.Configuration.Destinations.Select(destination => new DestinationDescriptor(
            destination.Id,
            destination.Name,
            KindName(destination.Kind),
            destination.Path,
            destination.Fingerprint,
            destination.Endpoint,
            destination.FailureDomain is { } domain ? DomainName(domain) : null,
            destination.DeepVerifyIntervalDays,
            destination.AddressDefect))]);

    private ServiceResult UpsertDestination(UpsertDestinationCommand command)
    {
        if (!TryParseKind(command.Destination.Kind, out var kind))
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                $"'{command.Destination.Kind}' is not a destination kind ({KindVocabulary}).");
        }

        FailureDomain? domain = null;
        if (command.Destination.FailureDomain is { } declaredDomain)
        {
            if (!TryParseDomain(declaredDomain, out var parsed))
            {
                return new ServiceError(
                    ServiceErrorReason.InvalidArgument,
                    $"'{declaredDomain}' is not a failure domain ({DomainVocabulary}).");
            }

            domain = parsed;
        }

        var configuration = runtime.Configuration;
        var existing = command.Destination.Id is { } id
            ? configuration.Destinations.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.Ordinal))
            : null;

        var replacement = new DestinationConfiguration
        {
            Id = command.Destination.Id ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            Name = command.Destination.Name,
            Kind = kind,
            Path = command.Destination.Path,
            Fingerprint = command.Destination.Fingerprint,
            Endpoint = command.Destination.Endpoint,
            FailureDomain = domain,
            Verification = existing?.Verification,
            DeepVerifyIntervalDays = command.Destination.DeepVerifyIntervalDays,
        };

        var destinations = configuration.Destinations.ToList();
        var index = existing is null
            ? -1
            : destinations.FindIndex(candidate => string.Equals(candidate.Id, existing.Id, StringComparison.Ordinal));

        var sets = configuration.BackupSets;
        if (index >= 0)
        {
            destinations[index] = replacement;

            // A rename follows through to every set that references the old
            // name — leaving the references behind would make the rename a
            // dangling-reference refusal one line later.
            if (!string.Equals(existing!.Name, replacement.Name, StringComparison.Ordinal))
            {
                sets = [.. sets.Select(set => set with
                {
                    Destinations = [.. set.Destinations.Select(reference =>
                        string.Equals(reference.Ref, existing.Name, StringComparison.Ordinal)
                            ? reference with { Ref = replacement.Name }
                            : reference)],
                })];
            }
        }
        else
        {
            destinations.Add(replacement);
        }

        try
        {
            (configuration with { Destinations = destinations, BackupSets = sets })
                .Save(runtime.ConfigurationPath);
        }
        catch (ClientStateException exception)
        {
            return new ServiceError(ServiceErrorReason.InvalidArgument, exception.Message);
        }

        return new AcknowledgedResult();
    }

    private ServiceResult DeleteDestination(DeleteDestinationCommand command)
    {
        var configuration = runtime.Configuration;
        var destination = configuration.FindDestination(command.Name);
        if (destination is null)
        {
            return new ServiceError(
                ServiceErrorReason.NotFound, $"No destination named '{command.Name}' is declared.");
        }

        // Never cascade: silently editing sets to unblock a delete would make
        // one removal quietly change what several sets protect (ADR-0037 §4).
        var referencing = configuration.BackupSets
            .Where(set => set.Destinations.Any(reference =>
                string.Equals(reference.Ref, command.Name, StringComparison.Ordinal)))
            .Select(set => $"'{set.Name}'")
            .ToList();
        if (referencing.Count > 0)
        {
            return new ServiceError(
                ServiceErrorReason.Refused,
                $"Destination '{command.Name}' is referenced by backup set(s) {string.Join(", ", referencing)}; "
                + "remove it from them first.");
        }

        var destinations = configuration.Destinations
            .Where(candidate => !string.Equals(candidate.Id, destination.Id, StringComparison.Ordinal))
            .ToList();

        try
        {
            (configuration with { Destinations = destinations }).Save(runtime.ConfigurationPath);
        }
        catch (ClientStateException exception)
        {
            return new ServiceError(ServiceErrorReason.InvalidArgument, exception.Message);
        }

        // FR-DEST-007: removal names what remains there and that the hub
        // stops managing it — the data at the destination is not deleted.
        List<string> lines =
        [
            $"Destination '{command.Name}' is no longer managed by this hub; nothing stored there was deleted.",
        ];
        switch (destination.Kind)
        {
            case DestinationKind.LocalPath:
                lines.Add($"The archive at '{destination.Path}' keeps whatever the last sync left.");
                break;

            case DestinationKind.Peer:
                lines.Add(
                    $"The peer at {destination.Endpoint} keeps every object it was sent. "
                    + "The pairing itself still stands; end it with `fallbackplan-agent unpair` if the "
                    + "peering is over too.");
                break;

            default:
                break;
        }

        return new ConfigurationChangeResult(lines);
    }

    private PairingsResult ListPairings() =>
        new([.. PeerGrantStore.Open(runtime.Options.StateDirectory).Grants
            .OrderBy(grant => grant.PairedAtUnixMilliseconds)
            .Select(grant => new PairingDescriptor(
                grant.Identity.Fingerprint, grant.Label, RoleName(grant.Role), grant.PairedAtUnixMilliseconds))]);

    private static ServiceResult BrowseFolders(BrowseFoldersCommand command)
    {
        if (command.Path is null)
        {
            return new FolderListingResult(null, null, ListRoots());
        }

        var path = Path.GetFullPath(command.Path);
        if (!Directory.Exists(path))
        {
            return new ServiceError(
                ServiceErrorReason.NotFound, $"'{path}' is not a directory on this machine.");
        }

        List<FolderDescriptor> folders = [];
        foreach (var child in Directory.EnumerateDirectories(path))
        {
            var name = Path.GetFileName(child);
            var hidden = false;
            var inaccessible = false;
            try
            {
                hidden = (File.GetAttributes(child) & FileAttributes.Hidden) != 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Shown rather than thrown: one unreadable child must not
                // cost the operator the rest of the listing.
                inaccessible = true;
            }

            folders.Add(new FolderDescriptor(name, child, hidden, inaccessible));
        }

        folders.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));

        List<FileEntryDescriptor>? files = null;
        if (command.IncludeFiles)
        {
            files = [];
            foreach (var child in Directory.EnumerateFiles(path))
            {
                var name = Path.GetFileName(child);
                long length = 0;
                var hidden = false;
                try
                {
                    var info = new FileInfo(child);
                    length = info.Length;
                    hidden = (info.Attributes & FileAttributes.Hidden) != 0;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A file whose metadata will not read still gets its name.
                }

                files.Add(new FileEntryDescriptor(name, length, hidden));
            }

            files.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        }

        return new FolderListingResult(path, Path.GetDirectoryName(path), folders, files);
    }

    private static List<FolderDescriptor> ListRoots()
    {
        List<FolderDescriptor> roots = [];

        if (OperatingSystem.IsWindows())
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                roots.Add(new FolderDescriptor(
                    drive.Name, drive.RootDirectory.FullName, Hidden: false, Inaccessible: !drive.IsReady));
            }

            return roots;
        }

        roots.Add(new FolderDescriptor("/", "/", Hidden: false, Inaccessible: false));

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) && Directory.Exists(home))
        {
            roots.Add(new FolderDescriptor(home, home, Hidden: false, Inaccessible: false));
        }

        return roots;
    }

    /// <summary>
    /// The preview verb's work (ADR-0038, FR-SVC-009): resolve the set,
    /// overlay any draft root and rules, and answer the source-versus-last-
    /// snapshot comparison. Runs on the reader lane; nothing is captured.
    /// </summary>
    private async ValueTask<ServiceResult> PreviewSetChangesAsync(
        PreviewSetChangesCommand command, CancellationToken cancellationToken)
    {
        var configuration = runtime.Configuration;
        var set = command.SetName is null
            ? configuration.BackupSets.Count > 0 ? configuration.BackupSets[0] : null
            : configuration.FindSet(command.SetName);

        if (set is null)
        {
            return new ServiceError(
                ServiceErrorReason.NotFound,
                command.SetName is null
                    ? "No backup set is configured."
                    : $"No backup set named '{command.SetName}' is configured.");
        }

        var root = command.Root ?? set.Root;
        var includes = command.IncludeRules ?? set.IncludeRules;
        var excludes = command.ExcludeRules ?? set.ExcludeRules;

        // Refused here as a stated error rather than downstream as a thrown
        // guard — the draft may be mid-edit, and a defect is its answer.
        if (!PathRuleSet.TryCreate(includes, excludes, caseSensitive: true, out _, out var ruleDefects))
        {
            return new ServiceError(ServiceErrorReason.InvalidArgument, string.Join("; ", ruleDefects));
        }

        if (!Directory.Exists(root))
        {
            return new ServiceError(ServiceErrorReason.NotFound, $"root '{root}' does not exist");
        }

        var limit = Math.Clamp(
            command.SampleLimit ?? SetChangeScan.DefaultSampleLimit, 1, SetChangeScan.MaxSampleLimit);
        var (comparison, baseline) = await SetChangeScan.CompareAsync(
            runtime, set, root, includes, excludes, limit, cancellationToken).ConfigureAwait(false);

        return new SetChangePreviewResult(
            set.Name,
            baseline is null ? null : Convert.ToHexStringLower(baseline.SnapshotId.Span),
            baseline?.CapturedAt,
            comparison.Unchanged,
            ToBucket(comparison.New),
            ToBucket(comparison.Updated),
            ToBucket(comparison.MetadataOnly),
            ToBucket(comparison.Moved),
            ToBucket(comparison.Deleted),
            ToBucket(comparison.NoLongerIncluded),
            comparison.Failures,
            limit);

        static ChangeBucketDescriptor ToBucket(Repository.SourceChangeBucket bucket) =>
            new(bucket.Count, bucket.Sample);
    }

    private static SetDraftValidationResult ValidateSetDraft(ValidateSetDraftCommand command)
    {
        List<string> defects = [];

        // Validity is case-independent, so case sensitivity here is a
        // placeholder the same way it is in configuration validation.
        if (!PathRuleSet.TryCreate(
            command.IncludeRules, command.ExcludeRules, caseSensitive: true, out _, out var ruleDefects))
        {
            defects.AddRange(ruleDefects);
        }

        List<string> nextRuns = [];
        if (!string.IsNullOrWhiteSpace(command.Schedule))
        {
            if (ScheduleDefect(command.Schedule) is { } defect)
            {
                defects.Add(defect);
            }
            else if (Schedule.TryParse(command.Schedule, out var schedule, out _))
            {
                // The preview walks the series by feeding each occurrence
                // back as the anchor — the same pure function the scheduler
                // runs, in the operator's wall-clock frame (NFR-TIME-001).
                var occurrence = DateTimeOffset.Now;
                for (var i = 0; i < 3; i++)
                {
                    occurrence = schedule!.NextRun(occurrence, occurrence);
                    nextRuns.Add(occurrence.ToString("u", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }

        return new SetDraftValidationResult(defects, nextRuns);
    }

    /// <summary>
    /// What is wrong with a schedule expression, or null when nothing is —
    /// including the interval overflow <see cref="Schedule.TryParse"/> lets
    /// escape as an exception.
    /// </summary>
    private static string? ScheduleDefect(string? schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule))
        {
            return null;
        }

        try
        {
            return Schedule.TryParse(schedule, out _, out var defect) ? null : defect;
        }
        catch (OverflowException)
        {
            return $"'{schedule.Trim()}': the interval is too large to mean anything; use a smaller number.";
        }
    }

    /// <summary>Whether a job has stopped moving on its own (10 §3's terminal states).</summary>
    private static bool HasSettled(Domain.Jobs.JobState state) => state is
        Domain.Jobs.JobState.Complete
        or Domain.Jobs.JobState.CompletedWithFailures
        or Domain.Jobs.JobState.Cancelled
        or Domain.Jobs.JobState.FailedRecoverable
        or Domain.Jobs.JobState.FailedPermanent;

    private static RetentionConfiguration? ToRetention(
        RetentionPolicyDescriptor? descriptor, RetentionConfiguration? existing) => descriptor switch
    {
        // Not spoken: keep what stands. A 1.6 client never speaks.
        null => existing,

        // Spoken with every field absent: the explicit "no policy".
        { IsEmpty: true } => null,

        _ => new RetentionConfiguration
        {
            KeepDaily = descriptor.KeepDaily,
            KeepWeekly = descriptor.KeepWeekly,
            KeepMonthly = descriptor.KeepMonthly,
            MinGenerations = descriptor.MinGenerations,
            DeferralDays = descriptor.DeferralDays,
        },
    };

    private static RetentionPolicyDescriptor? ToPolicyDescriptor(RetentionConfiguration? retention) =>
        retention is null
            ? null
            : new RetentionPolicyDescriptor(
                retention.KeepDaily, retention.KeepWeekly, retention.KeepMonthly,
                retention.MinGenerations, retention.DeferralDays);

    private static Dictionary<string, RetentionPolicyDescriptor>? ToOverrideDescriptors(
        IReadOnlyList<SetDestinationReference> references)
    {
        Dictionary<string, RetentionPolicyDescriptor> overrides = [];
        foreach (var reference in references)
        {
            if (ToPolicyDescriptor(reference.Retention) is { } descriptor)
            {
                overrides[reference.Ref] = descriptor;
            }
        }

        return overrides.Count == 0 ? null : overrides;
    }

    private static string KindName(DestinationKind kind) => kind switch
    {
        DestinationKind.LocalPath => "local-path",
        DestinationKind.Peer => "peer",
        DestinationKind.S3 => "s3",
        DestinationKind.AzureBlob => "azure-blob",
        DestinationKind.Dropbox => "dropbox",
        _ => kind.ToString(),
    };

    private static bool TryParseKind(string text, out DestinationKind kind)
    {
        (var known, kind) = text switch
        {
            "local-path" => (true, DestinationKind.LocalPath),
            "peer" => (true, DestinationKind.Peer),
            "s3" => (true, DestinationKind.S3),
            "azure-blob" => (true, DestinationKind.AzureBlob),
            "dropbox" => (true, DestinationKind.Dropbox),
            _ => (false, default),
        };

        return known;
    }

    private static string DomainName(FailureDomain domain) => domain switch
    {
        FailureDomain.SameVolume => "same-volume",
        FailureDomain.SameMachine => "same-machine",
        FailureDomain.SameSite => "same-site",
        FailureDomain.Independent => "independent",
        _ => domain.ToString(),
    };

    private static bool TryParseDomain(string text, out FailureDomain domain)
    {
        (var known, domain) = text switch
        {
            "same-volume" => (true, FailureDomain.SameVolume),
            "same-machine" => (true, FailureDomain.SameMachine),
            "same-site" => (true, FailureDomain.SameSite),
            "independent" => (true, FailureDomain.Independent),
            _ => (false, default),
        };

        return known;
    }

    private static string RoleName(PeerRole role) => role switch
    {
        PeerRole.StoresHere => "stores-here",
        PeerRole.StoresForUs => "stores-for-us",
        PeerRole.Both => "both",
        _ => role.ToString(),
    };
}
