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
/// rest. A run names the set it restored where its receipt does (ADR-0126),
/// and says how many of its files were resumed within, from the folder and
/// through a paired service alike (ADR-0134).
/// Through a paired service (ADR-0128), a name that is not a run's and a
/// limit below one are refused before the service is dialled. One run's
/// receipt is let go by its name, and only that one (ADR-0139): a name that
/// is not a run's, a run that kept none, and no run at all are refused, and
/// nothing is removed. A paired service is asked to let its receipt go by the
/// run's name alone, and what it refuses is refused as it says (ADR-0140).
/// </summary>
[TestClass]
public sealed class RestoreReceiptsVerbTests : IDisposable
{
    private readonly CliHarness _cli = new();

    public void Dispose() => _cli.Dispose();

    /// <summary>Keeps a receipt of one run's items under the state directory, as a run keeps its own.</summary>
    private void Kept(string run, params ReceiptItem[] items) =>
        new RestoreReceipt
        {
            SchemaVersion = RestoreReceipt.CurrentSchemaVersion,
            SnapshotId = new string('e', 32),
            StartedAt = 1_699_999_990_000,
            CompletedAt = 1_700_000_000_000,
            Items = items,
            Displaced = [],
            WrittenTo = "/srv/restored",
            Outcome = RestoreOutcome.Complete,
        }.Keep(Receipts, run);

    /// <summary>A service that lists the receipts it was given, and answers nothing else.</summary>
    private sealed class ListingClient(params FallbackPlan.Api.RestoreReceiptDescriptor[] receipts) : FallbackPlan.Api.IFallbackPlanClient
    {
        public FallbackPlan.Api.ContractVersion ServiceContractVersion => FallbackPlan.Api.ContractVersion.Current;

        public ValueTask<FallbackPlan.Api.ServiceResult> ExecuteAsync(FallbackPlan.Api.ServiceCommand command, CancellationToken cancellationToken) =>
            ValueTask.FromResult<FallbackPlan.Api.ServiceResult>(command is FallbackPlan.Api.ListRestoreReceiptsCommand
                ? new FallbackPlan.Api.RestoreReceiptsResult(receipts, receipts.Length)
                : new FallbackPlan.Api.ServiceError(FallbackPlan.Api.ServiceErrorReason.InvalidArgument, "not scripted"));

        public IAsyncEnumerable<FallbackPlan.Api.JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<FallbackPlan.Api.JobProgressEvent>();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A service that keeps the receipts of the runs it was given, and lets each go once.</summary>
    private sealed class ForgettingClient(params string[] kept) : FallbackPlan.Api.IFallbackPlanClient
    {
        private readonly HashSet<string> _kept = new(kept, StringComparer.Ordinal);

        public List<FallbackPlan.Api.ServiceCommand> Asked { get; } = [];

        public FallbackPlan.Api.ContractVersion ServiceContractVersion => FallbackPlan.Api.ContractVersion.Current;

        public ValueTask<FallbackPlan.Api.ServiceResult> ExecuteAsync(FallbackPlan.Api.ServiceCommand command, CancellationToken cancellationToken)
        {
            Asked.Add(command);
            return ValueTask.FromResult<FallbackPlan.Api.ServiceResult>(
                command is FallbackPlan.Api.ForgetRestoreReceiptCommand forget && _kept.Remove(forget.Run)
                    ? new FallbackPlan.Api.AcknowledgedResult()
                    : new FallbackPlan.Api.ServiceError(FallbackPlan.Api.ServiceErrorReason.NotFound, "the service keeps no such receipt"));
        }

