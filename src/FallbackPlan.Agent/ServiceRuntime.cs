using Bodu;
using FallbackPlan.Application;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Local;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using FallbackPlan.Diagnostics;

namespace FallbackPlan.Agent;

/// <summary>How a service is configured at start-up.</summary>
public sealed record ServiceOptions
{
    /// <summary>The directory holding one staging archive per backup set, each under its set id (ADR-0034).</summary>
    public required string ArchivesRoot { get; init; }

    /// <summary>The state directory whose writer role the service holds.</summary>
    public required string StateDirectory { get; init; }

    /// <summary>How often the scheduler evaluates due-ness.</summary>
    public int PollSeconds { get; init; } = 60;

    /// <summary>
    /// Where this service's diagnostics go (ADR-0043). Null runs silent,
    /// which is what a test wants and what a host must not leave as its
    /// default.
    /// </summary>
    /// <remarks>
    /// This replaced an <c>Action&lt;string, Exception?&gt;</c> whose only
    /// consumer was the job lane's last-resort catch. That delegate wrote a
    /// bare timestamp to a <c>TextWriter</c> with no level and no category,
    /// so it could not be filtered, could not be read by a client, and could
    /// not be turned down when it was noisy.
    /// </remarks>
    public LoggingComposition? Logging { get; init; }
}

/// <summary>
/// The long-lived service (ADR-0028 §2): sole holder of the state directory
/// and, per backup set, of that set's staging archive — its writer sequence,
/// its catalogue, its spool (ADR-0034, ADR-0028 amendment).
/// </summary>
/// <remarks>
/// <para>
/// Archives open lazily, one per set on its first use, because opening costs
/// an Argon2id derivation and a many-set hub should pay it per set actually
/// touched, not per set configured. The passphrase is therefore held for the
/// service's lifetime — the ADR-0028 §9 posture, extended from "unlock at
/// start" to "unlock as needed".
/// </para>
/// <para>
/// Each archive's <see cref="WriterSequence"/> exists on its handle and
/// nowhere else. The guard is an in-process lock per archive, which is right
/// for a sequence space owned by this one process; the state-directory lock
/// is what keeps it owned by this one process.
/// </para>
/// </remarks>
public sealed class ServiceRuntime : IAsyncDisposable
{
    private readonly StateDirectoryLock _writerRole;
    private readonly Passphrase? _passphrase;
    private readonly Dictionary<string, ArchiveHandle> _archives = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _archivesGate = new(1, 1);
    private bool _disposed;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _setGates =
        new(StringComparer.Ordinal);

    private ServiceRuntime(
        ServiceOptions options,
        StateDirectoryLock writerRole,
        Passphrase? passphrase,
        LocalState state,
        JobStateStore jobs)
    {
        Options = options;
        _writerRole = writerRole;
        _passphrase = passphrase;
        State = state;
        Jobs = jobs;
        Progress = new ProgressHub();
        Queue = new JobScheduler(Logger(options, typeof(JobScheduler)));
        GrantRecipient = GrantRecipient.Open(options.StateDirectory);
        WriteCredentials = new WriteCredentialStore(options.StateDirectory);
        InstallationCredential = new InstallationCredentialStore(options.StateDirectory);
        KitConfirmation = new RecoveryKitConfirmation(options.StateDirectory);
    }

    /// <summary>How this service was started.</summary>
    public ServiceOptions Options { get; }

    /// <summary>A logger for <paramref name="category"/>, or a silent one.</summary>
    private static ILogger Logger(ServiceOptions options, Type category) =>
        options.Logging?.Factory.CreateLogger(category.FullName!) ?? NullLogger.Instance;

    /// <summary>A logger for <typeparamref name="T"/>, or a silent one.</summary>
    internal ILogger LoggerFor<T>() => LoggerFor(typeof(T));

    /// <summary>
    /// A logger for a category named by type, for the static classes
    /// <see cref="LoggerFor{T}"/> cannot express.
    /// </summary>
    /// <param name="category">The type whose full name names the category.</param>
    internal ILogger LoggerFor(Type category) =>
        Options.Logging?.Factory.CreateLogger(category.FullName!) ?? NullLogger.Instance;

    /// <summary>Durable local state — device and writer identity.</summary>
    public LocalState State { get; }

    /// <summary>The job journal.</summary>
    public JobStateStore Jobs { get; }

    /// <summary>The per-(set, destination) sync ledger (FR-DEST-004).</summary>
    public DestinationSyncStore DestinationSync { get; private set; } = null!;

