using FallbackPlan.Agent;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The Agent host's command line (ADR-0027): usage, the passphrase-by-name
/// rule, the <c>--once</c> exit-code contract, and the error mapping that
/// must produce a stated reason rather than a stack trace. The pass itself
/// is covered by AgentPassTests; this covers everything around it, which
/// until now nothing could call.
/// </summary>
public sealed class AgentHostTests : IDisposable
{
    private readonly HostHarness _harness = new();

    private static Task<HostHarness.Invocation> RunAsync(params string[] args) =>
        HostHarness.RunAsync(AgentHost.RunAsync, args);

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public async Task Help_explains_the_usage_and_succeeds(string flag)
    {
        var result = await RunAsync(flag);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--passphrase-env", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_arguments_prints_help_rather_than_failing()
    {
        var result = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("usage", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("walk")]                                  // not a command
    [InlineData("run")]                                   // no options at all
    [InlineData("run", "--repo", "/tmp/nowhere")]         // missing state and passphrase
    public async Task An_incomplete_command_line_is_refused_with_the_usage(params string[] args)
    {
        var result = await RunAsync(args);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("usage", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unset_passphrase_variable_is_refused_by_name()
    {
        var result = await RunAsync(
            "run",
            "--repo", _harness.RepositoryPath,
            "--state", _harness.StateDirectory,
            "--passphrase-env", "FBP_VARIABLE_THAT_IS_NOT_SET");

        Assert.Equal(1, result.ExitCode);

        // The message must name the variable and must not carry the secret.
        Assert.Contains("FBP_VARIABLE_THAT_IS_NOT_SET", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_due_set_runs_once_and_reports_it()
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

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("docs", result.Output, StringComparison.Ordinal);
        Assert.Contains("ran", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_manual_only_set_is_skipped_and_still_succeeds()
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
        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("ran", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_pass_skips_the_set_it_just_ran()
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
        Assert.Equal(0, first.ExitCode);
        Assert.Contains("ran", first.Output, StringComparison.Ordinal);

        // The journal anchors the schedule, so the very next pass owes nothing.
        var second = await RunAsync(arguments);
        Assert.Equal(0, second.ExitCode);
        Assert.DoesNotContain("ran", second.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_set_whose_root_is_missing_fails_the_pass_without_a_stack_trace()
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
        Assert.Equal(2, result.ExitCode);
        Assert.DoesNotContain("   at ", result.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_passphrase_is_refused_without_a_stack_trace()
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

            Assert.Equal(1, result.ExitCode);
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
