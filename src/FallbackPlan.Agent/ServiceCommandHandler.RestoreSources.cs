using FallbackPlan.Api;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Catalogue;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Local;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Agent;

/// <summary>
/// The restore-source surface (ADR-0041): opening a per-set repository —
/// staging archive, local-path replica, or a peer's replica — as a handle
/// the source-aware restore verbs read through, and closing it again. The
/// open carries no passphrase: the runtime unlocks sources with the secret
/// it already holds (NFR-SEC-009 — nothing passphrase-shaped crosses the
/// contract), and the wizard's operator gate is the console's own local
/// verification.
/// </summary>
public sealed partial class ServiceCommandHandler
{
    /// <summary>
    /// What a restore verb reads through: either an open source handle or
    /// the legacy staging lookup, reduced to one shape so the plan probe and
    /// the run body exist once.
    /// </summary>
    private sealed record RestoreContext(
        Storage.Abstractions.IObjectStore Store,
        Domain.Identifiers.RepositoryId RepositoryId,
        RepositoryKeySet Keys,
        Func<CatalogueDb> OpenCatalogue,
        OpenRestoreSourceHandle? Source);

    /// <summary>
    /// Resolves the context a restore verb runs against. With a source id,
    /// the handle answers (touched, so the idle sweep sees a live wizard);
    /// without one, the staging archives are searched for the snapshot —
    /// today's path, byte for byte.
    /// </summary>
    private async ValueTask<(RestoreContext? Context, ServiceError? Error)> ResolveRestoreContextAsync(
        string? sourceId, byte[] snapshotId, string snapshotHex, CancellationToken cancellationToken)
    {
        if (sourceId is null)
        {
            var archive = await FindArchiveBySnapshotAsync(snapshotId, cancellationToken).ConfigureAwait(false);
            return archive is null
                ? (null, new ServiceError(
                    ServiceErrorReason.NotFound, $"No set's archive holds snapshot {snapshotHex}."))
                : (new RestoreContext(
                    archive.Store, archive.Repository.RepositoryId, archive.Repository.Keys,
                    archive.OpenReadCatalogue, Source: null), null);
        }

        var handle = runtime.RestoreSources.Find(sourceId);
        return handle is null
            ? (null, new ServiceError(
                ServiceErrorReason.NotFound, "This restore source has expired — unlock it again."))
            : (new RestoreContext(
                handle.Store, handle.RepositoryId, handle.Keys, handle.OpenReadCatalogue, handle), null);
    }

