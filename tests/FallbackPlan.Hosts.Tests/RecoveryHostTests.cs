using FallbackPlan.Recovery;

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

    /// <inheritdoc />
    public void Dispose() => _harness.Dispose();
}
