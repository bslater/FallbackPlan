using System.CommandLine;
using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Domain.Jobs;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The receipts the service keeps are read back as direct mode's are
/// (FR-RST-004, ADR-0124): <c>restore-receipts</c> over the service's state
/// directory lists the run the service named, with what it did, and prints
/// that run's receipt whole as the service kept it.
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
        Assert.Contains($"snapshot {snapshotId}", listed.Output, StringComparison.Ordinal);
        Assert.Contains("1 restore receipt kept under", listed.Output, StringComparison.Ordinal);
        Assert.AreEqual(0, shown.ExitCode, shown.Error);
        Assert.AreEqual((await File.ReadAllTextAsync(restored.ReceiptPath)).Trim(), shown.Output.Trim());
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
