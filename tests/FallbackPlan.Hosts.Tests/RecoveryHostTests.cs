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

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public async Task Help_explains_the_usage_and_succeeds(string flag)
    {
        var result = await RunAsync(flag);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--kit", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_reports_the_repository_the_kit_belongs_to()
    {
        var kit = await PrepareAsync();

        var result = await RunAsync(KitArguments("open", kit));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("repository", result.Output, StringComparison.Ordinal);
        Assert.Contains("unwrapped", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_transcribable_text_kit_opens_the_repository_too()
    {
        var kit = await PrepareAsync();

        // FR-KIT-003: the text form is what survives a printer and a
        // keyboard, so it must be accepted wherever the binary form is.
        var result = await RunAsync(KitArguments("open", kit + ".txt"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("unwrapped", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshots_lists_what_the_store_holds()
    {
        var kit = await PrepareAsync();

        var result = await RunAsync(KitArguments("snapshots", kit));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("verified", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_writes_the_files_back_from_the_kit_alone()
    {
        var kit = await PrepareAsync();

        var listing = await RunAsync(KitArguments("snapshots", kit));
        var snapshot = listing.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

        var destination = Path.Combine(_harness.WorkPath, "recovered");
        var result = await RunAsync([.. KitArguments("restore", kit), "--snapshot", snapshot, "--output", destination]);

        Assert.True(result.ExitCode == 0, result.All);
        Assert.Equal("recovery drill", File.ReadAllText(Path.Combine(destination, "notes.txt")));
        Assert.Equal(new string('x', 4_000), File.ReadAllText(Path.Combine(destination, "nested", "data.bin")));
    }

    // ------------------------------------------------------------ failures

    [Fact]
    public async Task A_wrong_passphrase_is_refused_with_a_stated_reason()
    {
        var kit = await PrepareAsync();

        const string variable = "FBP_RECOVERY_WRONG_PASSPHRASE";
        Environment.SetEnvironmentVariable(variable, "not the passphrase");
        try
        {
            var result = await RunAsync(
                "open", "--repo", _harness.RepositoryPath, "--kit", kit, "--passphrase-env", variable);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("passphrase", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("   at ", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task An_unset_passphrase_variable_is_refused_by_name()
    {
        var kit = await PrepareAsync();

        var result = await RunAsync(
            "open", "--repo", _harness.RepositoryPath, "--kit", kit,
            "--passphrase-env", "FBP_VARIABLE_THAT_IS_NOT_SET");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("FBP_VARIABLE_THAT_IS_NOT_SET", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_option_is_refused_by_name()
    {
        var result = await RunAsync("open", "--repo", _harness.RepositoryPath);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--kit", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_damaged_kit_is_refused_rather_than_half_read()
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

        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain("   at ", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_kit_file_that_does_not_exist_is_refused_cleanly()
    {
        await _harness.CreateRepositoryAsync();

        var result = await RunAsync(
            "open", "--repo", _harness.RepositoryPath,
            "--kit", Path.Combine(_harness.WorkPath, "absent.bin"),
            "--passphrase-env", _harness.PassphraseVariable);

        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain("   at ", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restoring_an_unknown_snapshot_is_refused()
    {
        var kit = await PrepareAsync();

        var result = await RunAsync(
        [
            .. KitArguments("restore", kit),
            "--snapshot", new string('f', 32),
            "--output", Path.Combine(_harness.WorkPath, "nothing"),
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain("   at ", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_command_is_refused()
    {
        var kit = await PrepareAsync();

        var result = await RunAsync(KitArguments("liberate", kit));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("liberate", result.Error, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public void Dispose() => _harness.Dispose();
}
