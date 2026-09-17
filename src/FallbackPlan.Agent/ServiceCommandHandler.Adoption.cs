using System.Security.Cryptography;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Descriptor;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Records;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Agent;

/// <summary>
/// Adopting a destination's archives after a rebuild
/// ([ADR-0061](../../docs/adr/0061-adopt-a-destinations-archives.md)): the
/// last piece of the recovery model ADR-0060 left — a person holds the
/// passphrase and knows where the backups are, and that has to be enough
/// to carry on <em>incrementally</em>, under the original ids, rather than
/// to start a second archive beside the first.
/// </summary>
/// <remarks>
/// <para>
/// Discovery is credential-free: every fact it reports is read from the
/// unencrypted descriptor or counted from cleartext object names. Adoption
/// is the provisioning ceremony of <c>ServiceCommandHandler.WriteOnly</c>
/// pointed at a destination rather than at a set: the envelope is opened,
/// the derived sealing public key is proved against the descriptor, and
/// <b>nothing is written before that proof</b>.
/// </para>
/// <para>
/// The order of the writes is the order a half-finished adoption can be
/// re-run over: the catalogue first (delete-and-rebuild), then the metadata
/// copy (if-absent puts), the writer identity, the credential (replaced),
/// the configuration, and the ledger last. A crash anywhere leaves a state
/// directory the next adopt overwrites cleanly, and no configured set whose
/// archive the service could mistake for one it must create.
/// </para>
/// </remarks>
public sealed partial class ServiceCommandHandler
{
    private async ValueTask<ServiceResult> DiscoverArchivesAsync(
        DiscoverArchivesCommand command, CancellationToken cancellationToken)
    {
        var (destination, refusal) = ResolveAdoptableDestination(command.DestinationName);
        if (refusal is not null)
        {
            return refusal;
        }

        var archives = new List<DiscoveredArchiveDescriptor>();
        var warnings = new List<string>();

        if (destination!.Kind == DestinationKind.Peer)
        {
            // A peer's inventory names what THIS device owns there (07 §3.5)
            // — after a claim (ADR-0053), the replicas the dead machine
            // wrote. Each is opened over the retrieval session and read
            // exactly as a directory would be.
            try
            {
                foreach (var repositoryIdHex in await PeerInventoryAsync(destination, cancellationToken).ConfigureAwait(false))
                {
                    var client = await PeerRetrievalClient.DialAsync(
                        runtime, destination, Convert.FromHexString(repositoryIdHex), cancellationToken)
                        .ConfigureAwait(false);
                    await using (client.ConfigureAwait(false))
                    {
                        await DescribeArchiveAsync(
                            new PeerRetrievalObjectStore(client), repositoryIdHex, archives, warnings, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
            catch (Protocol.PeerProtocolException refused)
            {
                return new ServiceError(
                    ServiceErrorReason.Failed, $"Peer '{destination.Name}' refused the retrieval: {refused.Message}");
            }
            catch (Exception unreachable) when (unreachable is IOException or System.Net.Sockets.SocketException)
            {
                return new ServiceError(
                    ServiceErrorReason.Unavailable, $"Peer '{destination.Name}' is not reachable: {unreachable.Message}");
            }

            return new ArchivesDiscoveredResult(destination.Name, archives, warnings);
        }

        foreach (var candidate in Directory.GetDirectories(destination.Path!).Order(StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(candidate, RepositoryLifecycle.DescriptorKey.Value)))
            {
                continue;
            }

            await DescribeArchiveAsync(
                new LocalFileSystemObjectStore(candidate), Path.GetFileName(candidate), archives, warnings, cancellationToken)
                .ConfigureAwait(false);
        }

        return new ArchivesDiscoveredResult(destination.Name, archives, warnings);
    }

    /// <summary>
    /// One discovery row from one candidate store: the descriptor and the
    /// cleartext snapshot survey, nothing keyed. A descriptor that does not
    /// read becomes a warning, not a failure — one damaged archive must not
    /// hide the ones beside it from the person looking for them.
    /// </summary>
    private async ValueTask DescribeArchiveAsync(
        IObjectStore store,
        string candidateName,
        List<DiscoveredArchiveDescriptor> archives,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        RepositoryDescriptor descriptor;
        try
        {
            descriptor = await RepositoryLifecycle.ReadDescriptorAsync(store, cancellationToken).ConfigureAwait(false);
        }
        catch (RepositoryOpenException damaged)
        {
            warnings.Add($"'{candidateName}' looks like an archive but its descriptor does not read: {damaged.Message}");
            return;
        }

        var survey = await FanOut.PublicationSurveyAsync(store, cancellationToken).ConfigureAwait(false);
        var repositoryId = descriptor.RepositoryId.ToString();
        var installationSealingKey = InstallationSealingPublicKey();
        archives.Add(new DiscoveredArchiveDescriptor(
            repositoryId,
            descriptor.FormatVersion,
            descriptor.CreatedAt,
            descriptor.CreatedBy,
            Convert.ToHexStringLower(descriptor.KdfSalt.Span),
            descriptor.KdfParameters.MemoryKiB,
            descriptor.KdfParameters.Iterations,
            descriptor.KdfParameters.Parallelism,
            Convert.ToHexStringLower(descriptor.SealingPublicKey.Span),
            survey.SnapshotObjects,
            survey.Sequence,
            OwnedRepositories().GetValueOrDefault(repositoryId),
            installationSealingKey is not null
                && descriptor.SealingPublicKey.Span.SequenceEqual(installationSealingKey)));
    }

    /// <summary>The repository ids this device owns at a peer (07 §3.5), lowercase hex.</summary>
    private async ValueTask<List<string>> PeerInventoryAsync(
        DestinationConfiguration destination, CancellationToken cancellationToken)
    {
        var inventory = await PeerRetrievalClient.DialAsync(
            runtime, destination, new byte[Protocol.RetrieveOpen.RepositoryIdLength], cancellationToken)
            .ConfigureAwait(false);
        await using (inventory.ConfigureAwait(false))
        {
            var page = await inventory.ListPageAsync(string.Empty, string.Empty, cancellationToken)
                .ConfigureAwait(false);
            return [.. page.Keys.Select(key => key.ToLowerInvariant()).Order(StringComparer.Ordinal)];
        }
    }

    private async ValueTask<ServiceResult> AdoptArchiveAsync(
        AdoptArchiveCommand command, CancellationToken cancellationToken)
    {
        var (destination, refusal) = ResolveAdoptableDestination(command.DestinationName);
        if (refusal is not null)
        {
            return refusal;
        }

        if (command.RepositoryId.Length != 32 || !command.RepositoryId.All(Uri.IsHexDigit))
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument, "A repository id is thirty-two hex characters.");
        }

        var repositoryIdHex = command.RepositoryId.ToLowerInvariant();
        var replicaRoot = destination!.Kind == DestinationKind.LocalPath ? Path.Combine(destination.Path!, repositoryIdHex) : null;
        if (replicaRoot is not null && !File.Exists(Path.Combine(replicaRoot, RepositoryLifecycle.DescriptorKey.Value)))
        {
            return new ServiceError(
                ServiceErrorReason.NotFound,
                $"Destination '{destination.Name}' holds no archive '{repositoryIdHex}' — run discovery and pick one it lists.");
        }

        if (replicaRoot is null)
        {
            // A peer: the replica must be this device's there — claimed
            // (ADR-0053) — which the owner inventory says before any
            // envelope is looked at, as the directory's existence does for
            // a local path.
            try
            {
                var inventory = await PeerInventoryAsync(destination, cancellationToken).ConfigureAwait(false);
                if (!inventory.Contains(repositoryIdHex, StringComparer.Ordinal))
                {
                    return new ServiceError(
                        ServiceErrorReason.NotFound,
                        $"Peer '{destination.Name}' holds no replica '{repositoryIdHex}' attributed to this machine — "
                        + "claim it first (ADR-0053), then run discovery and pick one it lists.");
                }
            }
            catch (Protocol.PeerProtocolException refused)
            {
                return new ServiceError(
                    ServiceErrorReason.Failed, $"Peer '{destination.Name}' refused the retrieval: {refused.Message}");
            }
            catch (Exception unreachable) when (unreachable is IOException or System.Net.Sockets.SocketException)
            {
                return new ServiceError(
                    ServiceErrorReason.Unavailable, $"Peer '{destination.Name}' is not reachable: {unreachable.Message}");
            }
        }

        if (ScheduleDefect(command.Schedule) is { } scheduleDefect)
        {
            return new ServiceError(ServiceErrorReason.InvalidArgument, scheduleDefect);
        }

        byte[] envelope;
        try
        {
            envelope = Convert.FromHexString(command.Envelope);
        }
        catch (FormatException)
        {
            return new ServiceError(ServiceErrorReason.InvalidArgument, "The provisioning envelope is not hex.");
        }

        RepositoryWriteCredential credential;
        try
        {
            (credential, _, _) = runtime.GrantRecipient.OpenProvision(envelope);
        }
        catch (Exception malformed) when (malformed is SealedContentException or ArgumentException)
        {
            // Too short to be an envelope at all, or sealed to someone else:
            // both are "not an envelope this service can open", and neither
            // is this verb's crash to report.
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                "The provisioning envelope does not open — it was sealed to a different service's recipient key.");
        }

        using (credential)
        {
            if (replicaRoot is null)
            {
                // Read over the retrieval session through the same steps a
                // directory takes.
                try
                {
                    var client = await PeerRetrievalClient.DialAsync(
                        runtime, destination, Convert.FromHexString(repositoryIdHex), cancellationToken)
                        .ConfigureAwait(false);
                    await using (client.ConfigureAwait(false))
                    {
                        return await AdoptFromStoreAsync(
                            command, destination, new PeerRetrievalObjectStore(client), repositoryIdHex, credential,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Protocol.PeerProtocolException refused)
                {
                    return new ServiceError(
                        ServiceErrorReason.Failed, $"Peer '{destination.Name}' refused the retrieval: {refused.Message}");
                }
                catch (Exception unreachable) when (unreachable is IOException or System.Net.Sockets.SocketException)
                {
                    return new ServiceError(
                        ServiceErrorReason.Unavailable, $"Peer '{destination.Name}' is not reachable: {unreachable.Message}");
                }
            }

            return await AdoptFromStoreAsync(
                command, destination,
                new LocalFileSystemObjectStore(replicaRoot, runtime.LoggerFor<LocalFileSystemObjectStore>()),
                repositoryIdHex, credential, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Everything below the resolve step reads through <see cref="IObjectStore"/>:
    /// a directory at a local path and a replica at a peer take the same road
    /// from here, which is what let the peer half of ADR-0061 add an
    /// enumerator and a store and nothing else.
    /// </summary>
    private async ValueTask<ServiceResult> AdoptFromStoreAsync(
        AdoptArchiveCommand command,
        DestinationConfiguration destination,
        IObjectStore replicaStore,
        string repositoryIdHex,
        RepositoryWriteCredential credential,
        CancellationToken cancellationToken)
    {
        {
            RepositoryDescriptor descriptor;
            try
            {
                descriptor = await RepositoryLifecycle.ReadDescriptorAsync(replicaStore, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (RepositoryOpenException damaged)
            {
                return new ServiceError(
                    ServiceErrorReason.Failed,
                    $"Archive '{repositoryIdHex}' at destination '{destination.Name}' has a descriptor that does not read: {damaged.Message}");
            }

            if (!string.Equals(descriptor.RepositoryId.ToString(), repositoryIdHex, StringComparison.Ordinal))
            {
                return new ServiceError(
                    ServiceErrorReason.Failed,
                    $"The directory '{repositoryIdHex}' at destination '{destination.Name}' holds a repository whose "
                    + $"descriptor names a different id ({descriptor.RepositoryId}) — it was moved or renamed by hand.");
            }

            // The proof, and the last line before anything can be written:
            // the passphrase the envelope was derived from is this
            // repository's, or it is not.
            if (!credential.SealingPublicKey.SequenceEqual(descriptor.SealingPublicKey.Span))
            {
                return new ServiceError(
                    ServiceErrorReason.InvalidArgument,
                    "The derived sealing public key does not match this repository's descriptor — the "
                    + "passphrase it was derived from is not this repository's.");
            }

            OpenedRepository repository;
            try
            {
                repository = await RepositoryLifecycle.OpenAsync(
                        replicaStore, credential, cancellationToken, runtime.LoggerFor(typeof(RepositoryLifecycle)))
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is KeyUnwrapFailedException or RepositoryOpenException)
            {
                return new ServiceError(
                    ServiceErrorReason.Failed,
                    $"Archive '{repositoryIdHex}' at destination '{destination.Name}' would not open: {exception.Message}");
            }

            using (repository)
            {
                return await AdoptOpenedArchiveAsync(
                    command, destination, replicaStore, repository, credential, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<ServiceResult> AdoptOpenedArchiveAsync(
        AdoptArchiveCommand command,
        DestinationConfiguration destination,
        IObjectStore replicaStore,
        OpenedRepository repository,
        RepositoryWriteCredential credential,
        CancellationToken cancellationToken)
    {
        var repositoryIdHex = repository.RepositoryId.ToString();
        var warnings = new List<string>();
        var lines = new List<string>();

        // The catalogue is rebuilt at the runtime's REAL path for this
        // repository — the one ArchiveForAsync opens by name — over the
        // replica, because the metadata store does not exist yet. A stale
        // one from a previous half-adoption is deleted rather than trusted.
        var cataloguePath = Path.Combine(runtime.Options.StateDirectory, $"catalogue-{repositoryIdHex}.db");
        if (File.Exists(cataloguePath))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(cataloguePath);
        }

        RecordedShape shape;
        var survey = await FanOut.PublicationSurveyAsync(replicaStore, cancellationToken).ConfigureAwait(false);
        using (var reader = await OpenMetadataReaderAsync(replicaStore, repository, cancellationToken).ConfigureAwait(false))
        using (var catalogue = await RebuildCatalogueAsync(
            replicaStore, repository, cataloguePath, reader, warnings, cancellationToken).ConfigureAwait(false))
        {
            shape = await ReadRecordedShapeAsync(catalogue, reader, replicaStore, repository, cancellationToken)
                .ConfigureAwait(false);
        }

        // What the set is declared as: the archive's record, overridden
        // field by field by what the caller said — and required exactly
        // where the archive recorded nothing.
        var setId = shape.SetId ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var name = command.SetName ?? shape.Policy?.SetName;
        if (string.IsNullOrWhiteSpace(name))
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                shape.Policy is null
                    ? "This archive holds no snapshot, so it records no shape — give the set a name and at least one root."
                    : "This archive records no set name — give the set a name.");
        }

        IReadOnlyList<BackupRootConfiguration> requestedRoots =
            command.Roots is { Count: > 0 } givenRoots
                ? [.. givenRoots.Select(root => new BackupRootConfiguration { Path = root.Path, Label = root.Label })]
                : [.. (shape.Policy?.Roots ?? []).Select(root => new BackupRootConfiguration { Path = root.Path, Label = root.Label })];
        if (requestedRoots.Count == 0)
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                "This archive records no root folders — give the set at least one root.");
        }

        var schedule = command.Schedule ?? shape.Policy?.Schedule;
        IReadOnlyList<string> includeRules = shape.Policy?.IncludeRules ?? [];
        IReadOnlyList<string> excludeRules = shape.Policy?.ExcludeRules ?? [];

        ClientConfiguration configuration;
        try
        {
            configuration = runtime.Configuration;
        }
        catch (ClientStateException exception)
        {
            return new ServiceError(ServiceErrorReason.InvalidArgument, exception.Message);
        }

        // Idempotence and its refusals, before any write.
        var existing = configuration.BackupSets
            .FirstOrDefault(set => string.Equals(set.Id, setId, StringComparison.Ordinal));
        if (existing is not null)
        {
            var existingRepository = await LocalRepositoryIdOfAsync(existing, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(existingRepository, repositoryIdHex, StringComparison.Ordinal))
            {
                return new ServiceError(
                    ServiceErrorReason.Refused,
                    $"Set '{existing.Name}' ({setId}) is already configured against a different archive"
                    + $"{(existingRepository is null ? string.Empty : $" ({existingRepository})")} — delete that set "
                    + "first if this archive is the one to keep.");
            }

            return await AcknowledgeAdoptedAsync(
                existing, destination, repositoryIdHex, credential, shape, survey, configuration, cancellationToken)
                .ConfigureAwait(false);
        }

        if (configuration.BackupSets.Any(set => string.Equals(set.Name, name, StringComparison.Ordinal)))
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                $"A backup set named '{name}' already exists — give the adopted set another name.");
        }

        IReadOnlyList<BackupRootConfiguration> resolvedRoots;
        try
        {
            resolvedRoots = ClientConfiguration.DeriveLabels(requestedRoots);
        }
        catch (ClientStateException exception)
        {
            return new ServiceError(ServiceErrorReason.InvalidArgument, exception.Message);
        }

        var replacement = new BackupSetConfiguration
        {
            Id = setId,
            Name = name,
            Roots = resolvedRoots,
            IncludeRules = includeRules,
            ExcludeRules = excludeRules,
            Schedule = schedule,
            Priority = command.Priority,
            DirectShip = true,
            Destinations = [new SetDestinationReference { Ref = destination.Name }],
        };

        // The same guards an upsert applies (FR-DEST-011, ADR-0051): an
        // adopted set must not be born capturing the service, or on the
        // volume its roots live on.
        var circular = CircularCapture.Defects([replacement], configuration.Destinations, ServiceStorage());
        if (circular.Count > 0)
        {
            return new ServiceError(ServiceErrorReason.InvalidArgument, string.Join(" ", circular));
        }

        if (destination.Kind == DestinationKind.LocalPath
            && LocalDestinationPlacement.Judge(
                [.. resolvedRoots.Select(root => root.Path)], destination.Path!, runtime.VolumeIdOf, runtime.DiskIdOf)
            is { } conflict)
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                $"Destination '{destination.Name}' shares {(conflict.SamePhysicalDisk ? "a physical drive" : "a volume")} "
                + $"with root '{conflict.Root}' — a backup on the drive the files live on dies with them. "
                + "Choose a local destination on a different drive (ADR-0051).");
        }

        // ---- writes ----

        var metadataPath = runtime.SetMetadataPath(setId);
        Directory.CreateDirectory(metadataPath);
        await ServiceRuntime.CopyMetadataAsync(
            replicaStore, new LocalFileSystemObjectStore(metadataPath, runtime.LoggerFor<LocalFileSystemObjectStore>()),
            cancellationToken).ConfigureAwait(false);

        var writerResumed = await ResumeWriterIdentityAsync(replicaStore, lines, cancellationToken).ConfigureAwait(false);

        runtime.WriteCredentials.Save(setId, credential);

        try
        {
            (configuration with { BackupSets = [.. configuration.BackupSets, replacement] })
                .Save(runtime.ConfigurationPath);
        }
        catch (ClientStateException exception)
        {
            return new ServiceError(ServiceErrorReason.InvalidArgument, exception.Message);
        }

        var nowMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SeedLedger(setId, destination.Name, survey, nowMs);
        await runtime.EvictArchiveAsync(setId, cancellationToken).ConfigureAwait(false);

        var missingRoots = resolvedRoots.Where(root => !Directory.Exists(root.Path)).Select(root => root.Path).ToList();
        lines.Insert(0, $"Set '{name}' adopted from destination '{destination.Name}' under its original id; "
            + $"{shape.SnapshotCount} snapshot(s) resume here, and the next backup is incremental.");
        if (missingRoots.Count > 0)
        {
            lines.Add($"Recorded root folder(s) not found on this machine: {string.Join(", ", missingRoots)} — "
                + "edit the set before its next run, or restore them there first.");
        }

        if (shape.OtherSetIds.Count > 0)
        {
            lines.Add($"The archive also holds snapshots for other set id(s): {string.Join(", ", shape.OtherSetIds)}.");
        }

        lines.AddRange(warnings.Select(warning => $"Catalogue rebuild: {warning}"));
        lines.Add("This service can add to the archive and read its structure, but never file contents.");

        var logger = runtime.LoggerFor<ServiceCommandHandler>();
        Log.ArchiveAdopted(logger, setId, repositoryIdHex, destination.Name, shape.SnapshotCount, writerResumed);

        return new ArchiveAdoptedResult(
            setId, name, repositoryIdHex,
            [.. resolvedRoots.Select(root => new BackupRootDescriptor(root.Path, root.Label))],
            missingRoots, schedule, includeRules, excludeRules,
            shape.SnapshotCount, shape.NewestSnapshotId, shape.NewestSnapshotAt,
            WriterIdentityResumed: writerResumed, AlreadyAdopted: false, Lines: lines);
    }

    /// <summary>
    /// The set is already configured against this very archive: nothing is
    /// repeated, but what a first adoption would have left behind is made
    /// sure of — the destination referenced, its ledger row seeded, the
    /// credential stored — so a second run over a half-finished first one
    /// converges instead of refusing.
    /// </summary>
    private async ValueTask<ServiceResult> AcknowledgeAdoptedAsync(
        BackupSetConfiguration existing,
        DestinationConfiguration destination,
        string repositoryIdHex,
        RepositoryWriteCredential credential,
        RecordedShape shape,
        (ulong Sequence, string? NewestSnapshotKey, int SnapshotObjects) survey,
        ClientConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var lines = new List<string> { $"Set '{existing.Name}' is already configured against this archive; nothing was repeated." };
        var set = existing;
        if (!existing.Destinations.Any(reference => string.Equals(reference.Ref, destination.Name, StringComparison.Ordinal)))
        {
            set = existing with
            {
                Destinations = [.. existing.Destinations, new SetDestinationReference { Ref = destination.Name }],
            };
            var sets = configuration.BackupSets
                .Select(candidate => string.Equals(candidate.Id, existing.Id, StringComparison.Ordinal) ? set : candidate)
                .ToList();
            try
            {
                (configuration with { BackupSets = sets }).Save(runtime.ConfigurationPath);
            }
            catch (ClientStateException exception)
            {
                return new ServiceError(ServiceErrorReason.InvalidArgument, exception.Message);
            }

            lines.Add($"Destination '{destination.Name}' is now referenced by the set.");
        }

        if (runtime.WriteCredentials.TryLoad(existing.Id) is { } held)
        {
            held.Dispose();
        }
        else
        {
            runtime.WriteCredentials.Save(existing.Id, credential);
            lines.Add("The write credential is stored.");
        }

        if (runtime.DestinationSync.Find(existing.Id, destination.Name) is null)
        {
            SeedLedger(existing.Id, destination.Name, survey, (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        await runtime.EvictArchiveAsync(existing.Id, cancellationToken).ConfigureAwait(false);

        return new ArchiveAdoptedResult(
            existing.Id, existing.Name, repositoryIdHex,
            [.. set.Roots.Select(root => new BackupRootDescriptor(root.Path, root.Label))],
            [.. set.Roots.Where(root => !Directory.Exists(root.Path)).Select(root => root.Path)],
            set.Schedule, set.IncludeRules, set.ExcludeRules,
            shape.SnapshotCount, shape.NewestSnapshotId, shape.NewestSnapshotAt,
            WriterIdentityResumed: false, AlreadyAdopted: true, Lines: lines);
    }

    /// <summary>
    /// Seeds the destination's ledger row as a completed baseline at the
    /// replica's own publication head: the sink then admits the destination
    /// on the next run, and the first fan-out pass reconciles by listing
    /// rather than owing a full copy — the replica IS the full copy.
    /// </summary>
    private void SeedLedger(
        string setId, string destination, (ulong Sequence, string? NewestSnapshotKey, int SnapshotObjects) survey, ulong nowMs) =>
        runtime.DestinationSync.RecordSuccess(
            setId, destination, objects: 0, nowMs, syncedSequence: survey.Sequence,
            keepFingerprint: null, reconciled: false, baselineSnapshotId: survey.NewestSnapshotKey);

    /// <summary>
    /// Takes the archive's writer identity when that is safe (ADR-0061 §4):
    /// this state directory has published nothing — no sequence file for any
    /// repository — and the archive has exactly one writer. Otherwise the new
    /// identity stays, and the caller is told the next run re-sends once,
    /// because the device dedup domain never reuses another writer's segment.
    /// </summary>
    private async ValueTask<bool> ResumeWriterIdentityAsync(
        IObjectStore replicaStore, List<string> lines, CancellationToken cancellationToken)
    {
        var writers = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var entry in replicaStore.ListAsync(
            ObjectPrefix.Parse("journal/"), ListOptions.Default, cancellationToken).ConfigureAwait(false))
        {
            var segments = entry.Key.Value.Split('/');
            if (segments.Length >= 3)
            {
                writers.Add(segments[1]);
            }
        }

        var current = runtime.State.WriterId;
        if (writers.Count == 1 && string.Equals(writers.Single(), Base32.Encode(current), StringComparison.Ordinal))
        {
            // Already this writer — a state directory restored from a copy.
            return true;
        }

        var published = Directory.Exists(runtime.Options.StateDirectory)
            && Directory.GetFiles(runtime.Options.StateDirectory, "sequence-*.txt").Length > 0;
        var decoded = new byte[16];
        if (writers.Count == 1
            && !published
            && Base32.TryDecode(writers.Single(), decoded, out var written)
            && written == 16)
        {
            runtime.State.AdoptWriterId(decoded);
            var writerHex = Convert.ToHexStringLower(decoded);
            Log.WriterIdentityResumed(runtime.LoggerFor<ServiceCommandHandler>(), writerHex);
            lines.Add("This installation now writes under the archive's writer identity, so unchanged files are reused.");
            return true;
        }

        lines.Add(writers.Count > 1
            ? $"The archive has {writers.Count} writers, so this machine keeps its own writer identity: the next run "
              + "re-sends unchanged files once (ADR-0006's device domain)."
            : "This machine has already published under its own writer identity and keeps it: the next run "
              + "re-sends unchanged files once (ADR-0006's device domain).");
        return false;
    }

    /// <summary>
    /// What the archive says about the set that wrote it: the newest
    /// snapshot's set id and its policy manifest (ADR-0061 §1 — roots, name,
    /// schedule, rules), plus the other set ids it holds snapshots for.
    /// </summary>
    private static async ValueTask<RecordedShape> ReadRecordedShapeAsync(
        CatalogueDb catalogue,
        RepositoryReader reader,
        IObjectStore store,
        OpenedRepository repository,
        CancellationToken cancellationToken)
    {
        var snapshots = catalogue.EnumerateSnapshots();
        if (snapshots.Count == 0)
        {
            return new RecordedShape(null, null, 0, null, null, []);
        }

        var newest = snapshots.MaxBy(row => row.CapturedAt)!;
        var setId = Convert.ToHexStringLower(newest.BackupSetId.Span);
        var others = snapshots
            .Select(row => Convert.ToHexStringLower(row.BackupSetId.Span))
            .Where(id => !string.Equals(id, setId, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        PolicyManifest? policy = null;
        var key = MetadataStoreKeys.Snapshot(newest.DeviceId.Span, newest.BackupSetId.Span, newest.SnapshotId.Span);
        using (var read = await store.OpenReadAsync(key, range: null, cancellationToken).ConfigureAwait(false))
        {
            if (read.Outcome == OpenReadOutcome.Found && read.Content is not null)
            {
                using var memory = new MemoryStream();
                await read.Content.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
                var record = StandaloneRecordFraming.Parse(memory.ToArray());
                var metadataKey = repository.Keys.DeriveClassKey(BlobClass.Metadata, record.KeyGeneration);
                try
                {
                    if (StandaloneRecordCipher.TryOpen(record, repository.RepositoryId, metadataKey, out var plaintext))
                    {
                        var manifest = SnapshotManifestCodec.Decode(plaintext).Manifest;
                        var policyRead = await reader.ReadSegmentAsync(manifest.PolicyManifest, cancellationToken)
                            .ConfigureAwait(false);
                        if (policyRead.Outcome == RecordReadOutcome.Ok)
                        {
                            policy = PolicyManifestCodec.Decode(policyRead.Plaintext!);
                        }
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(metadataKey);
                }
            }
        }

        return new RecordedShape(
            setId, policy, snapshots.Count, Convert.ToHexStringLower(newest.SnapshotId.Span), newest.CapturedAt, others);
    }

    /// <summary>The set id, policy and lineage facts an archive's newest snapshot records.</summary>
    private sealed record RecordedShape(
        string? SetId,
        PolicyManifest? Policy,
        int SnapshotCount,
        string? NewestSnapshotId,
        ulong? NewestSnapshotAt,
        IReadOnlyList<string> OtherSetIds);

    /// <summary>
    /// The destination adoption can read from: declared, and either a
    /// reachable local path or a peer. A peer's reachability is learned by
    /// dialling it, where the refusal can name what the peer said.
    /// </summary>
    private (DestinationConfiguration? Destination, ServiceError? Refusal) ResolveAdoptableDestination(string name)
    {
        DestinationConfiguration? destination;
        try
        {
            destination = runtime.Configuration.FindDestination(name);
        }
        catch (ClientStateException exception)
        {
            return (null, new ServiceError(ServiceErrorReason.InvalidArgument, exception.Message));
        }

        if (destination is null)
        {
            return (null, new ServiceError(ServiceErrorReason.NotFound, $"No destination named '{name}' is declared."));
        }

        if (destination.Kind == DestinationKind.Peer)
        {
            return (destination, null);
        }

        if (destination.Kind != DestinationKind.LocalPath)
        {
            return (null, new ServiceError(
                ServiceErrorReason.Refused,
                $"Destination '{name}' is of a kind this service cannot read archives from."));
        }

        if (string.IsNullOrWhiteSpace(destination.Path) || !Directory.Exists(destination.Path))
        {
            return (null, new ServiceError(
                ServiceErrorReason.Unavailable,
                $"Destination '{name}' is not reachable at '{destination.Path}'."));
        }

        return (destination, null);
    }

    /// <summary>
    /// Which configured set each locally held repository belongs to, by
    /// repository id — read from each set's own descriptor, never by opening
    /// anything.
    /// </summary>
    private Dictionary<string, string> OwnedRepositories()
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        ClientConfiguration configuration;
        try
        {
            configuration = runtime.Configuration;
        }
        catch (ClientStateException)
        {
            return owners;
        }

        foreach (var set in configuration.BackupSets)
        {
            if (LocalDescriptorOf(set) is { } descriptor)
            {
                owners.TryAdd(descriptor.RepositoryId.ToString(), set.Name);
            }
        }

        return owners;
    }

    /// <summary>
    /// The descriptor a configured set's local store holds — the metadata
    /// store of a direct-ship set, the staging archive otherwise — read from
    /// the file and never by opening the archive; null when the set has no
    /// archive yet or the file does not parse.
    /// </summary>
    private RepositoryDescriptor? LocalDescriptorOf(BackupSetConfiguration set)
    {
        foreach (var path in new[] { runtime.SetMetadataPath(set.Id), runtime.ArchivePath(set.Id) })
        {
            var descriptorPath = Path.Combine(path, RepositoryLifecycle.DescriptorKey.Value);
            if (!File.Exists(descriptorPath))
            {
                continue;
            }

            return RepositoryDescriptorCodec.Parse(File.ReadAllBytes(descriptorPath)) is DescriptorParseResult.Ok parsed
                ? parsed.Descriptor
                : null;
        }

        return null;
    }

    /// <summary>The repository id a configured set's local store holds, or null when it holds none.</summary>
    private async ValueTask<string?> LocalRepositoryIdOfAsync(BackupSetConfiguration set, CancellationToken cancellationToken)
    {
        foreach (var path in new[] { runtime.SetMetadataPath(set.Id), runtime.ArchivePath(set.Id) })
        {
            if (!File.Exists(Path.Combine(path, RepositoryLifecycle.DescriptorKey.Value)))
            {
                continue;
            }

            try
            {
                var descriptor = await RepositoryLifecycle.ReadDescriptorAsync(
                    new LocalFileSystemObjectStore(path), cancellationToken).ConfigureAwait(false);
                return descriptor.RepositoryId.ToString();
            }
            catch (RepositoryOpenException)
            {
                // Unreadable is not "a different archive"; the caller's
                // comparison then reads as mismatch and refuses by name.
                return null;
            }
        }

        return null;
    }

    private byte[]? InstallationSealingPublicKey()
    {
        using var provisioning = runtime.InstallationCredential.TryLoad();
        return provisioning?.Credential.SealingPublicKey.ToArray();
    }
}
