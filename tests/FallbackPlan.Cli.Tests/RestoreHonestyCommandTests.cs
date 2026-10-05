namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The restore verb tells the truth about the disk it writes to and the
/// metadata it will not apply (FR-RST-003, FR-RST-004; ADR-0083). In direct
/// mode the CLI runs the restore itself, so it is the CLI that refuses a run
/// that will not fit before it writes anything, and the CLI that says what
/// was not applied once it ends.
/// </summary>
/// <remarks>
/// The disk is the flow's own: <c>DirectGateway.AvailableBytesInFlow</c>
/// answers for every directory this test's restore asks about, and no other
/// test's.
/// </remarks>
[TestClass]
public sealed class RestoreHonestyCommandTests : IDisposable
{
    private readonly CliHarness _cli = new();

    [TestMethod]
    public async Task Restore_ThatWillNotFit_IsRefused_AndWritesNothing()
    {
        var snapshot = await BackUpAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");

        DirectGateway.AvailableBytesInFlow = _ => 1_000;
        var restore = await _cli.RunAsync("restore", snapshot, "--output", destination);

        Assert.AreNotEqual(0, restore.ExitCode, restore.All);
        Assert.Contains(destination, restore.All, StringComparison.Ordinal);
        Assert.Contains("1000", restore.All, StringComparison.Ordinal);
        Assert.Contains("--ignore-free-space", restore.All, StringComparison.Ordinal);
        Assert.IsFalse(Directory.Exists(destination), "a refused restore must not even create its folder");
    }

    [TestMethod]
    public async Task Restore_ToldToIgnoreFreeSpace_RestoresAnyway()
    {
        var snapshot = await BackUpAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");

        DirectGateway.AvailableBytesInFlow = _ => 1_000;
        var restore = await _cli.RunAsync("restore", snapshot, "--output", destination, "--ignore-free-space");

        Assert.IsTrue(restore.ExitCode == 0, restore.All);
        Assert.AreEqual("first", await File.ReadAllTextAsync(Path.Combine(destination, "one.txt")));
    }

    [TestMethod]
    public async Task Restore_SaysWhichCapturedMetadataItDidNotApply()
    {
        var snapshot = await BackUpAsync();

        var restore = await _cli.RunAsync("restore", snapshot, "--output", Path.Combine(_cli.WorkPath, "restored"));

        // An attribute every file here carries and no target writes back yet:
        // its owner on a POSIX host, its attribute bits on Windows. Access
        // times are written back everywhere now. Metadata alone does not fail
        // the restore.
        Assert.IsTrue(restore.ExitCode == 0, restore.All);
        var stillNotApplied = OperatingSystem.IsWindows() ? "file_attributes" : "owner";
        Assert.Contains($"{stillNotApplied} not applied", restore.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("accessed_at not applied", restore.Output, StringComparison.Ordinal);
    }

    public void Dispose() => _cli.Dispose();

    private async Task<string> BackUpAsync()
    {
        await _cli.InitAsync();
        _cli.WriteFile("tree/one.txt", "first");
        _cli.WriteFile("tree/nested/two.txt", "second");
        var backup = await _cli.RunAsync("backup", Path.Combine(_cli.WorkPath, "tree"));
        Assert.IsTrue(backup.ExitCode == 0, backup.All);

        var snapshots = await _cli.RunAsync("snapshots");
        Assert.IsTrue(snapshots.ExitCode == 0, snapshots.All);

        // The identifier is the leading hex token of the listing line.
        var first = snapshots.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        Assert.IsNotNull(first);
        return first.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }
}
