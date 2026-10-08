using FallbackPlan.Api;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Catalogue;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

namespace FallbackPlan.Agent;

/// <summary>
/// The restore-source surface (ADR-0041): opening a per-set repository —
/// staging archive, local-path replica, or a peer's replica — as a handle
/// the source-aware restore verbs read through, and closing it again. A
/// person opens one only under a restore grant derived from the set's
/// passphrase (ADR-0042 §5), and a source so opened is the proof every verb
/// that names a backup's files asks for (FR-WOR-007, ADR-0089). Nothing
/// passphrase-shaped crosses the contract but the sealed grant (NFR-SEC-009).
/// </summary>
public sealed partial class ServiceCommandHandler
{
    /// <summary>
    /// Whether this caller must prove the passphrase before a backup's file
    /// names are shown it (FR-WOR-007): every caller but the service's own work,
    /// which reads the structure plane on the write bundle alone (FR-WOR-003).
    /// </summary>
    private bool NamesNeedThePassphrase => Scope != CallerScope.Service;

    /// <summary>The refusal a caller meets who has not proved the passphrase for what it asks.</summary>
    /// <param name="what">What was asked, as a sentence's subject.</param>
    private static ServiceError PassphraseNeeded(string what) => new(
        ServiceErrorReason.Refused,
        $"{what} names the files a backup holds, so it needs the set's passphrase: open a restore source "
        + "under a restore grant derived from it, and name that source (FR-WOR-007).");

