using System.Text;
using FallbackPlan.Application;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// Builds a real repository with the CLI, then drives the Agent and Recovery
/// hosts against it in process. A recovery drill only means something
/// against a repository that was created the ordinary way, so the setup goes
/// through the same commands a user would run.
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

    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "fbp-host-tests", Guid.NewGuid().ToString("n"));

    public HostHarness()
    {
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(SourceRoot);
        Environment.SetEnvironmentVariable(PassphraseVariable, "hosts-tests-passphrase!!");
    }

    /// <summary>The repository store root.</summary>
    public string RepositoryPath => Path.Combine(_scratch, "repo");

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

    /// <summary>Creates the repository through the CLI, as a user would.</summary>
    public async Task CreateRepositoryAsync()
    {
        var exitCode = await Cli.CliApplication.RunAsync(
            ["init", "--repo", RepositoryPath, "--passphrase-env", PassphraseVariable]);
        Assert.AreEqual(0, exitCode);
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

    /// <summary>Exports a recovery kit through the CLI and returns its path.</summary>
    public async Task<string> ExportKitAsync()
    {
        Directory.CreateDirectory(WorkPath);
        var kit = Path.Combine(WorkPath, "kit.bin");

        var exitCode = await Cli.CliApplication.RunAsync(
        [
            "key-export", "--output", kit,
            "--repo", RepositoryPath, "--passphrase-env", PassphraseVariable, "--state", StateDirectory,
        ]);
        Assert.AreEqual(0, exitCode);
        return kit;
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
                Root = SourceRoot,
                Schedule = schedule,
                Destinations = [new SetDestinationReference { Ref = "vault" }],
            },
        ],
    }.Save(Path.Combine(StateDirectory, "config.json"));

    /// <inheritdoc />
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Environment.SetEnvironmentVariable(PassphraseVariable, null);
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
