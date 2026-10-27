using System.CommandLine;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Domain.Jobs;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The receipts the service keeps are read back as direct mode's are
/// (FR-RST-004, ADR-0124): <c>restore-receipts</c> over the service's state
/// directory lists the run the service named, with what it did, and prints
/// that run's receipt whole as the service kept it. The service answers them
/// too (contract 1.73, ADR-0125), newest first under a limit with how many it
/// keeps: what became of each run's files and where it wrote them, never the
/// paths, which are the backup's file names, and a receipt that does not read
/// said not to. Each names the set it restored (contract 1.74, ADR-0126).
/// </summary>
[TestClass]
public sealed class RestoreReceiptsServiceTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task ARestoreThroughTheService_IsReadBackUnderTheRunTheServiceNamed()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "the notes to come back");
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await RunBackupAndWaitAsync(runtime, handler);
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token);
        var snapshotId = Assert.ContainsSingle(source.Snapshots).SnapshotId;
        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(
                    snapshotId, null, Path.Combine(_harness.WorkPath, "restored"), Source: source.SourceId),
                _timeout.Token),
            out var restored);
        Assert.IsNotNull(restored.Run);
        Assert.IsNotNull(restored.ReceiptPath);

        var listed = await CliAsync("restore-receipts", "--state", _harness.StateDirectory);
        var shown = await CliAsync("restore-receipts", "--state", _harness.StateDirectory, "--run", restored.Run);

        Assert.AreEqual(0, listed.ExitCode, listed.Error);
        Assert.Contains($"{restored.Run}  Complete", listed.Output, StringComparison.Ordinal);
        Assert.Contains($"snapshot {snapshotId} of docs", listed.Output, StringComparison.Ordinal);
        Assert.Contains("1 restore receipt kept under", listed.Output, StringComparison.Ordinal);
        Assert.AreEqual(0, shown.ExitCode, shown.Error);
        Assert.AreEqual((await File.ReadAllTextAsync(restored.ReceiptPath)).Trim(), shown.Output.Trim());
    }

    [TestMethod]
    public async Task TheServiceListsTheReceiptsItKeeps_NewestFirst_NamingNoneOfTheBackupsFiles()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "the notes to come back");
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await RunBackupAndWaitAsync(runtime, handler);
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token);
        var snapshotId = Assert.ContainsSingle(source.Snapshots).SnapshotId;
        var first = await RestoreAsync(handler, snapshotId, source.SourceId, "first");
        var second = await RestoreAsync(handler, snapshotId, source.SourceId, "second");
        await File.WriteAllTextAsync(
            Path.Combine(_harness.StateDirectory, "receipts", "0000000000000000.json"), "{ not a receipt");
        File.SetLastWriteTimeUtc(
            Path.Combine(_harness.StateDirectory, "receipts", "0000000000000000.json"), DateTime.UnixEpoch.AddDays(1));

        var answer = await handler.ExecuteAsync(new ListRestoreReceiptsCommand(), _timeout.Token);

        Assert.IsInstanceOfType<RestoreReceiptsResult>(answer, out var listed, (answer as ServiceError)?.Message);
        Assert.AreEqual(3, listed.Total);
        CollectionAssert.AreEqual(
            new[] { second.Run, first.Run, "0000000000000000" },
            listed.Receipts.Select(receipt => receipt.Run).ToArray(),
            "newest first, the unreadable one by when it was written");
        var newest = listed.Receipts[0];
        Assert.AreEqual("complete", newest.Outcome);
        Assert.AreEqual("docs", newest.Set, "the run restored a snapshot of the docs set");
        Assert.AreEqual(snapshotId, newest.SnapshotId);
        Assert.AreEqual(second.OutputDirectory, newest.WrittenTo);
        Assert.IsNotNull(newest.EndedAt);
        Assert.AreEqual(second.Restored, newest.Restored);
        Assert.AreEqual(0L, newest.Failed);
        Assert.IsNull(newest.Problem);
        var unreadable = listed.Receipts[2];
        Assert.IsNull(unreadable.Outcome);
        Assert.IsNull(unreadable.Set);
        Assert.IsFalse(string.IsNullOrWhiteSpace(unreadable.Problem));
        Assert.DoesNotContain(
            "notes.txt",
            System.Text.Json.JsonSerializer.Serialize<ServiceResult>(listed, Api.Transport.FrameCodec.SerializerOptions),
            StringComparison.Ordinal,
            "the listing names none of the backup's files");
    }

    [TestMethod]
    public async Task ALimit_ListsTheNewest_AndTheTotalCountsTheRest_AndOneBelowOneIsRefused()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "the notes to come back");
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        await RunBackupAndWaitAsync(runtime, handler);
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token);
        var snapshotId = Assert.ContainsSingle(source.Snapshots).SnapshotId;
        await RestoreAsync(handler, snapshotId, source.SourceId, "first");
        var second = await RestoreAsync(handler, snapshotId, source.SourceId, "second");

        var limited = await handler.ExecuteAsync(new ListRestoreReceiptsCommand(1), _timeout.Token);
        var refused = await handler.ExecuteAsync(new ListRestoreReceiptsCommand(0), _timeout.Token);

        Assert.IsInstanceOfType<RestoreReceiptsResult>(limited, out var listed);
        Assert.AreEqual(second.Run, Assert.ContainsSingle(listed.Receipts).Run);
        Assert.AreEqual(2, listed.Total);
        Assert.IsInstanceOfType<ServiceError>(refused, out var error);
        Assert.AreEqual(ServiceErrorReason.InvalidArgument, error.Reason);
    }

    [TestMethod]
    public async Task EachReceipt_IsAnsweredAsWhatBecameOfItsFiles_AndWhereItWrote()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteConfiguration("every 1h");
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        new FallbackPlan.Restore.RestoreReceipt
        {
            SchemaVersion = FallbackPlan.Restore.RestoreReceipt.CurrentSchemaVersion,
            SnapshotId = new string('e', 32),
            StartedAt = 1_699_999_990_000,
            CompletedAt = 1_700_000_000_000,
            Items =
            [
                new FallbackPlan.Restore.ReceiptItem { Path = "found.txt", Outcome = "restored", Bytes = 1, FoundInPlace = true },
                new FallbackPlan.Restore.ReceiptItem { Path = "written.txt", Outcome = "restored", Bytes = 1 },
                new FallbackPlan.Restore.ReceiptItem { Path = "kept.txt", Outcome = "skipped", Bytes = 1 },
                new FallbackPlan.Restore.ReceiptItem { Path = "lost.txt", Outcome = "failed", Bytes = 1 },
                new FallbackPlan.Restore.ReceiptItem { Path = "streams.txt", Outcome = "degraded", Bytes = 1 },
            ],
            Displaced = [],
            WrittenTo = "/srv/restored",
            Outcome = FallbackPlan.Restore.RestoreOutcome.Partial,
            Set = "photos",
        }.Keep(Path.Combine(_harness.StateDirectory, "receipts"), "0123456789abcdef");

        var answer = await handler.ExecuteAsync(new ListRestoreReceiptsCommand(), _timeout.Token);

        Assert.IsInstanceOfType<RestoreReceiptsResult>(answer, out var listed, (answer as ServiceError)?.Message);
        Assert.AreEqual(
            new RestoreReceiptDescriptor(
                "0123456789abcdef", "partial", 1_700_000_000_000, new string('e', 32), "/srv/restored",
                Restored: 2, FoundInPlace: 1, Skipped: 1, Failed: 1, Degraded: 1, Set: "photos"),
            Assert.ContainsSingle(listed.Receipts));
        Assert.AreEqual(1, listed.Total);
    }

    private async Task<RestoreResult> RestoreAsync(
        ServiceCommandHandler handler, string snapshotId, string sourceId, string folder)
    {
        var answer = await handler.ExecuteAsync(
            new RunRestoreCommand(snapshotId, null, Path.Combine(_harness.WorkPath, folder), Source: sourceId),
            _timeout.Token);
        Assert.IsInstanceOfType<RestoreResult>(answer, out var restored, (answer as ServiceError)?.Message);
        Assert.IsNotNull(restored.Run);
        return restored;
    }

    private static Task<HostHarness.Invocation> CliAsync(params string[] arguments) =>
        HostHarness.RunAsync(
            (a, o, e, c) => Cli.CliApplication.RunAsync(
                a, new InvocationConfiguration { Output = o, Error = e, EnableDefaultExceptionHandler = false }),
            arguments);

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

    private async Task<ServiceRuntime> StartAsync()
    {
        await _harness.SetupAsync();

        return await ServiceRuntime.StartAsync(
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
    }
}
