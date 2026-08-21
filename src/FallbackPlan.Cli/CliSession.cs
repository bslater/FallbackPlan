using FallbackPlan.Application;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Local;
using FallbackPlan.Cli.Resources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FallbackPlan.Cli;

/// <summary>
/// A command's failure with a message for the operator — distinct from a bug,
/// which is allowed to escape with a stack trace.
/// </summary>
public sealed class CliFailureException : Exception
{
    public CliFailureException(string message)
        : base(message)
    {
    }

    public CliFailureException()
    {
    }

    public CliFailureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Shared plumbing for every command: the store at <c>--repo</c>, the
/// passphrase from <c>--passphrase-env</c>, the opened repository, and the
/// client-local state directory. Writer identity, the gapless sequence file,
/// the catalogue, and spool space are catalogue-domain client state
/// (specification 02 §2) — they live under the state directory, never inside
/// the repository.
/// </summary>
public sealed class CliSession : IDisposable
{
    private StateDirectoryLock? _writerRole;

    private CliSession(
        LocalFileSystemObjectStore store,
        OpenedRepository repository,
        string stateDirectory,
        StateDirectoryLock? writerRole,
        RepositoryReadAuthority? readAuthority)
    {
        Store = store;
        Repository = repository;
        StateDirectory = stateDirectory;
        _writerRole = writerRole;
        ReadAuthority = readAuthority;
    }

    /// <summary>Whether this command took the writer role — i.e. is in direct mode.</summary>
    public bool HoldsWriterRole => _writerRole is not null;

    public LocalFileSystemObjectStore Store { get; }

    public OpenedRepository Repository { get; }

    /// <summary>
    /// The full read authority of a write-only repository (ADR-0042 §5).
    /// Direct mode holds the passphrase, so it holds the whole capability —
    /// derived at open, alive for the command, zeroed with the session. Null
    /// on v1 repositories.
    /// </summary>
    public RepositoryReadAuthority? ReadAuthority { get; }

    public string StateDirectory { get; }

    /// <summary>
    /// The archive's identity in file names. Per-archive client state —
    /// spool, catalogue, sequence — is keyed by repository id so that the
    /// CLI's direct mode and the service name the same files for the same
    /// archive; keying by anything else would give one archive two sequence
    /// spaces under one writer identity, which is the ADR-0028 hazard by the
    /// back door (ADR-0034).
    /// </summary>
    private string RepositoryIdHex => Repository.RepositoryId.ToString();

    public string SpoolDirectory => Path.Combine(StateDirectory, "spool", RepositoryIdHex);

    /// <summary>The disposable catalogue — one of the three separated stores (11 §3), one per archive.</summary>
    public string CataloguePath => Path.Combine(StateDirectory, $"catalogue-{RepositoryIdHex}.db");

    /// <summary>The user-editable configuration — the second store (11 §3).</summary>
    public string ConfigurationPath => Path.Combine(StateDirectory, "config.json");

    /// <summary>Durable local state — the third store: identities and job history.</summary>
    public LocalState State => _state ??= LocalState.LoadOrCreate(StateDirectory);

    private LocalState? _state;

    /// <summary>The repository's current generation — the larger of the bundle's two.</summary>
    public KeyGeneration CurrentGeneration =>
        Repository.CurrentDataGeneration.Value >= Repository.CurrentMetadataGeneration.Value
            ? Repository.CurrentDataGeneration
            : Repository.CurrentMetadataGeneration;

