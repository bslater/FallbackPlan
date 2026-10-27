using System.Text.Json;
using FallbackPlan.Api;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// <c>restore</c> asks a run as the console asks one (FR-RST-001, ADR-0117):
/// several paths of a snapshot, what to do about a file in the way, a landing
/// in place, and the original location. In direct mode the CLI restores in
/// place, as it always has, so it takes several paths and a policy; the
/// original location it restores from the set's configured roots, which
/// <see cref="RestoreOriginalDirectTests"/> holds (ADR-0119). A plan, and a
/// plan kept, is of the run asked, so the file keeps what the console's does.
/// </summary>
[TestClass]
public sealed class RestoreShapeCommandTests : IDisposable
{
    private readonly CliHarness _cli = new();

    [TestMethod]
    public async Task SeveralPaths_RestoreEach_AndNothingElse()
    {
        var snapshot = await BackUpAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");

        var run = await _cli.RunAsync("restore", snapshot, "one.txt", "nested", "--output", destination);

        Assert.AreEqual(0, run.ExitCode, run.All);
        Assert.AreEqual("first", File.ReadAllText(Path.Combine(destination, "one.txt")));
        Assert.AreEqual("second", File.ReadAllText(Path.Combine(destination, "nested", "two.txt")));
        Assert.IsFalse(Directory.Exists(Path.Combine(destination, "other")), "a path not asked is not restored");
    }

