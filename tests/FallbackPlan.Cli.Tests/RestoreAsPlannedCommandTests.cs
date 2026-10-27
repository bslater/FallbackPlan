using FallbackPlan.Api;
using FallbackPlan.Cli;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// A restore run as a kept plan says it would run (FR-RST-003, ADR-0114):
/// <c>restore --as-planned &lt;file&gt;</c> runs only the run the file was
/// kept for, and only while planning it again says what the kept plan said —
/// the files, what is missing, what is held archived, the conflicts. A plan
/// kept and approved is then the run that happens, and a replica or a target
/// that moved since is said before anything is written.
/// </summary>
/// <remarks>
/// In direct mode the CLI plans and runs itself, as <c>restore</c> does. A
/// kept plan of a run the command line does not ask — several paths, a
/// target, a policy, a landing in place, which the console asks — is refused
/// by name, as is a file that is not a kept plan.
/// </remarks>
[TestClass]
public sealed class RestoreAsPlannedCommandTests : IDisposable
{
    private readonly CliHarness _cli = new();

    [TestMethod]
    public async Task AsPlanned_TheRunAPlanWasKeptFor_Restores()
    {
        var snapshot = await BackUpAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");
        var file = Path.Combine(_cli.WorkPath, "plan.json");
        Assert.AreEqual(0, (await _cli.RunAsync("restore", snapshot, "--output", destination, "--export", file)).ExitCode);

        var run = await _cli.RunAsync("restore", snapshot, "--output", destination, "--as-planned", file);

        Assert.AreEqual(0, run.ExitCode, run.All);
        Assert.Contains("as kept in", run.All, StringComparison.Ordinal);
        Assert.AreEqual("first", File.ReadAllText(Path.Combine(destination, "one.txt")));
        Assert.AreEqual("second", File.ReadAllText(Path.Combine(destination, "nested", "two.txt")));
    }

    [TestMethod]
    public async Task AsPlanned_WhenThePlanNowSaysOtherwise_RestoresNothing_AndSaysWhatChanged()
    {
        // Approved when every file's content was there; since, it has gone.
        var snapshot = await BackUpAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");
        var file = Path.Combine(_cli.WorkPath, "plan.json");
        Assert.AreEqual(0, (await _cli.RunAsync("restore", snapshot, "--output", destination, "--export", file)).ExitCode);
        Directory.Delete(Path.Combine(_cli.RepositoryPath, "blobs", "data"), recursive: true);

        var run = await _cli.RunAsync("restore", snapshot, "--output", destination, "--as-planned", file);

        Assert.AreNotEqual(0, run.ExitCode, run.All);
        Assert.Contains("no longer as kept", run.All, StringComparison.Ordinal);
        Assert.Contains("missing", run.All, StringComparison.Ordinal);
        Assert.Contains("one.txt", run.All, StringComparison.Ordinal);
        Assert.Contains("nothing was restored", run.All, StringComparison.Ordinal);
        Assert.IsFalse(Directory.Exists(destination), "a plan that changed restores nothing, not even the folder");
    }

    [TestMethod]
    public async Task AsPlanned_ForAnotherRun_IsRefused_AndRestoresNothing()
    {
        var snapshot = await BackUpAsync();
        var file = Path.Combine(_cli.WorkPath, "plan.json");
        Assert.AreEqual(0, (await _cli.RunAsync(
            "restore", snapshot, "--output", Path.Combine(_cli.WorkPath, "approved"), "--export", file)).ExitCode);
        var elsewhere = Path.Combine(_cli.WorkPath, "elsewhere");

        var run = await _cli.RunAsync("restore", snapshot, "nested", "--output", elsewhere, "--as-planned", file);

        Assert.AreNotEqual(0, run.ExitCode, run.All);
        Assert.Contains("another run", run.All, StringComparison.Ordinal);
        Assert.Contains("approved", run.All, StringComparison.Ordinal);
        Assert.IsFalse(Directory.Exists(elsewhere));
    }

    [TestMethod]
    public async Task AsPlanned_ForAnotherPathOfTheSameBackup_IsRefused_AndRestoresNothing()
    {
        // Approved for all of it; asked for one folder of it, to the same place.
        var snapshot = await BackUpAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");
        var file = Path.Combine(_cli.WorkPath, "plan.json");
        Assert.AreEqual(0, (await _cli.RunAsync("restore", snapshot, "--output", destination, "--export", file)).ExitCode);

        var run = await _cli.RunAsync("restore", snapshot, "nested", "--output", destination, "--as-planned", file);

        Assert.AreNotEqual(0, run.ExitCode, run.All);
        Assert.Contains("another run", run.All, StringComparison.Ordinal);
        Assert.Contains("all of it", run.All, StringComparison.Ordinal);
        Assert.IsFalse(Directory.Exists(destination));
    }

    [TestMethod]
    public async Task AsPlanned_APlanOfARunTheCommandLineDoesNotAsk_IsRefused()
    {
        // The console keeps plans of several paths, a target and a policy.
        var snapshot = await BackUpAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");
        var file = Path.Combine(_cli.WorkPath, "console-plan.json");
        await File.WriteAllTextAsync(file, RestorePlanFile.Serialize(
            new PlanRestoreCommand(snapshot, null, Paths: ["one.txt", "nested"], OutputDirectory: destination, Existing: "overwrite"),
            new RestorePlanResult(2, 11, []),
            DateTimeOffset.UtcNow));

        var run = await _cli.RunAsync("restore", snapshot, "--output", destination, "--as-planned", file);

        Assert.AreNotEqual(0, run.ExitCode, run.All);
        Assert.Contains("the command line does not ask", run.All, StringComparison.Ordinal);
        Assert.IsFalse(Directory.Exists(destination));
    }

    [TestMethod]
    public async Task AsPlanned_AFileThatIsNotAKeptPlan_IsRefused_SayingWhy()
    {
        var snapshot = await BackUpAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");
        var file = Path.Combine(_cli.WorkPath, "notes.json");
        await File.WriteAllTextAsync(file, """{"kind":"something-else","version":1}""");

        var run = await _cli.RunAsync("restore", snapshot, "--output", destination, "--as-planned", file);

        Assert.AreNotEqual(0, run.ExitCode, run.All);
        Assert.Contains("not a kept restore plan", run.All, StringComparison.Ordinal);
        Assert.IsFalse(Directory.Exists(destination));
    }

    [TestMethod]
    [DataRow("--plan")]
    [DataRow("--resume")]
    public async Task AsPlanned_BesideAPlanOrAResume_IsRefused(string beside)
    {
        var snapshot = await BackUpAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");
        var file = Path.Combine(_cli.WorkPath, "plan.json");
        Assert.AreEqual(0, (await _cli.RunAsync("restore", snapshot, "--output", destination, "--export", file)).ExitCode);

        var run = await _cli.RunAsync("restore", snapshot, "--output", destination, "--as-planned", file, beside);

        Assert.AreNotEqual(0, run.ExitCode, run.All);
        Assert.Contains("--as-planned", run.All, StringComparison.Ordinal);
        Assert.IsFalse(Directory.Exists(destination));
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