        public IAsyncEnumerable<FallbackPlan.Api.JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<FallbackPlan.Api.JobProgressEvent>();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

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
    public async Task ARunThatResumedWithinAFile_SaysHowManyOfItsFilesWere()
    {
        Kept("0123456789abcdef",
            new ReceiptItem { Path = "found.txt", Outcome = "restored", Bytes = 1, FoundInPlace = true },
            new ReceiptItem { Path = "resumed.txt", Outcome = "restored", Bytes = 8, ResumedAt = 4 },
            new ReceiptItem { Path = "written.txt", Outcome = "restored", Bytes = 1 });
        Kept("fedcba9876543210",
            new ReceiptItem { Path = "resumed.txt", Outcome = "restored", Bytes = 8, ResumedAt = 4 },
            new ReceiptItem { Path = "written.txt", Outcome = "restored", Bytes = 1 });

        var listed = await CliHarness.RunRawAsync("restore-receipts", "--state", _cli.StatePath);

        Assert.AreEqual(0, listed.ExitCode, listed.All);
        Assert.Contains("3 restored (1 found in place, 1 resumed within), 0 skipped, 0 failed", listed.Output, StringComparison.Ordinal);
        Assert.Contains("2 restored (1 resumed within), 0 skipped, 0 failed", listed.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ThroughAPairedService_ARunThatResumedWithinAFile_SaysHowManyOfItsFilesWere()
    {
        await using var client = new ListingClient(
            new FallbackPlan.Api.RestoreReceiptDescriptor("0123456789abcdef", "complete", 1_700_000_000_000, new string('e', 32), "/srv/restored", 3, 0, 0, 0, 0, ResumedWithin: 2));
        using var output = new StringWriter();

        Assert.AreEqual(0, await RestoreReceiptReport.WriteServiceKeptAsync(output, client, null, CancellationToken.None));

        Assert.Contains("3 restored (2 resumed within), 0 skipped, 0 failed", output.ToString(), StringComparison.Ordinal);
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
    public async Task ThroughAPairedService_WhatIsNotARunsName_AndALimitBelowOne_AreRefusedBeforeItIsDialled()
    {
        // Nothing listens on the port: a refusal that dialled first would say that instead.
        string[] connect = ["--connect", "127.0.0.1:1", "--state", _cli.StatePath, "--fingerprint", new string('f', 64)];

        var notARun = await CliHarness.RunRawAsync(["restore-receipts", "--run", Path.Combine("..", "config"), .. connect]);
        var belowOne = await CliHarness.RunRawAsync(["restore-receipts", "--limit", "0", .. connect]);

        Assert.AreEqual(1, notARun.ExitCode, notARun.All);
        Assert.Contains("sixteen", notARun.Error, StringComparison.Ordinal);
        Assert.AreEqual(1, belowOne.ExitCode, belowOne.All);
        Assert.Contains("--limit takes at least 1", belowOne.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Forget_LetsOneRunsReceiptGo_AndLeavesTheRest()
    {
        Kept("0123456789abcdef", new ReceiptItem { Path = "a.txt", Outcome = "restored", Bytes = 1 });
        Kept("fedcba9876543210", new ReceiptItem { Path = "b.txt", Outcome = "restored", Bytes = 1 });

        var forgot = await CliHarness.RunRawAsync(
            "restore-receipts", "--state", _cli.StatePath, "--run", "0123456789abcdef", "--forget");

        Assert.AreEqual(0, forgot.ExitCode, forgot.All);
        Assert.Contains($"let go the receipt of run 0123456789abcdef kept under '{Receipts}'", forgot.Output, StringComparison.Ordinal);
        Assert.IsFalse(File.Exists(Path.Combine(Receipts, "0123456789abcdef.json")));
        Assert.IsTrue(File.Exists(Path.Combine(Receipts, "fedcba9876543210.json")), "the other run's receipt is kept");

        var listed = await CliHarness.RunRawAsync("restore-receipts", "--state", _cli.StatePath);
        Assert.Contains("1 restore receipt kept under", listed.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Forget_ARunThatKeptNoReceipt_IsRefusedNamingIt()
    {
        Directory.CreateDirectory(Receipts);

        var forgot = await CliHarness.RunRawAsync(
            "restore-receipts", "--state", _cli.StatePath, "--run", "0123456789abcdef", "--forget");

        Assert.AreEqual(1, forgot.ExitCode, forgot.All);
        Assert.Contains("0123456789abcdef", forgot.Error, StringComparison.Ordinal);
        Assert.Contains("no receipt", forgot.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Forget_WhatIsNotARunsName_IsRefused_AndRemovesNothing()
    {
        Directory.CreateDirectory(Receipts);
        var config = Path.Combine(_cli.StatePath, "config.json");
        await File.WriteAllTextAsync(config, "{}");

        var forgot = await CliHarness.RunRawAsync(
            "restore-receipts", "--state", _cli.StatePath, "--run", Path.Combine("..", "config"), "--forget");

        Assert.AreEqual(1, forgot.ExitCode, forgot.All);
        Assert.Contains("sixteen", forgot.Error, StringComparison.Ordinal);
        Assert.IsTrue(File.Exists(config));
    }

    [TestMethod]
    public async Task Forget_WithoutARun_IsRefused_AndRemovesNothing()
    {
        Kept("0123456789abcdef", new ReceiptItem { Path = "a.txt", Outcome = "restored", Bytes = 1 });

        var forgot = await CliHarness.RunRawAsync("restore-receipts", "--state", _cli.StatePath, "--forget");

        Assert.AreEqual(1, forgot.ExitCode, forgot.All);
        Assert.Contains("--forget lets one run's receipt go: name it with --run", forgot.Error, StringComparison.Ordinal);
        Assert.IsTrue(File.Exists(Path.Combine(Receipts, "0123456789abcdef.json")));
    }

    [TestMethod]
    public async Task Forget_ThroughAPairedService_AsksItToLetTheRunsReceiptGo_ByItsNameAlone()
    {
        await using var client = new ForgettingClient("0123456789abcdef", "fedcba9876543210");
        using var output = new StringWriter();

        Assert.AreEqual(0, await RestoreReceiptReport.LetGoThroughServiceAsync(output, client, "0123456789abcdef", CancellationToken.None));

        Assert.IsInstanceOfType<FallbackPlan.Api.ForgetRestoreReceiptCommand>(Assert.ContainsSingle(client.Asked), out var asked);
        Assert.AreEqual("0123456789abcdef", asked.Run);
        Assert.Contains("let go the receipt of run 0123456789abcdef the service kept", output.ToString(), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Forget_ThroughAPairedService_ARunItKeepsNoReceiptOf_IsRefusedAsTheServiceSays()
    {
        await using var client = new ForgettingClient();
        using var output = new StringWriter();

        var refused = await Assert.ThrowsExactlyAsync<CliFailureException>(
            () => RestoreReceiptReport.LetGoThroughServiceAsync(output, client, "0123456789abcdef", CancellationToken.None));

        Assert.Contains("the service keeps no such receipt", refused.Message, StringComparison.Ordinal);
        Assert.AreEqual(string.Empty, output.ToString());
    }

    [TestMethod]
    public async Task Forget_ThroughAPairedService_WhatIsNotARunsName_IsRefusedBeforeItIsDialled()
    {
        // Nothing listens on the port: a refusal that dialled first would say that instead.
        var forgot = await CliHarness.RunRawAsync(
            "restore-receipts", "--run", Path.Combine("..", "config"), "--forget",
            "--connect", "127.0.0.1:1", "--state", _cli.StatePath, "--fingerprint", new string('f', 64));

        Assert.AreEqual(1, forgot.ExitCode, forgot.All);
        Assert.Contains("sixteen", forgot.Error, StringComparison.Ordinal);
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
