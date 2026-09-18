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
    /// <summary>The directory holding a staging archive per staging-mode backup set, each under its set id (ADR-0034); a direct-ship set keeps nothing here (ADR-0046).</summary>
    public required string ArchivesRoot { get; init; }

    /// <summary>The state directory whose writer role the service holds.</summary>
    public required string StateDirectory { get; init; }

    /// <summary>How often the scheduler evaluates due-ness.</summary>
    public int PollSeconds { get; init; } = 60;

    /// <summary>
    /// An explicit backup-pool width (1..5), taking precedence over the
    /// configuration's <c>max_concurrent_backups</c> — a host's or a test
    /// harness's knob for a deterministic pool. Null reads the configuration.
    /// </summary>
    public int? MaxConcurrentBackupsOverride { get; init; }

    /// <summary>
    /// How long a preempted run may stay suspended before it self-cancels to
    /// the interruption-safe re-run path (ADR-0047 Amendment 1 rule 5). An
    /// hour when null — a host's or a test harness's knob, deliberately not
    /// configuration: the bound guards the service's own memory and intents.
    /// </summary>
    public TimeSpan? MaxPauseOverride { get; init; }

    /// <summary>
    /// Wraps the store a ship-sink opens per destination — the fault-injection
    /// seam the 04 §5.1 kill sweep runs through the direct-ship path
    /// (destination name, real store) → store the run writes. Null, the
    /// production value, writes the replica directly.
    /// </summary>
    internal Func<string, Storage.Abstractions.IObjectStore, Storage.Abstractions.IObjectStore>?
        ReplicaStoreDecorator { get; init; }

    /// <summary>
    /// Overrides the free-space probe behind the ship-sink's capacity floor
    /// (FR-DEST-010): destination root → available bytes, null meaning "the
    /// platform will not say", which is answered as room. Null, the
    /// production value, asks <see cref="DriveInfo"/>.
    /// </summary>
    internal Func<string, long?>? AvailableBytesProbe { get; init; }

    /// <summary>
    /// Overrides the volume-identity probe behind destination placement
    /// (ADR-0051, FR-DEST-017) and the failure-domain comparison
    /// (ADR-0018): path → volume id, null meaning "the platform will not
    /// say". A test harness's knob — a fixture's every path shares one real
    /// volume, which would refuse every fixture set. Null, the production
    /// value, asks the filesystem via the nearest existing ancestor.
    /// </summary>
    internal Func<string, ulong?>? VolumeIdentityOverride { get; init; }

    /// <summary>
    /// Overrides the physical-drive probe behind destination placement
    /// (ADR-0051's "different physical hdd where possible"): path → drive
    /// name, null meaning it cannot be named. Null, the production value,
    /// asks <see cref="Filesystem.Local.PhysicalDisk"/>.
    /// </summary>
    internal Func<string, string?>? PhysicalDiskOverride { get; init; }

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
/// and, per backup set, of that set's archive — its staging archive, or a
/// direct-ship set's local metadata store (ADR-0046) — with its writer
/// sequence, its catalogue, its spool (ADR-0034, ADR-0028 amendment).
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
    private readonly Dictionary<string, ArchiveHandle> _archives = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _archivesGate = new(1, 1);
    private bool _disposed;

    /// <summary>Guards <see cref="_configurationFingerprint"/>.</summary>
    private readonly Lock _configurationAnnounced = new();

    /// <summary>The canonical-content hash of the last configuration announced (event 3742).</summary>
    private byte[]? _configurationFingerprint;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _setGates =
        new(StringComparer.Ordinal);

    private ServiceRuntime(
        ServiceOptions options,
        StateDirectoryLock writerRole,
        LocalState state,
        JobStateStore jobs)
    {
        Options = options;
        _writerRole = writerRole;
        State = state;
        Jobs = jobs;
        Progress = new ProgressHub();
        Queue = new JobScheduler(
            Logger(options, typeof(JobScheduler)), ConfiguredBackupPoolWidth(options), options.MaxPauseOverride);
        GrantRecipient = GrantRecipient.Open(options.StateDirectory);
        WriteCredentials = new WriteCredentialStore(options.StateDirectory);
        InstallationCredential = new InstallationCredentialStore(options.StateDirectory);
        ReplicaOwners = ReplicaOwnerStore.Open(options.StateDirectory);
    }

    /// <summary>How this service was started.</summary>
    public ServiceOptions Options { get; }

    /// <summary>A logger for <paramref name="category"/>, or a silent one.</summary>
    private static ILogger Logger(ServiceOptions options, Type category) =>
        options.Logging?.Factory.CreateLogger(category.FullName!) ?? NullLogger.Instance;

    /// <summary>
    /// The backup pool's width (ADR-0047), read once when the service starts
    /// — the workers spawn here, so a configuration change applies at the
    /// next start. A configuration that will not load answers the default:
    /// the pool's width must never be the reason a service refuses to start,
    /// and the load path re-validates loudly everywhere else.
    /// </summary>
    // Internal, not private: the host's startup configuration record
    // (ADR-0049) reports the same width the queue was built with.
    internal static int ConfiguredBackupPoolWidth(ServiceOptions options)
    {
        if (options.MaxConcurrentBackupsOverride is { } width)
        {
            return Math.Clamp(width, 1, 5);
        }

        try
        {
            return Math.Clamp(
                ClientConfiguration.Load(Path.Combine(options.StateDirectory, "config.json"))
                    .EffectiveMaxConcurrentBackups,
                1,
                5);
        }
        catch (ClientStateException)
        {
            return 2;
        }
    }

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

    /// <summary>
    /// Serialises backup enqueues (ADR-0027 §1 via ADR-0047 Amendment 3):
    /// the already-running check, the journal begin and the queue insert
    /// must be one atomic step, or two triggers arriving together — a
    /// manual click racing the upsert's first backup, two clicks, a click
    /// racing the pass — both pass the check and the set runs twice over
    /// one spool directory.
    /// </summary>
    internal object BackupEnqueueGate { get; } = new();

    /// <summary>The open restore sources (ADR-0041).</summary>
    internal RestoreSourceRegistry RestoreSources { get; } = new();

    /// <summary>The service's envelope recipient keypair (ADR-0042 §4).</summary>
    internal GrantRecipient GrantRecipient { get; }

    /// <summary>The per-set write credentials this service holds (ADR-0042 §5).</summary>
    internal WriteCredentialStore WriteCredentials { get; }

    /// <summary>
    /// Which peer each replica stored here belongs to (peer-protocol 05 §2).
    /// </summary>
    /// <remarks>
    /// The runtime's, and the one instance in the process: the remote
    /// binding borrows it rather than opening its own, because the
    /// attribution the retrieval gate consults and the attribution the
    /// operator re-points (ADR-0053 §3) must be the same object. Two stores
    /// over one file would each write the whole file from its own picture,
    /// and the override would be undone by the next offer the listener
    /// recorded.
    /// </remarks>
    public ReplicaOwnerStore ReplicaOwners { get; }

    /// <summary>
    /// What first-run setup provisioned, from which every set's archive —
    /// staging, or a direct-ship set's metadata store — is created
    /// (ADR-0044 §2).
    /// </summary>
    internal InstallationCredentialStore InstallationCredential { get; }

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
    /// How far first-run setup has got: <c>setup_required</c> or
    /// <c>ready</c> (FR-SVC-011).
    /// </summary>
    /// <remarks>
    /// Two states, because "has a passphrase" and "is finished" are the same
    /// thing: the passphrase is the whole recovery credential (ADR-0060),
    /// and nothing else has to be produced or saved for the ceremony to be
    /// complete. There was a third, <c>kit_required</c>, while the ceremony
    /// ended with a saved recovery kit; it went with the kit.
    /// </remarks>
    public string SetupState => IsSetUp ? "ready" : "setup_required";

    /// <summary>
    /// The volume a path sits on, or null when the platform will not say —
    /// consulted via the nearest existing ancestor, so a destination
    /// directory that is not created yet still answers for where it would
    /// land. One probe for the failure-domain comparison (ADR-0018) and the
    /// placement condition (ADR-0051), so status and choosing cannot
    /// disagree about what shares a drive.
    /// </summary>
    internal Func<string, ulong?> VolumeIdOf => Options.VolumeIdentityOverride ?? DefaultVolumeIdOf;

    /// <summary>The physical drive behind a path, or null where it cannot be named (ADR-0051).</summary>
    internal Func<string, string?> DiskIdOf => Options.PhysicalDiskOverride ?? Filesystem.Local.PhysicalDisk.Identify;

    private static ulong? DefaultVolumeIdOf(string path)
    {
        var current = Path.GetFullPath(path);
        while (current is not null && !Directory.Exists(current) && !File.Exists(current))
        {
            current = Path.GetDirectoryName(current);
        }

        return current is not null && Filesystem.Local.LocalFileSystemSource.TryStat(current, out var stat)
            ? stat.Device
            : null;
    }

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
    /// Reads the configuration through the logged path: every read leaves its
    /// Debug record, and the first read — or one whose content differs from
    /// the last — is announced at Information (event 3742).
    /// </summary>
    /// <remarks>
    /// The scheduler reads through here every pass, so the announcement has
    /// to be change-detected rather than unconditional: unconditional, the
    /// one message was 98% of a real installation's Information tier, saying
    /// each time that nothing had happened. The runtime is the right holder
    /// of the memory because <see cref="ClientConfiguration.Load"/> is a pure
    /// function of the file and must stay one. The fingerprint is of the
    /// canonical export rather than the raw bytes, so reformatting the file
    /// is not an event but changing what it says is.
    /// </remarks>
    /// <exception cref="ClientStateException">The file is invalid — the message names the defect.</exception>
    public ClientConfiguration LoadConfiguration()
    {
        var configuration = ClientConfiguration.Load(ConfigurationPath, LoggerFor<ClientConfiguration>());

        var fingerprint = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(configuration.ExportJson()));
        lock (_configurationAnnounced)
        {
            if (_configurationFingerprint is { } seen && fingerprint.AsSpan().SequenceEqual(seen))
            {
                return configuration;
            }

            _configurationFingerprint = fingerprint;
        }

        var announce = LoggerFor<ServiceRuntime>();
        Log.ConfigurationChanged(
            announce,
            configuration.SchemaVersion,
            configuration.BackupSets.Count,
            configuration.Destinations.Count);
        return configuration;
    }

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

    /// <summary>
    /// The directory holding a direct-ship set's metadata store (ADR-0046):
    /// descriptor, keys, journal, index, snapshots — never blob content.
    /// </summary>
    /// <param name="setId">The set's 32-hex identity.</param>
    public string SetMetadataPath(string setId) => Path.Combine(Options.StateDirectory, "sets", setId);

    /// <summary>
    /// Whether a set's archive exists on disk yet — its staging archive, or
    /// a direct-ship set's metadata store (ADR-0046); which of the two also
    /// decides which mode <see cref="ArchiveForAsync(BackupSetConfiguration, CancellationToken)"/> opens.
    /// </summary>
    /// <param name="setId">The set's 32-hex identity.</param>
    public bool ArchiveExists(string setId) =>
        File.Exists(Path.Combine(ArchivePath(setId), RepositoryLifecycle.DescriptorKey.Value))
        || File.Exists(Path.Combine(SetMetadataPath(setId), RepositoryLifecycle.DescriptorKey.Value));

    /// <summary>
    /// Takes the writer role. No archive opens here: each set's archive —
    /// its staging archive, or a direct-ship set's metadata store — opens,
    /// or is created, on first use, so start-up cost does
    /// not scale with sets configured. Failure to take the role is refused
    /// with the holder named — never worked around (FR-SVC-002).
    /// </summary>
    /// <param name="options">How to start.</param>
    /// <param name="cancellationToken">Cancels start-up.</param>
    /// <returns>The running service.</returns>
    public static ValueTask<ServiceRuntime> StartAsync(ServiceOptions options, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var writerRole = StateDirectoryLock.Acquire(options.StateDirectory, StateDirectoryLock.ServiceRole);
        try
        {
            var state = LocalState.LoadOrCreate(options.StateDirectory);
            var jobs = JobStateStore.Open(options.StateDirectory);
            var notices = NoticeStore.Open(options.StateDirectory, Logger(options, typeof(NoticeStore)));

            // The journal's own crash leftovers (ADR-0049): rows a previous
            // process left mid-run would otherwise claim to be running for
            // ever — the queue that owned them died with that process. Safe
            // exactly here, because the writer role acquired above means no
            // other live process owns any of them. Not silent: somebody's
            // 3 a.m. run was interrupted, and that is a notice at breakfast.
            var nowMs = (ulong)DateTimeOffset.Now.ToUnixTimeMilliseconds();
            var settled = jobs.SettleUnfinished(
                nowMs, "interrupted — the service stopped while this run was live; it retries on the next pass");
            if (settled.Count > 0)
            {
                notices.Raise(
                    "jobs-interrupted",
                    $"{settled.Count} backup run(s) were interrupted by a service stop and will retry on the next pass.",
                    nowMs);
            }

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
                new ServiceRuntime(options, writerRole, state, jobs)
                {
                    DestinationSync = DestinationSyncStore.Open(options.StateDirectory),
                    Notices = notices,
                });
        }
        catch
        {
            writerRole.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The set's archive, opened on first use — its staging archive, or a
    /// direct-ship set's metadata store fronted by the ship sink (ADR-0046)
    /// — and created on first backup, because neither is something anybody
    /// runs `init` for (ADR-0034 §1).
    /// </summary>
    /// <param name="set">The set whose archive to resolve.</param>
    /// <param name="cancellationToken">Cancels an open or create.</param>
    /// <returns>The archive, held open by the runtime until disposal.</returns>
    /// <exception cref="RepositoryOpenException">The archive on disk refused to open, or no credential this service holds opens it.</exception>
    public async ValueTask<ArchiveHandle> ArchiveForAsync(
        BackupSetConfiguration set, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(set);
        return await ArchiveForAsync(set.Id, createIfMissing: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A set's archive — staging or direct-ship metadata store — when it
    /// already exists on disk; null when the set has never been backed up. Read paths use this so that listing
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
    /// Every configured set whose archive exists on disk — staging or
    /// direct-ship — with the archive open — the enumeration behind snapshots, status, verify and check.
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
    /// Copies a staging archive's metadata — everything except blob content
    /// and the lifecycle objects that never leave staging — into a
    /// direct-ship set's metadata store, once, at the first open after the
    /// flip (ADR-0046). Idempotent: every put is if-absent.
    /// </summary>
    private ValueTask MigrateStagingMetadataAsync(
        string setId, LocalFileSystemObjectStore metadata, CancellationToken cancellationToken) =>
        CopyMetadataAsync(
            new LocalFileSystemObjectStore(ArchivePath(setId), LoggerFor<LocalFileSystemObjectStore>()),
            metadata, cancellationToken);

    /// <summary>
    /// Copies a repository's metadata — everything except blob content and
    /// the lifecycle objects that never leave the writer's side — from one
    /// store into a direct-ship set's metadata store. The staging migration
    /// (ADR-0046) and archive adoption (ADR-0061) are the same copy from
    /// different sources. Idempotent: every put is if-absent.
    /// </summary>
    internal static async ValueTask CopyMetadataAsync(
        Storage.Abstractions.IObjectStore from, LocalFileSystemObjectStore metadata, CancellationToken cancellationToken) =>
        _ = await CopyBackAsync(from, metadata, admitBlob: null, cancellationToken).ConfigureAwait(false);

    /// <summary>What a copy-back moved: objects and bytes, for the notice and the log.</summary>
    internal readonly record struct CopiedBack(long Objects, long Bytes);

    /// <summary>
    /// Copies what <paramref name="from"/> holds and <paramref name="to"/>
    /// lacks, if-absent, in publication order: the descriptor, the blobs
    /// admitted, the journal and index, everything else, and the snapshot
    /// manifests last — so an interrupted copy never leaves a manifest in
    /// place without the blobs it references, and an archive never lists
    /// history it cannot restore (FR-GC-009,
    /// [ADR-0062 Amendment 2](../../docs/adr/0062-the-destination-is-the-rollback-witness.md)).
    /// Tombstones and leases are the writer's own and never travel.
    /// </summary>
    /// <param name="from">The destination's copy, the newer one.</param>
    /// <param name="to">The set's own store — a metadata store or a staging archive.</param>
    /// <param name="admitBlob">Which blob keys travel; null admits none, which is the metadata-only copy.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>What was copied.</returns>
    internal static async ValueTask<CopiedBack> CopyBackAsync(
        Storage.Abstractions.IObjectStore from, Storage.Abstractions.IObjectStore to,
        Func<string, bool>? admitBlob, CancellationToken cancellationToken)
    {
        // Listed once and bucketed, because a store lists in its own order
        // and the order here is a correctness property, not a preference.
        var phases = new List<Storage.Abstractions.ObjectEntry>[CopyBackPhases.Length + 1];
        for (var index = 0; index < phases.Length; index++)
        {
            phases[index] = [];
        }

        await foreach (var entry in from.ListAsync(
            Storage.Abstractions.ObjectPrefix.All, Storage.Abstractions.ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            var key = entry.Key.Value;
            if (key.StartsWith("tombstones/", StringComparison.Ordinal)
                || key.StartsWith("leases/", StringComparison.Ordinal))
            {
                continue;
            }

            if (key.StartsWith("blobs/", StringComparison.Ordinal) && (admitBlob is null || !admitBlob(key)))
            {
                continue;
            }

            phases[CopyBackPhaseOf(key)].Add(entry);
        }

        var objects = 0L;
        var bytes = 0L;
        foreach (var phase in phases)
        {
            foreach (var entry in phase)
            {
                var put = await to.PutAsync(
                    entry.Key,
                    async token =>
                    {
                        var read = await from.OpenReadAsync(entry.Key, range: null, token).ConfigureAwait(false);
                        return read.Outcome == Storage.Abstractions.OpenReadOutcome.Found && read.Content is not null
                            ? read.Content
                            : throw new IOException($"Object {entry.Key.Value} listed but could not be read to copy.");
                    },
                    Storage.Abstractions.PutConditions.IfNotExists,
                    cancellationToken).ConfigureAwait(false);
                if (put.Outcome == Storage.Abstractions.PutOutcome.Created)
                {
                    objects++;
                    bytes += entry.Length;
                }
            }
        }

        return new CopiedBack(objects, bytes);
    }

    /// <summary>Publication order for a copy-back; the catch-all phase sits between the named prefixes and the manifests.</summary>
    private static readonly string[] CopyBackPhases =
        ["repository-format", "blobs/", "journal/", "index/", "hints/", "audit/", "snapshots/"];

    private static int CopyBackPhaseOf(string key)
    {
        if (key is "repository-format")
        {
            return 0;
        }

        if (key.StartsWith("snapshots/", StringComparison.Ordinal))
        {
            return CopyBackPhases.Length;
        }

        for (var index = 1; index < CopyBackPhases.Length - 1; index++)
        {
            if (key.StartsWith(CopyBackPhases[index], StringComparison.Ordinal))
            {
                return index;
            }
        }

        // Anything unnamed lands after the named prefixes and before the
        // manifests, which is where the copier's own catch-all phase sits.
        return CopyBackPhases.Length - 1;
    }

    /// <summary>The set as configured, or null when the id names none — or the file will not load.</summary>
    private BackupSetConfiguration? FindConfiguredSet(string setId)
    {
        try
        {
            return Configuration.BackupSets.FirstOrDefault(set =>
                string.Equals(set.Id, setId, StringComparison.Ordinal));
        }
        catch (ClientStateException)
        {
            return null;
        }
    }

    /// <summary>
    /// Opens — or creates — a set's archive from the installation
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
    /// <summary>
    /// The write credential a set opens with, for a caller that needs the
    /// credential itself rather than an open archive: the set's own where it
    /// was provisioned per set (ADR-0042 §5), otherwise the installation's
    /// (ADR-0044 §2), which is what every set created after setup was
    /// written under. Null when the service holds neither. The caller owns
    /// and disposes what it gets; the installation's is a copy, so the
    /// stored provisioning is never handed out.
    /// </summary>
    internal RepositoryWriteCredential? TryLoadCredentialFor(string setId)
    {
        if (WriteCredentials.TryLoad(setId) is { } perSet)
        {
            return perSet;
        }

        using var provisioning = InstallationCredential.TryLoad();
        return provisioning is null ? null : RepositoryWriteCredential.FromBytes(provisioning.Credential.ToBytes());
    }

    private async ValueTask<OpenedRepository?> OpenFromInstallationAsync(
        string setId, LocalFileSystemObjectStore store, bool descriptorExists, bool createIfMissing,
        CancellationToken cancellationToken)
    {
        using var provisioning = InstallationCredential.TryLoad();
        if (provisioning is null)
        {
            return null;
        }

        if (!descriptorExists)
        {
            if (!createIfMissing)
            {
                return null;
            }

            // The first backup of a set on a set-up installation: the set is
            // created from the installation credential, under the
            // installation's salt, because the installation is what holds a
            // passphrase's authority — not because anybody remembered a
            // dialog (ADR-0044).
            return await RepositoryLifecycle.CreateAsync(
                    store, provisioning.Credential, provisioning.KdfSalt.ToArray(), provisioning.KdfParameters,
                    createdBy: Environment.MachineName,
                    (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cancellationToken,
                    LoggerFor(typeof(RepositoryLifecycle)))
                .ConfigureAwait(false);
        }

        var descriptor = await RepositoryLifecycle.ReadDescriptorAsync(store, cancellationToken)
            .ConfigureAwait(false);

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

        return await RepositoryLifecycle.OpenAsync(
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

            // Which mode: the configuration's direct_ship flag decides, and a
            // direct-ship set's local store holds metadata only (ADR-0046). A
            // flagged set that still has a staging archive is mid-migration:
            // its metadata is copied into the metadata store at this first
            // open, and the staging archive stays on disk — a read-only seed
            // source the sink falls back to — until the explicit
            // retire_staging verb proves nothing would be lost and deletes it.
            var stagingExists = File.Exists(
                Path.Combine(ArchivePath(setId), RepositoryLifecycle.DescriptorKey.Value));
            var directShip = FindConfiguredSet(setId)?.DirectShip == true;

            var path = directShip ? SetMetadataPath(setId) : ArchivePath(setId);
            Directory.CreateDirectory(path);
            var store = new LocalFileSystemObjectStore(path, LoggerFor<LocalFileSystemObjectStore>());
            var descriptorExists = File.Exists(Path.Combine(path, RepositoryLifecycle.DescriptorKey.Value));

            if (directShip && stagingExists && !descriptorExists)
            {
                await MigrateStagingMetadataAsync(setId, store, cancellationToken).ConfigureAwait(false);
                descriptorExists = true;
                Notices.Raise(
                    $"staging-retirable:{setId}",
                    $"Set '{setId}' now publishes straight to its destinations; its staging archive remains as "
                    + "a seed source. Once a scheduler pass has finished seeding, retire it with the "
                    + "retire_staging verb to reclaim the space (ADR-0046).",
                    (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }

            OpenedRepository repository;
            if (WriteCredentials.TryLoad(setId) is { } credential)
            {
                // A provisioned write-only set (ADR-0042 §5): the credential
                // is the open, no passphrase involved — and the archive's
                // descriptor was written at provisioning, so absence here is
                // damage to name, never something to quietly re-create.
                using (credential)
                {
                    repository = descriptorExists
                        ? await RepositoryLifecycle.OpenAsync(
                                store, credential, cancellationToken, LoggerFor(typeof(RepositoryLifecycle)))
                            .ConfigureAwait(false)
                        : throw new RepositoryOpenException(
                            $"Set '{setId}' is provisioned write-only but its staging archive is missing — "
                            + "adopt it again with the passphrase (ADR-0042 §10).");
                }
            }
            else if (await OpenFromInstallationAsync(setId, store, descriptorExists, createIfMissing, cancellationToken)
                .ConfigureAwait(false) is { } fromInstallation)
            {
                repository = fromInstallation;
            }
            else
            {
                // No per-set credential and no installation credential: the
                // service holds nothing that opens or creates an archive.
                // A service never holds a passphrase (ADR-0042 §5), so the
                // remedies are the two ceremonies that leave a credential
                // behind, named here rather than guessed at.
                throw new RepositoryOpenException(
                    $"Set '{setId}' is not provisioned and this installation has not run first-run setup; run "
                    + "setup to give this installation its passphrase, or provision the set (ADR-0044, ADR-0042 §10).");
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

                // A direct-ship set's working store is the sink: blobs to the
                // destinations, metadata locally AND to the destinations
                // (ADR-0046). The repository was opened/created against the
                // metadata store above, so its descriptor and keys are the
                // planning copy the sink seeds outward from.
                var sink = directShip
                    ? new DestinationShipSink(
                        this, store, setId, repositoryIdHex, LoggerFor<DestinationShipSink>(),
                        stagingFallback: stagingExists
                            ? new LocalFileSystemObjectStore(
                                ArchivePath(setId), LoggerFor<LocalFileSystemObjectStore>())
                            : null)
                    : null;

                archive = new ArchiveHandle
                {
                    Store = (Storage.Abstractions.IObjectStore?)sink ?? store,
                    ShipSink = sink,
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

            await AdoptObservedHeadAsync(setId, archive, cancellationToken).ConfigureAwait(false);

            _archives.Add(setId, archive);
            return archive;
        }
        finally
        {
            _archivesGate.Release();
        }
    }

    /// <summary>
    /// Heals a direct-ship set whose local metadata has fallen behind a
    /// destination (ADR-0062): copies the destination's metadata into the
    /// set's metadata store, rebuilds the catalogue in place from it, and
    /// moves the writer past whatever the healed archive now attests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three steps are each idempotent and each safe to repeat after a
    /// failure: the copy is if-absent, the rebuild upserts, and the sequence
    /// only ever rises. So a heal that fails halfway leaves a state
    /// directory the next pass heals again, and the caller decides that by
    /// the same test that decided this one — the destination's journal head
    /// still exceeds the local one.
    /// </para>
    /// <para>
    /// Runs inside the fan-out pass, under the set gate, over the runtime's
    /// live catalogue handle: the rebuild adds what is missing and disturbs
    /// nothing, which is what lets it run without evicting the archive
    /// under a read path.
    /// </para>
    /// </remarks>
    /// <param name="setId">The set's 32-hex identity.</param>
    /// <param name="archive">The set's open archive — a direct-ship one, whose store is the sink, or a staging one.</param>
    /// <param name="replica">The destination's replica, the newer copy.</param>
    /// <param name="cancellationToken">Cancels the heal.</param>
    /// <returns>What was copied back, or why the heal did not happen, in a sentence for the notice.</returns>
    internal async ValueTask<HealOutcome> HealFromDestinationAsync(
        string setId, ArchiveHandle archive, Storage.Abstractions.IObjectStore replica, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(setId);
        ThrowHelper.ThrowIfNull(archive);
        ThrowHelper.ThrowIfNull(replica);

        try
        {
            CopiedBack copied;
            Storage.Abstractions.IObjectStore rebuildFrom;
            if (archive.ShipSink is null)
            {
                // A staging set lacks content as well as metadata, and the
                // content it is owed is exactly the closure of the snapshots
                // it does not list — never everything the destination holds,
                // because staging retirement sheds historic data blobs on
                // purpose and a heal must not bring history back. Metadata
                // blobs all travel: retirement keeps every one, and the
                // rebuild needs them.
                var needed = await ClosureOfMissingSnapshotsAsync(archive, replica, cancellationToken)
                    .ConfigureAwait(false);
                copied = await CopyBackAsync(
                    replica, archive.Store,
                    key => key.StartsWith("blobs/meta/", StringComparison.Ordinal) || needed.Contains(key),
                    cancellationToken).ConfigureAwait(false);
                rebuildFrom = archive.Store;
            }
            else
            {
                var metadata = new LocalFileSystemObjectStore(SetMetadataPath(setId), LoggerFor<LocalFileSystemObjectStore>());
                copied = await CopyBackAsync(replica, metadata, admitBlob: null, cancellationToken).ConfigureAwait(false);
                rebuildFrom = replica;
            }

            var warnings = new List<string>();
            using (var reader = await CatalogueRebuild.OpenMetadataReaderAsync(rebuildFrom, archive.Repository, cancellationToken)
                .ConfigureAwait(false))
            {
                await CatalogueRebuild.RebuildIntoAsync(
                    this, archive.Catalogue, rebuildFrom, archive.Repository, reader, warnings, cancellationToken)
                    .ConfigureAwait(false);
            }

            var log = LoggerFor<ServiceRuntime>();
            foreach (var warning in warnings)
            {
                Log.HealRebuildFinding(log, setId, warning);
            }

            // Over the healed sink now, so the index plane's watermarks count
            // too: the pass moved the writer past the destination's journal
            // head before calling this, and this only ever raises further.
            await AdoptObservedHeadAsync(setId, archive, cancellationToken).ConfigureAwait(false);
            return new HealOutcome(null, copied.Objects, copied.Bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new HealOutcome(exception.Message, 0, 0);
        }
    }

    /// <summary>How a heal from a destination ended.</summary>
    /// <param name="Failure">Why it did not happen, or null when it did.</param>
    /// <param name="CopiedObjects">Objects brought back.</param>
    /// <param name="CopiedBytes">Bytes brought back.</param>
    internal sealed record HealOutcome(string? Failure, long CopiedObjects, long CopiedBytes);

    /// <summary>
    /// The blob keys a staging archive is owed: the closure, walked at the
    /// destination under the metadata key, of every snapshot the destination
    /// lists and the archive does not. A destination snapshot that will not
    /// decode, or a closure that will not walk, fails the heal rather than
    /// narrowing it: the pass then converges nothing, which is the protection,
    /// and the damage is the verifier's to name.
    /// </summary>
    private static async ValueTask<HashSet<string>> ClosureOfMissingSnapshotsAsync(
        ArchiveHandle archive, Storage.Abstractions.IObjectStore replica, CancellationToken cancellationToken)
    {
        var listed = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var entry in archive.Store.ListAsync(
            Storage.Abstractions.ObjectPrefix.Parse("snapshots/"), Storage.Abstractions.ListOptions.Default,
            cancellationToken).ConfigureAwait(false))
        {
            listed.Add(entry.Key.Value);
        }

        var survey = await Retention.StagingMark.SurveyAsync(replica, archive.Repository, cancellationToken)
            .ConfigureAwait(false);
        if (survey.Undecodable.Count > 0)
        {
            throw new InvalidDataException(
                $"the destination holds a snapshot that will not decode ({survey.Undecodable[0]}), so what its history needs cannot be told");
        }

        var missing = survey.Snapshots.Where(snapshot => !listed.Contains(snapshot.StoreKey.Value)).ToList();
        var needed = new HashSet<string>(StringComparer.Ordinal);
        if (missing.Count == 0)
        {
            return needed;
        }

        using var reader = new Repository.RepositoryReader(archive.Repository.RepositoryId, archive.Repository.Keys, replica);
        await reader.LoadBlobsAsync(cancellationToken).ConfigureAwait(false);
        var (reachable, unwalkable) = await Retention.StagingMark.MarkAsync(reader, missing, cancellationToken)
            .ConfigureAwait(false);
        if (unwalkable.Count > 0)
        {
            throw new InvalidDataException(
                $"the destination's newer history would not walk ({unwalkable[0]}), so what it needs cannot be told");
        }

        foreach (var blob in reader.Blobs)
        {
            if (blob.Records.Any(record => reachable.Contains(record.ObjectId)))
            {
                needed.Add(blob.StoreKey.Value);
            }
        }

        return needed;
    }

    /// <summary>
    /// Asks the repository how far this writer had got, and moves the local
    /// sequence past it when local state turns out to be behind (NFR-SEC-005).
    /// </summary>
    /// <remarks>
    /// The sequence file is allocation state and lives in the state directory;
    /// the repository's own signed index and its journal keys carry the same
    /// fact and live at every destination. Consulting them at open is what
    /// turns a state directory that was lost, restored from an older copy, or
    /// replaced by a rebuilt machine into a recovery rather than an I/O error
    /// halfway through the next backup. It never lowers the sequence: a writer
    /// ahead of the published head is the ordinary case.
    /// </remarks>
    internal async ValueTask AdoptObservedHeadAsync(
        string setId, ArchiveHandle archive, CancellationToken cancellationToken)
    {
        SequenceAdoption adoption;
        try
        {
            var loader = new IndexLoader(
                archive.Store, archive.Repository.RepositoryId, archive.Repository.Credential,
                LoggerFor<IndexLoader>());
            var index = await loader.LoadAsync(
                currentGeneration: 0, gapPatienceGenerations: 0, isSequenceAccountedAsync: null,
                blobState: null, cancellationToken).ConfigureAwait(false);

            adoption = archive.Sequence.AdoptObservedHead(
                await ObservedHead.OfAsync(archive.Store, Writer, index, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            // A repository too damaged to read its own index is a problem the
            // damage surfaces name properly. Refusing the open over it would
            // deny the restore that is the way out of that state, and the
            // colliding-put refusal still stands behind this.
            Log.ObservedHeadUnavailable(LoggerFor<ServiceRuntime>(), setId, exception.Message);
            return;
        }

        if (adoption is not SequenceAdoption.Adopted adopted)
        {
            return;
        }

        Log.ObservedHeadAdopted(LoggerFor<ServiceRuntime>(), setId, adopted.From, adopted.To);
        Notices.Raise(
            $"sequence-adopted:{setId}",
            $"Set '{setId}' would have re-used writer sequence numbers its own history already holds: local "
            + $"state said {adopted.From}, the repository attests {adopted.To - 1}. The writer has moved past "
            + "it, so backups continue — but the state directory was lost, restored from an older copy, or "
            + "belongs to a rebuilt machine, and anything else kept beside it deserves the same suspicion.",
            (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// Closes and forgets a set's open archive handle so the next open reads
    /// the configuration fresh — the storage-shape flip's seam (ADR-0046,
    /// contract 1.23): a flagged staging set migrates at that next open, in
    /// this process, no restart. The caller must have ensured no run holds
    /// the handle; the command boundary refuses the flip while one is live.
    /// </summary>
    /// <param name="setId">The set whose handle to evict; absent is a no-op.</param>
    /// <param name="cancellationToken">Cancels waiting for the archive gate.</param>
    /// <returns>A task that completes once the handle is closed and forgotten.</returns>
    public async ValueTask EvictArchiveAsync(string setId, CancellationToken cancellationToken)
    {
        await _archivesGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_archives.Remove(setId, out var open))
            {
                open.Dispose();
            }
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
