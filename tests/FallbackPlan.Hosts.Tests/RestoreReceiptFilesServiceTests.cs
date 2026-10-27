using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Restore;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The files an earlier restore did not restore are answered by its kept
/// receipt (FR-RST-004, ADR-0127), and only through a source of the set the
/// receipt records, unlocked with that set's passphrase by the session that
/// asks (FR-WOR-007), because a receipt's paths are the backup's file names.
/// Each file that failed, was skipped or was degraded comes back with what
/// became of it and why, under a limit with a count of the rest. A receipt
/// that records no set is refused, as is a source of another set, a run that
/// kept no receipt, and a name that is not a run's, each naming no file.
/// </summary>
[TestClass]
public sealed class RestoreReceiptFilesServiceTests : IDisposable
{
    private const string Run = "0123456789abcdef";

    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    private string Receipts => Path.Combine(_harness.StateDirectory, "receipts");

    [TestMethod]
    public async Task TheFilesARunDidNotRestore_AreAnsweredThroughASourceOfTheSetItsReceiptRecords()
    {
        var (runtime, handler) = await StartAsync();
        await using var _ = runtime;
        Keep(_harness.DocsSetId);
        var source = (await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token)).SourceId;

        var answer = await handler.ExecuteAsync(new GetRestoreReceiptCommand(Run, source), _timeout.Token);

