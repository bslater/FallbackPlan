using System.Text.Json;
using FallbackPlan.Restore;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The <c>restore-receipts</c> verb reads back the restore receipts a state
/// directory keeps (FR-RST-004, ADR-0124), file-direct and read-only, as
/// <c>receipts</c> reads the peers'. Each run is listed newest first with
/// what it did and where it wrote, and one asked by its run is printed whole
/// as it was kept. A path that holds nothing because it was mistyped is not
/// "nothing restored", a name that is not a run's is refused before it
/// reaches a path, and a receipt that does not read is said to, beside the
/// rest. A run names the set it restored where its receipt does (ADR-0126).
/// </summary>
[TestClass]
public sealed class RestoreReceiptsVerbTests : IDisposable
{
    private readonly CliHarness _cli = new();

    public void Dispose() => _cli.Dispose();

    private string Receipts => Path.Combine(_cli.StatePath, "receipts");

    [TestMethod]
    public async Task TheReceiptsKept_AreListedNewestFirst_WithWhatEachRunDidAndWhereItWrote()
    {
        var snapshot = await BackUpAsync();
        var first = await RestoreAsync(snapshot, "a");
        var second = await RestoreAsync(snapshot, "b");

        var listed = await CliHarness.RunRawAsync("restore-receipts", "--state", _cli.StatePath);

        Assert.AreEqual(0, listed.ExitCode, listed.All);
        var firstAt = listed.Output.IndexOf(first, StringComparison.Ordinal);
        var secondAt = listed.Output.IndexOf(second, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, firstAt, listed.Output);
        Assert.IsGreaterThanOrEqualTo(0, secondAt, listed.Output);
        Assert.IsLessThan(firstAt, secondAt, "the newest is listed first");
        Assert.Contains("Complete", listed.Output, StringComparison.Ordinal);
        Assert.Contains($"snapshot {snapshot}", listed.Output, StringComparison.Ordinal);
        Assert.DoesNotContain($"snapshot {snapshot} of", listed.Output, StringComparison.Ordinal, "a run that knew no set names none");
        Assert.Contains($"written to {Path.Combine(_cli.WorkPath, "a")}", listed.Output, StringComparison.Ordinal);
        Assert.Contains($"written to {Path.Combine(_cli.WorkPath, "b")}", listed.Output, StringComparison.Ordinal);
        Assert.Contains($"{RestoredIn(first)} restored, 0 skipped, 0 failed", listed.Output, StringComparison.Ordinal);
        Assert.Contains("2 restore receipts kept under", listed.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task EachRun_SaysWhenItEnded_AndCountsWhatWasDegraded_AndWhatAResumeFoundInPlace()
    {
        new RestoreReceipt
        {
            SchemaVersion = RestoreReceipt.CurrentSchemaVersion,
            SnapshotId = new string('e', 32),
            StartedAt = 1_699_999_990_000,
            CompletedAt = 1_700_000_000_000,
            Items =
            [
                new ReceiptItem { Path = "found.txt", Outcome = "restored", Bytes = 1, FoundInPlace = true },
                new ReceiptItem { Path = "written.txt", Outcome = "restored", Bytes = 1 },
                new ReceiptItem { Path = "kept.txt", Outcome = "skipped", Bytes = 1 },
                new ReceiptItem { Path = "lost.txt", Outcome = "failed", Bytes = 1 },
                new ReceiptItem { Path = "streams.txt", Outcome = "degraded", Bytes = 1 },
            ],
            Displaced = [],
            WrittenTo = "/srv/restored",
            Outcome = RestoreOutcome.Partial,
            Set = "docs",
        }.Keep(Receipts, "0123456789abcdef");

        var listed = await CliHarness.RunRawAsync("restore-receipts", "--state", _cli.StatePath);

        Assert.AreEqual(0, listed.ExitCode, listed.All);
        Assert.Contains(
            $"0123456789abcdef  Partial  ended 2023-11-14 22:13:20Z  snapshot {new string('e', 32)} of docs",
            listed.Output,
            StringComparison.Ordinal);
        Assert.Contains(
            "2 restored (1 found in place), 1 skipped, 1 failed, 1 degraded", listed.Output, StringComparison.Ordinal);
        Assert.Contains("written to /srv/restored", listed.Output, StringComparison.Ordinal);
        Assert.Contains("1 restore receipt kept under", listed.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ARunAskedByItsName_IsPrintedWholeAsKept()
    {
        var snapshot = await BackUpAsync();
        var run = await RestoreAsync(snapshot, "a");

        var shown = await CliHarness.RunRawAsync("restore-receipts", "--state", _cli.StatePath, "--run", run);

        Assert.AreEqual(0, shown.ExitCode, shown.All);
        Assert.AreEqual(
            (await File.ReadAllTextAsync(Path.Combine(Receipts, $"{run}.json"))).Trim(),
            shown.Output.Trim());
    }

    [TestMethod]
    public async Task ARunThatKeptNoReceipt_IsRefusedNamingIt()
    {
        Directory.CreateDirectory(Receipts);

        var shown = await CliHarness.RunRawAsync(
            "restore-receipts", "--state", _cli.StatePath, "--run", "0123456789abcdef");

        Assert.AreEqual(1, shown.ExitCode, shown.All);
        Assert.Contains("0123456789abcdef", shown.Error, StringComparison.Ordinal);
        Assert.Contains("no receipt", shown.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task WhatIsNotARunsName_IsRefusedBeforeItReachesAPath()
    {
        Directory.CreateDirectory(Receipts);
        await File.WriteAllTextAsync(Path.Combine(_cli.StatePath, "config.json"), "{}");

        var shown = await CliHarness.RunRawAsync(
            "restore-receipts", "--state", _cli.StatePath, "--run", Path.Combine("..", "config"));

        Assert.AreEqual(1, shown.ExitCode, shown.All);
        Assert.Contains("sixteen", shown.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("{}", shown.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ALimit_ListsTheNewest_AndSaysHowManyAreKept()
    {
        var snapshot = await BackUpAsync();
        var first = await RestoreAsync(snapshot, "a");
        var second = await RestoreAsync(snapshot, "b");

        var listed = await CliHarness.RunRawAsync("restore-receipts", "--state", _cli.StatePath, "--limit", "1");

        Assert.AreEqual(0, listed.ExitCode, listed.All);
        Assert.Contains(second, listed.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(first, listed.Output, StringComparison.Ordinal);
        Assert.Contains("1 of 2", listed.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ALimitBelowOne_IsRefused()
    {
        Directory.CreateDirectory(_cli.StatePath);

        var listed = await CliHarness.RunRawAsync("restore-receipts", "--state", _cli.StatePath, "--limit", "0");

        Assert.AreEqual(1, listed.ExitCode, listed.All);
        Assert.Contains("--limit takes at least 1", listed.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AReceiptThatDoesNotRead_IsListedAsUnreadable_AndTheRestStillAre()
    {
        var snapshot = await BackUpAsync();
        var run = await RestoreAsync(snapshot, "a");
        await File.WriteAllTextAsync(Path.Combine(Receipts, "fedcba9876543210.json"), "{ not a receipt");

        var listed = await CliHarness.RunRawAsync("restore-receipts", "--state", _cli.StatePath);

        Assert.AreEqual(0, listed.ExitCode, listed.All);
        Assert.Contains("fedcba9876543210", listed.Output, StringComparison.Ordinal);
        Assert.Contains("unreadable", listed.Output, StringComparison.Ordinal);
        Assert.Contains(run, listed.Output, StringComparison.Ordinal);
        Assert.Contains("2 restore receipts kept under", listed.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AStateDirectoryThatDoesNotExist_IsRefusedByPath_NotAnsweredEmpty()
    {
        var missing = Path.Combine(_cli.WorkPath, "no-such-state");

        var listed = await CliHarness.RunRawAsync("restore-receipts", "--state", missing);

        Assert.AreEqual(1, listed.ExitCode, listed.All);
        Assert.Contains("no state directory", listed.Error, StringComparison.Ordinal);
        Assert.Contains(missing, listed.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("kept", listed.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AStateDirectoryThatKeepsNone_SaysSoInWords()
    {
        Directory.CreateDirectory(_cli.StatePath);

        var listed = await CliHarness.RunRawAsync("restore-receipts", "--state", _cli.StatePath);

        Assert.AreEqual(0, listed.ExitCode, listed.All);
        Assert.Contains("No restore receipt is kept under", listed.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task WithoutAStateDirectory_IsUsage()
    {
        var listed = await CliHarness.RunRawAsync("restore-receipts");

        Assert.AreEqual(1, listed.ExitCode);
        Assert.Contains("--state", listed.Error, StringComparison.Ordinal);
    }

    /// <summary>Restores the snapshot to a folder of the work tree and returns the run its receipt is kept under.</summary>
    private async Task<string> RestoreAsync(string snapshot, string folder)
    {
        var run = await _cli.RunAsync("restore", snapshot, "--output", Path.Combine(_cli.WorkPath, folder));
        Assert.AreEqual(0, run.ExitCode, run.All);
        var line = run.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Single(candidate => candidate.StartsWith("receipt: ", StringComparison.Ordinal));
        return Path.GetFileNameWithoutExtension(line["receipt: ".Length..]);
    }

    /// <summary>How many of the run's items its kept receipt says were restored.</summary>
    private int RestoredIn(string run)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Receipts, $"{run}.json")));
        return document.RootElement.GetProperty("items").EnumerateArray()
            .Count(item => item.GetProperty("outcome").GetString() == "restored");
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