    /// <summary>The durable notices ledger (architecture 10 §3.1's third channel).</summary>
    public NoticeStore Notices { get; private set; } = null!;

    /// <summary>Where progress goes.</summary>
    public ProgressHub Progress { get; }

    /// <summary>What is running and what is waiting.</summary>
    public JobScheduler Queue { get; }

    /// <summary>The open restore sources (ADR-0041).</summary>
    internal RestoreSourceRegistry RestoreSources { get; } = new();

    /// <summary>
    /// The passphrase the runtime unlocks v1 archives with — the ADR-0028 §9
    /// posture. Handed to the restore-source opens so a replica or peer
    /// repository unlocks with the same secret its staging archive did;
    /// never exposed on any contract surface (NFR-SEC-009). Null when the
    /// service started passphrase-free — the ADR-0042 posture, where every
    /// provisioned set opens with its write credential instead.
    /// </summary>
    internal Passphrase? ArchivePassphrase => _passphrase;

    /// <summary>The service's envelope recipient keypair (ADR-0042 §4).</summary>
    internal GrantRecipient GrantRecipient { get; }

    /// <summary>The per-set write credentials this service holds (ADR-0042 §5).</summary>
    internal WriteCredentialStore WriteCredentials { get; }

    /// <summary>
    /// What first-run setup provisioned, from which every set's staging
    /// archive is created (ADR-0044 §2).
    /// </summary>
    internal InstallationCredentialStore InstallationCredential { get; }

    /// <summary>
    /// Whether the operator confirmed saving this installation's recovery
    /// kit (FR-KIT-004).
    /// </summary>
    internal RecoveryKitConfirmation KitConfirmation { get; }

