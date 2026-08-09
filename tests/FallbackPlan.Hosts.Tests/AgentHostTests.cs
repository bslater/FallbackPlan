using FallbackPlan.Agent;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The Agent host's command line (ADR-0027): usage, the passphrase-by-name
/// rule, the <c>--once</c> exit-code contract, and the error mapping that
/// must produce a stated reason rather than a stack trace. The pass itself
/// is covered by AgentPassTests; this covers everything around it, which
/// until now nothing could call.
/// </summary>
[TestClass]
public sealed class AgentHostTests : IDisposable
{
    private readonly HostHarness _harness = new();

    private static Task<HostHarness.Invocation> RunAsync(params string[] args) =>
        HostHarness.RunAsync(AgentHost.RunAsync, args);

    [TestMethod]
    [DataRow("--help")]
    [DataRow("-h")]
    [DataRow("help")]
    public async Task Help_explains_the_usage_and_succeeds(string flag)
    {
        var result = await RunAsync(flag);

        Assert.AreEqual(0, result.ExitCode);
        Assert.Contains("--passphrase-env", result.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AgentHost_NoArguments_PrintsHelpRatherThanFailing()
    {
        var result = await RunAsync();

        Assert.AreEqual(0, result.ExitCode);
        Assert.Contains("usage", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    [DataRow("walk")]                                  // not a command
    [DataRow("run")]                                   // no options at all
    [DataRow("run", "--repo", "/tmp/nowhere")]         // missing state and passphrase
    public async Task An_incomplete_command_line_is_refused_with_the_usage(params string[] args)
    {
        var result = await RunAsync(args);

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("usage", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task AgentHost_PassphraseVariableIsUnset_RefusesNamingTheVariable()
    {
        var result = await RunAsync(
            "run",
            "--repo", _harness.RepositoryPath,
            "--state", _harness.StateDirectory,
            "--passphrase-env", "FBP_VARIABLE_THAT_IS_NOT_SET");

        Assert.AreEqual(1, result.ExitCode);

        // The message must name the variable and must not carry the secret.
        Assert.Contains("FBP_VARIABLE_THAT_IS_NOT_SET", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AgentPass_ABackupSetIsDue_RunsItOnceAndReportsIt()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "agent host");
        _harness.WriteConfiguration("every 4h");

        var result = await RunAsync(
            "run",
            "--repo", _harness.RepositoryPath,
            "--state", _harness.StateDirectory,
            "--passphrase-env", _harness.PassphraseVariable,
            "--once");

        Assert.AreEqual(0, result.ExitCode);
        Assert.Contains("docs", result.Output, StringComparison.Ordinal);
        Assert.Contains("ran", result.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AgentPass_ABackupSetIsManualOnly_SkipsItAndStillSucceeds()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "agent host");
        _harness.WriteConfiguration(string.Empty);

        var result = await RunAsync(
            "run",
            "--repo", _harness.RepositoryPath,
            "--state", _harness.StateDirectory,
            "--passphrase-env", _harness.PassphraseVariable,
            "--once");

        // An empty schedule means manual-only: nothing runs, and that is a
        // success, not a failure (ADR-0027 §1).
        Assert.AreEqual(0, result.ExitCode);
        Assert.DoesNotContain("ran", result.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AgentPass_RunTwiceInsideTheInterval_SkipsTheSetItJustRan()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "agent host");
        _harness.WriteConfiguration("every 4h");

        string[] arguments =
        [
            "run",
            "--repo", _harness.RepositoryPath,
            "--state", _harness.StateDirectory,
            "--passphrase-env", _harness.PassphraseVariable,
            "--once",
        ];

        var first = await RunAsync(arguments);
        Assert.AreEqual(0, first.ExitCode);
        Assert.Contains("ran", first.Output, StringComparison.Ordinal);

        // The journal anchors the schedule, so the very next pass owes nothing.
        var second = await RunAsync(arguments);
        Assert.AreEqual(0, second.ExitCode);
        Assert.DoesNotContain("ran", second.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AgentPass_ABackupSetsRootIsMissing_FailsWithoutAStackTrace()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 4h");
        Directory.Delete(_harness.SourceRoot, recursive: true);

        var result = await RunAsync(
            "run",
            "--repo", _harness.RepositoryPath,
            "--state", _harness.StateDirectory,
            "--passphrase-env", _harness.PassphraseVariable,
            "--once");

        // A failed set is exit code 2 — distinct from a usage error (1), so a
        // supervisor can tell "I was invoked wrongly" from "a backup failed".
        Assert.AreEqual(2, result.ExitCode);
        Assert.DoesNotContain("   at ", result.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AgentHost_PassphraseIsWrong_RefusesWithoutAStackTrace()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "agent host");
        _harness.WriteConfiguration("every 4h");

        const string variable = "FBP_HOST_TEST_WRONG_PASSPHRASE";
        Environment.SetEnvironmentVariable(variable, "not the passphrase");
        try
        {
            var result = await RunAsync(
                "run",
                "--repo", _harness.RepositoryPath,
                "--state", _harness.StateDirectory,
                "--passphrase-env", variable,
                "--once");

            Assert.AreEqual(1, result.ExitCode);
            Assert.DoesNotContain("   at ", result.All, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _harness.Dispose();
}
