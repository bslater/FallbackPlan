using FallbackPlan.Cli;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// A restore cut short is resumed from the command line (FR-RST-008,
/// ADR-0109): <c>restore --resume</c> runs the restore again, leaves each file
/// already holding the snapshot's content as it stands, and lands the rest.
/// It is measured for what it still has to write, so a disk the cut-short run
/// filled most of does not refuse the run that finishes it.
/// </summary>
/// <remarks>
/// In direct mode the run lands in place, where <c>--output</c> points, so
/// there is no run to name. Running again without <c>--resume</c> moves what
/// it finds aside, as <c>CommandTests</c> holds.
/// </remarks>
[TestClass]
public sealed class RestoreResumeCommandTests : IDisposable
{
    private readonly CliHarness _cli = new();

    [TestMethod]
    public async Task Resume_LeavesWhatTheEarlierRunLanded_AndRestoresTheRest()
    {
        var snapshot = await BackUpAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");
        var first = await _cli.RunAsync("restore", snapshot, "--output", destination);
        Assert.AreEqual(0, first.ExitCode, first.All);

        // Cut short before its second file.
        File.Delete(Path.Combine(destination, "nested", "two.txt"));

        var resumed = await _cli.RunAsync("restore", snapshot, "--output", destination, "--resume");

        Assert.AreEqual(0, resumed.ExitCode, resumed.All);
        Assert.Contains("1 file(s) already in place", resumed.All, StringComparison.Ordinal);
        Assert.IsFalse(
            Directory.Exists(Path.Combine(destination, ".fbp-displaced")),
            "what the earlier run landed is the run's own, not something in its way");
        Assert.AreEqual("first", await File.ReadAllTextAsync(Path.Combine(destination, "one.txt")));
        Assert.AreEqual("second", await File.ReadAllTextAsync(Path.Combine(destination, "nested", "two.txt")));
    }

    [TestMethod]
    public async Task Resume_ThatFitsOnlyBecauseOfWhatLanded_IsNotRefused()
    {
        var snapshot = await BackUpAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");
        var first = await _cli.RunAsync("restore", snapshot, "--output", destination);
        Assert.AreEqual(0, first.ExitCode, first.All);

        // Less free than the whole run needs, and more than what is left.
        DirectGateway.AvailableBytesInFlow = _ => 1_000;

        var resumed = await _cli.RunAsync("restore", snapshot, "--output", destination, "--resume");
        Assert.AreEqual(0, resumed.ExitCode, resumed.All);
        Assert.Contains("2 file(s) already in place", resumed.All, StringComparison.Ordinal);

        var again = await _cli.RunAsync("restore", snapshot, "--output", destination);
        Assert.AreNotEqual(0, again.ExitCode, "a run that resumes nothing needs room for every file");
        Assert.Contains("would not fit", again.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Resume_NamingARun_InDirectMode_IsRefusedBeforeAnythingIsAsked()
    {
        // A snapshot nobody holds: had the restore been asked for, that
        // would be the answer.
        await _cli.InitAsync();

        var refused = await _cli.RunAsync(
            "restore", new string('0', 32), "--output", Path.Combine(_cli.WorkPath, "restored"),
            "--resume", "0123456789abcdef");

        Assert.AreNotEqual(0, refused.ExitCode, refused.All);
        Assert.Contains("lands in place", refused.All, StringComparison.Ordinal);
        Assert.DoesNotContain("knows nothing", refused.All, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("--plan")]
    [DataRow("--export")]
    public async Task Resume_WithAPlan_IsRefused_AndRestoresNothing(string planning)
    {
        var snapshot = await BackUpAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");
        string[] arguments = planning == "--export"
            ? ["restore", snapshot, "--output", destination, "--resume", "--export", Path.Combine(_cli.WorkPath, "plan.json")]
            : ["restore", snapshot, "--output", destination, "--resume", "--plan"];

        var refused = await _cli.RunAsync(arguments);

        Assert.AreNotEqual(0, refused.ExitCode, refused.All);
        Assert.Contains("--resume", refused.All, StringComparison.Ordinal);
        Assert.IsFalse(Directory.Exists(destination));
        Assert.IsFalse(File.Exists(Path.Combine(_cli.WorkPath, "plan.json")));
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
