using System.Text;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Sets a real installation up with the agent's own <c>setup</c> verb, then
/// drives the Agent and Recovery hosts against it in process. A recovery
/// drill only means something against an installation that was set up the
/// ordinary way, so the fixture goes through the same commands a user would
/// run, and the "docs" set's archive is created the way the service creates
/// one: from the installation credential setup stored.
/// </summary>
public sealed class HostHarness : IDisposable
{
    /// <summary>
    /// The environment variable carrying this harness's passphrase. It is
    /// unique per instance on purpose: xUnit runs test classes in parallel,
    /// and a shared variable means one class's teardown clears it while
    /// another is mid-run — which is exactly how this first failed.
    /// </summary>
    public string PassphraseVariable { get; } = "FBP_HOST_TEST_" + Guid.NewGuid().ToString("N");

    /// <summary>The variable naming the first account's password, for the setup verb (FR-USR-006).</summary>
    public string PasswordVariable { get; } = "FBP_HOST_TEST_PW_" + Guid.NewGuid().ToString("N");

    /// <summary>The owner account the setup verb creates (FR-USR-001).</summary>
    public const string OwnerUser = "ben";

    /// <summary>The owner account's password.</summary>
    public const string OwnerPassword = "The-0wner-passw0rd";

    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "fbp-host-tests", Guid.NewGuid().ToString("n"));

    private bool _setUp;

    public HostHarness()
    {
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(SourceRoot);
        // Strong enough for the setup verb's own gate (ADR-0044 §6), which
        // is the gate a real installation's passphrase passes.
        Environment.SetEnvironmentVariable(PassphraseVariable, "The hosts-tests Passphrase 42 of this installation!");
        Environment.SetEnvironmentVariable(PasswordVariable, OwnerPassword);
    }

    /// <summary>The "docs" set's 32-hex identity, matching <see cref="WriteConfiguration"/>.</summary>
    public string DocsSetId { get; } = new string('a', 32);

    /// <summary>The root holding a staging archive per staging-mode set (ADR-0034); a direct-ship set stages nothing here.</summary>
    public string ArchivesRoot => Path.Combine(_scratch, "archives");

    /// <summary>
    /// The "docs" set's staging archive — the path CLI direct-mode verbs and
    /// recovery assertions aim at. The harness's "docs" set stages
    /// (ADR-0034), so this is the archive the service opens for it; a
    /// direct-ship fixture keeps its store under <c>state/sets</c> instead.
    /// </summary>
    public string RepositoryPath => Path.Combine(ArchivesRoot, DocsSetId);

    /// <summary>The client-local state directory (config, jobs, catalogue, spool).</summary>
    public string StateDirectory => Path.Combine(_scratch, "state");

    /// <summary>The directory a backup set points at.</summary>
    public string SourceRoot => Path.Combine(_scratch, "source");

    /// <summary>A scratch directory for kits and restore targets.</summary>
    public string WorkPath => Path.Combine(_scratch, "work");

    /// <summary>The result of one host invocation.</summary>
    public sealed record Invocation(int ExitCode, string Output, string Error)
    {
        /// <summary>Both streams, for assertions that do not care which carried the text.</summary>
        public string All => Output + Error;
    }

    /// <summary>Runs a host entry point with captured output.</summary>
    public static async Task<Invocation> RunAsync(
        Func<string[], TextWriter, TextWriter, CancellationToken, Task<int>> host,
        params string[] args)
    {
        ArgumentNullException.ThrowIfNull(host);

        var output = new StringWriter(new StringBuilder(), System.Globalization.CultureInfo.InvariantCulture);
        var error = new StringWriter(new StringBuilder(), System.Globalization.CultureInfo.InvariantCulture);

        var exitCode = await host(args, output, error, CancellationToken.None);

        return new Invocation(exitCode, output.ToString(), error.ToString());
    }

    /// <summary>
    /// First-run setup through the agent's own verb, as a headless operator
    /// would run it (ADR-0044): the passphrase becomes the installation's
    /// credential and the first account is created. Once per harness; a second call is a no-op,
    /// because the verb itself refuses a second run.
    /// </summary>
    public async Task SetupAsync()
    {
        if (_setUp)
        {
            return;
        }

        Directory.CreateDirectory(WorkPath);
        var result = await RunAsync(
            AgentHost.RunAsync,
            "setup", "--archives", ArchivesRoot, "--state", StateDirectory,
            "--passphrase-env", PassphraseVariable, "--acknowledge-loss",
            "--user", OwnerUser, "--password-env", PasswordVariable);
        Assert.AreEqual(0, result.ExitCode, $"setup failed: {result.All}");
        _setUp = true;
    }

    /// <summary>
    /// Sets the installation up and creates the "docs" set's staging archive
    /// the way the service creates one on a set's first backup: write-only,
    /// from the installation credential, under the installation's salt. The
    /// CLI's direct-mode verbs then open it with the same passphrase.
    /// </summary>
    public async Task CreateRepositoryAsync()
    {
        await SetupAsync();

        if (File.Exists(Path.Combine(RepositoryPath, RepositoryLifecycle.DescriptorKey.Value)))
        {
            return;
        }

        using var provisioning = new InstallationCredentialStore(StateDirectory).TryLoad();
        Assert.IsNotNull(provisioning, "setup stored no installation credential");

        Directory.CreateDirectory(RepositoryPath);
        (await RepositoryLifecycle.CreateAsync(
            new LocalFileSystemObjectStore(RepositoryPath), provisioning.Credential,
            provisioning.KdfSalt.ToArray(), provisioning.KdfParameters,
            createdBy: Environment.MachineName,
            (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), CancellationToken.None)).Dispose();
    }

    /// <summary>
    /// The restore grant a console sends when it opens a restore source on a
    /// set-up installation (ADR-0042 §5): the sealing scalar, re-derived from
    /// the passphrase under the installation's salt and sealed to the
    /// service's recipient key, as hex. <paramref name="execute"/> is the
    /// service's command surface, however the test reaches it.
    /// </summary>
    public async Task<string> RestoreGrantAsync(
        Func<ServiceCommand, CancellationToken, ValueTask<ServiceResult>> execute, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execute);

        Assert.IsInstanceOfType<ServiceDescriptionResult>(
            await execute(new DescribeServiceCommand(), cancellationToken), out var description);

        using var provisioning = new InstallationCredentialStore(StateDirectory).TryLoad();
        Assert.IsNotNull(provisioning, "the installation is not set up");

        using var passphrase = Passphrase.Create(Environment.GetEnvironmentVariable(PassphraseVariable)!);
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, provisioning.KdfParameters, provisioning.KdfSalt, KdfValidationMode.OpenRepository);
        return Convert.ToHexStringLower(
            WriteOnlyProvisioning.SealGrant(
                Convert.FromHexString(description.RestoreGrantRecipient!), authority.SealingPrivateKey));
    }

    /// <summary>
    /// Opens a restore source under a restore grant, the way the console
    /// does before any restore on a set-up installation: the service holds no
    /// content key of its own (ADR-0042 §7), so a run with no source opened
    /// this way reads every record as sealed.
    /// </summary>
    public async Task<RestoreSourceOpenedResult> OpenGrantedSourceAsync(
        Func<ServiceCommand, CancellationToken, ValueTask<ServiceResult>> execute,
        string setName, string? destinationName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execute);

        var opened = await execute(
            new OpenRestoreSourceCommand(
                setName, destinationName, Envelope: await RestoreGrantAsync(execute, cancellationToken)),
            cancellationToken);
        Assert.IsInstanceOfType<RestoreSourceOpenedResult>(
            opened, out var source, (opened as ServiceError)?.Message ?? opened.GetType().Name);
        return source;
    }

    /// <summary>
    /// The reclaim grant a console sends with <c>retention --apply</c> on a
    /// set-up installation (ADR-0055 §6): the reclaim sub-root, re-derived
    /// from the passphrase under the installation's salt and sealed to the
    /// service <paramref name="handler"/> fronts, as hex.
    /// </summary>
    public async Task<string> ReclaimGrantAsync(ServiceCommandHandler handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);

        Assert.IsInstanceOfType<ServiceDescriptionResult>(
            await handler.ExecuteAsync(new DescribeServiceCommand(), cancellationToken), out var description);

        using var provisioning = new InstallationCredentialStore(StateDirectory).TryLoad();
        Assert.IsNotNull(provisioning, "the installation is not set up");

        using var passphrase = Passphrase.Create(Environment.GetEnvironmentVariable(PassphraseVariable)!);
        using var authority = WriteOnlyDerivation.Derive(
            passphrase, provisioning.KdfParameters, provisioning.KdfSalt, KdfValidationMode.OpenRepository);
        return Convert.ToHexStringLower(
            WriteOnlyProvisioning.SealReclaimGrant(
                Convert.FromHexString(description.RestoreGrantRecipient!), authority.ReclaimKeySeed));
    }

    /// <summary>Backs the source tree up through the CLI, so the store holds a real snapshot.</summary>
    public async Task BackUpAsync()
    {
        var exitCode = await Cli.CliApplication.RunAsync(
        [
            "backup", SourceRoot,
            "--repo", RepositoryPath, "--passphrase-env", PassphraseVariable, "--state", StateDirectory,
        ]);
        Assert.AreEqual(0, exitCode);
    }

    /// <summary>Writes a source file the backup set will capture.</summary>
    public string WriteSourceFile(string relativePath, string content)
    {
        var full = Path.Combine(SourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>Writes a configuration with one backup set on the given schedule.</summary>
    public void WriteConfiguration(string schedule) => new ClientConfiguration
    {
        SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
        Destinations =
        [
            new DestinationConfiguration
            {
                Id = new string('d', 32),
                Name = "vault",
                Kind = DestinationKind.LocalPath,
                Path = Path.Combine(StateDirectory, "vault"),
            },
        ],
        BackupSets =
        [
            new BackupSetConfiguration
            {
                Id = new string('a', 32),
                Name = "docs",
                Roots = [new BackupRootConfiguration { Path = SourceRoot }],
                Schedule = schedule,
                Destinations = [new SetDestinationReference { Ref = "vault" }],
            },
        ],
    }.Save(Path.Combine(StateDirectory, "config.json"));

    /// <summary>
    /// Adds (or replaces) a configured backup set by editing the file
    /// directly — fixture setup for tests that need a set to simply exist,
    /// WITHOUT the upsert verb's queued first backup (ADR-0047). The runtime
    /// re-reads the file per access, so the set is visible immediately.
    /// </summary>
    public void AddConfiguredSet(
        string id, string name, string destination, string? schedule = null,
        IReadOnlyList<string>? excludeRules = null)
    {
        var path = Path.Combine(StateDirectory, "config.json");
        var configuration = ClientConfiguration.Load(path);
        (configuration with
        {
            BackupSets =
            [
                .. configuration.BackupSets.Where(set => !string.Equals(set.Id, id, StringComparison.Ordinal)),
                new BackupSetConfiguration
                {
                    Id = id,
                    Name = name,
                    Roots = [new BackupRootConfiguration { Path = SourceRoot }],
                    Schedule = schedule,
                    ExcludeRules = excludeRules ?? [],
                    Destinations = [new SetDestinationReference { Ref = destination }],
                },
            ],
        }).Save(path);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Environment.SetEnvironmentVariable(PassphraseVariable, null);
        Environment.SetEnvironmentVariable(PasswordVariable, null);
        if (Directory.Exists(_scratch))
        {
            try
            {
                Directory.Delete(_scratch, recursive: true);
            }
            catch (IOException)
            {
                // A scratch directory that outlives the test is noise, not a failure.
            }
        }
    }
}
