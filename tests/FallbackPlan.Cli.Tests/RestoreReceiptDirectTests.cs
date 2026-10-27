using System.Text.Json;
using FallbackPlan.Cli;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// In direct mode a restore keeps its receipt (FR-RST-004, ADR-0122), where
/// the service keeps one: the run's own file under the state directory's
/// receipts, named by the run. The report says where. A run that writes
/// nothing, refused before it begins, keeps none, and a plan keeps none.
/// </summary>
[TestClass]
public sealed class RestoreReceiptDirectTests : IDisposable
{
    private readonly CliHarness _cli = new();

    public void Dispose() => _cli.Dispose();

    private string Receipts => Path.Combine(_cli.StatePath, "receipts");

    [TestMethod]
    public async Task ARestore_KeepsItsReceipt_AndSaysWhere()
    {
        var snapshot = await BackUpAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");

        var run = await _cli.RunAsync("restore", snapshot, "--output", destination);

        Assert.AreEqual(0, run.ExitCode, run.All);
        var kept = Assert.ContainsSingle(Directory.GetFiles(Receipts, "*.json"));
        Assert.MatchesRegex(@"^[0-9a-f]{16}\.json$", Path.GetFileName(kept), "named by its run");
        Assert.Contains($"receipt: {kept}", run.Output, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(kept));
        var root = document.RootElement;
        Assert.AreEqual(snapshot, root.GetProperty("snapshot_id").GetString());
        Assert.AreEqual(destination, root.GetProperty("written_to").GetString());
        Assert.AreEqual("Complete", root.GetProperty("outcome").GetString());
        var paths = root.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("path").GetString()).ToList();
        CollectionAssert.Contains(paths, "one.txt");
        CollectionAssert.Contains(paths, "nested/two.txt");
    }

    [TestMethod]
    public async Task TwoRestores_KeepTwoReceipts()
    {
        var snapshot = await BackUpAsync();

        var first = await _cli.RunAsync("restore", snapshot, "--output", Path.Combine(_cli.WorkPath, "a"));
        var second = await _cli.RunAsync("restore", snapshot, "--output", Path.Combine(_cli.WorkPath, "b"));

        Assert.AreEqual(0, first.ExitCode, first.All);
        Assert.AreEqual(0, second.ExitCode, second.All);
        Assert.HasCount(2, Directory.GetFiles(Receipts, "*.json"));
    }

    [TestMethod]
    public async Task ARestoreRefusedBeforeItBegins_AndAPlan_KeepNoReceipt()
    {
        var snapshot = await BackUpAsync();
        var destination = Path.Combine(_cli.WorkPath, "restored");
        DirectGateway.AvailableBytesInFlow = _ => 1;

        var refused = await _cli.RunAsync("restore", snapshot, "--output", destination);
        var plan = await _cli.RunAsync("restore", snapshot, "--output", destination, "--plan");

        Assert.AreNotEqual(0, refused.ExitCode, refused.All);
        Assert.AreNotEqual(0, plan.ExitCode, plan.All);
        Assert.IsFalse(Directory.Exists(Receipts) && Directory.GetFiles(Receipts, "*.json").Length > 0, "nothing ran, so nothing is receipted");
    }

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
