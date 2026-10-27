using System.Text.Json;
using FallbackPlan.Application;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// In direct mode, <c>restore --target original</c> puts each file back where
/// it was captured (FR-RST-001, ADR-0119). The snapshot's set, as this
/// state's configuration has it, says its roots, as the same file says them
/// to the service. One root takes the whole plan, and several each take their
/// own label's files. The run lands in place, under the policy asked, as
/// every direct run does. A snapshot of no set configured here, or a label
/// the set no longer carries, is refused before anything is written. The
/// plan and the run measure the room at every root together, and a plan kept
/// for the original location runs as kept. A run of a set's snapshot names
/// the set in its receipt, wherever it lands (FR-RST-004, ADR-0126).
/// </summary>
[TestClass]
public sealed class RestoreOriginalDirectTests : IDisposable
{
    private static readonly string SetId = new('d', 32);

    private readonly CliHarness _cli = new();

    public void Dispose() => _cli.Dispose();

    [TestMethod]
    public async Task OneRoot_AFileGoesBackWhereItWasCaptured_AndAPathNotAskedIsLeft()
    {
        var root = Path.Combine(_cli.WorkPath, "docs");
        var one = _cli.WriteFile("docs/one.txt", "first");
        var two = _cli.WriteFile("docs/nested/two.txt", "second");
        var snapshot = await BackUpAsync((root, "docs"));
        await File.WriteAllTextAsync(one, "edited");
        await File.WriteAllTextAsync(two, "edited too");

        var run = await _cli.RunAsync("restore", snapshot, "one.txt", "--target", "original", "--existing", "overwrite");

        Assert.AreEqual(0, run.ExitCode, run.All);
        Assert.AreEqual("first", await File.ReadAllTextAsync(one));
        Assert.AreEqual("edited too", await File.ReadAllTextAsync(two), "a path not asked is left as it stands");
        Assert.Contains($"to {root}", run.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task SeveralRoots_EachLabelsFilesGoBackIntoTheirOwnRoot()
    {
        var docs = Path.Combine(_cli.WorkPath, "docs");
        var media = Path.Combine(_cli.WorkPath, "media");
        var one = _cli.WriteFile("docs/one.txt", "first");
        var clip = _cli.WriteFile("media/clip.txt", "clip");
        var snapshot = await BackUpAsync((docs, "docs"), (media, "media"));
        await File.WriteAllTextAsync(one, "edited");
        await File.WriteAllTextAsync(clip, "edited clip");

        var run = await _cli.RunAsync(
            "restore", snapshot, "docs/one.txt", "media/clip.txt", "--target", "original", "--existing", "overwrite");

        Assert.AreEqual(0, run.ExitCode, run.All);
        Assert.AreEqual("first", await File.ReadAllTextAsync(one));
        Assert.AreEqual("clip", await File.ReadAllTextAsync(clip));
        Assert.IsFalse(Directory.Exists(Path.Combine(docs, "docs")), "a root's files go back without its label");
        Assert.IsFalse(Directory.Exists(Path.Combine(docs, "media")), "a label's files go back into its own root");
        Assert.Contains("2 file(s)", run.Output, StringComparison.Ordinal);
        Assert.Contains($"to {docs}; {media}", run.Output, StringComparison.Ordinal);

        // One receipt for the run, each path under the label it was asked by.
        var kept = await File.ReadAllTextAsync(Assert.ContainsSingle(Directory.GetFiles(Path.Combine(_cli.StatePath, "receipts"), "*.json")));
        Assert.Contains("\"docs/one.txt\"", kept, StringComparison.Ordinal);
        Assert.Contains("\"media/clip.txt\"", kept, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(kept);
        Assert.AreEqual("docs", document.RootElement.GetProperty("set").GetString(), "the merged receipt names the set");
        Assert.AreEqual(SetId, document.RootElement.GetProperty("set_id").GetString(), "and records which it is");
    }

    [TestMethod]
    public async Task ARestoreOfASetsSnapshotToAFolder_NamesTheSetInItsReceipt()
    {
        var root = Path.Combine(_cli.WorkPath, "docs");
        _cli.WriteFile("docs/one.txt", "first");
        var snapshot = await BackUpAsync((root, "docs"));

        var run = await _cli.RunAsync("restore", snapshot, "--output", Path.Combine(_cli.WorkPath, "restored"));

        Assert.AreEqual(0, run.ExitCode, run.All);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            Assert.ContainsSingle(Directory.GetFiles(Path.Combine(_cli.StatePath, "receipts"), "*.json"))));
        Assert.AreEqual("docs", document.RootElement.GetProperty("set").GetString());
        Assert.AreEqual(SetId, document.RootElement.GetProperty("set_id").GetString());
    }

    [TestMethod]
    public async Task AFileInTheWay_IsMovedAside_WhereNoPolicyIsAsked()
    {
        var root = Path.Combine(_cli.WorkPath, "docs");
        var one = _cli.WriteFile("docs/one.txt", "first");
        var snapshot = await BackUpAsync((root, "docs"));
        await File.WriteAllTextAsync(one, "edited");

        var run = await _cli.RunAsync("restore", snapshot, "one.txt", "--target", "original");

        Assert.AreEqual(0, run.ExitCode, run.All);
        Assert.AreEqual("first", await File.ReadAllTextAsync(one));
        var aside = Assert.ContainsSingle(Directory.GetFiles(Path.Combine(root, ".fbp-displaced"), "one.txt", SearchOption.AllDirectories));
        Assert.AreEqual("edited", await File.ReadAllTextAsync(aside), "the file in the way is kept");
    }

    [TestMethod]
    public async Task ASnapshotOfNoSetConfiguredHere_IsRefused_AndNothingIsWritten()
    {
        // A set is configured over the very folder, but the snapshot is of
        // the folder named on the command line, which is no set's.
        var root = Path.Combine(_cli.WorkPath, "docs");
        var one = _cli.WriteFile("docs/one.txt", "first");
        await _cli.InitAsync();
        Configure((root, "docs"));
        var backup = await _cli.RunAsync("backup", root);
        Assert.AreEqual(0, backup.ExitCode, backup.All);
        var snapshot = await LatestSnapshotAsync();
        await File.WriteAllTextAsync(one, "edited");

        var run = await _cli.RunAsync("restore", snapshot, "one.txt", "--target", "original", "--existing", "overwrite");

        Assert.AreNotEqual(0, run.ExitCode, run.All);
        Assert.Contains("not configured", run.All, StringComparison.Ordinal);
        Assert.Contains("--output", run.All, StringComparison.Ordinal);
        Assert.AreEqual("edited", await File.ReadAllTextAsync(one));
    }

    [TestMethod]
    public async Task ALabelTheSetNoLongerCarries_IsRefusedByName_AndNothingIsWritten()
    {
        var docs = Path.Combine(_cli.WorkPath, "docs");
        var one = _cli.WriteFile("docs/one.txt", "first");
        _cli.WriteFile("media/clip.txt", "clip");
        var snapshot = await BackUpAsync((docs, "docs"), (Path.Combine(_cli.WorkPath, "media"), "media"));
        Configure((docs, "docs"), (Path.Combine(_cli.WorkPath, "archive"), "archive"));
        await File.WriteAllTextAsync(one, "edited");

        var run = await _cli.RunAsync(
            "restore", snapshot, "docs/one.txt", "media/clip.txt", "--target", "original", "--existing", "overwrite");

        Assert.AreNotEqual(0, run.ExitCode, run.All);
        Assert.Contains("'media'", run.All, StringComparison.Ordinal);
        Assert.AreEqual("edited", await File.ReadAllTextAsync(one), "the run is refused whole");
    }

    [TestMethod]
    public async Task ThePlan_IsOfTheFilesAsked_AtTheirRoots()
    {
        var docs = Path.Combine(_cli.WorkPath, "docs");
        _cli.WriteFile("docs/one.txt", "first");
        _cli.WriteFile("media/clip.txt", "clip");
        var snapshot = await BackUpAsync((docs, "docs"), (Path.Combine(_cli.WorkPath, "media"), "media"));

        var plan = await _cli.RunAsync("restore", snapshot, "docs/one.txt", "media/clip.txt", "--target", "original", "--plan");

        Assert.AreEqual(0, plan.ExitCode, plan.All);
        Assert.Contains("plan: 2 file(s) back where they were captured", plan.All, StringComparison.Ordinal);
        Assert.Contains($"room: {docs}", plan.All, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task TheRoomTheRunNeeds_IsMeasuredAtEveryRoot()
    {
        // The second root's file alone outgrows the room this flow's disk
        // says it has, so a run measured at the first root alone would fit.
        var docs = Path.Combine(_cli.WorkPath, "docs");
        var media = Path.Combine(_cli.WorkPath, "media");
        var one = _cli.WriteFile("docs/one.txt", "first");
        var clip = Path.Combine(media, "clip.bin");
        Directory.CreateDirectory(media);
        await File.WriteAllBytesAsync(clip, System.Security.Cryptography.RandomNumberGenerator.GetBytes(1024 * 1024));
        var snapshot = await BackUpAsync((docs, "docs"), (media, "media"));
        File.Delete(clip);
        await File.WriteAllTextAsync(one, "edited");
        DirectGateway.AvailableBytesInFlow = _ => 256 * 1024;
        string[] asked = ["restore", snapshot, "docs/one.txt", "media/clip.bin", "--target", "original", "--existing", "overwrite"];

        var plan = await _cli.RunAsync([.. asked, "--plan"]);
        var run = await _cli.RunAsync(asked);

        Assert.AreNotEqual(0, plan.ExitCode, plan.All);
        Assert.Contains("would not fit", plan.All, StringComparison.Ordinal);
        Assert.AreNotEqual(0, run.ExitCode, run.All);
        Assert.Contains("would not fit", run.All, StringComparison.Ordinal);
        Assert.AreEqual("edited", await File.ReadAllTextAsync(one), "nothing is written");
        Assert.IsFalse(File.Exists(clip));
    }

    [TestMethod]
    public async Task APlanKeptForTheOriginalLocation_RunsAsKept()
    {
        var root = Path.Combine(_cli.WorkPath, "docs");
        var one = _cli.WriteFile("docs/one.txt", "first");
        var snapshot = await BackUpAsync((root, "docs"));
        await File.WriteAllTextAsync(one, "edited");
        var file = Path.Combine(_cli.WorkPath, "plan.json");
        string[] asked = ["restore", snapshot, "one.txt", "--target", "original", "--existing", "overwrite"];

        var kept = await _cli.RunAsync([.. asked, "--export", file]);
        var run = await _cli.RunAsync([.. asked, "--as-planned", file]);

        Assert.AreEqual(0, kept.ExitCode, kept.All);
        Assert.AreEqual(0, run.ExitCode, run.All);
        Assert.AreEqual("first", await File.ReadAllTextAsync(one));
    }

    private void Configure(params (string Path, string Label)[] roots)
    {
        Directory.CreateDirectory(_cli.StatePath);
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('1', 32),
                    Name = "vault",
                    Kind = DestinationKind.LocalPath,
                    Path = Path.Combine(_cli.WorkPath, "vault"),
                },
            ],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = SetId,
                    Name = "docs",
                    Roots = [.. roots.Select(root => new BackupRootConfiguration { Path = root.Path, Label = root.Label })],
                    Destinations = [new SetDestinationReference { Ref = "vault" }],
                },
            ],
        }.Save(Path.Combine(_cli.StatePath, "config.json"));
    }

    private async Task<string> BackUpAsync(params (string Path, string Label)[] roots)
    {
        await _cli.InitAsync();
        Configure(roots);
        var backup = await _cli.RunAsync("backup", "--set", "docs");
        Assert.AreEqual(0, backup.ExitCode, backup.All);
        return await LatestSnapshotAsync();
    }

    private async Task<string> LatestSnapshotAsync()
    {
        var snapshots = await _cli.RunAsync("snapshots");
        Assert.AreEqual(0, snapshots.ExitCode, snapshots.All);

        // The identifier is the leading hex token of the newest listing line.
        var first = snapshots.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        Assert.IsNotNull(first);
        return first.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }
}