    /// <summary>
    /// The source a command names as its proof of the passphrase, or why it
    /// is not one (FR-WOR-007): none named, expired, opened without a grant,
    /// unlocked by another session, or another set's. Null for the service's
    /// own work, which needs no proof.
    /// </summary>
    /// <param name="sourceId">The source the command names.</param>
    /// <param name="sessionId">The session the command came from.</param>
    /// <param name="setId">The set whose files the command names, when one set's; null when the source decides.</param>
    /// <param name="what">What was asked, for the refusal.</param>
    private ServiceError? RefuseUnproved(string? sourceId, string? sessionId, string? setId, string what)
    {
        if (!NamesNeedThePassphrase)
        {
            return null;
        }

        if (sourceId is null)
        {
            return PassphraseNeeded(what);
        }

        var handle = runtime.RestoreSources.Find(sourceId);
        if (handle is null)
        {
            return new ServiceError(
                ServiceErrorReason.NotFound, "This restore source has expired — unlock it again.");
        }

        if (handle.ReadAuthority is null)
        {
            return PassphraseNeeded(what);
        }

        if (!string.Equals(handle.SessionId, sessionId, StringComparison.Ordinal))
        {
            return new ServiceError(
                ServiceErrorReason.Refused,
                "This restore source was unlocked by another session, and serves only that one — unlock it again "
                + "with the passphrase (FR-WOR-007).");
        }

        return setId is null || string.Equals(handle.SetId, setId, StringComparison.Ordinal)
            ? null
            : new ServiceError(
                ServiceErrorReason.InvalidArgument,
                $"This restore source unlocks set '{handle.SetName}', not set '{SetNameOf(setId)}': each set "
                + "answers to its own passphrase, so open a source of that set (FR-WOR-007).");
    }
    /// <summary>
    /// What a restore verb reads through: either an open source handle or
    /// the legacy staging lookup, reduced to one shape so the plan probe and
    /// the run body exist once.
    /// </summary>
    /// <param name="Store">The store the restore reads first.</param>
    /// <param name="RepositoryId">The repository's identity.</param>
    /// <param name="Keys">The repository's keys.</param>
    /// <param name="OpenCatalogue">Opens a read connection on the catalogue that answers for the store.</param>
    /// <param name="Source">The open source handle, or null for the legacy staging lookup.</param>
    /// <param name="Set">
    /// The set whose own archive this is, when it is one — with
    /// <paramref name="Archive"/>, what lets the restore read around damage
    /// from the set's other copies (FR-RST-007). Null for a destination's
    /// replica, which is read alone.
    /// </param>
    /// <param name="Archive">The set's archive, when <paramref name="Set"/> is.</param>
    private sealed record RestoreContext(
        Storage.Abstractions.IObjectStore Store,
        Domain.Identifiers.RepositoryId RepositoryId,
        RepositoryKeySet Keys,
        Func<CatalogueDb> OpenCatalogue,
        OpenRestoreSourceHandle? Source,
        Application.BackupSetConfiguration? Set = null,
        ArchiveHandle? Archive = null)
    {
        /// <summary>
        /// The set's other copies to read around damage from, or null when
        /// this restore reads one copy alone. Disposed with the run: a peer
        /// dialled for it is hung up afterwards.
        /// </summary>
        public SetCopies? OtherCopies(ServiceRuntime runtime) =>
            Set is null || Archive is null
                ? null
                : new SetCopies(runtime, Set, Archive, excluding: null, includeStaging: false, userInitiated: true);

        /// <summary>
        /// How the restore's own store is named among the copies: the staging
        /// archive, or — for a direct-ship set, whose store reads through its
        /// destinations — nothing, since each of those is named in its turn.
        /// </summary>
        public string? OwnCopyName => Archive?.ShipSink is null ? SetCopies.StagingName : null;
    }

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
            var found = await FindArchiveBySnapshotAsync(snapshotId, cancellationToken).ConfigureAwait(false);
            return found is not { } located
                ? (null, new ServiceError(
                    ServiceErrorReason.NotFound, $"No set's archive holds snapshot {snapshotHex}."))
                : (new RestoreContext(
                    located.Archive.Store, located.Archive.Repository.RepositoryId, located.Archive.Repository.Keys,
                    located.Archive.OpenReadCatalogue, Source: null, located.Set, located.Archive), null);
        }

        var handle = runtime.RestoreSources.Find(sourceId);
        if (handle is null)
        {
            return (null, new ServiceError(
                ServiceErrorReason.NotFound, "This restore source has expired — unlock it again."));
        }

        // Only the set's own archive reads around damage. A destination
        // opened by name is read alone: a drill restores through it to prove
        // that copy restores, and one that read around its damage would pass.
        var set = handle.IsSetArchive
            ? runtime.Configuration.BackupSets.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, handle.SetId, StringComparison.Ordinal))
            : null;
        var archive = set is null
            ? null
            : await runtime.ExistingArchiveAsync(set.Id, cancellationToken).ConfigureAwait(false);
        return (new RestoreContext(
            handle.Store, handle.RepositoryId, handle.Keys, handle.OpenReadCatalogue, handle,
            archive is null ? null : set, archive), null);
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
            // What the operator is told has to be true of the shape they
            // chose: a direct-ship set stages nothing, so naming a staging
            // archive would describe a copy that does not exist and send
            // anyone diagnosing a restore to an empty archives root. Its
            // local store holds metadata only; the content is read through
            // the sink from whichever destination holds it (ADR-0046).
            var directShip = set.DirectShip;
            var archive = await runtime.ExistingArchiveAsync(set.Id, cancellationToken).ConfigureAwait(false);
            if (archive is null)
            {
                return new ServiceError(
                    ServiceErrorReason.NotFound,
                    $"Backup set '{set.Name}' has never backed up — its "
                    + (directShip ? "metadata store" : "staging archive") + " does not exist.");
            }

            if (command.Envelope is null && NamesNeedThePassphrase)
            {
                return PassphraseNeeded("Opening a backup to browse or restore");
            }

            handle = new OpenRestoreSourceHandle
            {
                SourceId = sourceId,
                SetId = set.Id,
                SetName = set.Name,
                IsSetArchive = true,
                Location = directShip ? "metadata store" : "staging",
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

            // Before the replica is opened, which is a catalogue rebuild and,
            // at a peer, a dial: a person with no grant gets nothing of it.
            if (command.Envelope is null && NamesNeedThePassphrase)
            {
                return PassphraseNeeded("Opening a backup to browse or restore");
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

                case Application.DestinationKind.S3 or Application.DestinationKind.AzureBlob:
                    var (storeOpened, storeRefusal) = await OpenObjectStoreSourceAsync(
                        sourceId, set, destination, warnings, cancellationToken).ConfigureAwait(false);
                    if (storeOpened is null)
                    {
                        return storeRefusal!;
                    }

                    handle = storeOpened;
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
            // Oldest first, the reverse of list_snapshots: the wizard's
            // effective-date step reads the list as a timeline, taking the
            // last snapshot at or before the date and calling the first the
            // earliest.
            foreach (var row in catalogue.EnumerateSnapshots().Reverse())
            {
                snapshots.Add(new SnapshotDescriptor(
                    Convert.ToHexStringLower(row.SnapshotId.Span),
                    Convert.ToHexStringLower(row.BackupSetId.Span),
                    row.CapturedAt,
                    row.CaptureStatus,
                    catalogue.CountFiles(row.SnapshotId.Span),
                    Destinations: null,
                    ConsistencyMethod: row.ConsistencyMethod,
                    ObservedClockSkewMs: row.ObservedClockSkewMs));
            }
        }

        handle.SessionId = command.SessionId;
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
    /// <summary>
    /// Whether the restore sources this handler opens read through their
    /// destination's transfer limit (NFR-PERF-013, ADR-0074). Set only on the
    /// handler a background drill restores through; every handler that serves
    /// a person leaves it false, so a person's restore is never paced.
    /// </summary>
    internal bool PacesRestoreSources { get; init; }

    private Application.ByteRateLimiter? SourcePacing(Application.DestinationConfiguration destination) =>
        PacesRestoreSources ? runtime.Pacing.ForDestination(destination) : null;

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

            var store = PacedObjectStore.Over(StoreComposition.OpenLocal(replicaRoot), SourcePacing(destination));
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
                var store = PacedObjectStore.Over(new PeerRetrievalObjectStore(client), SourcePacing(destination));
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
    /// Opens an object-store destination's replica of one set as a source
    /// (ADR-0091, ADR-0093): the replica under the destination's prefix at the
    /// repository id the staging archive names, or — with staging lost — each
    /// folder the prefix holds, tried until one both unlocks and holds this
    /// set's snapshots. The local path's search, over a listing.
    /// </summary>
    private async ValueTask<(OpenRestoreSourceHandle? Handle, ServiceError? Refusal)> OpenObjectStoreSourceAsync(
        string sourceId,
        Application.BackupSetConfiguration set,
        Application.DestinationConfiguration destination,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        Storage.Abstractions.IPrefixedObjectStore? prefix;
        string? refusal;
        try
        {
            prefix = StoreComposition.OpenObjectStore(runtime, destination, repositoryIdHex: null, out refusal);
        }
        catch (Domain.ClientStateException damaged)
        {
            return (null, new ServiceError(ServiceErrorReason.Failed, damaged.Message));
        }

        if (prefix is null)
        {
            return (null, new ServiceError(
                ServiceErrorReason.Failed, $"Destination '{destination.Name}' cannot be read: {refusal}."));
        }

        var setId = Convert.FromHexString(set.Id);
        try
        {
            List<string> candidates = [];
            var staging = await runtime.ExistingArchiveAsync(set.Id, cancellationToken).ConfigureAwait(false);
            if (staging is not null)
            {
                candidates.Add(staging.Repository.RepositoryId.ToString());
            }
            else
            {
                await foreach (var child in prefix.ListChildrenAsync(cancellationToken).ConfigureAwait(false))
                {
                    candidates.Add(child);
                }
            }

            foreach (var repositoryIdHex in candidates)
            {
                var replica = StoreComposition.OpenObjectStore(runtime, destination, repositoryIdHex, out _)!;
                if (!(await replica.GetMetadataAsync(RepositoryLifecycle.DescriptorKey, cancellationToken)
                        .ConfigureAwait(false)).Found)
                {
                    continue;
                }

                var store = PacedObjectStore.Over(replica, SourcePacing(destination));
                OpenedRepository repository;
                try
                {
                    repository = await OpenSourceRepositoryAsync(set, store, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is KeyUnwrapFailedException or RepositoryOpenException)
                {
                    warnings.Add($"a replica '{repositoryIdHex}' in the store would not open: {exception.Message}");
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
        catch (Storage.Abstractions.StoreUnreachableException unreachable)
        {
            return (null, new ServiceError(
                ServiceErrorReason.Unavailable,
                $"Destination '{destination.Name}' is not reachable: {unreachable.Message}"));
        }
        catch (IOException refused)
        {
            return (null, new ServiceError(
                ServiceErrorReason.Failed,
                $"Destination '{destination.Name}' refused the read: {refused.Message}"));
        }
    }

    /// <summary>
    /// Opens a restore-grant envelope and attaches the read authority to the
    /// source handle (ADR-0042 §5). The envelope carries the derived sealing
    /// scalar, sealed to this service's recipient key; the scalar is proved
    /// against the repository's descriptor copy of the sealing public key —
    /// a mismatch is a wrong passphrase at the client and is refused by
    /// name.
    /// </summary>
    private ServiceError? AttachReadAuthority(OpenRestoreSourceHandle handle, string envelopeHex)
    {
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

            // The credential the set opens with — its own, or the
            // installation's for a set created after setup — never only the
            // per-set store, which a set-up installation leaves empty.
            using var credential = runtime.TryLoadCredentialFor(handle.SetId);
            if (credential is null)
            {
                return new ServiceError(
                    ServiceErrorReason.Failed,
                    $"Set '{handle.SetName}' holds no write credential on this service — run first-run setup, "
                    + "or provision the set (ADR-0044, ADR-0042 §10).");
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
    /// write-only set with the credential it opens with — its own, or the
    /// installation's — since a v2 replica carries the same descriptor, so
    /// the same bundle proves and opens it (ADR-0042 §5). A candidate no
    /// credential answers for is a stated refusal the probing loops surface
    /// as a warning like any other failed open.
    /// </summary>
    private async ValueTask<OpenedRepository> OpenSourceRepositoryAsync(
        Application.BackupSetConfiguration set,
        Storage.Abstractions.IObjectStore store,
        CancellationToken cancellationToken)
    {
        if (runtime.TryLoadCredentialFor(set.Id) is { } credential)
        {
            using (credential)
            {
                return await RepositoryLifecycle.OpenAsync(
                        store, credential, cancellationToken, runtime.LoggerFor(typeof(RepositoryLifecycle)),
                        // A restore source is read: the store may be a peer's
                        // replica, which has no put at all.
                        Repository.StoreUse.ReadingOnly)
                    .ConfigureAwait(false);
            }
        }

        throw new RepositoryOpenException(
            $"Set '{set.Name}' holds no write credential this service can open its archive with — run "
            + "first-run setup, or provision the set (ADR-0044, ADR-0042 §10).");
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

            using (var reader = await CatalogueRebuild.OpenMetadataReaderAsync(store, repository, cancellationToken)
                .ConfigureAwait(false))
            using (var catalogue = await CatalogueRebuild.OpenRebuiltAsync(
                runtime, store, repository, cataloguePath, reader, warnings, cancellationToken).ConfigureAwait(false))
            {
                if (!catalogue.EnumerateSnapshots().Any(row => row.BackupSetId.Span.SequenceEqual(setId)))
                {
                    TryDeleteCache(cacheDirectory);
                    return null;
                }
            }

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

    /// <summary>
    /// Closes a source. Idempotent: closing the closed acknowledges, and so
    /// does closing a source another session unlocked, which closes nothing.
    /// </summary>
    private async ValueTask<ServiceResult> CloseRestoreSourceAsync(CloseRestoreSourceCommand command)
    {
        if (!NamesNeedThePassphrase
            || runtime.RestoreSources.Find(command.SourceId) is not { } held
            || string.Equals(held.SessionId, command.SessionId, StringComparison.Ordinal))
        {
            await runtime.RestoreSources.CloseAsync(command.SourceId).ConfigureAwait(false);
        }

        await runtime.RestoreSources.SweepAsync().ConfigureAwait(false);
        return new AcknowledgedResult();
    }

    private static void TryDeleteCache(string cacheDirectory)
    {
        try
        {
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