        Assert.IsInstanceOfType<RestoreReceiptResult>(answer, out var read, (answer as ServiceError)?.Message);
        Assert.AreEqual(Run, read.Receipt.Run);
        Assert.AreEqual("partial", read.Receipt.Outcome);
        CollectionAssert.AreEqual(
            new[] { "kept.txt skipped: a newer file was in the way", "lost.txt failed: no copy would read", "streams.txt degraded: streams not written back" },
            read.Items.Select(item => $"{item.Path} {item.Outcome}: {item.Detail}").ToArray(),
            "the files it did not restore, in the receipt's order, and none it did");
        Assert.AreEqual(0, read.Unlisted);
    }

    [TestMethod]
    public async Task ALimit_AnswersTheFirst_AndCountsTheRest()
    {
        var (runtime, handler) = await StartAsync();
        await using var _ = runtime;
        Keep(_harness.DocsSetId);
        var source = (await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token)).SourceId;

        var limited = await handler.ExecuteAsync(new GetRestoreReceiptCommand(Run, source, Limit: 1), _timeout.Token);
        var refused = await handler.ExecuteAsync(new GetRestoreReceiptCommand(Run, source, Limit: 0), _timeout.Token);

        Assert.IsInstanceOfType<RestoreReceiptResult>(limited, out var read, (limited as ServiceError)?.Message);
        Assert.AreEqual("kept.txt", Assert.ContainsSingle(read.Items).Path);
        Assert.AreEqual(2, read.Unlisted);
        Assert.IsInstanceOfType<ServiceError>(refused, out var error);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, error.Reason);
    }

    [TestMethod]
    public async Task WithoutASource_IsRefused_ForThePassphrase_NamingNoFile()
    {
        var (runtime, handler) = await StartAsync();
        await using var _ = runtime;
        Keep(_harness.DocsSetId);

        var answer = await handler.ExecuteAsync(new GetRestoreReceiptCommand(Run), _timeout.Token);

        Assert.IsInstanceOfType<ServiceError>(answer, out var error);
        Assert.AreEqual(ServiceErrorReason.Refused, error.Reason);
        Assert.Contains("passphrase", error.Message, StringComparison.Ordinal);
        AssertNamesNoFile(error.Message);
    }

    [TestMethod]
    public async Task ThroughASourceOfAnotherSet_IsRefused_NamingNoFile()
    {
        var (runtime, handler) = await StartAsync();
        await using var _ = runtime;
        Keep(new string('b', 32));
        var source = (await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token)).SourceId;

        var answer = await handler.ExecuteAsync(new GetRestoreReceiptCommand(Run, source), _timeout.Token);

        Assert.IsInstanceOfType<ServiceError>(answer, out var error);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, error.Reason, error.Message);
        Assert.Contains("each set answers to its own passphrase", error.Message, StringComparison.Ordinal);
        AssertNamesNoFile(error.Message);
    }

    [TestMethod]
    public async Task AReceiptThatRecordsNoSet_IsRefused_AndSaysWhereItIsRead()
    {
        var (runtime, handler) = await StartAsync();
        await using var _ = runtime;
        Keep(setId: null);
        var source = (await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token)).SourceId;

        var answer = await handler.ExecuteAsync(new GetRestoreReceiptCommand(Run, source), _timeout.Token);

        Assert.IsInstanceOfType<ServiceError>(answer, out var error);
        Assert.AreEqual(ServiceErrorReason.Refused, error.Reason, error.Message);
        Assert.Contains($"restore-receipts --run {Run}", error.Message, StringComparison.Ordinal);
        AssertNamesNoFile(error.Message);
    }

    [TestMethod]
    public async Task ARunThatKeptNoReceipt_AndWhatIsNotARunsName_AreRefused()
    {
        var (runtime, handler) = await StartAsync();
        await using var _ = runtime;
        var source = (await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token)).SourceId;

        var none = await handler.ExecuteAsync(new GetRestoreReceiptCommand(Run, source), _timeout.Token);
        var notARun = await handler.ExecuteAsync(new GetRestoreReceiptCommand("../config", source), _timeout.Token);

        Assert.IsInstanceOfType<ServiceError>(none, out var missing);
        Assert.AreEqual(ServiceErrorReason.NotFound, missing.Reason, missing.Message);
        Assert.Contains(Run, missing.Message, StringComparison.Ordinal);
        Assert.IsInstanceOfType<ServiceError>(notARun, out var refused);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, refused.Reason, refused.Message);
    }

    [TestMethod]
    public async Task ARestoreThroughTheService_RecordsTheIdentityOfTheSetItWasAskedThrough()
    {
        var (runtime, handler) = await StartAsync();
        await using var _ = runtime;
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token);
        var snapshotId = Assert.ContainsSingle(source.Snapshots).SnapshotId;

        var answer = await handler.ExecuteAsync(
            new RunRestoreCommand(snapshotId, null, Path.Combine(_harness.WorkPath, "restored"), Source: source.SourceId),
            _timeout.Token);

        Assert.IsInstanceOfType<RestoreResult>(answer, out var restored, (answer as ServiceError)?.Message);
        var kept = RestoreReceipt.ReadKept(Receipts, restored.Run!);
        Assert.AreEqual(_harness.DocsSetId, kept?.Receipt?.SetId);
        Assert.AreEqual("docs", kept?.Receipt?.Set);
        Assert.IsInstanceOfType<RestoreReceiptsResult>(
            await handler.ExecuteAsync(new ListRestoreReceiptsCommand(), _timeout.Token), out var listed);
        Assert.AreEqual(_harness.DocsSetId, Assert.ContainsSingle(listed.Receipts).SetId, "the listing says which set it is");
    }

    private static void AssertNamesNoFile(string message)
    {
        foreach (var name in new[] { "kept.txt", "lost.txt", "streams.txt", "written.txt" })
        {
            Assert.DoesNotContain(name, message, StringComparison.Ordinal);
        }
    }

    private void Keep(string? setId) => new RestoreReceipt
    {
        SchemaVersion = RestoreReceipt.CurrentSchemaVersion,
        SnapshotId = new string('e', 32),
        StartedAt = 1_699_999_990_000,
        CompletedAt = 1_700_000_000_000,
        Items =
        [
            new ReceiptItem { Path = "written.txt", Outcome = "restored", Bytes = 1 },
            new ReceiptItem { Path = "kept.txt", Outcome = "skipped", Bytes = 1, Detail = "a newer file was in the way" },
            new ReceiptItem { Path = "lost.txt", Outcome = "failed", Bytes = 1, Detail = "no copy would read" },
            new ReceiptItem { Path = "streams.txt", Outcome = "degraded", Bytes = 1, Detail = "streams not written back" },
        ],
        Displaced = [],
        WrittenTo = "/srv/restored",
        Outcome = RestoreOutcome.Partial,
        Set = setId is null ? null : "docs",
        SetId = setId,
    }.Keep(Receipts, Run);

    private async Task<(ServiceRuntime Runtime, ServiceCommandHandler Handler)> StartAsync()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "the notes to come back");
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));
        await _harness.SetupAsync();

        var runtime = await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                // The placement condition (ADR-0051) judges by volume, and the
                // fixture's every path shares one real volume — the vaults are
                // told apart by name, the compliant install's shape.
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
            },
            _timeout.Token);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await RunBackupAndWaitAsync(runtime, handler);
        return (runtime, handler);
    }

    private async Task RunBackupAndWaitAsync(ServiceRuntime runtime, ServiceCommandHandler handler)
    {
        var progress = runtime.Progress.WatchAsync(_timeout.Token);
        var watching = Task.Run(
            async () =>
            {
                await foreach (var observation in progress)
                {
                    if (observation.Progress.State is JobState.Complete or JobState.CompletedWithFailures)
                    {
                        return;
                    }
                }
            },
            _timeout.Token);

        Assert.IsInstanceOfType<JobAcceptedResult>(
            await handler.ExecuteAsync(new RunBackupCommand("docs", Full: false), _timeout.Token));
        await watching;
    }
}
