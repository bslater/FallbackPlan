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
/// With --json the receipts are listed as the service answers them, from the
/// folder and through a paired service alike, under a limit and with how many
/// are kept (ADR-0141). With --forget-all every run's receipt is let go at
/// once, each said, and nothing else the folder holds; through a paired
/// service, each it lists is let go by its name, and one it no longer keeps is
/// named while the rest go. A run named beside it is refused, before the
/// service is dialled (FR-RST-004, ADR-0161). With --older-than-days beside
/// it, only the receipts of runs that ended longer ago than that go, from the
/// folder and through a paired service alike, and how many stay is said; a
/// receipt that does not say when its run ended stays, and is named. Without
/// --forget-all, or below one day, it is refused before anything is removed
/// or the service dialled (ADR-0164).
/// </summary>
[TestClass]
public sealed class RestoreReceiptsVerbTests : IDisposable
{
    private const ulong LongAgo = 1_700_000_000_000;

    private readonly CliHarness _cli = new();

    private static ulong Now => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void Dispose() => _cli.Dispose();

    /// <summary>Keeps a receipt of one run's items under the state directory, as a run keeps its own.</summary>
    /// <summary>Keeps a receipt of a run that ended at <paramref name="completedAt"/>.</summary>
    private void KeptEnded(string run, ulong completedAt) =>
        new RestoreReceipt
        {
            SchemaVersion = RestoreReceipt.CurrentSchemaVersion,
            SnapshotId = new string('e', 32),
            StartedAt = completedAt - 10_000,
            CompletedAt = completedAt,
            Items = [new ReceiptItem { Path = "a.txt", Outcome = "restored", Bytes = 1 }],
            Displaced = [],
            WrittenTo = "/srv/restored",
            Outcome = RestoreOutcome.Complete,
        }.Keep(Receipts, run);

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

    /// <summary>
    /// A service that keeps the receipts of the runs it was given, lists them,
    /// and lets each go once; one it lists but no longer keeps is refused.
    /// </summary>
    private sealed class KeepingClient(string[] kept, params string[] listedButGone) : FallbackPlan.Api.IFallbackPlanClient
    {
        private readonly HashSet<string> _kept = new(kept, StringComparer.Ordinal);

        public List<FallbackPlan.Api.ServiceCommand> Asked { get; } = [];

        /// <summary>When each run listed ended, where not long ago; null for one whose receipt does not read.</summary>
        public Dictionary<string, ulong?> EndedAt { get; } = new(StringComparer.Ordinal);

        public FallbackPlan.Api.ContractVersion ServiceContractVersion => FallbackPlan.Api.ContractVersion.Current;

        public ValueTask<FallbackPlan.Api.ServiceResult> ExecuteAsync(FallbackPlan.Api.ServiceCommand command, CancellationToken cancellationToken)
        {
            Asked.Add(command);
            FallbackPlan.Api.ServiceResult answer = command switch
            {
                FallbackPlan.Api.ListRestoreReceiptsCommand { Limit: null } => new FallbackPlan.Api.RestoreReceiptsResult(
                    [.. _kept.Concat(listedButGone).Select(Describe)],
                    _kept.Count + listedButGone.Length),
                FallbackPlan.Api.ForgetRestoreReceiptCommand forget when _kept.Remove(forget.Run) => new FallbackPlan.Api.AcknowledgedResult(),
                _ => new FallbackPlan.Api.ServiceError(FallbackPlan.Api.ServiceErrorReason.NotFound, "the service keeps no such receipt"),
            };
            return ValueTask.FromResult(answer);
        }

