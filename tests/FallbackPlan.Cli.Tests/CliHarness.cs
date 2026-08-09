using System.CommandLine;
using System.Text;
using FallbackPlan.Cli;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// Drives the CLI the way a user does — a real argument list, a real
/// repository on disk, a real exit code — but in process, so the commands
/// are covered by tests rather than only by hand.
/// </summary>
/// <remarks>
/// Output is captured through <see cref="InvocationConfiguration"/> rather
/// than by redirecting <see cref="Console"/>, so these tests are safe to run
/// in parallel with everything else.
/// </remarks>
public sealed class CliHarness : IDisposable
{
    private const string PassphraseVariable = "FBP_TEST_PASSPHRASE";

    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "fbp-cli-tests", Guid.NewGuid().ToString("n"));

    public CliHarness()
    {
        Directory.CreateDirectory(_scratch);
        Environment.SetEnvironmentVariable(PassphraseVariable, "correct horse battery staple");
    }

    /// <summary>The repository store root the commands operate on.</summary>
    public string RepositoryPath => Path.Combine(_scratch, "repo");

    /// <summary>The client-local state directory (writer identity, catalogue, spool).</summary>
    public string StatePath => Path.Combine(_scratch, "state");

    /// <summary>A scratch directory for sources and restore targets.</summary>
    public string WorkPath => Path.Combine(_scratch, "work");

    /// <summary>The result of one command invocation.</summary>
    public sealed record Invocation(int ExitCode, string Output, string Error)
    {
        /// <summary>Both streams, for assertions that do not care which one carried the text.</summary>
        public string All => Output + Error;
    }

    /// <summary>Runs one command with the repository, passphrase and state arguments already supplied.</summary>
    public Task<Invocation> RunAsync(params string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return RunRawAsync(
        [
            .. args,
            "--repo", RepositoryPath,
            "--passphrase-env", PassphraseVariable,
            "--state", StatePath,
        ]);
    }

    /// <summary>Runs one command with exactly the arguments given.</summary>
    public static async Task<Invocation> RunRawAsync(params string[] args)
    {
        var output = new StringWriter(new StringBuilder(), System.Globalization.CultureInfo.InvariantCulture);
        var error = new StringWriter(new StringBuilder(), System.Globalization.CultureInfo.InvariantCulture);

        var exitCode = await CliApplication.RunAsync(
            args, new InvocationConfiguration { Output = output, Error = error, EnableDefaultExceptionHandler = false });

        return new Invocation(exitCode, output.ToString(), error.ToString());
    }

    /// <summary>Runs a command that takes no --state option (init).</summary>
    public Task<Invocation> RunWithoutStateAsync(params string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return RunRawAsync([.. args, "--repo", RepositoryPath, "--passphrase-env", PassphraseVariable]);
    }

    /// <summary>Creates a repository, asserting that it succeeded.</summary>
    public async Task<Invocation> InitAsync()
    {
        var result = await RunWithoutStateAsync("init");
        Assert.IsTrue(result.ExitCode == 0, $"init failed: {result.All}");
        return result;
    }

    /// <summary>Writes a file under <see cref="WorkPath"/> and returns its full path.</summary>
    public string WriteFile(string relativePath, string content = "contents")
    {
        var full = Path.Combine(WorkPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
