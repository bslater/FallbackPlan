using FallbackPlan.Agent;
using FallbackPlan.Application;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Recovery;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The standalone recovery tool's command line (architecture 08 §5;
/// FR-KIT-006), driven against a repository built the ordinary way: the kit
/// plus the passphrase open it, list its snapshots, and restore its files —
/// with no catalogue, no state directory and no Agent.
/// </summary>
/// <remarks>
/// This is the last line of defence, so its failure modes matter as much as
/// its success: a wrong passphrase, a damaged kit and an unknown snapshot
/// must each produce a stated reason and a non-zero exit, never a stack
/// trace an operator has to interpret during a disaster.
/// </remarks>
[TestClass]
public sealed class RecoveryHostTests : IDisposable
{
    private readonly HostHarness _harness = new();

    private static Task<HostHarness.Invocation> RunAsync(params string[] args) =>
        HostHarness.RunAsync(RecoveryHost.RunAsync, args);

    private async Task<string> PrepareAsync()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "recovery drill");
        _harness.WriteSourceFile("nested/data.bin", new string('x', 4_000));
        await _harness.BackUpAsync();
        return await _harness.ExportKitAsync();
    }

    private string[] KitArguments(string command, string kit) =>
    [
        command,
        "--repo", _harness.RepositoryPath,
        "--kit", kit,
        "--passphrase-env", _harness.PassphraseVariable,
    ];

    [TestMethod]
    [DataRow("--help")]
    [DataRow("-h")]
    [DataRow("help")]
    public async Task RecoveryHost_EachHelpFlag_PrintsTheUsageAndSucceeds(string flag)
    {
        var result = await RunAsync(flag);

        Assert.AreEqual(0, result.ExitCode);
        Assert.Contains("--kit", result.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Open_ARecoveryKit_ReportsTheRepositoryItBelongsTo()
    {
        var kit = await PrepareAsync();

        var result = await RunAsync(KitArguments("open", kit));

        Assert.AreEqual(0, result.ExitCode);
        Assert.Contains("repository", result.Output, StringComparison.Ordinal);
        Assert.Contains("unwrapped", result.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Open_TheTranscribableTextKit_OpensTheRepositoryToo()
    {
        var kit = await PrepareAsync();

        // FR-KIT-003: the text form is what survives a printer and a
        // keyboard, so it must be accepted wherever the binary form is.
        var result = await RunAsync(KitArguments("open", kit + ".txt"));

        Assert.AreEqual(0, result.ExitCode);
        Assert.Contains("unwrapped", result.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Snapshots_AKitAndItsStore_ListsWhatTheStoreHolds()
    {
        var kit = await PrepareAsync();

        var result = await RunAsync(KitArguments("snapshots", kit));

        Assert.AreEqual(0, result.ExitCode);
        Assert.Contains("verified", result.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Restore_TheKitAlone_WritesTheFilesBack()
    {
        var kit = await PrepareAsync();

        var listing = await RunAsync(KitArguments("snapshots", kit));
        var snapshot = listing.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

        var destination = Path.Combine(_harness.WorkPath, "recovered");
        var result = await RunAsync([.. KitArguments("restore", kit), "--snapshot", snapshot, "--output", destination]);

        Assert.IsTrue(result.ExitCode == 0, result.All);
        Assert.AreEqual("recovery drill", File.ReadAllText(Path.Combine(destination, "notes.txt")));
        Assert.AreEqual(new string('x', 4_000), File.ReadAllText(Path.Combine(destination, "nested", "data.bin")));
    }

    // ------------------------------------------------------------ failures

    [TestMethod]
    public async Task Open_PassphraseIsWrong_RefusesWithAStatedReason()
    {
        var kit = await PrepareAsync();

        const string variable = "FBP_RECOVERY_WRONG_PASSPHRASE";
        Environment.SetEnvironmentVariable(variable, "not the passphrase");
        try
        {
            var result = await RunAsync(
                "open", "--repo", _harness.RepositoryPath, "--kit", kit, "--passphrase-env", variable);

            Assert.AreEqual(1, result.ExitCode);
            Assert.Contains("passphrase", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("   at ", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [TestMethod]
    [DataRow("trace")]
    [DataRow("verbose")]
    [DataRow("debug")]
    [DataRow("info")]
    [DataRow("information")]
    [DataRow("warn")]
    [DataRow("warning")]
    [DataRow("error")]
    [DataRow("critical")]
    [DataRow("fatal")]
    [DataRow("none")]
    [DataRow("off")]
    public async Task RecoveryHost_EachSpellingOfALevel_IsAccepted(string level)
    {
        // The recovery tool carries its own forty-line sink rather than the
        // shared composition (ADR-0043 §1), so its level vocabulary is a
        // second implementation of the same promise. A spelling the service
        // accepts and this tool rejects is a difference nobody would find
        // until the day it matters.
        var kit = await PrepareAsync();

        var result = await RunAsync([.. KitArguments("open", kit), "--log-level", level]);

        Assert.AreEqual(0, result.ExitCode, result.Error);
    }

    [TestMethod]
    public async Task RecoveryHost_AnUnknownLevel_IsRefusedNamingTheOnesThatExist()
    {
        var result = await RunAsync("open", "--log-level", "chatty");

        // Refused before anything is opened, and refused with the list — a
        // tool used once every few years cannot assume the operator remembers
        // the vocabulary.
        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("chatty", result.Error, StringComparison.Ordinal);
        Assert.Contains("--log-level", result.Error, StringComparison.Ordinal);
        Assert.Contains("warning", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RecoveryHost_AtInformation_WritesItsProgressToStandardError()
    {
        var kit = await PrepareAsync();

        var result = await RunAsync([.. KitArguments("open", kit), "--log-level", "information"]);

        Assert.AreEqual(0, result.ExitCode, result.Error);

        // Event ids, not prose: 3100 is the kit read and 3101 the keys
        // derived, and those are what an operator quotes back down a phone.
        Assert.Contains("3100", result.Error, StringComparison.Ordinal);
        Assert.Contains("3101", result.Error, StringComparison.Ordinal);

        // Diagnostics never contaminate the answer. `open` prints a report
        // somebody may be reading or redirecting.
        Assert.DoesNotContain("3100", result.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RecoveryHost_AtTheDefaultLevel_KeepsInformationToItself()
    {
        var kit = await PrepareAsync();

        // Warning is the floor when nobody asks (ADR-0043 §6): the tool's own
        // report is its output, and a wall of Information on top of it during
        // a recovery is noise at the worst moment.
        var result = await RunAsync(KitArguments("open", kit));

        Assert.AreEqual(0, result.ExitCode, result.Error);
        Assert.DoesNotContain("3100", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RecoveryHost_AtNone_SaysNothingEvenWhenSomethingIsWrong()
    {
        var kit = await PrepareAsync();
        var variable = "FBP_RECOVERY_WRONG_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "not the passphrase at all");

        try
        {
            var result = await RunAsync(
                "open",
                "--repo", _harness.RepositoryPath,
                "--kit", kit,
                "--passphrase-env", variable,
                "--log-level", "none");

            // The refusal itself still reaches the operator — that is the
            // tool's answer, not its log. What `none` silences is 3102.
            Assert.AreEqual(1, result.ExitCode);
            Assert.DoesNotContain("3102", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [TestMethod]
    public async Task RecoveryHost_AWrongPassphraseAtWarning_RecordsTheRefusal()
    {
        var kit = await PrepareAsync();
        var variable = "FBP_RECOVERY_WRONG_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "not the passphrase at all");

        try
        {
            // 3102 is a Warning, so it clears the default floor without
            // anybody asking — which is the point of it being a Warning.
            var result = await RunAsync(
                "open",
                "--repo", _harness.RepositoryPath,
                "--kit", kit,
                "--passphrase-env", variable);

            Assert.AreEqual(1, result.ExitCode);
            Assert.Contains("3102", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [TestMethod]
    public async Task RecoveryHost_PassphraseVariableIsUnset_RefusesNamingTheVariable()
    {
        var kit = await PrepareAsync();

        var result = await RunAsync(
            "open", "--repo", _harness.RepositoryPath, "--kit", kit,
            "--passphrase-env", "FBP_VARIABLE_THAT_IS_NOT_SET");

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("FBP_VARIABLE_THAT_IS_NOT_SET", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RecoveryHost_ARequiredOptionIsMissing_RefusesNamingIt()
    {
        var result = await RunAsync("open", "--repo", _harness.RepositoryPath);

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("--kit", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Open_TheKitIsDamaged_RefusesRatherThanReadingItHalfway()
    {
        var kit = await PrepareAsync();

        // Flip a byte in the middle of the transcribed text: the kit's own
        // checksums must catch it (FR-KIT-003).
        var text = File.ReadAllText(kit + ".txt").ToCharArray();
        var index = Array.FindIndex(text, text.Length / 2, character => char.IsAsciiLetterLower(character));
        text[index] = text[index] == 'a' ? 'b' : 'a';
        var damaged = Path.Combine(_harness.WorkPath, "damaged.txt");
        File.WriteAllText(damaged, new string(text));

        var result = await RunAsync(
            "open", "--repo", _harness.RepositoryPath, "--kit", damaged,
            "--passphrase-env", _harness.PassphraseVariable);

        Assert.AreEqual(1, result.ExitCode);
        Assert.DoesNotContain("   at ", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Open_TheKitFileDoesNotExist_RefusesWithAMessage()
    {
        await _harness.CreateRepositoryAsync();

        var result = await RunAsync(
            "open", "--repo", _harness.RepositoryPath,
            "--kit", Path.Combine(_harness.WorkPath, "absent.bin"),
            "--passphrase-env", _harness.PassphraseVariable);

        Assert.AreEqual(1, result.ExitCode);
        Assert.DoesNotContain("   at ", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Restore_SnapshotIsUnknown_RefusesWithAMessage()
    {
        var kit = await PrepareAsync();

        var result = await RunAsync(
        [
            .. KitArguments("restore", kit),
            "--snapshot", new string('f', 32),
            "--output", Path.Combine(_harness.WorkPath, "nothing"),
        ]);

        Assert.AreEqual(1, result.ExitCode);
        Assert.DoesNotContain("   at ", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RecoveryHost_VerbIsUnknown_RefusesWithNonZeroExit()
    {
        var kit = await PrepareAsync();

        var result = await RunAsync(KitArguments("liberate", kit));

        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("liberate", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Restore_ADirectShipSetAfterTheMachineIsGone_ComesBackFromTheDestinationAlone()
    {
        // The shape a set created today actually has (ADR-0046): content
        // ships straight to the destinations and the agent's state holds
        // metadata only. So deleting the state directory is not a stand-in
        // for losing the machine — for this set it IS the loss, and the
        // destination is the only complete copy left in the world.
        //
        // NFR-OPS-005 and FR-KIT-006 are about exactly that morning. The
        // earlier proof of them ran against a staging archive in the
        // archives root, which a direct-ship installation never writes, so
        // this is the first time the claim is held to the default shape.
        var passphrase = "The one long Passphrase 42 of this installation!";
        var password = "The-0wner-passw0rd";
        var passwordVariable = "FBP_RECOVERY_OWNER_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(_harness.PassphraseVariable, passphrase);
        Environment.SetEnvironmentVariable(passwordVariable, password);

        var vault = Path.Combine(_harness.WorkPath, "vault");
        var kit = Path.Combine(_harness.WorkPath, "installation-kit.bin");
        Directory.CreateDirectory(vault);

        try
        {
            var setup = await HostHarness.RunAsync(
                AgentHost.RunAsync,
                "setup", "--archives", _harness.ArchivesRoot, "--state", _harness.StateDirectory,
                "--passphrase-env", _harness.PassphraseVariable, "--acknowledge-loss",
                "--kit-output", kit, "--user", "ben", "--password-env", passwordVariable);
            Assert.AreEqual(0, setup.ExitCode, setup.Error);

            _harness.WriteSourceFile("docs/notes.txt", "the words worth keeping");
            _harness.WriteSourceFile("docs/big.bin", new string('r', 300_000));
            WriteDirectShipConfiguration(vault);

            await using (var runtime = await StartAsync(passphrase))
            {
                var set = runtime.Configuration.BackupSets.Single();
                var outcome = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true)
                    .WaitAsync(TestContext.CancellationTokenSource.Token);
                Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
            }

            var replica = Assert.ContainsSingle(Directory.GetDirectories(vault));

            // The machine is gone. Everything the installation knew — the
            // metadata store, the catalogue, the credential — goes with it.
            Directory.Delete(_harness.StateDirectory, recursive: true);
            Directory.Delete(_harness.SourceRoot, recursive: true);
            Assert.IsTrue(
                !Directory.Exists(_harness.ArchivesRoot)
                    || Directory.GetFiles(_harness.ArchivesRoot, "*", SearchOption.AllDirectories).Length == 0,
                "a direct-ship installation stages nothing, so the archives root cannot be a fallback");

            // The trailing separator is what a shell's tab-completion gives a
            // person typing this path, and it used to make every key resolve
            // "outside the store root" — found by the recovery drill.
            var listed = await RunAsync(
                "snapshots", "--repo", replica + Path.DirectorySeparatorChar, "--kit", kit,
                "--passphrase-env", _harness.PassphraseVariable);
            Assert.AreEqual(0, listed.ExitCode, listed.Error);
            Assert.DoesNotContain("SIGNATURE-FAILED", listed.Output, StringComparison.Ordinal);

            var snapshot = listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Split(' ')[0];
            var output = Path.Combine(_harness.WorkPath, "recovered");
            var restored = await RunAsync(
                "restore", "--repo", replica, "--kit", kit,
                "--passphrase-env", _harness.PassphraseVariable,
                "--snapshot", snapshot, "--output", output);

            Assert.AreEqual(0, restored.ExitCode, restored.Error);
            Assert.Contains(", 0 failed,", restored.Output, StringComparison.Ordinal);

            var notes = Assert.ContainsSingle(
                Directory.GetFiles(output, "notes.txt", SearchOption.AllDirectories));
            Assert.AreEqual("the words worth keeping", await File.ReadAllTextAsync(notes));
            var big = Assert.ContainsSingle(
                Directory.GetFiles(output, "big.bin", SearchOption.AllDirectories));
            Assert.AreEqual(300_000, new FileInfo(big).Length);
        }
        finally
        {
            Environment.SetEnvironmentVariable(passwordVariable, null);
        }
    }

    /// <summary>One direct-ship set against one local vault, written straight to config.</summary>
    /// <param name="vault">Where the destination's replicas land.</param>
    private void WriteDirectShipConfiguration(string vault) =>
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('1', 32), Name = "vault",
                    Kind = DestinationKind.LocalPath, Path = vault,
                },
            ],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = _harness.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                    Schedule = "every 1h",
                    Destinations = [new SetDestinationReference { Ref = "vault" }],
                    DirectShip = true,
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));

    private async Task<ServiceRuntime> StartAsync(string passphraseText)
    {
        using var passphrase = Passphrase.Create(passphraseText);
        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                // The fixture's paths share one real volume; the vault is
                // told apart by name, which is the compliant shape ADR-0051
                // describes.
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
            },
            passphrase,
            TestContext.CancellationTokenSource.Token);
    }

    /// <summary>MSTest's per-test context, for its cancellation token.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <inheritdoc />
    public void Dispose() => _harness.Dispose();
}
