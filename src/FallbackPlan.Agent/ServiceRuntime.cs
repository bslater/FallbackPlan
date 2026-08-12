using Bodu;
using FallbackPlan.Application;
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
    /// <summary>The repository the service writes to.</summary>
    public required string RepositoryPath { get; init; }

    /// <summary>The state directory whose writer role the service holds.</summary>
    public required string StateDirectory { get; init; }

    /// <summary>How often the scheduler evaluates due-ness.</summary>
    public int PollSeconds { get; init; } = 60;
}

/// <summary>
/// The long-lived service (ADR-0028 §2): sole holder of the repository writer
/// identity, the state directory, and the catalogue for as long as it runs.
/// </summary>
/// <remarks>
/// <para>
/// What changed from the poll-loop Agent it replaces is not only ownership but
/// cost. That version re-derived Argon2id, re-parsed the configuration, and
/// opened and closed SQLite on <i>every poll</i> — sixty seconds of work thrown
/// away and redone, because nothing was allowed to live between passes. Holding
/// the writer role is what makes holding the open repository correct.
/// </para>
/// <para>
/// One <see cref="WriterSequence"/> exists here and nowhere else. Its guard is
/// an in-process lock, which was the wrong tool for a sequence space shared
/// across processes and is exactly the right one for a sequence space owned by
/// this one.
/// </para>
/// </remarks>
public sealed class ServiceRuntime : IAsyncDisposable
{
    private readonly StateDirectoryLock _writerRole;
    private readonly ArchiveHandle _archive;

    private ServiceRuntime(
        ServiceOptions options,
        StateDirectoryLock writerRole,
        ArchiveHandle archive,
        LocalState state,
        JobStateStore jobs)
    {
        Options = options;
        _writerRole = writerRole;
        _archive = archive;
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

    /// <summary>Where progress goes.</summary>
    public ProgressHub Progress { get; }

    /// <summary>What is running and what is waiting.</summary>
    public JobScheduler Queue { get; }

    /// <summary>The archive the service writes, with everything held open for it.</summary>
    public ArchiveHandle Archive => _archive;

    /// <summary>The opened repository.</summary>
    public OpenedRepository Repository => _archive.Repository;

    /// <summary>The object store.</summary>
    public LocalFileSystemObjectStore Store => _archive.Store;

    /// <summary>The writer-side catalogue. Read paths open their own.</summary>
    public CatalogueDb Catalogue => _archive.Catalogue;

    /// <summary>The one sequence allocator for this device's writer role.</summary>
    public WriterSequence Sequence => _archive.Sequence;

    /// <summary>This device's writer identity.</summary>
    public WriterId Writer => WriterId.FromBytes(State.WriterId);

    /// <summary>Where blobs are spooled before upload.</summary>
    public string SpoolDirectory => _archive.SpoolDirectory;

    /// <summary>Where the client configuration lives.</summary>
    public string ConfigurationPath => Path.Combine(Options.StateDirectory, "config.json");

    /// <summary>The current configuration, re-read so an edit takes effect without a restart.</summary>
    public ClientConfiguration Configuration => ClientConfiguration.Load(ConfigurationPath);

    /// <summary>
    /// Takes the writer role and opens everything the service holds for its
    /// lifetime. Failure to take the role is refused with the holder named —
    /// never worked around (FR-SVC-002).
    /// </summary>
    /// <param name="options">How to start.</param>
    /// <param name="passphrase">Unlocks the repository. Held by this process only.</param>
    /// <param name="cancellationToken">Cancels start-up.</param>
    /// <returns>The running service.</returns>
    public static async ValueTask<ServiceRuntime> StartAsync(
        ServiceOptions options, Passphrase passphrase, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(options);
        ThrowHelper.ThrowIfNull(passphrase);

        var writerRole = StateDirectoryLock.Acquire(options.StateDirectory, StateDirectoryLock.ServiceRole);
        try
        {
            var store = new LocalFileSystemObjectStore(options.RepositoryPath);
            var repository = await RepositoryLifecycle.OpenAsync(store, passphrase, cancellationToken)
                .ConfigureAwait(false);

            ArchiveHandle archive;
            try
            {
                var cataloguePath = Path.Combine(options.StateDirectory, "catalogue.db");
                archive = new ArchiveHandle
                {
                    Store = store,
                    Repository = repository,
                    Catalogue = CatalogueDb.Open(cataloguePath, repository.RepositoryId),
                    Sequence = new WriterSequence(
                        new FileSequenceStateStore(Path.Combine(options.StateDirectory, "sequence.txt"))),
                    SpoolDirectory = Path.Combine(options.StateDirectory, "spool"),
                    CataloguePath = cataloguePath,
                };
            }
            catch
            {
                repository.Dispose();
                throw;
            }

            try
            {
                var state = LocalState.LoadOrCreate(options.StateDirectory);
                var jobs = JobStateStore.Open(options.StateDirectory);

                return new ServiceRuntime(options, writerRole, archive, state, jobs);
            }
            catch
            {
                archive.Dispose();
                throw;
            }
        }
        catch
        {
            writerRole.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a second catalogue connection for a read path.
    /// </summary>
    /// <returns>A catalogue the caller owns and must dispose.</returns>
    /// <remarks>
    /// A restore may run alongside a backup (ADR-0029 §4), and the catalogue
    /// holds one unsynchronised connection. Under WAL a reader does not block
    /// the writer, so a read path gets its own connection rather than
    /// contending for the writer's.
    /// </remarks>
    public CatalogueDb OpenReadCatalogue() => _archive.OpenReadCatalogue();

    /// <summary>Stops the service and releases the writer role.</summary>
    /// <returns>A task that completes when everything is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        await Queue.DisposeAsync().ConfigureAwait(false);
        Progress.Complete();
        _archive.Dispose();
        _writerRole.Dispose();
    }
}