    /// <summary>
    /// Whether this installation has a passphrase behind it — the state
    /// <c>describe_service</c> reports so a client knows to run setup
    /// (FR-SVC-011).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A set provisioned the older per-set way counts. An installation that
    /// predates ADR-0044 is already working, and telling it to set itself up
    /// would offer a ceremony whose only possible outcome is a second,
    /// unrelated derivation root.
    /// </para>
    /// <para>
    /// <b>A configuration that will not load is not an answer of "no".</b>
    /// This is read by <c>describe_service</c>, which is the first thing a
    /// console asks and the verb that decides whether it can talk to this
    /// service at all — so a typo in <c>config.json</c> must not take it
    /// down, and must certainly not present a set-up installation with a
    /// ceremony that would mint it a second root. The installation credential
    /// is a file on disk and answers without the configuration; the per-set
    /// fallback is what needs the set list, and it simply does not get
    /// consulted when the list cannot be read.
    /// </para>
    /// </remarks>
    public bool IsSetUp
    {
        get
        {
            if (InstallationCredential.Holds)
            {
                return true;
            }

            try
            {
                return Configuration.BackupSets.Any(set => WriteCredentials.Holds(set.Id));
            }
            catch (ClientStateException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// How far first-run setup has got: <c>setup_required</c>,
    /// <c>kit_required</c>, or <c>ready</c> (FR-SVC-011, FR-KIT-004).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three states rather than two, because "has a passphrase" and "is
    /// finished" stopped being the same thing once the ceremony had to end
    /// with a saved kit. The middle state is what makes the confirmation
    /// real: a closed tab between provisioning and confirming resumes at the
    /// kit step instead of leaving an installation that looks finished and
    /// has no kit.
    /// </para>
    /// <para>
    /// An installation provisioned the older per-set way is <c>ready</c>
    /// whatever the kit says. It predates this ceremony, it works, and
    /// demanding a kit for an installation credential it does not have would
    /// be offering a step that cannot complete.
    /// </para>
    /// </remarks>
    public string SetupState =>
        !InstallationCredential.Holds
            ? IsSetUp ? "ready" : "setup_required"
            : KitConfirmation.Holds ? "ready" : "kit_required";

    /// <summary>The throwaway per-source catalogue root, purged at start.</summary>
    internal string RestoreCacheRoot => Path.Combine(Options.StateDirectory, "restore-cache");

    /// <summary>Where persisted restore receipts land (FR-RST-004).</summary>
    internal string ReceiptsRoot => Path.Combine(Options.StateDirectory, "receipts");

    /// <summary>This device's writer identity — one per device, shared by every archive it writes.</summary>
    public WriterId Writer => WriterId.FromBytes(State.WriterId);

    /// <summary>Where the client configuration lives.</summary>
    public string ConfigurationPath => Path.Combine(Options.StateDirectory, "config.json");

    /// <summary>The current configuration, re-read so an edit takes effect without a restart.</summary>
    /// <remarks>
    /// Deliberately unlogged. This re-reads on every access, which is what
    /// makes an edit take effect without a restart — and is also why a record
    /// here would be a record several times a pass, turning "the configuration
    /// was loaded" from a fact into noise. The load that is worth a line is
    /// the one a person can point at: <see cref="LoadConfiguration"/>, called
    /// when the service starts and when an edit is applied.
    /// </remarks>
    public ClientConfiguration Configuration => ClientConfiguration.Load(ConfigurationPath);

    /// <summary>
    /// Reads the configuration as an event worth recording — the service
    /// starting, or an edit taking effect.
    /// </summary>
    /// <exception cref="ClientStateException">The file is invalid — the message names the defect.</exception>
    public ClientConfiguration LoadConfiguration() =>
        ClientConfiguration.Load(ConfigurationPath, LoggerFor<ClientConfiguration>());

    /// <summary>
    /// The per-set exclusion between a destructive retention apply and a
    /// running sync (ADR-0029 Amendment 2). The transfer lane was justified
    /// by "fan-out reads sealed, immutable objects"; the staging trim made
    /// staging mutable, so the two operations that can now disagree about
    /// one set's objects — a trim that verified a replica holds a blob, and
    /// a convergence that is about to drop that blob there — serialise on
    /// this gate. Fan-out waits (a retention pass is minutes); a retention
    /// apply tries and defers, because a first sync can run for hours and
    /// the writer lane must never stall behind it.
    /// </summary>
    /// <param name="setId">The set's 32-hex identity.</param>
    public SemaphoreSlim SetGate(string setId)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(setId);
        return _setGates.GetOrAdd(setId, _ => new SemaphoreSlim(1, 1));
    }

    /// <summary>The directory holding one set's staging archive.</summary>
    /// <param name="setId">The set's 32-hex identity.</param>
    public string ArchivePath(string setId) => Path.Combine(Options.ArchivesRoot, setId);

    /// <summary>Whether a set's staging archive exists on disk yet.</summary>
    /// <param name="setId">The set's 32-hex identity.</param>
    public bool ArchiveExists(string setId) =>
        File.Exists(Path.Combine(ArchivePath(setId), RepositoryLifecycle.DescriptorKey.Value));

    /// <summary>
    /// Takes the writer role. No archive opens here: each set's staging
    /// archive opens — or is created — on first use, so start-up cost does
    /// not scale with sets configured. Failure to take the role is refused
    /// with the holder named — never worked around (FR-SVC-002).
    /// </summary>
    /// <param name="options">How to start.</param>
    /// <param name="passphrase">Unlocks and creates archives. The runtime keeps its own copy for its lifetime.</param>
    /// <param name="cancellationToken">Cancels start-up.</param>
    /// <returns>The running service.</returns>
    public static ValueTask<ServiceRuntime> StartAsync(
        ServiceOptions options, Passphrase? passphrase, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var writerRole = StateDirectoryLock.Acquire(options.StateDirectory, StateDirectoryLock.ServiceRole);
        try
        {
            var state = LocalState.LoadOrCreate(options.StateDirectory);
            var jobs = JobStateStore.Open(options.StateDirectory);

            // Crash leftovers: per-source catalogue caches are worthless
            // without their (gone) handles, and a failed purge is only noise.
            try
            {
                var restoreCache = Path.Combine(options.StateDirectory, "restore-cache");
                if (Directory.Exists(restoreCache))
                {
                    Directory.Delete(restoreCache, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }

            return ValueTask.FromResult(
                new ServiceRuntime(options, writerRole, passphrase?.Clone(), state, jobs)
                {
                    DestinationSync = DestinationSyncStore.Open(options.StateDirectory),
                    Notices = NoticeStore.Open(
                        options.StateDirectory, Logger(options, typeof(NoticeStore))),
                });
        }
        catch
        {
            writerRole.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The set's staging archive, opened on first use — and created on first
    /// backup, because staging is internal and nobody runs `init` for it
    /// (ADR-0034 §1).
    /// </summary>
    /// <param name="set">The set whose archive to resolve.</param>
    /// <param name="cancellationToken">Cancels an open or create.</param>
    /// <returns>The archive, held open by the runtime until disposal.</returns>
    /// <exception cref="RepositoryOpenException">The archive on disk refused to open.</exception>
    /// <exception cref="KeyUnwrapFailedException">The passphrase is wrong for an existing archive.</exception>
    public async ValueTask<ArchiveHandle> ArchiveForAsync(
        BackupSetConfiguration set, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(set);
        return await ArchiveForAsync(set.Id, createIfMissing: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A set's staging archive when it already exists on disk; null when the
    /// set has never been backed up. Read paths use this so that listing
    /// snapshots never mints an empty archive as a side effect.
    /// </summary>
    /// <param name="setId">The set's 32-hex identity.</param>
    /// <param name="cancellationToken">Cancels an open.</param>
    /// <returns>The archive, or null when none exists.</returns>
    public async ValueTask<ArchiveHandle?> ExistingArchiveAsync(string setId, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(setId);
        return ArchiveExists(setId)
            ? await ArchiveForAsync(setId, createIfMissing: false, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <summary>
    /// Every configured set whose staging archive exists, with the archive
    /// open — the enumeration behind snapshots, status, verify and check.
    /// </summary>
    /// <param name="cancellationToken">Cancels the opens.</param>
    /// <returns>Pairs of set and open archive, in configuration order.</returns>
    public async ValueTask<IReadOnlyList<(BackupSetConfiguration Set, ArchiveHandle Archive)>> ExistingArchivesAsync(
        CancellationToken cancellationToken)
    {
        var result = new List<(BackupSetConfiguration, ArchiveHandle)>();
        foreach (var set in Configuration.BackupSets)
        {
            if (await ExistingArchiveAsync(set.Id, cancellationToken).ConfigureAwait(false) is { } archive)
            {
                result.Add((set, archive));
            }
        }

        return result;
    }

    /// <summary>
    /// Opens — or creates — a set's staging archive from the installation
    /// credential first-run setup provisioned (ADR-0044 §2), or returns null
    /// when this installation has none or this set is not its business.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returning null rather than throwing is what keeps the ladder in
    /// <see cref="ArchiveForAsync(string, bool, CancellationToken)"/> a ladder: two of the three ways out of
    /// here are "not mine", and the arm below still has a passphrase to try.
    /// </para>
    /// <para>
    /// A format 1 archive is one of those ways out. An installation set up
    /// after such a set already existed keeps opening it with the passphrase;
    /// nothing is migrated, because write-only is chosen at creation
    /// (ADR-0042).
    /// </para>
    /// </remarks>
    private async ValueTask<OpenedRepository?> OpenFromInstallationAsync(
        string setId, LocalFileSystemObjectStore store, bool createIfMissing, CancellationToken cancellationToken)
    {
        using var provisioning = InstallationCredential.TryLoad();
        if (provisioning is null)
        {
            return null;
        }

        if (!ArchiveExists(setId))
        {
            if (!createIfMissing)
            {
                return null;
            }

            // The first backup of a set on a set-up installation. This is
            // what replaces the silent format-1 CreateAsync below: a set
            // created after setup is write-only because the installation is,
            // not because anybody remembered a dialog.
            return await RepositoryLifecycle.CreateWriteOnlyFromCredentialAsync(
                    store, provisioning.Credential, provisioning.KdfSalt.ToArray(), provisioning.KdfParameters,
                    createdBy: Environment.MachineName,
                    (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cancellationToken,
                    LoggerFor(typeof(RepositoryLifecycle)))
                .ConfigureAwait(false);
        }

        var descriptor = await RepositoryLifecycle.ReadDescriptorAsync(store, cancellationToken)
            .ConfigureAwait(false);

        if (!RepositoryLifecycle.IsWriteOnly(descriptor))
        {
            return null;
        }

        if (!provisioning.Credential.SealingPublicKey.SequenceEqual(descriptor.SealingPublicKey.Span))
        {
            // A write-only archive under a DIFFERENT root — a replica moved
            // in from another installation, or a state directory restored
            // over the top of one. Opening it with the wrong credential
            // would write records this archive's passphrase can never read
            // back, so it is refused by name and adoption is the remedy.
            throw new RepositoryOpenException(
                $"Set '{setId}' has a write-only staging archive derived from a different passphrase than "
                + "this installation's — adopt the set with that passphrase (ADR-0042 §10).");
        }

        return await RepositoryLifecycle.OpenWriteOnlyAsync(
                store, provisioning.Credential, cancellationToken, LoggerFor(typeof(RepositoryLifecycle)))
            .ConfigureAwait(false);
    }

    private async ValueTask<ArchiveHandle> ArchiveForAsync(
        string setId, bool createIfMissing, CancellationToken cancellationToken)
    {
        await _archivesGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_archives.TryGetValue(setId, out var open))
            {
                return open;
            }

            var path = ArchivePath(setId);
            Directory.CreateDirectory(path);
            var store = new LocalFileSystemObjectStore(path, LoggerFor<LocalFileSystemObjectStore>());

            OpenedRepository repository;
            if (WriteCredentials.TryLoad(setId) is { } credential)
            {
                // A provisioned write-only set (ADR-0042 §5): the credential
                // is the open, no passphrase involved — and the archive's
                // descriptor was written at provisioning, so absence here is
                // damage to name, never something to quietly re-create.
                using (credential)
                {
                    repository = ArchiveExists(setId)
                        ? await RepositoryLifecycle.OpenWriteOnlyAsync(
                                store, credential, cancellationToken, LoggerFor(typeof(RepositoryLifecycle)))
                            .ConfigureAwait(false)
                        : throw new RepositoryOpenException(
                            $"Set '{setId}' is provisioned write-only but its staging archive is missing — "
                            + "adopt it again with the passphrase (ADR-0042 §10).");
                }
            }
            else if (await OpenFromInstallationAsync(setId, store, createIfMissing, cancellationToken)
                .ConfigureAwait(false) is { } fromInstallation)
            {
                repository = fromInstallation;
            }
            else if (_passphrase is null)
            {
                throw new RepositoryOpenException(
                    $"Set '{setId}' is not provisioned write-only and this service started without a "
                    + "passphrase; run first-run setup to give this installation one, start the service "
                    + "with a passphrase, or provision the set (ADR-0044, ADR-0042).");
            }
            else if (ArchiveExists(setId))
            {
                repository = await RepositoryLifecycle.OpenAsync(
                        store, _passphrase, cancellationToken, LoggerFor(typeof(RepositoryLifecycle)))
                    .ConfigureAwait(false);
            }
            else if (createIfMissing)
            {
                repository = await RepositoryLifecycle.CreateAsync(
                        store, _passphrase, RepositoryCreationSettings.Default,
                        (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cancellationToken,
                        LoggerFor(typeof(RepositoryLifecycle)))
                    .ConfigureAwait(false);
            }
            else
            {
                throw new RepositoryOpenException($"No staging archive exists for set '{setId}'.");
            }

            ArchiveHandle archive;
            try
            {
                // Per-archive client state is keyed by REPOSITORY id, not set
                // id, so the CLI's direct mode and this service name the same
                // sequence file for the same archive — two names would be two
                // sequence spaces under one writer identity (ADR-0034).
                var repositoryIdHex = repository.RepositoryId.ToString();
                var cataloguePath = Path.Combine(Options.StateDirectory, $"catalogue-{repositoryIdHex}.db");
                var catalogueLogger = LoggerFor<CatalogueDb>();
                archive = new ArchiveHandle
                {
                    Store = store,
                    Repository = repository,
                    Catalogue = CatalogueDb.Open(cataloguePath, repository.RepositoryId, catalogueLogger),
                    CatalogueLogger = catalogueLogger,
                    Sequence = new WriterSequence(
                        new FileSequenceStateStore(Path.Combine(Options.StateDirectory, $"sequence-{repositoryIdHex}.txt"))),
                    SpoolDirectory = Path.Combine(Options.StateDirectory, "spool", repositoryIdHex),
                    CataloguePath = cataloguePath,
                };
            }
            catch
            {
                repository.Dispose();
                throw;
            }

            _archives.Add(setId, archive);
            return archive;
        }
        finally
        {
            _archivesGate.Release();
        }
    }

    /// <summary>Stops the service and releases the writer role.</summary>
    /// <returns>A task that completes when everything is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        // A host that stopped the runtime deliberately and then leaves an
        // await-using scope disposes twice; the second call must be a no-op.
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await Queue.DisposeAsync().ConfigureAwait(false);
        Progress.Complete();
        await RestoreSources.DisposeAsync().ConfigureAwait(false);

        foreach (var archive in _archives.Values)
        {
            archive.Dispose();
        }

        _archives.Clear();
        _passphrase?.Dispose();
        GrantRecipient.Dispose();
        _archivesGate.Dispose();

        // After the queue has drained: nothing can be waiting on a set gate.
        foreach (var gate in _setGates.Values)
        {
            gate.Dispose();
        }

        _setGates.Clear();
        _writerRole.Dispose();
    }
}
