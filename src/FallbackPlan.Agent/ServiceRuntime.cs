using Bodu;
using FallbackPlan.Application;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Local;
using CatalogueDb = FallbackPlan.Repository.Catalogue.Catalogue;

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
    private readonly Passphrase _passphrase;
    private readonly Dictionary<string, ArchiveHandle> _archives = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _archivesGate = new(1, 1);

    private ServiceRuntime(
        ServiceOptions options,
        StateDirectoryLock writerRole,
        Passphrase passphrase,
        LocalState state,
        JobStateStore jobs)
    {
        Options = options;
        _writerRole = writerRole;
        _passphrase = passphrase;
        State = state;
        Jobs = jobs;
        Progress = new ProgressHub();
        Queue = new JobScheduler();
    }

    /// <summary>How this service was started.</summary>
    public ServiceOptions Options { get; }

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

    /// <summary>This device's writer identity — one per device, shared by every archive it writes.</summary>
    public WriterId Writer => WriterId.FromBytes(State.WriterId);

    /// <summary>Where the client configuration lives.</summary>
    public string ConfigurationPath => Path.Combine(Options.StateDirectory, "config.json");

    /// <summary>The current configuration, re-read so an edit takes effect without a restart.</summary>
    public ClientConfiguration Configuration => ClientConfiguration.Load(ConfigurationPath);

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
        ServiceOptions options, Passphrase passphrase, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(options);
        ThrowHelper.ThrowIfNull(passphrase);
        cancellationToken.ThrowIfCancellationRequested();

        var writerRole = StateDirectoryLock.Acquire(options.StateDirectory, StateDirectoryLock.ServiceRole);
        try
        {
            var state = LocalState.LoadOrCreate(options.StateDirectory);
            var jobs = JobStateStore.Open(options.StateDirectory);

            return ValueTask.FromResult(
                new ServiceRuntime(options, writerRole, passphrase.Clone(), state, jobs)
                {
                    DestinationSync = DestinationSyncStore.Open(options.StateDirectory),
                    Notices = NoticeStore.Open(options.StateDirectory),
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
            var store = new LocalFileSystemObjectStore(path);

            OpenedRepository repository;
            if (ArchiveExists(setId))
            {
                repository = await RepositoryLifecycle.OpenAsync(store, _passphrase, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (createIfMissing)
            {
                repository = await RepositoryLifecycle.CreateAsync(
                        store, _passphrase, RepositoryCreationSettings.Default,
                        (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cancellationToken)
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
                archive = new ArchiveHandle
                {
                    Store = store,
                    Repository = repository,
                    Catalogue = CatalogueDb.Open(cataloguePath, repository.RepositoryId),
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
        await Queue.DisposeAsync().ConfigureAwait(false);
        Progress.Complete();

        foreach (var archive in _archives.Values)
        {
            archive.Dispose();
        }

        _archives.Clear();
        _passphrase.Dispose();
        _archivesGate.Dispose();
        _writerRole.Dispose();
    }
}
