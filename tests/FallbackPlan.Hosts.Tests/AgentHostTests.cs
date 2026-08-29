using FallbackPlan.Agent;
using FallbackPlan.Application;

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
    public async Task AgentHost_EachHelpFlag_PrintsTheUsageAndSucceeds(string flag)
    {
        var result = await RunAsync(flag);

        Assert.AreEqual(0, result.ExitCode);
        Assert.Contains("--passphrase-env", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The level the host will run at, proven by what the rolling file holds
    /// after a one-pass run: the binding-up record is Information, so it is
    /// present at debug and absent at error.
    /// </summary>
    private async Task<bool> LoggedAtInformationAsync(params string[] extra)
    {
        var logs = Path.Combine(_harness.StateDirectory, "logs");
        if (Directory.Exists(logs))
        {
            Directory.Delete(logs, recursive: true);
        }

        string[] args =
            ["run", "--archives", _harness.ArchivesRoot, "--state", _harness.StateDirectory, "--once"];
        // 0 is a clean pass and 2 is "a set failed" — this harness has no
        // repository, so the set fails and that is fine. 1 is the one code
        // that would mean the host never got as far as logging, which is
        // exactly what these tests would otherwise mistake for a level.
        var result = await RunAsync([.. args, .. extra]);
        Assert.AreNotEqual(1, result.ExitCode, result.Error);

        var file = Path.Combine(logs, "fallbackplan-current.log");
        for (var attempt = 0; attempt < 100 && !File.Exists(file); attempt++)
        {
            await Task.Delay(10);
        }

        return File.Exists(file);
    }

    [TestMethod]
    public async Task AgentHost_AtStart_RecordsTheEffectiveConfigurationItOperatesAgainst()
    {
        // FR-SVC-010's startup record: what the service RESOLVED, not what
        // was typed — the directories with their provenance, the operating
        // posture, and each set — so a diagnostic log alone can reconstruct
        // what the service was operating against.
        _harness.WriteConfiguration("every 1h");
        var logs = Path.Combine(_harness.StateDirectory, "logs");

        var result = await RunAsync(
            "run", "--archives", _harness.ArchivesRoot, "--state", _harness.StateDirectory, "--once");
        Assert.AreNotEqual(1, result.ExitCode, result.Error);

        var file = Path.Combine(logs, "fallbackplan-current.log");
        for (var attempt = 0; attempt < 100 && !File.Exists(file); attempt++)
        {
            await Task.Delay(10);
        }

        var log = await File.ReadAllTextAsync(file);
        Assert.Contains(_harness.StateDirectory, log, StringComparison.Ordinal);
        Assert.Contains(_harness.ArchivesRoot, log, StringComparison.Ordinal);
        Assert.Contains("named by flag", log, StringComparison.Ordinal);
        Assert.Contains("backup pool", log, StringComparison.Ordinal);
        Assert.Contains("docs", log, StringComparison.Ordinal);
        Assert.Contains("every 1h", log, StringComparison.Ordinal);
        Assert.Contains("vault", log, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AgentHost_ALogLevelNobodyRecognises_IsRefusedNamingTheOnesThatExist()
    {
        // Refused rather than ignored: silently falling back to Information
        // after a typo is how somebody spends an afternoon wondering why
        // --log-level changed nothing.
        var result = await RunAsync(
            "run", "--archives", _harness.ArchivesRoot, "--state", _harness.StateDirectory,
            "--once", "--log-level", "dbeug");

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("dbeug", result.Error, StringComparison.Ordinal);
        Assert.Contains("information", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AgentHost_TheConfiguredLevel_IsInForceWithNoFlagAndNoVariable()
    {
        // The third source (ADR-0043 §6): the one that survives a restart,
        // which is what an installed service needs — nobody is there to pass
        // it a flag.
        _harness.WriteConfiguration("every 1h");
        var path = Path.Combine(_harness.StateDirectory, "config.json");
        var configuration = ClientConfiguration.Load(path) with
        {
            Logging = new LoggingConfiguration { Level = "error" },
        };
        configuration.Save(path);

        Assert.IsFalse(
            await LoggedAtInformationAsync(),
            "config.json asked for error; an Information record must not reach the file.");
    }

    [TestMethod]
    public async Task AgentHost_TheFlag_OutranksTheConfiguredLevel()
    {
        _harness.WriteConfiguration("every 1h");
        var path = Path.Combine(_harness.StateDirectory, "config.json");
        (ClientConfiguration.Load(path) with { Logging = new LoggingConfiguration { Level = "error" } })
            .Save(path);

        Assert.IsTrue(
            await LoggedAtInformationAsync("--log-level", "debug"),
            "The flag names the level for this invocation and outranks the file.");
    }

    [TestMethod]
    public async Task AgentHost_AConfigurationThatWillNotLoad_StillStartsLogging()
    {
        // The chicken and egg: a file the service is about to refuse must not
        // also stop it logging, because the refusal is one of the first things
        // worth having in the log.
        await File.WriteAllTextAsync(
            Path.Combine(_harness.StateDirectory, "config.json"), "{ this is not json");

        var result = await RunAsync(
            "run", "--archives", _harness.ArchivesRoot, "--state", _harness.StateDirectory, "--once");

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("config.json", result.Error, StringComparison.Ordinal);
    }

    // No-arguments behavior moved: a bare invocation now STARTS the service
    // on the default locations (FR-SVC-016) rather than printing help —
    // AgentDefaultLocationsTests owns that pin. The help flags above stay the
    // way to ask for usage. Path-less invocations stopped being incomplete
    // for the same reason, so the one refusal left to pin is a verb that
    // does not exist.
    [TestMethod]
    public async Task AgentHost_AVerbNobodyRecognises_RefusesWithTheUsage()
    {
        var result = await RunAsync("walk");

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("usage", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task AgentHost_PassphraseVariableIsUnset_RefusesNamingTheVariable()
    {
        var result = await RunAsync(
            "run",
            "--archives", _harness.ArchivesRoot,
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
            "--archives", _harness.ArchivesRoot,
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
            "--archives", _harness.ArchivesRoot,
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
            "--archives", _harness.ArchivesRoot,
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
            "--archives", _harness.ArchivesRoot,
            "--state", _harness.StateDirectory,
            "--passphrase-env", _harness.PassphraseVariable,
            "--once");

        // A failed set is exit code 2 — distinct from a usage error (1), so a
        // supervisor can tell "I was invoked wrongly" from "a backup failed".
        Assert.AreEqual(2, result.ExitCode);
        Assert.DoesNotContain("   at ", result.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AgentHost_PassphraseIsWrong_FailsTheSetWithoutAStackTrace()
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
                "--archives", _harness.ArchivesRoot,
                "--state", _harness.StateDirectory,
                "--passphrase-env", variable,
                "--once");

            // Archives open lazily, one per set on first use (ADR-0034), so a
            // wrong passphrase surfaces where it is discovered: the set whose
            // existing archive refused to unwrap fails permanently — exit 2, a
            // failed set — rather than the whole invocation being refused up
            // front. Still no stack trace: it is an operator message, not a
            // crash.
            Assert.AreEqual(2, result.ExitCode);
            Assert.DoesNotContain("   at ", result.All, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [TestMethod]
    public async Task AgentHost_TheOldRepoFlag_IsRefusedNamingTheRename()
    {
        var result = await RunAsync(
            "run",
            "--repo", _harness.RepositoryPath,
            "--state", _harness.StateDirectory,
            "--passphrase-env", _harness.PassphraseVariable,
            "--once");

        // Pre-1.0 breaks are sanctioned but never silent (ADR-0034): the old
        // flag gets directions, not a guess at what the caller meant.
        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("--archives", result.Error, StringComparison.Ordinal);
        Assert.Contains("ADR-0034", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A log directory that cannot be opened is said once, on standard error,
    /// and the verb runs anyway (ADR-0043 §6).
    /// </summary>
    /// <remarks>
    /// Standard output is the stream that carries a verb's answer — a unit
    /// file, a report, a list somebody is piping — so a diagnostics warning
    /// belongs nowhere near it. The state directory here is unopenable by
    /// construction rather than by permission: its parent is an ordinary file,
    /// which no user can descend into.
    /// </remarks>
    [TestMethod]
    public async Task AgentHost_WhenTheLogDirectoryCannotBeOpened_WarnsOnErrorAndCarriesOn()
    {
        var occupied = Path.Combine(Path.GetTempPath(), "fbp-agent-host-" + Guid.NewGuid().ToString("n"));
        await File.WriteAllTextAsync(occupied, "a file has no children");

        try
        {
            var result = await RunAsync("retention", "--state", Path.Combine(occupied, "state"));

            Assert.Contains("diagnostics are not being written", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("diagnostics are not being written", result.Output, StringComparison.Ordinal);

            // It got past composing logging and reached the verb, which then
            // refuses the unopenable state directory with a stated reason —
            // the half that used to be an access-denied stack trace. (Before
            // paths gained defaults, an incomplete command line masked this
            // with a usage error instead.)
            Assert.AreEqual(1, result.ExitCode);
            Assert.Contains("error:", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("   at ", result.All, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(occupied);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _harness.Dispose();
}