        private FallbackPlan.Api.RestoreReceiptDescriptor Describe(string run) =>
            EndedAt.TryGetValue(run, out var endedAt) && endedAt is null
                ? new FallbackPlan.Api.RestoreReceiptDescriptor(run, null, null, null, null, 0, 0, 0, 0, 0, Problem: "not a receipt")
                : new FallbackPlan.Api.RestoreReceiptDescriptor(
                    run, "complete", endedAt ?? LongAgo, new string('e', 32), "/srv/restored", 1, 0, 0, 0, 0);

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
    public async Task ForgetAll_LetsEveryRunsReceiptGo_SayingEach()
    {
        Kept("0123456789abcdef", new ReceiptItem { Path = "a.txt", Outcome = "restored", Bytes = 1 });
        Kept("fedcba9876543210", new ReceiptItem { Path = "b.txt", Outcome = "restored", Bytes = 1 });

        var forgot = await CliHarness.RunRawAsync("restore-receipts", "--state", _cli.StatePath, "--forget-all");
        var listed = await CliHarness.RunRawAsync("restore-receipts", "--state", _cli.StatePath);

        Assert.AreEqual(0, forgot.ExitCode, forgot.All);
        Assert.Contains($"let go the receipt of run 0123456789abcdef kept under '{Receipts}'", forgot.Output, StringComparison.Ordinal);
        Assert.Contains($"let go the receipt of run fedcba9876543210 kept under '{Receipts}'", forgot.Output, StringComparison.Ordinal);
        Assert.Contains($"let go 2 restore receipts kept under '{Receipts}'", forgot.Output, StringComparison.Ordinal);
        Assert.Contains($"No restore receipt is kept under '{Receipts}'.", listed.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ForgetAll_LeavesEverythingThatIsNotARunsReceipt()
    {
        Kept("0123456789abcdef", new ReceiptItem { Path = "a.txt", Outcome = "restored", Bytes = 1 });
        var notes = Path.Combine(Receipts, "notes.json");
        var halfWritten = Path.Combine(Receipts, "fedcba9876543210.json.tmp");
        var peers = Directory.CreateDirectory(Path.Combine(Receipts, "peers")).FullName;
        await File.WriteAllTextAsync(notes, "{}");
        await File.WriteAllTextAsync(halfWritten, "{");

        var forgot = await CliHarness.RunRawAsync("restore-receipts", "--state", _cli.StatePath, "--forget-all");

        Assert.AreEqual(0, forgot.ExitCode, forgot.All);
        Assert.IsFalse(File.Exists(Path.Combine(Receipts, "0123456789abcdef.json")), "the run's receipt is let go");
        Assert.IsTrue(File.Exists(notes), "a file not named as a run's is kept");
        Assert.IsTrue(File.Exists(halfWritten), "and so is one half-written beside a run's");
        Assert.IsTrue(Directory.Exists(peers), "and so is a folder beside them");
        Assert.Contains($"let go 1 restore receipt kept under '{Receipts}'", forgot.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ForgetAll_WhereNoneIsKept_SaysSo()
    {
        Directory.CreateDirectory(Receipts);

        var forgot = await CliHarness.RunRawAsync("restore-receipts", "--state", _cli.StatePath, "--forget-all");

        Assert.AreEqual(0, forgot.ExitCode, forgot.All);
        Assert.Contains($"No restore receipt is kept under '{Receipts}'.", forgot.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ForgetAll_WithARun_IsRefused_AndRemovesNothing()
    {
        Kept("0123456789abcdef", new ReceiptItem { Path = "a.txt", Outcome = "restored", Bytes = 1 });

        var forgot = await CliHarness.RunRawAsync(
            "restore-receipts", "--state", _cli.StatePath, "--run", "0123456789abcdef", "--forget-all");

        Assert.AreEqual(1, forgot.ExitCode, forgot.All);
        Assert.Contains("--forget-all lets every run's receipt go: it names no run", forgot.Error, StringComparison.Ordinal);
        Assert.IsTrue(File.Exists(Path.Combine(Receipts, "0123456789abcdef.json")));
    }

    [TestMethod]
    public async Task ForgetAll_ThroughAPairedService_LetsEachReceiptItListsGo_ByItsName()
    {
        await using var client = new KeepingClient(["0123456789abcdef", "fedcba9876543210"]);
        using var output = new StringWriter();

        Assert.AreEqual(0, await RestoreReceiptReport.LetGoAllThroughServiceAsync(output, client, olderThanDays: null, CancellationToken.None));

        Assert.IsInstanceOfType<FallbackPlan.Api.ListRestoreReceiptsCommand>(client.Asked[0], out var listed);
        Assert.IsNull(listed.Limit, "every receipt it keeps is listed");
        CollectionAssert.AreEquivalent(
            new[] { "0123456789abcdef", "fedcba9876543210" },
            client.Asked.OfType<FallbackPlan.Api.ForgetRestoreReceiptCommand>().Select(forget => forget.Run).ToArray());
        Assert.Contains("let go the receipt of run 0123456789abcdef the service kept", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("let go 2 restore receipts the service kept", output.ToString(), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ForgetAll_ThroughAPairedService_AReceiptItNoLongerKeeps_IsNamed_AndTheRestGo()
    {
        await using var client = new KeepingClient(["0123456789abcdef"], "fedcba9876543210");
        using var output = new StringWriter();

        var failed = await Assert.ThrowsExactlyAsync<CliFailureException>(
            () => RestoreReceiptReport.LetGoAllThroughServiceAsync(output, client, olderThanDays: null, CancellationToken.None));

        Assert.Contains("fedcba9876543210", failed.Message, StringComparison.Ordinal);
        Assert.Contains("the service keeps no such receipt", failed.Message, StringComparison.Ordinal);
        Assert.Contains("let go the receipt of run 0123456789abcdef the service kept", output.ToString(), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ForgetAll_OlderThanDays_LetsGoOnlyTheReceiptsOfRunsThatEndedLongerAgo()
    {
        KeptEnded("0123456789abcdef", LongAgo);
        KeptEnded("fedcba9876543210", Now - (2 * 86_400_000));

        var forgot = await CliHarness.RunRawAsync(
            "restore-receipts", "--state", _cli.StatePath, "--forget-all", "--older-than-days", "30");

        Assert.AreEqual(0, forgot.ExitCode, forgot.All);
        Assert.IsFalse(File.Exists(Path.Combine(Receipts, "0123456789abcdef.json")), "a run that ended longer ago goes");
        Assert.IsTrue(File.Exists(Path.Combine(Receipts, "fedcba9876543210.json")), "one that ended within the days stays");
        Assert.Contains($"let go the receipt of run 0123456789abcdef kept under '{Receipts}'", forgot.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("let go the receipt of run fedcba9876543210", forgot.Output, StringComparison.Ordinal);
        Assert.Contains($"let go 1 restore receipt kept under '{Receipts}'", forgot.Output, StringComparison.Ordinal);
        Assert.Contains("1 restore receipt whose run ended within 30 days stays", forgot.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ForgetAll_OlderThanDays_KeepsAReceiptThatDoesNotSayWhenItsRunEnded_AndNamesIt()
    {
        Directory.CreateDirectory(Receipts);
        var unreadable = Path.Combine(Receipts, "fedcba9876543210.json");
        await File.WriteAllTextAsync(unreadable, "{ not a receipt");
        // Written long ago, which says nothing of when its run ended.
        File.SetLastWriteTimeUtc(unreadable, DateTime.UnixEpoch.AddDays(1));

        var forgot = await CliHarness.RunRawAsync(
            "restore-receipts", "--state", _cli.StatePath, "--forget-all", "--older-than-days", "30");

        Assert.AreEqual(0, forgot.ExitCode, forgot.All);
        Assert.IsTrue(File.Exists(unreadable));
        Assert.Contains(
            "the receipt of run fedcba9876543210 does not say when its run ended, so it stays", forgot.Output, StringComparison.Ordinal);
        Assert.Contains($"let go 0 restore receipts kept under '{Receipts}'", forgot.Output, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task OlderThanDays_WithoutForgetAll_IsRefused_AndRemovesNothing()
    {
        KeptEnded("0123456789abcdef", LongAgo);

        var refused = await CliHarness.RunRawAsync("restore-receipts", "--state", _cli.StatePath, "--older-than-days", "30");

        Assert.AreEqual(1, refused.ExitCode, refused.All);
        Assert.Contains("--older-than-days says which receipts --forget-all lets go", refused.Error, StringComparison.Ordinal);
        Assert.IsTrue(File.Exists(Path.Combine(Receipts, "0123456789abcdef.json")));
    }

    [TestMethod]
    public async Task OlderThanDays_BelowOne_IsRefused_AndRemovesNothing()
    {
        KeptEnded("0123456789abcdef", LongAgo);

        var refused = await CliHarness.RunRawAsync(
            "restore-receipts", "--state", _cli.StatePath, "--forget-all", "--older-than-days", "0");

        Assert.AreEqual(1, refused.ExitCode, refused.All);
        Assert.Contains("--older-than-days takes at least one day", refused.Error, StringComparison.Ordinal);
        Assert.IsTrue(File.Exists(Path.Combine(Receipts, "0123456789abcdef.json")));
    }

    [TestMethod]
    public async Task ForgetAll_OlderThanDays_ThroughAPairedService_LetsGoOnlyThoseOfRunsThatEndedLongerAgo()
    {
        await using var client = new KeepingClient(["0123456789abcdef", "fedcba9876543210", "00112233445566ff"])
        {
            EndedAt = { ["fedcba9876543210"] = Now - 3_600_000, ["00112233445566ff"] = null },
        };
        using var output = new StringWriter();

        Assert.AreEqual(
            0, await RestoreReceiptReport.LetGoAllThroughServiceAsync(output, client, olderThanDays: 1, CancellationToken.None));

        Assert.AreEqual(
            "0123456789abcdef", Assert.ContainsSingle(client.Asked.OfType<FallbackPlan.Api.ForgetRestoreReceiptCommand>()).Run);
        Assert.Contains("let go the receipt of run 0123456789abcdef the service kept", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("let go 1 restore receipt the service kept", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("1 restore receipt whose run ended within 1 day stays", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "the receipt of run 00112233445566ff does not say when its run ended, so it stays", output.ToString(), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task OlderThanDays_ThroughAPairedService_WithoutForgetAll_IsRefusedBeforeItIsDialled()
    {
        // Nothing listens on the port: a refusal that dialled first would say that instead.
        var refused = await CliHarness.RunRawAsync(
            "restore-receipts", "--older-than-days", "30",
            "--connect", "127.0.0.1:1", "--state", _cli.StatePath, "--fingerprint", new string('f', 64));

        Assert.AreEqual(1, refused.ExitCode, refused.All);
        Assert.Contains("--older-than-days says which receipts --forget-all lets go", refused.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ForgetAll_ThroughAPairedService_WithARun_IsRefusedBeforeItIsDialled()
    {
        // Nothing listens on the port: a refusal that dialled first would say that instead.
        var forgot = await CliHarness.RunRawAsync(
            "restore-receipts", "--run", "0123456789abcdef", "--forget-all",
            "--connect", "127.0.0.1:1", "--state", _cli.StatePath, "--fingerprint", new string('f', 64));

        Assert.AreEqual(1, forgot.ExitCode, forgot.All);
        Assert.Contains("--forget-all lets every run's receipt go: it names no run", forgot.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AsJson_EachReceiptKept_IsListedAsTheServiceAnswersIt_NewestFirst()
    {
        Kept("0123456789abcdef", new ReceiptItem { Path = "a.txt", Outcome = "restored", Bytes = 1, FoundInPlace = true },
            new ReceiptItem { Path = "b.txt", Outcome = "failed", Bytes = 0 });
        Directory.CreateDirectory(Receipts);
        await File.WriteAllTextAsync(Path.Combine(Receipts, "fedcba9876543210.json"), "{ not a receipt");
        File.SetLastWriteTimeUtc(Path.Combine(Receipts, "fedcba9876543210.json"), DateTime.UnixEpoch.AddDays(1));

        var listed = await CliHarness.RunRawAsync("restore-receipts", "--state", _cli.StatePath, "--json");

        Assert.AreEqual(0, listed.ExitCode, listed.All);
        using var json = JsonDocument.Parse(listed.Output);
        Assert.AreEqual(2, json.RootElement.GetProperty("total").GetInt32());
        var receipts = json.RootElement.GetProperty("receipts");
        Assert.AreEqual(2, receipts.GetArrayLength());
        var newest = receipts[0];
        Assert.AreEqual("0123456789abcdef", newest.GetProperty("run").GetString());
        Assert.AreEqual("complete", newest.GetProperty("outcome").GetString());
        Assert.AreEqual(1_700_000_000_000L, newest.GetProperty("ended_at").GetInt64());
        Assert.AreEqual("/srv/restored", newest.GetProperty("written_to").GetString());
        Assert.AreEqual(1, newest.GetProperty("restored").GetInt32());
        Assert.AreEqual(1, newest.GetProperty("found_in_place").GetInt32());
        Assert.AreEqual(1, newest.GetProperty("failed").GetInt32());
        var unreadable = receipts[1];
        Assert.AreEqual("fedcba9876543210", unreadable.GetProperty("run").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(unreadable.GetProperty("problem").GetString()));
        Assert.DoesNotContain("a.txt", listed.Output, StringComparison.Ordinal, "the listing names none of the backup's files");
    }

    [TestMethod]
    public async Task AsJson_ALimit_ListsTheNewest_AndTheTotalCountsTheRest_AndNoneKeptIsAnEmptyList()
    {
        Directory.CreateDirectory(_cli.StatePath);
        var none = await CliHarness.RunRawAsync("restore-receipts", "--state", _cli.StatePath, "--json");
        Kept("0123456789abcdef", new ReceiptItem { Path = "a.txt", Outcome = "restored", Bytes = 1 });
        Kept("fedcba9876543210", new ReceiptItem { Path = "b.txt", Outcome = "restored", Bytes = 1 });

        var limited = await CliHarness.RunRawAsync("restore-receipts", "--state", _cli.StatePath, "--json", "--limit", "1");

        Assert.AreEqual(0, none.ExitCode, none.All);
        using (var empty = JsonDocument.Parse(none.Output))
        {
            Assert.AreEqual(0, empty.RootElement.GetProperty("total").GetInt32());
            Assert.AreEqual(0, empty.RootElement.GetProperty("receipts").GetArrayLength());
        }

        Assert.AreEqual(0, limited.ExitCode, limited.All);
        using var json = JsonDocument.Parse(limited.Output);
        Assert.AreEqual(2, json.RootElement.GetProperty("total").GetInt32());
        Assert.AreEqual(1, json.RootElement.GetProperty("receipts").GetArrayLength());
    }

    [TestMethod]
    public async Task AsJson_ThroughAPairedService_IsListedAsItAnswers()
    {
        await using var client = new ListingClient(
            new FallbackPlan.Api.RestoreReceiptDescriptor("0123456789abcdef", "complete", 1_700_000_000_000, new string('e', 32), "/srv/restored", 3, 0, 0, 0, 0, ResumedWithin: 2));
        using var output = new StringWriter();

        Assert.AreEqual(0, await RestoreReceiptReport.WriteServiceKeptJsonAsync(output, client, null, CancellationToken.None));

        using var json = JsonDocument.Parse(output.ToString());
        Assert.AreEqual(1, json.RootElement.GetProperty("total").GetInt32());
        var receipt = Assert.ContainsSingle(json.RootElement.GetProperty("receipts").EnumerateArray().ToList());
        Assert.AreEqual("0123456789abcdef", receipt.GetProperty("run").GetString());
        Assert.AreEqual(2, receipt.GetProperty("resumed_within").GetInt32());
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
