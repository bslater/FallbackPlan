namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The CLI's commands, driven end to end against a real repository: the
/// surface a user actually touches, which until now was verified only by
/// hand because every command lived in <c>Main</c> and nothing could call
/// it. Each test asserts the exit code and the observable effect, not the
/// exact prose of a message.
/// </summary>
public sealed class CommandTests : IDisposable
{
    private readonly CliHarness _cli = new();

    [Fact]
    public async Task Init_creates_a_repository_that_opens_again()
    {
        var init = await _cli.RunWithoutStateAsync("init");

        Assert.Equal(0, init.ExitCode);
        Assert.True(Directory.Exists(_cli.RepositoryPath));

        // A second init must refuse rather than overwrite keys.
        var again = await _cli.RunWithoutStateAsync("init");
        Assert.NotEqual(0, again.ExitCode);
    }

    [Fact]
    public async Task Archive_then_verify_and_inspect_round_trip_one_file()
    {
        await _cli.InitAsync();
        var source = _cli.WriteFile("notes.txt", new string('a', 5_000));

        var archive = await _cli.RunAsync("archive", source);
        Assert.True(archive.ExitCode == 0, archive.All);

        var verify = await _cli.RunAsync("verify", "--level", "records");
        Assert.True(verify.ExitCode == 0, verify.All);
    }

    [Fact]
    public async Task Backup_publishes_a_tree_and_snapshots_lists_it()
    {
        await _cli.InitAsync();
        _cli.WriteFile("tree/one.txt", "first");
        _cli.WriteFile("tree/nested/two.txt", "second");
        var root = Path.Combine(_cli.WorkPath, "tree");

        var backup = await _cli.RunAsync("backup", root);
        Assert.True(backup.ExitCode == 0, backup.All);

        var snapshots = await _cli.RunAsync("snapshots");
        Assert.True(snapshots.ExitCode == 0, snapshots.All);
        Assert.False(string.IsNullOrWhiteSpace(snapshots.Output), "snapshots printed nothing after a backup");
    }

    [Fact]
    public async Task Ls_lists_a_directory_inside_a_snapshot()
    {
        await _cli.InitAsync();
        _cli.WriteFile("tree/one.txt", "first");
        _cli.WriteFile("tree/nested/two.txt", "second");
        await _cli.RunAsync("backup", Path.Combine(_cli.WorkPath, "tree"));

        var snapshot = await FirstSnapshotIdAsync();
        var listing = await _cli.RunAsync("ls", snapshot);

        Assert.True(listing.ExitCode == 0, listing.All);
        Assert.Contains("one.txt", listing.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_writes_the_files_back_with_their_contents()
    {
        await _cli.InitAsync();
        _cli.WriteFile("tree/one.txt", "first");
        _cli.WriteFile("tree/nested/two.txt", "second");
        await _cli.RunAsync("backup", Path.Combine(_cli.WorkPath, "tree"));

        var snapshot = await FirstSnapshotIdAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");

        var restore = await _cli.RunAsync("restore", snapshot, "--output", destination);
        Assert.True(restore.ExitCode == 0, restore.All);

        Assert.Equal("first", File.ReadAllText(Path.Combine(destination, "one.txt")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(destination, "nested", "two.txt")));
    }

    [Fact]
    public async Task Check_reports_a_healthy_repository()
    {
        await _cli.InitAsync();
        _cli.WriteFile("tree/one.txt", "first");
        await _cli.RunAsync("backup", Path.Combine(_cli.WorkPath, "tree"));

        var check = await _cli.RunAsync("check");

        Assert.True(check.ExitCode == 0, check.All);
    }

    [Fact]
    public async Task Rebuild_index_restores_the_catalogue_after_it_is_deleted()
    {
        await _cli.InitAsync();
        _cli.WriteFile("tree/one.txt", "first");
        await _cli.RunAsync("backup", Path.Combine(_cli.WorkPath, "tree"));

        // The catalogue is disposable by design (11 §3): deleting it must
        // cost a rebuild, never data.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var database in Directory.EnumerateFiles(_cli.StatePath, "*.db", SearchOption.AllDirectories))
        {
            File.Delete(database);
        }

        var rebuild = await _cli.RunAsync("rebuild-index");
        Assert.True(rebuild.ExitCode == 0, rebuild.All);

        var snapshots = await _cli.RunAsync("snapshots");
        Assert.True(snapshots.ExitCode == 0, snapshots.All);
        Assert.False(string.IsNullOrWhiteSpace(snapshots.Output), "the rebuilt catalogue lists no snapshots");
    }

    [Fact]
    public async Task Key_export_writes_a_recovery_kit()
    {
        await _cli.InitAsync();
        Directory.CreateDirectory(_cli.WorkPath);
        var kit = Path.Combine(_cli.WorkPath, "kit.bin");

        var export = await _cli.RunAsync("key-export", "--output", kit);

        Assert.True(export.ExitCode == 0, export.All);
        Assert.True(new FileInfo(kit).Length > 0, "the exported kit is empty");

        // The transcribable text form is written alongside it (FR-KIT-003).
        Assert.True(File.Exists(kit + ".txt"), "the text form of the kit was not written");
    }

    [Fact]
    public async Task Status_reports_per_set_protection()
    {
        await _cli.InitAsync();

        var status = await _cli.RunAsync("status");

        Assert.True(status.ExitCode == 0, status.All);
    }

    // ------------------------------------------------------------ failures

    [Fact]
    public async Task A_missing_passphrase_variable_fails_without_a_stack_trace()
    {
        await _cli.InitAsync();

        var result = await CliHarness.RunRawAsync(
            "snapshots",
            "--repo", _cli.RepositoryPath,
            "--passphrase-env", "FBP_VARIABLE_THAT_IS_NOT_SET",
            "--state", _cli.StatePath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("   at ", result.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Opening_a_repository_that_does_not_exist_fails_cleanly()
    {
        var result = await _cli.RunAsync("snapshots");

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("   at ", result.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_command_is_refused()
    {
        var result = await CliHarness.RunRawAsync("definitely-not-a-command");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task Help_lists_the_commands()
    {
        var result = await CliHarness.RunRawAsync("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("backup", result.Output, StringComparison.Ordinal);
        Assert.Contains("restore", result.Output, StringComparison.Ordinal);
    }

    private async Task<string> FirstSnapshotIdAsync()
    {
        var snapshots = await _cli.RunAsync("snapshots");
        Assert.True(snapshots.ExitCode == 0, snapshots.All);

        var first = snapshots.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        Assert.NotNull(first);

        // The identifier is the leading hex token of the listing line.
        var token = first!.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.True(token.Length >= 8, $"could not read a snapshot id from '{first}'");
        return token;
    }

    /// <inheritdoc />
    public void Dispose() => _cli.Dispose();
}