    public static Passphrase ReadPassphrase(string environmentVariable)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariable);
        return string.IsNullOrEmpty(value)
            ? throw new CliFailureException(Strings.FormatCliSession_EnvironmentVariableUnsetEmpty(environmentVariable))
            : Passphrase.Create(value);
    }

    /// <summary>Opens an existing repository (01 §6 steps 1–3) and binds the state directory.</summary>
    /// <param name="repoPath">The repository.</param>
    /// <param name="passphraseEnvironmentVariable">The variable naming the passphrase.</param>
    /// <param name="stateDirectory">The state directory, or null for the default.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <param name="logger">Where what the open noticed is recorded.</param>
    /// <returns>The session; dispose to release what it holds.</returns>
    public static ValueTask<CliSession> OpenAsync(
        string repoPath,
        string passphraseEnvironmentVariable,
        string? stateDirectory,
        CancellationToken cancellationToken,
        ILogger? logger = null) =>
        OpenAsync(repoPath, passphraseEnvironmentVariable, stateDirectory, writerRole: false, cancellationToken, logger);

    /// <summary>
    /// Opens a session, optionally taking the device's writer role for the
    /// duration of the command (ADR-0028 §3).
    /// </summary>
    /// <param name="repoPath">The repository.</param>
    /// <param name="passphraseEnvironmentVariable">The variable naming the passphrase.</param>
    /// <param name="stateDirectory">The state directory, or null for the default.</param>
    /// <param name="writerRole">
    /// Whether this command writes. A command that does must hold the role
    /// exclusively; a command that only reads must not take it, so it can run
    /// alongside a service.
    /// </param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <param name="logger">Where what the open noticed is recorded.</param>
    /// <returns>The session; dispose to release the role.</returns>
    public static async ValueTask<CliSession> OpenAsync(
        string repoPath,
        string passphraseEnvironmentVariable,
        string? stateDirectory,
        bool writerRole,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        var log = logger ?? NullLogger.Instance;

        var store = new LocalFileSystemObjectStore(repoPath);
        using var passphrase = ReadPassphrase(passphraseEnvironmentVariable);

        OpenedRepository repository;
        RepositoryReadAuthority? readAuthority = null;
        try
        {
            // A write-only repository has no key object to unwrap: direct
            // mode derives the full authority from the passphrase and proves
            // it against the descriptor (ADR-0042 §1) — same variable, same
            // commands, different open underneath.
            var descriptor = await RepositoryLifecycle.ReadDescriptorAsync(store, cancellationToken).ConfigureAwait(false);
            if (RepositoryLifecycle.IsWriteOnly(descriptor))
            {
                (repository, readAuthority) = await RepositoryLifecycle.OpenWriteOnlyForReadAsync(
                    store, passphrase, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                repository = await RepositoryLifecycle.OpenAsync(store, passphrase, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (RepositoryOpenException exception)
        {
            throw new CliFailureException(exception.Message, exception);
        }
        catch (KeyUnwrapFailedException exception)
        {
            throw new CliFailureException(exception.Message, exception);
        }

        // Facts about the repository, not results of the command — so they go
        // to the log, which is where somebody diagnosing "why did this stop
        // reading" will look, rather than into the middle of a report.
        if (repository.UnstableFormatWarning)
        {
            Log.UnstableFormat(log, repository.RepositoryId);
        }

        if (repository.KdfBelowCreationMinimums)
        {
            Log.KdfBelowMinimums(log, repository.RepositoryId);
        }

        var state = stateDirectory ?? DefaultStateDirectory(repository.RepositoryId);
        Directory.CreateDirectory(state);
        Directory.CreateDirectory(Path.Combine(state, "spool"));

        StateDirectoryLock? role = null;
        if (writerRole)
        {
            // Direct mode is not a fallback that happens silently (ADR-0028 §3).
            // If a service holds the role, this command is refused naming the
            // holder — never run anyway against state another process owns.
            try
            {
                role = StateDirectoryLock.Acquire(state, StateDirectoryLock.DirectRole);
            }
            catch (ClientStateException exception)
            {
                repository.Dispose();
                readAuthority?.Dispose();
                throw new CliFailureException(exception.Message, exception);
            }
        }

        return new CliSession(store, repository, state, role, readAuthority);
    }

    /// <summary>
    /// Takes the writer role on a session that was opened without it (ADR-0028
    /// §4), or refuses naming the holder.
    /// </summary>
    /// <remarks>
    /// Mode resolution needs the state directory before it can decide anything,
    /// and the default one is derived from the repository id — so the session
    /// has to be open before the question "is a service listening here" can even
    /// be asked. This is how the answer "no" turns into direct mode without
    /// opening the repository a second time.
    /// </remarks>
    /// <exception cref="CliFailureException">Another process holds the role.</exception>
    public void TakeWriterRole()
    {
        if (_writerRole is not null)
        {
            return;
        }

        try
        {
            _writerRole = StateDirectoryLock.Acquire(StateDirectory, StateDirectoryLock.DirectRole);
        }
        catch (ClientStateException exception)
        {
            throw new CliFailureException(exception.Message, exception);
        }
    }

    private static string DefaultStateDirectory(RepositoryId repositoryId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "fallbackplan",
        "state",
        Base32.Encode(repositoryId.ToArray()));

    /// <summary>This client's writer identity — created once, then stable (02 §4).</summary>
    public WriterId Writer => WriterId.FromBytes(State.WriterId);

    /// <summary>This client's device identity for snapshot addressing (01 §2).</summary>
    public byte[] DeviceId => State.DeviceId;

    /// <summary>This client's default backup-set identity.</summary>
    public byte[] BackupSetId => State.DefaultBackupSetId;

    public WriterSequence CreateSequence() =>
        new(new FileSequenceStateStore(Path.Combine(StateDirectory, $"sequence-{RepositoryIdHex}.txt")));

    /// <inheritdoc />
    public void Dispose()
    {
        _writerRole?.Dispose();
        ReadAuthority?.Dispose();
        Repository.Dispose();
    }
}