    /// <summary>
    /// Opens a restore source and answers its snapshots. On the reader lane:
    /// a replica open costs an Argon2 derivation plus a catalogue rebuild,
    /// which is heavy read work exactly like a restore.
    /// </summary>
    private async ValueTask<ServiceResult> OpenRestoreSourceAsync(
        OpenRestoreSourceCommand command, CancellationToken cancellationToken)
    {
        // Abandoned wizards are reclaimed on the verbs that touch the
        // registry — cheap, and always before growing it.
        await runtime.RestoreSources.SweepAsync().ConfigureAwait(false);

        var set = runtime.Configuration.FindSet(command.SetName);
        if (set is null)
        {
            return new ServiceError(
                ServiceErrorReason.NotFound, $"No backup set named '{command.SetName}' is configured.");
        }

        var sourceId = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));

        OpenRestoreSourceHandle handle;
        var warnings = new List<string>();
        if (command.DestinationName is null)
        {
            var archive = await runtime.ExistingArchiveAsync(set.Id, cancellationToken).ConfigureAwait(false);
            if (archive is null)
            {
                return new ServiceError(
                    ServiceErrorReason.NotFound,
                    $"Backup set '{set.Name}' has never backed up — its staging archive does not exist.");
            }

            handle = new OpenRestoreSourceHandle
            {
                SourceId = sourceId,
                SetId = set.Id,
                SetName = set.Name,
                Location = "staging",
                Store = archive.Store,
                OwnedRepository = null,
                RepositoryId = archive.Repository.RepositoryId,
                Keys = archive.Repository.Keys,
                CataloguePath = archive.CataloguePath,
                CatalogueLogger = runtime.LoggerFor<CatalogueDb>(),
                CacheDirectory = null,
            };
        }
        else
        {
            var destination = runtime.Configuration.FindDestination(command.DestinationName);
            if (destination is null)
            {
                return new ServiceError(
                    ServiceErrorReason.NotFound, $"No destination named '{command.DestinationName}' is declared.");
            }

            switch (destination.Kind)
            {
                case Application.DestinationKind.LocalPath:
                    var (opened, refusal) = await OpenReplicaSourceAsync(
                        sourceId, set, destination, warnings, cancellationToken).ConfigureAwait(false);
                    if (opened is null)
                    {
                        return refusal!;
                    }

                    handle = opened;
                    break;

                case Application.DestinationKind.Peer:
                    var (peerOpened, peerRefusal) = await OpenPeerSourceAsync(
                        sourceId, set, destination, warnings, cancellationToken).ConfigureAwait(false);
                    if (peerOpened is null)
                    {
                        return peerRefusal!;
                    }

                    handle = peerOpened;
                    break;

                default:
                    return new ServiceError(
                        ServiceErrorReason.InvalidArgument,
                        $"Destination '{destination.Name}' ({destination.Kind}) cannot serve restores.");
            }
        }

        if (command.Envelope is not null)
        {
            var grantRefusal = AttachReadAuthority(handle, command.Envelope);
            if (grantRefusal is not null)
            {
                await handle.DisposeAsync().ConfigureAwait(false);
                return grantRefusal;
            }
        }

        var snapshots = new List<SnapshotDescriptor>();
        using (var catalogue = handle.OpenReadCatalogue())
        {
            // Oldest first, matching list_snapshots — the wizard's
            // effective-date step resolves against CapturedAt client-side.
            foreach (var row in catalogue.EnumerateSnapshots().Reverse())
            {
                snapshots.Add(new SnapshotDescriptor(
                    Convert.ToHexStringLower(row.SnapshotId.Span),
                    Convert.ToHexStringLower(row.BackupSetId.Span),
                    row.CapturedAt,
                    row.CaptureStatus,
                    catalogue.CountFiles(row.SnapshotId.Span)));
            }
        }

        runtime.RestoreSources.Add(handle);
        return new RestoreSourceOpenedResult(sourceId, set.Name, handle.Location, snapshots, warnings);
    }

    /// <summary>
    /// Opens a local-path destination's replica of one set as a source: the
    /// replica directory is a complete repository (the owner's own bytes),
    /// opened with the runtime's passphrase and given a throwaway catalogue
    /// rebuilt from its own index plane — checkpoint plus deltas, metadata
    /// reads only.
    /// </summary>
    private async ValueTask<(OpenRestoreSourceHandle? Handle, ServiceError? Refusal)> OpenReplicaSourceAsync(
        string sourceId,
        Application.BackupSetConfiguration set,
        Application.DestinationConfiguration destination,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destination.Path) || !Directory.Exists(destination.Path))
        {
            return (null, new ServiceError(
                ServiceErrorReason.Unavailable,
                $"Destination '{destination.Name}' is not reachable at '{destination.Path}'."));
        }

        // The replica lives under the destination at the REPOSITORY id
        // (ADR-0034; FanOut composes the same path). The staging archive
        // names it directly; with staging lost — the very scenario replica
        // restore exists for — each candidate directory is tried until one
        // both unlocks and holds this set's snapshots.
        var candidates = new List<string>();
        var staging = await runtime.ExistingArchiveAsync(set.Id, cancellationToken).ConfigureAwait(false);
        if (staging is not null)
        {
            candidates.Add(Path.Combine(destination.Path, staging.Repository.RepositoryId.ToString()));
        }
        else
        {
            candidates.AddRange(Directory.GetDirectories(destination.Path));
        }

        var setId = Convert.FromHexString(set.Id);
        foreach (var replicaRoot in candidates)
        {
            if (!File.Exists(Path.Combine(replicaRoot, RepositoryLifecycle.DescriptorKey.Value)))
            {
                continue;
            }

            var store = new LocalFileSystemObjectStore(replicaRoot);
            OpenedRepository repository;
            try
            {
                repository = await OpenSourceRepositoryAsync(set, store, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is KeyUnwrapFailedException or RepositoryOpenException)
            {
                // Another owner's replica, or damage — either way not this
                // set's; keep looking rather than failing the open.
                warnings.Add($"a replica at '{Path.GetFileName(replicaRoot)}' would not open: {exception.Message}");
                continue;
            }

            var handle = await BuildSourceAsync(
                sourceId, set, setId, destination.Name, store, repository, transport: null,
                warnings, cancellationToken).ConfigureAwait(false);
            if (handle is null)
            {
                repository.Dispose();
                continue;
            }

            return (handle, null);
        }

        return (null, new ServiceError(
            ServiceErrorReason.NotFound,
            $"Destination '{destination.Name}' holds no readable replica of set '{set.Name}'."
            + (warnings.Count == 0 ? string.Empty : $" ({string.Join("; ", warnings)})")));
    }

    /// <summary>
    /// Opens a peer destination's replica of one set over the retrieval
    /// session (peer-protocol 07). The repository id comes from the staging
    /// archive when one is alive; with staging lost, the destination's owner
    /// inventory names what this hub owns there and each candidate is tried
    /// until one holds the set's snapshots.
    /// </summary>
    private async ValueTask<(OpenRestoreSourceHandle? Handle, ServiceError? Refusal)> OpenPeerSourceAsync(
        string sourceId,
        Application.BackupSetConfiguration set,
        Application.DestinationConfiguration destination,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var setId = Convert.FromHexString(set.Id);
        List<string> candidates;
        try
        {
            var staging = await runtime.ExistingArchiveAsync(set.Id, cancellationToken).ConfigureAwait(false);
            if (staging is not null)
            {
                candidates = [staging.Repository.RepositoryId.ToString()];
            }
            else
            {
                // The owner inventory (07 §3.5): what this hub owns there.
                var inventory = await PeerRetrievalClient.DialAsync(
                    runtime, destination, new byte[Protocol.RetrieveOpen.RepositoryIdLength], cancellationToken)
                    .ConfigureAwait(false);
                await using (inventory.ConfigureAwait(false))
                {
                    var page = await inventory.ListPageAsync(string.Empty, string.Empty, cancellationToken)
                        .ConfigureAwait(false);
                    candidates = [.. page.Keys];
                }
            }

            foreach (var repositoryIdHex in candidates)
            {
                var client = await PeerRetrievalClient.DialAsync(
                    runtime, destination, Convert.FromHexString(repositoryIdHex), cancellationToken)
                    .ConfigureAwait(false);
                var store = new PeerRetrievalObjectStore(client);
                OpenedRepository repository;
                try
                {
                    repository = await OpenSourceRepositoryAsync(set, store, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is KeyUnwrapFailedException or RepositoryOpenException)
                {
                    warnings.Add($"a replica '{repositoryIdHex}' at the peer would not open: {exception.Message}");
                    await client.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                var handle = await BuildSourceAsync(
                    sourceId, set, setId, destination.Name, store, repository, client,
                    warnings, cancellationToken).ConfigureAwait(false);
                if (handle is null)
                {
                    repository.Dispose();
                    await client.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                return (handle, null);
            }

            return (null, new ServiceError(
                ServiceErrorReason.NotFound,
                $"Peer '{destination.Name}' holds no readable replica of set '{set.Name}'."
                + (warnings.Count == 0 ? string.Empty : $" ({string.Join("; ", warnings)})")));
        }
        catch (Protocol.PeerProtocolException refused)
        {
            return (null, new ServiceError(
                ServiceErrorReason.Failed,
                $"Peer '{destination.Name}' refused the retrieval: {refused.Message}"));
        }
        catch (Exception unreachable) when (unreachable is IOException or System.Net.Sockets.SocketException)
        {
            return (null, new ServiceError(
                ServiceErrorReason.Unavailable,
                $"Peer '{destination.Name}' is not reachable: {unreachable.Message}"));
        }
    }

    /// <summary>
    /// Opens a restore-grant envelope and attaches the read authority to the
    /// source handle (ADR-0042 §5). The envelope carries the derived sealing
    /// scalar, sealed to this service's recipient key; the scalar is proved
    /// against the repository's descriptor copy of the sealing public key —
    /// a mismatch is a wrong passphrase at the client and is refused by
    /// name. A grant on a v1 source is ignored, as the contract says.
    /// </summary>
    private ServiceError? AttachReadAuthority(OpenRestoreSourceHandle handle, string envelopeHex)
    {
        if (!handle.Keys.WriteOnly)
        {
            return null;
        }

        byte[] envelope;
        try
        {
            envelope = Convert.FromHexString(envelopeHex);
        }
        catch (FormatException)
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument, "The restore-grant envelope is not hex.");
        }

        byte[] scalar;
        try
        {
            scalar = runtime.GrantRecipient.OpenGrant(envelope);
        }
        catch (SealedContentException)
        {
            return new ServiceError(
                ServiceErrorReason.InvalidArgument,
                "The restore-grant envelope does not open — it was sealed to a different service's recipient key.");
        }

        try
        {
            if (!ContentSealing.PublicKeyOf(scalar).AsSpan().SequenceEqual(handle.Keys.SealingPublicKey))
            {
                return new ServiceError(
                    ServiceErrorReason.InvalidArgument,
                    "The granted key does not reproduce this repository's sealing public key — the passphrase it "
                    + "was derived from is not this repository's.");
            }

            using var credential = runtime.WriteCredentials.TryLoad(handle.SetId);
            if (credential is null)
            {
                return new ServiceError(
                    ServiceErrorReason.Failed,
                    $"Set '{handle.SetName}' holds no write credential on this service — provision it first (ADR-0042 §10).");
            }

            handle.ReadAuthority = RepositoryReadAuthority.FromParts(credential, scalar);
            return null;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(scalar);
        }
    }

    /// <summary>
    /// Opens a candidate source repository the way its set opens: a
    /// provisioned write-only set with its credential — a v2 replica carries
    /// the same descriptor, so the same bundle proves and opens it
    /// (ADR-0042 §5) — and a v1 set with the runtime's passphrase. A v1
    /// candidate on a passphrase-free service is a stated refusal the
    /// probing loops surface as a warning like any other failed open.
    /// </summary>
    private async ValueTask<OpenedRepository> OpenSourceRepositoryAsync(
        Application.BackupSetConfiguration set,
        Storage.Abstractions.IObjectStore store,
        CancellationToken cancellationToken)
    {
        if (runtime.WriteCredentials.TryLoad(set.Id) is { } credential)
        {
            using (credential)
            {
                return await RepositoryLifecycle.OpenWriteOnlyAsync(
                        store, credential, cancellationToken, runtime.LoggerFor(typeof(RepositoryLifecycle)))
                    .ConfigureAwait(false);
            }
        }

        var passphrase = runtime.ArchivePassphrase
            ?? throw new RepositoryOpenException(
                "This service started without a passphrase and the set is not provisioned write-only (ADR-0042).");

        return await RepositoryLifecycle.OpenAsync(
                store, passphrase, cancellationToken, runtime.LoggerFor(typeof(RepositoryLifecycle)))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The shared tail of every non-staging open: a throwaway catalogue —
    /// index-plane rebuild for locations, manifest projection for snapshots
    /// and paths (FR-MAN-002), metadata-class footers only — then the
    /// membership check that this repository really is the named set's.
    /// Null (with the cache deleted) when it is some other set's; the caller
    /// disposes what it opened.
    /// </summary>
    private async ValueTask<OpenRestoreSourceHandle?> BuildSourceAsync(
        string sourceId,
        Application.BackupSetConfiguration set,
        byte[] setId,
        string location,
        Storage.Abstractions.IObjectStore store,
        OpenedRepository repository,
        IAsyncDisposable? transport,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var cacheDirectory = Path.Combine(runtime.RestoreCacheRoot, sourceId);
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            var cataloguePath = Path.Combine(cacheDirectory, "catalogue.db");
            var generation = Math.Max(
                repository.CurrentDataGeneration.Value, repository.CurrentMetadataGeneration.Value);

            RebuildReport report;
            using (var catalogue = CatalogueDb.Open(
                cataloguePath, repository.RepositoryId, runtime.LoggerFor<CatalogueDb>()))
            {
                // The index plane rebuilds the locations; no blob inventory
                // is taken, because an entry naming a trimmed blob is
                // re-answered honestly by the plan probe, which asks the
                // store per blob (FR-RST-003).
                report = await new CatalogueRebuilder(
                    new IndexLoader(
                        store, repository.RepositoryId, repository.Hierarchy, runtime.LoggerFor<IndexLoader>()),
                    runtime.LoggerFor<CatalogueRebuilder>())
                    .RebuildAsync(
                        catalogue, generation, gapPatienceGenerations: 2,
                        isSequenceAccountedAsync: null, cancellationToken)
                    .ConfigureAwait(false);

                using (var reader = new RepositoryReader(repository.RepositoryId, repository.Keys, store))
                {
                    var metadataBlobs = new List<Storage.Abstractions.ObjectKey>();
                    await foreach (var blob in store.ListAsync(
                        Storage.Abstractions.ObjectPrefix.Parse("blobs/meta/"),
                        Storage.Abstractions.ListOptions.Default, cancellationToken).ConfigureAwait(false))
                    {
                        metadataBlobs.Add(blob.Key);
                    }

                    await reader.LoadBlobsAsync(metadataBlobs, cancellationToken).ConfigureAwait(false);
                    await CatalogueProjector.ProjectAsync(
                        catalogue, reader, store, repository.RepositoryId, repository.Keys,
                        repository.Hierarchy, cancellationToken).ConfigureAwait(false);
                }

                if (!catalogue.EnumerateSnapshots().Any(row => row.BackupSetId.Span.SequenceEqual(setId)))
                {
                    TryDeleteCache(cacheDirectory);
                    return null;
                }
            }

            warnings.AddRange(report.Findings.Select(finding => $"{finding.Kind}: {finding.Detail}"));
            return new OpenRestoreSourceHandle
            {
                SourceId = sourceId,
                SetId = set.Id,
                SetName = set.Name,
                Location = location,
                Store = store,
                OwnedRepository = repository,
                RepositoryId = repository.RepositoryId,
                Keys = repository.Keys,
                CataloguePath = cataloguePath,
                CatalogueLogger = runtime.LoggerFor<CatalogueDb>(),
                CacheDirectory = cacheDirectory,
                Transport = transport,
            };
        }
        catch
        {
            TryDeleteCache(cacheDirectory);
            throw;
        }
    }

    /// <summary>Closes a source. Idempotent: closing the closed acknowledges.</summary>
    private async ValueTask<ServiceResult> CloseRestoreSourceAsync(CloseRestoreSourceCommand command)
    {
        await runtime.RestoreSources.CloseAsync(command.SourceId).ConfigureAwait(false);
        await runtime.RestoreSources.SweepAsync().ConfigureAwait(false);
        return new AcknowledgedResult();
    }

    private static void TryDeleteCache(string cacheDirectory)
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(cacheDirectory))
            {
                Directory.Delete(cacheDirectory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
