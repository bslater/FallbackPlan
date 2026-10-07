using System.Security.Cryptography;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain;
using FallbackPlan.Protocol;

namespace FallbackPlan.Agent;

/// <summary>
/// The configuration surface (ADR-0037): destination CRUD, set deletion, the
/// installation's own settings (Amendment 1), the folder browser, draft
/// validation, and the pairing listing. Everything here edits
/// <c>config.json</c> through <see cref="ClientConfiguration.Save"/>, so a
/// refusal leaves the file untouched. It is the validator's own message, or,
/// for a setting a paired console may send, the parser's words said at the
/// boundary without the file's path.
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
        // The same rule as the enqueue guard (ADR-0047 Amendment 3):
        // unsettled AND still in the queue — an orphaned journal row must
        // not wedge a deletion behind a cancel that would refuse it.
        var running = runtime.Jobs.Jobs.LastOrDefault(job =>
            string.Equals(job.BackupSetId, set.Id, StringComparison.Ordinal)
            && !HasSettled(job.State)
            && runtime.Queue.IsActive(job.Id));
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
        // because "deleted the set" is the dangerous misreading (ADR-0037 §4)
        // — and what remains depends on the set's shape: a direct-ship set
        // has no staging archive, only its local metadata store (ADR-0046).
        List<string> lines =
        [
            $"Backup set '{command.Name}' is no longer configured; no data was deleted.",
            set.DirectShip
                ? $"Its local metadata store remains at '{runtime.SetMetadataPath(set.Id)}'."
                : $"Its staging archive remains at '{runtime.ArchivePath(set.Id)}'.",
        ];
        lines.AddRange(set.Destinations.Select(reference =>
            $"Destination '{reference.Ref}' keeps every copy it holds for this set."));

        return new ConfigurationChangeResult(lines);
    }

    private DestinationsResult ListDestinations() =>
        new([.. runtime.Configuration.Destinations.Select(DescriptorOf)]);

    private DestinationDescriptor DescriptorOf(DestinationConfiguration destination)
    {
        var credential = CredentialHeld(destination);
        return new DestinationDescriptor(
            destination.Id,
            destination.Name,
            KindName(destination.Kind),
            destination.Path,
            destination.Fingerprint,
            destination.Endpoint,
            destination.FailureDomain is { } domain ? DomainName(domain) : null,
            destination.DeepVerifyIntervalDays,
            destination.AddressDefect,
            destination.Priority,
            destination.TransferLimit,
            destination.DrillIntervalDays,
            destination.Bucket,
            destination.Region,
            destination.Prefix,
            AddressingName(destination.Addressing),
            credential.Stored,
            destination.Account,
            destination.Container,
            credential.Kind,
            credential.Expires);
    }

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

        if (!TryParseAddressing(command.Destination.Addressing, out var addressing))
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                $"Destination '{command.Destination.Name}': '{command.Destination.Addressing}' is not a bucket "
                + "addressing (path | virtual-host).");
        }

        var configuration = runtime.Configuration;
        var existing = command.Destination.Id is { } id
            ? configuration.Destinations.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.Ordinal))
            : null;

        // Contract 1.44 carries the transfer limit and the drill cadence. Null
        // keeps what the file says, as it does for every field an older client
        // cannot send; an empty limit and a zero cadence clear. Each is judged
        // here, with the parser's own words, so the refusal names the
        // destination and never the configuration file's path.
        var transferLimit = existing?.TransferLimit;
        if (command.Destination.TransferLimit is { } limitText)
        {
            if (limitText.Length == 0)
            {
                transferLimit = null;
            }
            else if (ByteRate.TryParse(limitText, out var rate, out var limitDefect))
            {
                transferLimit = rate!.Text;
            }
            else
            {
                return new ServiceError(
                    ServiceErrorReason.InvalidArgument,
                    $"Destination '{command.Destination.Name}': transfer_limit {limitDefect}");
            }
        }

        var drillIntervalDays = existing?.DrillIntervalDays;
        if (command.Destination.DrillIntervalDays is { } drillDays)
        {
            if (drillDays < 0)
            {
                return new ServiceError(
                    ServiceErrorReason.InvalidArgument,
                    $"Destination '{command.Destination.Name}': drill_interval_days must be a positive number of "
                    + $"days, or 0 to remove the cadence; {drillDays} is neither.");
            }

            drillIntervalDays = drillDays == 0 ? null : drillDays;
        }

        // A relative path is pinned to an absolute one HERE, at the moment
        // the operator can still see what it meant. Stored verbatim, it
        // resolves against whatever working directory the service happens to
        // run with — which is how a replica tree appeared beside the logs in
        // the 2026-08 report while the intended folder stayed empty.
        var declaredPath = command.Destination.Path;
        var resolvedFromRelative =
            kind == DestinationKind.LocalPath && declaredPath is { Length: > 0 } && !Path.IsPathRooted(declaredPath);
        var path = resolvedFromRelative ? Path.GetFullPath(declaredPath!) : declaredPath;

        var replacement = new DestinationConfiguration
        {
            Id = command.Destination.Id ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            Name = command.Destination.Name,
            Kind = kind,
            Path = path,
            Fingerprint = command.Destination.Fingerprint,
            // Only a container's endpoint may be left out, which names the
            // account at the public service; an empty one is the same.
            Endpoint = kind == DestinationKind.AzureBlob
                ? NullIfEmpty(command.Destination.Endpoint)
                : command.Destination.Endpoint,
            FailureDomain = domain,
            DeepVerifyIntervalDays = command.Destination.DeepVerifyIntervalDays,
            // Null preserves — a pre-1.17 client cannot see the field.
            Priority = command.Destination.Priority ?? existing?.Priority,
            // No wire field, so no client can speak for it: an edit keeps what
            // the file says (ADR-0037 §1).
            Verification = existing?.Verification,
            DrillIntervalDays = drillIntervalDays,
            TransferLimit = transferLimit,
            // An empty text is no value, as the console's empty field is: the
            // configuration's own validation then says which ones an object
            // store cannot do without and which other kinds may not carry.
            Bucket = NullIfEmpty(command.Destination.Bucket),
            Region = NullIfEmpty(command.Destination.Region),
            Prefix = NullIfEmpty(command.Destination.Prefix),
            Addressing = addressing,
            Account = NullIfEmpty(command.Destination.Account),
            Container = NullIfEmpty(command.Destination.Container),
        };

        // The circular-capture guard (FR-DEST-011), entered from this door:
        // a destination declared inside a set's captured sources is refused
        // unless that set's own excludes provably fence it off.
        var circular = CircularCapture.Defects(configuration.BackupSets, [replacement], serviceStorage: []);
        if (circular.Count > 0)
        {
            return new ServiceError(ServiceErrorReason.InvalidArgument, string.Join(" ", circular));
        }

        // The placement condition, entered from this door (ADR-0051,
        // FR-DEST-017): moving an already-referenced local destination's
        // path onto a root's drive creates the same violation choosing it
        // would — judged only when the path actually changes, so a standing
        // older binding survives unrelated edits (ADR-0035). A Debug build
        // lets the move stand and says so (Amendment 2).
        List<string> allowed = [];
        if (kind == DestinationKind.LocalPath
            && path is { Length: > 0 }
            && existing is not null
            && !string.Equals(existing.Path, path, StringComparison.Ordinal))
        {
            foreach (var set in configuration.BackupSets.Where(set => set.Destinations.Any(reference =>
                string.Equals(reference.Ref, existing.Name, StringComparison.Ordinal))))
            {
                if (LocalDestinationPlacement.Judge(
                        [.. set.Roots.Select(root => root.Path)], path,
                        runtime.VolumeIdOf, runtime.DiskIdOf) is { } conflict)
                {
                    var onto = $"{(conflict.SamePhysicalDisk ? "the same physical drive as" : "the same volume as")} "
                        + $"root '{conflict.Root}' of backup set '{set.Name}'";
                    if (!runtime.AllowsSameDrivePlacement)
                    {
                        return new ServiceError(
                            ServiceErrorReason.InvalidArgument,
                            $"Moving '{existing.Name}' to '{path}' would put it on {onto} — a backup on the drive "
                            + "the files live on dies with them (ADR-0051).");
                    }

                    allowed.Add(AllowedOnlyInDebug($"Moving '{existing.Name}' to '{path}' puts it on {onto}"));
                }
            }
        }

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

        // The resolution is the one part of the declaration the operator did
        // not type, so it is said back rather than silently stored, as is a
        // move only a Debug build lets stand.
        List<string> said = [];
        if (resolvedFromRelative)
        {
            said.Add($"Destination '{replacement.Name}' named the relative path '{declaredPath}'; stored as '{path}'.");
        }

        said.AddRange(allowed);
        return said.Count > 0 ? new ConfigurationChangeResult(said) : new AcknowledgedResult();
    }

    private ServiceSettingsResult GetServiceSettings()
    {
        var configuration = runtime.Configuration;
        return new ServiceSettingsResult(
            configuration.BackgroundWindow,
            configuration.BackgroundReadLimit,
            configuration.MaxConcurrentBackups,
            runtime.BackupPoolWidth);
    }

    private ServiceResult UpdateServiceSettings(UpdateServiceSettingsCommand command)
    {
        // Every value is judged before anything is written: the request is one
        // decision, and half of it landing would leave the operator guessing
        // which half. The parsers' own words name the defect; the configuration
        // file's path is never in a refusal a paired console can receive.
        string? window = null;
        if (command.BackgroundWindow is { Length: > 0 } windowText)
        {
            if (!BackgroundWindow.TryParse(windowText, out var parsedWindow, out var windowDefect))
            {
                return new ServiceError(ServiceErrorReason.InvalidArgument, $"background_window: {windowDefect}");
            }

            window = parsedWindow!.Text;
        }

        string? readLimit = null;
        if (command.BackgroundReadLimit is { Length: > 0 } rateText)
        {
            if (!ByteRate.TryParse(rateText, out var rate, out var rateDefect))
            {
                return new ServiceError(ServiceErrorReason.InvalidArgument, $"background_read_limit: {rateDefect}");
            }

            readLimit = rate!.Text;
        }

        if (command.MaxConcurrentBackups is { } requestedWidth && requestedWidth is not 0 and (< 1 or > 5))
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                $"max_concurrent_backups: the backup pool takes 1 to 5, or 0 to return to the default "
                + $"(ADR-0047); {requestedWidth} is neither.");
        }

        var configuration = runtime.Configuration;
        var updated = configuration;
        var lines = new List<string>();

        if (command.BackgroundWindow is not null
            && !string.Equals(window, configuration.BackgroundWindow, StringComparison.Ordinal))
        {
            updated = updated with { BackgroundWindow = window };
            lines.Add(window is null
                ? "Background window removed: background work may start at any hour, from the next pass."
                : $"Background window set to {window}: background work starts only inside it, from the next pass.");
        }

        if (command.BackgroundReadLimit is not null
            && !string.Equals(readLimit, configuration.BackgroundReadLimit, StringComparison.Ordinal))
        {
            updated = updated with { BackgroundReadLimit = readLimit };
            lines.Add(readLimit is null
                ? "Background read limit removed: background captures read unpaced, from the next capture."
                : $"Background reads limited to {readLimit}, from the next background capture.");
        }

        if (command.MaxConcurrentBackups is { } width)
        {
            int? stored = width == 0 ? null : width;
            if (stored != configuration.MaxConcurrentBackups)
            {
                updated = updated with { MaxConcurrentBackups = stored };
                lines.Add(
                    $"max_concurrent_backups {(stored is { } set ? $"set to {set}" : "returned to the default of 2")}; "
                    + $"the pool is sized when the service starts, so it runs {runtime.BackupPoolWidth} until the "
                    + "service restarts.");
            }
        }

        if (lines.Count == 0)
        {
            return new ConfigurationChangeResult(["No setting changed."]);
        }

        try
        {
            updated.Save(runtime.ConfigurationPath);
        }
        catch (ClientStateException exception)
        {
            return new ServiceError(ServiceErrorReason.InvalidArgument, exception.Message);
        }

        return new ConfigurationChangeResult(lines);
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

            case DestinationKind.S3 or DestinationKind.AzureBlob:
                lines.Add(
                    $"The {StoreComposition.Describe(destination)} keeps every object it was sent"
                    + (destination.Prefix is { } prefix ? $" under '{prefix}'" : string.Empty) + ".");
                var held = runtime.DestinationCredentials.KindHeld(destination.Id);
                if (runtime.DestinationCredentials.Delete(destination.Id))
                {
                    lines.Add(held switch
                    {
                        DestinationCredentialStore.SharedKeyKind =>
                            "Its account key is forgotten: this service holds it no longer. Rotate it at the "
                            + "account if anything other than this service ever held it.",
                        DestinationCredentialStore.SharedAccessSignatureKind =>
                            "Its shared access signature is forgotten: this service holds it no longer. Revoke it "
                            + "at the account too, through the policy it was issued under, if nothing else uses it.",
                        _ => "Its access key is forgotten: this service holds it no longer. Revoke it at the "
                            + "provider too if nothing else uses it.",
                    });
                }

                break;

            default:
                break;
        }

        return new ConfigurationChangeResult(lines);
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    /// <summary>
    /// Retires a migrated direct-ship set's staging archive (ADR-0046,
    /// FR-DEST-002's spirit): the one deliberately destructive act of the
    /// migration, refused while it would lose anything. Every object staging
    /// holds (lifecycle objects aside — they never leave staging) must be
    /// present in the union of the set's destination replicas.
    /// </summary>
    /// <summary>
    /// Moves one set's repository to the latest format this build writes
    /// (ADR-0066, contract 1.36) by appending a signed record. The descriptor
    /// is not touched: a destination seeds one only if absent and a peer
    /// keeps the copy it has, so a rewritten descriptor would move the source
    /// alone and leave every copy claiming the older format over newer blobs.
    /// </summary>
    private async ValueTask<ServiceResult> UpgradeSetFormatAsync(
        UpgradeSetFormatCommand command, CancellationToken cancellationToken)
    {
        var set = runtime.Configuration.FindSet(command.SetName);
        if (set is null)
        {
            return new ServiceError(
                ServiceErrorReason.NotFound, $"No backup set named '{command.SetName}' is configured.");
        }

        // The eviction below swaps the archive handle out beneath whoever
        // holds it, which is the storage-shape flip's rule applied to the one
        // other edit with the same blast radius.
        var lastJob = runtime.Jobs.Jobs.LastOrDefault(job => job.BackupSetId == set.Id);
        if (lastJob is not null && !JobStateStore.HasSettled(lastJob.State) && runtime.Queue.IsActive(lastJob.Id))
        {
            return new ServiceError(
                ServiceErrorReason.Refused,
                $"Backup set '{set.Name}' has a run in progress — the format cannot change under a live run. "
                + "Cancel it or let it finish, then upgrade again.");
        }

        if (await runtime.ExistingArchiveAsync(set.Id, cancellationToken).ConfigureAwait(false) is not { } archive)
        {
            return new ServiceError(
                ServiceErrorReason.Refused,
                $"Backup set '{set.Name}' holds no archive yet, so there is nothing to upgrade — a set this "
                + $"build creates is born at format version {FormatLimits.FormatVersion}. Back it up once.");
        }

        var effective = archive.Repository.EffectiveFormatVersion;
        if (effective >= FormatLimits.FormatVersion)
        {
            return new ServiceError(
                ServiceErrorReason.Refused,
                $"Backup set '{set.Name}' already writes repository format version {effective}; this build "
                + $"writes {FormatLimits.FormatVersion}, so there is nothing to move it to.");
        }

        ushort from;
        try
        {
            from = await runtime.UpgradeSetFormatAsync(
                set.Id, FormatLimits.FormatVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return new ServiceError(ServiceErrorReason.Failed, exception.Message);
        }

        var nowMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        runtime.Notices.Resolve($"format-upgradable:{set.Id}", nowMs);

        return new ConfigurationChangeResult(
        [
            $"Backup set '{set.Name}': repository format {from} → {FormatLimits.FormatVersion}. The record is "
            + "signed under the repository's signing key and appended; the descriptor is unchanged.",
            "Everything already sealed stays exactly as it is and reads as it always did; the newer format "
            + "begins at the set's next blob.",
            "The record reaches each destination on the next reconciling pass — until it arrives, a copy holds "
            + "newer blobs than the record it has, which costs nothing because every blob declares its own "
            + "container.",
            "This cannot be undone, and a build older than this one would read the newer blobs as damage "
            + "rather than as a format it does not know (ADR-0066).",
        ]);
    }

    private async ValueTask<ServiceResult> RetireStagingAsync(
        RetireStagingCommand command, CancellationToken cancellationToken)
    {
        var configuration = runtime.Configuration;
        var set = configuration.FindSet(command.SetName);
        if (set is null)
        {
            return new ServiceError(
                ServiceErrorReason.NotFound, $"No backup set named '{command.SetName}' is configured.");
        }

        if (!set.DirectShip)
        {
            return new ServiceError(
                ServiceErrorReason.Refused,
                $"Backup set '{set.Name}' is not direct-ship; its staging archive is where its backups live.");
        }

        var stagingPath = runtime.ArchivePath(set.Id);
        if (!File.Exists(Path.Combine(stagingPath, Repository.RepositoryLifecycle.DescriptorKey.Value)))
        {
            return new ServiceError(
                ServiceErrorReason.Refused, $"Backup set '{set.Name}' holds no staging archive to retire.");
        }

        // The archive names the repository id every replica directory is
        // keyed by — one open, outside the loop, because it answers the same
        // for every destination.
        var archive = await runtime.ExistingArchiveAsync(set.Id, cancellationToken).ConfigureAwait(false);
        if (archive is null)
        {
            return new ServiceError(
                ServiceErrorReason.Refused, $"Backup set '{set.Name}' has no archive open to compare against.");
        }

        // The union of what the destinations hold. Reachability is required
        // of every referenced local-path destination: an absent drive might
        // be the only holder of something staging is about to stop holding.
        var union = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in set.Destinations)
        {
            if (configuration.FindDestination(reference.Ref) is not
                { Kind: DestinationKind.LocalPath } destination)
            {
                continue;
            }

            if (destination.AddressDefect is { } defect)
            {
                return new ServiceError(
                    ServiceErrorReason.Refused, $"Destination '{destination.Name}': {defect}");
            }

            if (!Directory.Exists(destination.Path))
            {
                return new ServiceError(
                    ServiceErrorReason.Refused,
                    $"Destination '{destination.Name}' at '{destination.Path}' is not reachable; retirement "
                    + "needs every destination present to prove nothing would be lost.");
            }

            var replica = new Storage.Local.LocalFileSystemObjectStore(
                Path.Combine(destination.Path!, archive.Repository.RepositoryId.ToString()));
            await foreach (var entry in replica.ListAsync(
                Storage.Abstractions.ObjectPrefix.All, Storage.Abstractions.ListOptions.Default, cancellationToken)
                .ConfigureAwait(false))
            {
                union.Add(entry.Key.Value);
            }
        }

        // What retirement could actually cost is narrower than what staging
        // happens to hold, and the difference is the whole of this gate. The
        // flip copied every non-blob object into the metadata store
        // (ADR-0046), so only blob content can be lost here — and only blob
        // content that a snapshot the repository still lists can reach. An
        // object outside that closure is not history anybody is owed, and no
        // pass will ever carry it: the copy follows the snapshot graph and,
        // under a per-destination policy, the keep-set closure (FR-GC-010),
        // so a blob a policy stranded or an interrupted run left behind is
        // invisible to it. Demanding one anyway refused retirement for ever
        // and made the archive's disk space the hostage, which is the one
        // thing retirement exists to release.
        var survey = await Retention.StagingMark.SurveyAsync(
            archive.Store, archive.Repository, cancellationToken).ConfigureAwait(false);
        if (survey.Undecodable.Count > 0)
        {
            return new ServiceError(
                ServiceErrorReason.Refused,
                $"{survey.Undecodable.Count} snapshot object(s) will not decode, so what the staging archive "
                + "still owes cannot be established; run the verify verb before retiring.");
        }

        using var reader = new Repository.RepositoryReader(
            archive.Repository.RepositoryId, archive.Repository.Keys, archive.Store);
        await reader.LoadBlobsAsync(cancellationToken).ConfigureAwait(false);

        var (reachable, unwalkable) = await Retention.StagingMark.MarkAsync(
            reader, survey.Snapshots, cancellationToken).ConfigureAwait(false);
        if (unwalkable.Count > 0)
        {
            return new ServiceError(
                ServiceErrorReason.Refused,
                $"{unwalkable.Count} manifest(s) in the live history would not read, so nothing here can "
                + "prove the archive is safe to delete; run the verify verb before retiring.");
        }

        var needed = reader.Blobs
            .Where(blob => blob.Records.Any(record => reachable.Contains(record.ObjectId)))
            .Select(blob => blob.StoreKey.Value)
            .ToHashSet(StringComparer.Ordinal);

        // The metadata plane's own safety check. Migration is idempotent and
        // runs at the first open after the flip, so this should hold of every
        // set that got here — but it is the one way a non-blob object could
        // exist in staging alone, and deleting the only copy of an index
        // delta is not something to discover afterwards.
        var metadata = new Storage.Local.LocalFileSystemObjectStore(runtime.SetMetadataPath(set.Id));
        var metadataHeld = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var entry in metadata.ListAsync(
            Storage.Abstractions.ObjectPrefix.All, Storage.Abstractions.ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            metadataHeld.Add(entry.Key.Value);
        }

        var staging = new Storage.Local.LocalFileSystemObjectStore(stagingPath);
        var missing = new List<string>();
        var unmigrated = new List<string>();
        var discarded = 0L;
        await foreach (var entry in staging.ListAsync(
            Storage.Abstractions.ObjectPrefix.All, Storage.Abstractions.ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            var key = entry.Key.Value;
            if (key.StartsWith("tombstones/", StringComparison.Ordinal)
                || key.StartsWith("leases/", StringComparison.Ordinal)
                || union.Contains(key))
            {
                continue;
            }

            if (!key.StartsWith("blobs/", StringComparison.Ordinal))
            {
                if (!metadataHeld.Contains(key))
                {
                    unmigrated.Add(key);
                }

                continue;
            }

            // A blob the reader could not open is absent from `needed` by
            // construction, and that is the right answer: damaged bytes no
            // restore can use are not a reason to hold the archive. The
            // count says so rather than letting it pass in silence.
            if (needed.Contains(key))
            {
                missing.Add(key);
            }
            else
            {
                discarded++;
            }
        }

        if (unmigrated.Count > 0)
        {
            return new ServiceError(
                ServiceErrorReason.Refused,
                $"{unmigrated.Count} metadata object(s) — {Sample(unmigrated)} — are held by the staging "
                + "archive alone and never reached this set's metadata store; the flip's migration did not "
                + "complete, and retiring now would delete the only copy.");
        }

        if (missing.Count > 0)
        {
            return new ServiceError(
                ServiceErrorReason.Refused,
                $"{missing.Count} blob(s) the live history still needs — {Sample(missing)} — have reached no "
                + "destination; run a scheduler pass (or the sync verb) to finish seeding, then retire again.");
        }

        try
        {
            Directory.Delete(stagingPath, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ServiceError(ServiceErrorReason.Failed, exception.Message);
        }

        var nowMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        runtime.Notices.Resolve($"staging-retirable:{set.Id}", nowMs);

        return new ConfigurationChangeResult(
        [
            $"Backup set '{set.Name}': the staging archive was retired; every blob the live history needs "
            + "is at a destination.",
            discarded > 0
                ? $"{discarded} blob(s) no live snapshot reaches went with it — history a retention policy "
                + "dropped, or bytes an interrupted run left behind. Nothing referenced them."
                : "It held nothing beyond that.",
            "The set publishes straight to its destinations; the agent keeps metadata only (ADR-0046).",
        ]);
    }

    /// <summary>
    /// Up to three keys of a refusal's evidence, so the message names
    /// something the operator can go and look at rather than a bare count.
    /// </summary>
    private static string Sample(List<string> keys) =>
        string.Join(", ", keys.Take(3)) + (keys.Count > 3 ? ", …" : string.Empty);

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

        // Draft roots make an unresolvable set answerable (ADR-0040): the
        // walk classifies against an empty baseline, which is what an editor
        // building a brand-new set needs. Without draft roots, an unknown
        // set stays a stated miss.
        IReadOnlyList<FallbackPlan.Filesystem.ScanRoot> roots;
        if (command.Roots is { Count: > 0 } draftRoots)
        {
            var labelled = ClientConfiguration.DeriveLabels(
                [.. draftRoots.Select(root => new BackupRootConfiguration { Path = root.Path, Label = root.Label })]);
            roots = [.. labelled.Select(root => new FallbackPlan.Filesystem.ScanRoot(root.Path, root.Label))];
        }
        else if (command.Root is { } draftRoot)
        {
            roots = [new FallbackPlan.Filesystem.ScanRoot(draftRoot)];
        }
        else if (set is not null)
        {
            roots = SetChangeScan.ScanRootsOf(set);
        }
        else
        {
            return new ServiceError(
                ServiceErrorReason.NotFound,
                command.SetName is null
                    ? "No backup set is configured."
                    : $"No backup set named '{command.SetName}' is configured.");
        }

        var includes = command.IncludeRules ?? set?.IncludeRules ?? [];
        var excludes = command.ExcludeRules ?? set?.ExcludeRules ?? [];

        // Refused here as a stated error rather than downstream as a thrown
        // guard — the draft may be mid-edit, and a defect is its answer.
        if (!PathRuleSet.TryCreate(includes, excludes, caseSensitive: true, out _, out var ruleDefects))
        {
            return new ServiceError(ServiceErrorReason.InvalidArgument, string.Join("; ", ruleDefects));
        }

        var missing = roots.Where(root => !Directory.Exists(root.Path)).Select(root => root.Path).ToList();
        if (missing.Count > 0)
        {
            return new ServiceError(
                ServiceErrorReason.NotFound,
                missing.Count == 1
                    ? $"root '{missing[0]}' does not exist"
                    : $"roots do not exist: '{string.Join("', '", missing)}'");
        }

        // A deleted file, and one the rules stop capturing, is named by the
        // backup alone, so those names go only to a caller who unlocked the set
        // with the passphrase (FR-WOR-007). What is on disk now is named either
        // way: the folder picker shows it to anyone signed in.
        var unlocked = !NamesNeedThePassphrase;
        if (command.Source is not null)
        {
            if (RefuseUnproved(command.Source, command.SessionId, set?.Id ?? string.Empty, "A comparison") is { } unproved)
            {
                return unproved;
            }

            unlocked = true;
        }

        var limit = Math.Clamp(
            command.SampleLimit ?? SetChangeScan.DefaultSampleLimit, 1, SetChangeScan.MaxSampleLimit);
        var (comparison, baseline) = set is null
            ? (await Repository.SourceComparer.CompareAsync(
                new FallbackPlan.Filesystem.Local.LocalFileSystemSource(), roots, includes, excludes,
                catalogue: null, baselineSnapshotId: null, limit, cancellationToken).ConfigureAwait(false),
                (Repository.Catalogue.CatalogueSnapshot?)null)
            : await SetChangeScan.CompareAsync(
                runtime, set, roots, includes, excludes, limit, cancellationToken).ConfigureAwait(false);

        var withheld = !unlocked && baseline is not null;
        return new SetChangePreviewResult(
            set?.Name ?? command.SetName ?? "(draft)",
            baseline is null ? null : Convert.ToHexStringLower(baseline.SnapshotId.Span),
            baseline?.CapturedAt,
            comparison.Unchanged,
            ToBucket(comparison.New),
            ToBucket(comparison.Updated),
            ToBucket(comparison.MetadataOnly),
            ToBucket(comparison.Moved),
            ToBucket(comparison.Deleted, withheld),
            ToBucket(comparison.NoLongerIncluded, withheld),
            comparison.Failures,
            limit,
            withheld);

        static ChangeBucketDescriptor ToBucket(Repository.SourceChangeBucket bucket, bool withheld = false) =>
            new(bucket.Count, withheld ? [] : bucket.Sample);
    }

    private SetDraftValidationResult ValidateSetDraft(ValidateSetDraftCommand command)
    {
        List<string> defects = [];
        List<string> allowed = [];

        // Validity is case-independent, so case sensitivity here is a
        // placeholder the same way it is in configuration validation.
        if (!PathRuleSet.TryCreate(
            command.IncludeRules, command.ExcludeRules, caseSensitive: true, out _, out var ruleDefects))
        {
            defects.AddRange(ruleDefects);
        }

        // The circular-capture guard, live in the editor (FR-DEST-011): a
        // defect rather than a warning, because the save this draft previews
        // would be refused for exactly this reason. Judged against the
        // declared destinations whatever the draft references — any set
        // capturing any destination's storage is the hazard.
        if (command.Roots is { Count: > 0 } draftRoots && ConfigurationOrNull() is { } declared)
        {
            var draft = new BackupSetConfiguration
            {
                Id = new string('0', 32),
                Name = string.Empty,
                Roots = ClientConfiguration.DeriveLabels(
                    [.. draftRoots.Select(path => new BackupRootConfiguration { Path = path })]),
                IncludeRules = command.IncludeRules,
                ExcludeRules = command.ExcludeRules,
            };
            defects.AddRange(CircularCapture.Defects(
                [draft], declared.Destinations, ServiceStorage(), named: false));

            // The placement condition (ADR-0051, FR-DEST-017) is a defect for
            // the same reason, judged as the save judges it, in its words.
            // That takes the set the draft is of: a draft naming none comes
            // from a client older than the question (contract 1.49), and a
            // standing binding the save would leave alone cannot be told from
            // a new one, so it is not asked. A Debug build lets the binding
            // stand, so the draft warns in the line the save will give
            // (Amendment 2).
            if (command.SetId is { Length: > 0 } setId && command.Destinations is { Count: > 0 } chosen)
            {
                var existing = declared.BackupSets.FirstOrDefault(set =>
                    string.Equals(set.Id, setId, StringComparison.Ordinal));
                foreach (var (destination, conflict) in PlacementConflicts(declared, existing, draftRoots, chosen))
                {
                    if (runtime.AllowsSameDrivePlacement)
                    {
                        allowed.Add(PlacementAllowed(destination, conflict));
                    }
                    else
                    {
                        defects.Add(PlacementRefusal(destination, conflict));
                    }
                }
            }
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

        var durability = DurabilityWarnings(command);
        return new SetDraftValidationResult(
            defects, nextRuns, allowed.Count == 0 ? durability : [.. allowed, .. durability ?? []]);
    }

    /// <summary>
    /// What is sound but unwise about where this draft would be durable
    /// (FR-SNP-007, ADR-0018).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A warning, never a defect: the operator may have exactly one disk and
    /// know it, and a product that refused to protect anything until they
    /// bought a second one would protect nothing at all. What it must not do
    /// is let them believe they are covered — which is why the wording says
    /// what the status page will go on to say, in the same words.
    /// </para>
    /// <para>
    /// The comparison is <see cref="DestinationStatus.Describe"/>'s, not a
    /// second one written for drafts. It already handles the declaration
    /// winning over inference, the every-root rule for multi-root sets
    /// (ADR-0040), and the conservative answer when the platform will not say
    /// which volume a path is on.
    /// </para>
    /// <para>
    /// This is where FR-SNP-007's "first run warns" lives, and it warns on
    /// every edit rather than only the first — the requirement's failure is a
    /// person believing a backup survives something it does not, and that
    /// belief is available to form at any point, not only once.
    /// </para>
    /// </remarks>
    private List<string>? DurabilityWarnings(ValidateSetDraftCommand command)
    {
        if (command.Roots is not { Count: > 0 } roots || command.Destinations is not { Count: > 0 } names)
        {
            // The draft did not ask. An editor that has not reached the
            // destination step yet should not be told its set is undurable.
            return null;
        }

        var configuration = ConfigurationOrNull();
        if (configuration is null)
        {
            return null;
        }

        var nowMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var best = FailureDomain.SameVolume;
        var unknown = new List<string>();

        foreach (var name in names)
        {
            var destination = configuration.FindDestination(name);
            if (destination is null)
            {
                unknown.Add(name);
                continue;
            }

            var input = DestinationStatus.Describe(
                name, destination, [.. roots], record: null, lastCompletedAt: 0, nowMs, runtime.VolumeIdOf,
                hasSnapshot: false);

            if (input.Domain > best)
            {
                best = input.Domain;
            }
        }

        var warnings = new List<string>();

        foreach (var name in unknown)
        {
            warnings.Add(
                $"'{name}' is not a declared destination, so this set would reference something that does "
                + "not exist.");
        }

        if (unknown.Count == names.Count)
        {
            return warnings;
        }

        if (best <= FailureDomain.SameMachine)
        {
            warnings.Add(
                best == FailureDomain.SameVolume
                    ? "Every destination for this set is on the same volume as its source. Losing that disk "
                        + "loses the backup with it, so snapshots will report `captured`, never `protected`."
                    : "Every destination for this set is on this machine. Another disk survives losing a "
                        + "disk and nothing more, so snapshots will report `captured`, never `protected`. "
                        + "A paired peer or a removable drive kept elsewhere is what changes that.");
        }

        return warnings.Count == 0 ? null : warnings;
    }

    /// <summary>
    /// The service's own directories, for the circular-capture guard — a
    /// source root over either captures the service into its own backup, and
    /// only the agent knows where they are.
    /// </summary>
    private (string Description, string Path)[] ServiceStorage() =>
    [
        ("state directory", runtime.Options.StateDirectory),
        ("archives root", runtime.Options.ArchivesRoot),
    ];

    /// <summary>The configuration, or null when it will not load.</summary>
    /// <remarks>
    /// A draft check is advice. It must not be the thing that turns a typo in
    /// <c>config.json</c> into a failed request, when the editor asking is
    /// very likely the way that typo gets fixed.
    /// </remarks>
    private ClientConfiguration? ConfigurationOrNull()
    {
        try
        {
            return runtime.Configuration;
        }
        catch (ClientStateException)
        {
            return null;
        }
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
