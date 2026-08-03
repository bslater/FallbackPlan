using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Index;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Cli;

/// <summary>
/// A command's failure with a message for the operator — distinct from a bug,
/// which is allowed to escape with a stack trace.
/// </summary>
internal sealed class CliFailureException : Exception
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
internal sealed class CliSession : IDisposable
{
    private CliSession(LocalFileSystemObjectStore store, OpenedRepository repository, string stateDirectory)
    {
        Store = store;
        Repository = repository;
        StateDirectory = stateDirectory;
    }

    public LocalFileSystemObjectStore Store { get; }

    public OpenedRepository Repository { get; }

    public string StateDirectory { get; }

    public string SpoolDirectory => Path.Combine(StateDirectory, "spool");

    public string CataloguePath => Path.Combine(StateDirectory, "catalogue.db");

    /// <summary>The repository's current generation — the larger of the bundle's two.</summary>
    public KeyGeneration CurrentGeneration =>
        Repository.CurrentDataGeneration.Value >= Repository.CurrentMetadataGeneration.Value
            ? Repository.CurrentDataGeneration
            : Repository.CurrentMetadataGeneration;

    public static Passphrase ReadPassphrase(string environmentVariable)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariable);
        return string.IsNullOrEmpty(value)
            ? throw new CliFailureException(
                $"Environment variable '{environmentVariable}' is unset or empty — the passphrase is passed by name, never on the command line.")
            : Passphrase.Create(value);
    }

    /// <summary>Opens an existing repository (01 §6 steps 1–3) and binds the state directory.</summary>
    public static async ValueTask<CliSession> OpenAsync(
        string repoPath, string passphraseEnvironmentVariable, string? stateDirectory, CancellationToken cancellationToken)
    {
        var store = new LocalFileSystemObjectStore(repoPath);
        using var passphrase = ReadPassphrase(passphraseEnvironmentVariable);

        OpenedRepository repository;
        try
        {
            repository = await RepositoryLifecycle.OpenAsync(store, passphrase, cancellationToken).ConfigureAwait(false);
        }
        catch (RepositoryOpenException exception)
        {
            throw new CliFailureException(exception.Message, exception);
        }
        catch (KeyUnwrapFailedException exception)
        {
            throw new CliFailureException(exception.Message, exception);
        }

        if (repository.UnstableFormatWarning)
        {
            Console.Error.WriteLine(
                "warning: this repository was written under an UNSTABLE format version — it may become unreadable by future releases (specification 01 §3.2).");
        }

        if (repository.KdfBelowCreationMinimums)
        {
            Console.Error.WriteLine(
                "warning: the repository's stored KDF parameters fall below current creation minimums (specification 03 §2).");
        }

        var state = stateDirectory ?? DefaultStateDirectory(repository.RepositoryId);
        Directory.CreateDirectory(state);
        Directory.CreateDirectory(Path.Combine(state, "spool"));

        return new CliSession(store, repository, state);
    }

    private static string DefaultStateDirectory(RepositoryId repositoryId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "fallbackplan",
        "state",
        Base32.Encode(repositoryId.ToArray()));

    /// <summary>This client's writer identity — created once, then stable (02 §4).</summary>
    public WriterId Writer => WriterId.FromBytes(LoadOrCreateIdentity("writer-id"));

    /// <summary>This client's device identity for snapshot addressing (01 §2).</summary>
    public byte[] DeviceId => LoadOrCreateIdentity("device-id");

    /// <summary>This client's default backup-set identity.</summary>
    public byte[] BackupSetId => LoadOrCreateIdentity("backup-set-id");

    public WriterSequence CreateSequence() =>
        new(new FileSequenceStateStore(Path.Combine(StateDirectory, "sequence.txt")));

    private byte[] LoadOrCreateIdentity(string name)
    {
        var path = Path.Combine(StateDirectory, name);
        if (File.Exists(path))
        {
            var text = File.ReadAllText(path).Trim();
            return Convert.FromHexString(text);
        }

        var bytes = RandomNumberGenerator.GetBytes(16);
        File.WriteAllText(path, Convert.ToHexString(bytes).ToLowerInvariant());
        return bytes;
    }

    /// <inheritdoc />
    public void Dispose() => Repository.Dispose();
}
