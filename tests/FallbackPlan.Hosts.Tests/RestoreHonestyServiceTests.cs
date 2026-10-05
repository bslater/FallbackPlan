using FallbackPlan.Agent;
using FallbackPlan.Api;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A restore commanded through the contract tells the truth about the disk it
/// writes to and the metadata it will not apply (FR-RST-003, FR-RST-004;
/// ADR-0083). Given the run's shape, the plan measures the volume the run
/// would write to against what is free there, and says when it will not fit.
/// The run refuses before it writes anything, unless it is told to restore
/// anyway. Both say which captured metadata will not be applied. The plan
/// gives counts; the result summarises the receipt, which names it per item.
/// </summary>
[TestClass]
public sealed class RestoreHonestyServiceTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    [TestMethod]
    public async Task Plan_NamingAFolderThatWillNotHoldTheRestore_SaysSoBeforeAnythingMoves()
    {
        await BackUpTwoFilesAsync();
        await using var runtime = await StartAsync(availableBytes: 1_000);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var snapshotId = await SnapshotIdAsync(handler);
        var destination = Path.Combine(_harness.WorkPath, "restored");

        Assert.IsInstanceOfType<RestorePlanResult>(
            await handler.ExecuteAsync(
                new PlanRestoreCommand(snapshotId, null, OutputDirectory: destination), _timeout.Token),
            out var plan);

        // One volume holds the folder and the engine's working directory, so
        // the need is one figure.
        Assert.IsNotNull(plan.Space);
        var space = Assert.ContainsSingle(plan.Space);
        Assert.AreEqual(destination, space.Directory);
        Assert.AreEqual(1_000L, space.AvailableBytes);
        Assert.IsGreaterThan(1_000L, space.NeededBytes);
        Assert.IsFalse(space.Working);

        // Said where a client that predates the figure already looks, too.
        Assert.IsGreaterThanOrEqualTo(1L, plan.Conflicts);
        Assert.IsNotNull(plan.ConflictSample);
        Assert.Contains(
            line => line.Contains(destination, StringComparison.Ordinal) && line.Contains("1000", StringComparison.Ordinal),
            plan.ConflictSample);

        Assert.IsFalse(Directory.Exists(destination), "a plan writes nothing");
    }

    [TestMethod]
    public async Task Run_ThatWillNotFit_IsRefused_AndWritesNothingAtAll()
    {
        await BackUpTwoFilesAsync();
        await using var runtime = await StartAsync(availableBytes: 1_000);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var snapshotId = await SnapshotIdAsync(handler);
        var destination = Path.Combine(_harness.WorkPath, "restored");
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(snapshotId, null, destination, Source: source.SourceId), _timeout.Token),
            out var refused);

        // A rule, not a failure: nothing ran.
        Assert.AreEqual(ServiceErrorReason.Refused, refused.Reason);
        Assert.Contains(destination, refused.Message, StringComparison.Ordinal);
        Assert.Contains("1000", refused.Message, StringComparison.Ordinal);

        Assert.IsFalse(Directory.Exists(destination), "a refused restore must not even create its folder");
        Assert.IsFalse(
            Directory.Exists(runtime.ReceiptsRoot) && Directory.EnumerateFileSystemEntries(runtime.ReceiptsRoot).Any(),
            "a restore that never ran has no receipt");
    }

    [TestMethod]
    public async Task Run_ToldToIgnoreFreeSpace_RestoresAnyway()
    {
        // The estimate counts what a restore writes, and a volume that
        // compresses what it stores holds more than that. A person who knows
        // theirs does must be able to proceed.
        await BackUpTwoFilesAsync();
        await using var runtime = await StartAsync(availableBytes: 1_000);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var snapshotId = await SnapshotIdAsync(handler);
        var destination = Path.Combine(_harness.WorkPath, "restored");
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token);

        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(snapshotId, null, destination, Source: source.SourceId, IgnoreFreeSpace: true),
                _timeout.Token),
            out var restored);

        Assert.AreEqual("complete", restored.Outcome);
        Assert.AreEqual(2, restored.Restored);
        Assert.AreEqual(
            "hello",
            await File.ReadAllTextAsync(Path.Combine(restored.OutputDirectory, "notes.txt"), _timeout.Token));
    }

    [TestMethod]
    public async Task Run_WithRoom_SummarisesWhatItDidNotApply_AndTheReceiptNamesItPerItem()
    {
        await BackUpTwoFilesAsync();
        await using var runtime = await StartAsync(availableBytes: null);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var snapshotId = await SnapshotIdAsync(handler);
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token);

        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(
                    snapshotId, null, Path.Combine(_harness.WorkPath, "restored"), Source: source.SourceId),
                _timeout.Token),
            out var restored);

        Assert.AreEqual("complete", restored.Outcome, "metadata alone does not change what a restore achieved");

        // An attribute every file here carries and no target writes back yet:
        // its owner on a POSIX host, its attribute bits on Windows. Access
        // times are written back everywhere now, so they are not listed.
        Assert.IsNotNull(restored.NotApplied);
        Assert.Contains(line => line.StartsWith(StillNotApplied, StringComparison.Ordinal), restored.NotApplied);
        Assert.DoesNotContain(line => line.StartsWith("accessed_at", StringComparison.Ordinal), restored.NotApplied);

        var receipt = await File.ReadAllTextAsync(restored.ReceiptPath!, _timeout.Token);
        Assert.Contains("\"not_applied\"", receipt, StringComparison.Ordinal);
        Assert.Contains($"\"{StillNotApplied}\"", receipt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"accessed_at\"", receipt, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Plan_OverRealFiles_DeclaresTheCapturedMetadataItWillNotApply_WithCounts()
    {
        await BackUpTwoFilesAsync();
        await using var runtime = await StartAsync(availableBytes: null);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var snapshotId = await SnapshotIdAsync(handler);

        Assert.IsInstanceOfType<RestorePlanResult>(
            await handler.ExecuteAsync(new PlanRestoreCommand(snapshotId, null), _timeout.Token), out var plan);

        Assert.IsNotNull(plan.Degradations);
        var declared = OperatingSystem.IsWindows() ? "File attributes" : "Ownership";
        Assert.Contains(
            line => line.Contains(declared, StringComparison.Ordinal) && line.Contains("2 file(s)", StringComparison.Ordinal),
            plan.Degradations);
        Assert.DoesNotContain(line => line.Contains("Access times", StringComparison.Ordinal), plan.Degradations);

        // With no folder named there is nothing to measure against, so the
        // plan answers as it always did, plus what its files will write.
        Assert.IsNull(plan.Space);
        Assert.AreEqual(plan.Bytes, plan.WriteBytes);
    }

    /// <summary>What every file here carries and no target writes back yet.</summary>
    private static string StillNotApplied => OperatingSystem.IsWindows() ? "file_attributes" : "owner";

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    private async Task BackUpTwoFilesAsync()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        _harness.WriteSourceFile("nested/deeper.txt", "deeper");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");
    }

    private async Task<string> SnapshotIdAsync(ServiceCommandHandler handler)
    {
        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token), out var snapshots);
        return Assert.ContainsSingle(snapshots.Snapshots).SnapshotId;
    }

    private async Task<ServiceRuntime> StartAsync(long? availableBytes)
    {
        await _harness.SetupAsync();

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                // The fixture's paths share one real volume; the vaults are
                // told apart by name, the compliant install's shape. The
                // restore folder and the engine's working directory share
                // the other.
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
                AvailableBytesProbe = _ => availableBytes,
            },
            _timeout.Token);
    }
}