    [TestMethod]
    public async Task UnderRename_AFileInTheWayStays_AndTheRestoredCopyIsWrittenBesideIt()
    {
        var snapshot = await BackUpAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "one.txt"), "mine");

        var run = await _cli.RunAsync("restore", snapshot, "one.txt", "--output", destination, "--existing", "rename");

        Assert.AreEqual(0, run.ExitCode, run.All);
        Assert.AreEqual("mine", File.ReadAllText(Path.Combine(destination, "one.txt")));
        Assert.AreEqual("first", File.ReadAllText(Assert.ContainsSingle(Directory.GetFiles(destination, "one (restored *).txt"))));
        Assert.IsFalse(Directory.Exists(Path.Combine(destination, ".fbp-displaced")), "rename moves nothing aside");
    }

    [TestMethod]
    public async Task UnderOverwrite_AFileInTheWayIsReplaced()
    {
        var snapshot = await BackUpAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "one.txt"), "mine");

        var run = await _cli.RunAsync("restore", snapshot, "one.txt", "--output", destination, "--existing", "overwrite");

        Assert.AreEqual(0, run.ExitCode, run.All);
        Assert.AreEqual("first", File.ReadAllText(Path.Combine(destination, "one.txt")));
        Assert.IsEmpty(Directory.GetFiles(destination, "one (restored *).txt"));
        Assert.IsFalse(Directory.Exists(Path.Combine(destination, ".fbp-displaced")), "overwrite keeps nothing aside");
    }

    [TestMethod]
    public async Task TheRoomARunNeeds_IsMeasuredUnderThePolicyAsked()
    {
        // An overwrite frees each file it replaces once its copy has landed,
        // and a move aside keeps every one, so beside two files in the way
        // only the overwrite fits on this disk. The disk is the flow's own.
        var snapshot = await BackUpAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");
        Directory.CreateDirectory(Path.Combine(destination, "nested"));
        await File.WriteAllTextAsync(Path.Combine(destination, "one.txt"), "mine");
        await File.WriteAllTextAsync(Path.Combine(destination, "nested", "two.txt"), "mine too");
        DirectGateway.AvailableBytesInFlow = _ => 10_000;
        string[] run = ["restore", snapshot, "one.txt", "nested", "--output", destination];

        var movingAside = await _cli.RunAsync([.. run, "--plan"]);
        var overwritingPlan = await _cli.RunAsync([.. run, "--existing", "overwrite", "--plan"]);
        var refused = await _cli.RunAsync(run);
        var overwritten = await _cli.RunAsync([.. run, "--existing", "overwrite"]);

        Assert.AreNotEqual(0, movingAside.ExitCode, movingAside.All);
        Assert.Contains("would not fit", movingAside.All, StringComparison.Ordinal);
        Assert.AreEqual(0, overwritingPlan.ExitCode, overwritingPlan.All);
        Assert.AreNotEqual(0, refused.ExitCode, refused.All);
        Assert.Contains("would not fit", refused.All, StringComparison.Ordinal);
        Assert.AreEqual(0, overwritten.ExitCode, overwritten.All);
        Assert.AreEqual("first", File.ReadAllText(Path.Combine(destination, "one.txt")));
    }

    [TestMethod]
    public async Task InPlace_InDirectMode_LandsWhereOutputPoints_AsEveryDirectRunDoes()
    {
        var snapshot = await BackUpAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");

        var run = await _cli.RunAsync("restore", snapshot, "one.txt", "--output", destination, "--in-place");

        Assert.AreEqual(0, run.ExitCode, run.All);
        Assert.AreEqual("first", File.ReadAllText(Path.Combine(destination, "one.txt")));
    }

    [TestMethod]
    public async Task WithoutAnOutput_ARestoreToAFolder_IsRefused()
    {
        var snapshot = await BackUpAsync();

        var run = await _cli.RunAsync("restore", snapshot, "one.txt");

        Assert.AreNotEqual(0, run.ExitCode, run.All);
        Assert.Contains("--output", run.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task TheOriginalLocation_BesideAnOutput_IsRefused()
    {
        var snapshot = await BackUpAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");

        var run = await _cli.RunAsync("restore", snapshot, "one.txt", "--target", "original", "--output", destination);

        Assert.AreNotEqual(0, run.ExitCode, run.All);
        Assert.Contains("takes no --output", run.All, StringComparison.Ordinal);
        Assert.IsFalse(Directory.Exists(destination));
    }

    [TestMethod]
    [DataRow("--existing", "keep")]
    [DataRow("--target", "elsewhere")]
    public async Task AChoiceThatIsNotOne_IsRefused(string option, string value)
    {
        var snapshot = await BackUpAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");

        var run = await _cli.RunAsync("restore", snapshot, "--output", destination, option, value);

        Assert.AreNotEqual(0, run.ExitCode, run.All);
        Assert.IsFalse(Directory.Exists(destination));
    }

    [TestMethod]
    public async Task ThePlan_IsOfThePathsAsked()
    {
        var snapshot = await BackUpAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");

        var plan = await _cli.RunAsync("restore", snapshot, "one.txt", "nested", "--output", destination, "--plan");

        Assert.AreEqual(0, plan.ExitCode, plan.All);
        Assert.Contains("plan: 2 file(s)", plan.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task APlanKept_IsOfTheRunAsked_AsTheConsoleKeepsOne()
    {
        var snapshot = await BackUpAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");
        var file = Path.Combine(_cli.WorkPath, "plan.json");

        var kept = await _cli.RunAsync(
            "restore", snapshot, "one.txt", "nested", "--output", destination, "--existing", "rename", "--in-place",
            "--export", file);

        Assert.AreEqual(0, kept.ExitCode, kept.All);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        var root = document.RootElement;
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("path").ValueKind, "several paths are kept as paths");
        Assert.AreEqual("one.txt", root.GetProperty("paths")[0].GetString());
        Assert.AreEqual("nested", root.GetProperty("paths")[1].GetString());
        Assert.AreEqual("rename", root.GetProperty("existing").GetString());
        Assert.IsTrue(root.GetProperty("in_place").GetBoolean());
    }

    [TestMethod]
    public async Task APlanKeptOfOnePath_KeepsItAsVersionOneDid()
    {
        // A plan of one path reads back the same to an older reader.
        var snapshot = await BackUpAsync();
        var file = Path.Combine(_cli.WorkPath, "plan.json");

        var kept = await _cli.RunAsync(
            "restore", snapshot, "nested", "--output", Path.Combine(_cli.WorkPath, "restored"), "--export", file);

        Assert.AreEqual(0, kept.ExitCode, kept.All);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        Assert.AreEqual("nested", document.RootElement.GetProperty("path").GetString());
        Assert.AreEqual(JsonValueKind.Null, document.RootElement.GetProperty("paths").ValueKind);
    }

    public void Dispose() => _cli.Dispose();

    private async Task<string> BackUpAsync()
    {
        await _cli.InitAsync();
        _cli.WriteFile("tree/one.txt", "first");
        _cli.WriteFile("tree/nested/two.txt", "second");
        _cli.WriteFile("tree/other/three.txt", "third");

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
