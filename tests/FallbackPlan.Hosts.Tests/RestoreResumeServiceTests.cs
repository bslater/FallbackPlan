using System.Text.Json;
using FallbackPlan.Agent;
using FallbackPlan.Api;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A restore cut short is resumed through the service (FR-RST-008,
/// ADR-0109). Each run lands in a quarantine folder of its own, so a resume
/// names the run it continues by that folder's name. It lands there, leaves
/// each file already holding the snapshot's content as it stands, lands the
/// rest, and says how many it found in place, as its receipt does file by
/// file. It is measured for what it still writes. A name that is not a run's,
/// or a run whose folder is not there, is refused before anything is written.
/// </summary>
[TestClass]
public sealed class RestoreResumeServiceTests : IDisposable
{
    private const string NoSuchRun = "0123456789abcdef";

    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));
    private long? _available;

    [TestMethod]
    public async Task Resume_ContinuesTheNamedRunInItsFolder_AndLeavesWhatLanded()
    {
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var destination = Path.Combine(_harness.WorkPath, "restored");
        var first = await RestoreAsync(handler, destination);
        Assert.AreEqual("complete", first.Outcome);

        // Cut short before its second file.
        File.Delete(Path.Combine(first.OutputDirectory, "nested", "deeper.txt"));

        var resumed = await RestoreAsync(handler, destination, resume: Path.GetFileName(first.OutputDirectory));

        Assert.AreEqual("complete", resumed.Outcome);
        Assert.AreEqual(first.OutputDirectory, resumed.OutputDirectory, "the resumed run lands where the run it continues landed");
        Assert.AreEqual(2, resumed.Restored);
        Assert.AreEqual(1, resumed.FoundInPlace);
        Assert.AreEqual(0, resumed.Displaced);
        Assert.AreEqual("deeper", await File.ReadAllTextAsync(Path.Combine(first.OutputDirectory, "nested", "deeper.txt"), _timeout.Token));
        Assert.ContainsSingle(Directory.EnumerateDirectories(Path.Combine(destination, ".fbp-quarantine")));

        // The receipt says which one.
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(resumed.ReceiptPath!, _timeout.Token));
        var found = receipt.RootElement.GetProperty("items").EnumerateArray()
            .Where(item => item.TryGetProperty("found_in_place", out var flag) && flag.GetBoolean())
            .Select(item => item.GetProperty("path").GetString())
            .ToList();
        Assert.AreEqual("notes.txt", Assert.ContainsSingle(found));
        Assert.AreNotEqual(first.ReceiptPath, resumed.ReceiptPath, "a resumed run is a run of its own, with a receipt of its own");
    }

    [TestMethod]
    public async Task Resume_WhoseRemainderFits_IsNotRefusedForWhatLanded()
    {
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var destination = Path.Combine(_harness.WorkPath, "restored");
        var first = await RestoreAsync(handler, destination);

        _available = 1_000;

        var resumed = await RestoreAsync(handler, destination, resume: Path.GetFileName(first.OutputDirectory));
        Assert.AreEqual("complete", resumed.Outcome);
        Assert.AreEqual(2, resumed.FoundInPlace);

        Assert.IsInstanceOfType<ServiceError>(await AskAsync(handler, destination, resume: null), out var fresh);
        Assert.AreEqual(ServiceErrorReason.Refused, fresh.Reason, "a run that resumes nothing needs room for every file");
    }

    [TestMethod]
    public async Task Resume_OfARunWhoseFolderIsNotThere_IsRefused_AndWritesNothing()
    {
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var destination = Path.Combine(_harness.WorkPath, "restored");

        Assert.IsInstanceOfType<ServiceError>(await AskAsync(handler, destination, NoSuchRun), out var refused);

        Assert.AreEqual(ServiceErrorReason.NotFound, refused.Reason);
        Assert.Contains(NoSuchRun, refused.Message, StringComparison.Ordinal);
        Assert.IsFalse(Directory.Exists(destination), "a refused resume must not even create its folder");
    }

    [TestMethod]
    [DataRow("../../escaped")]
    [DataRow("0123456789ABCDEF")]
    [DataRow("0123456789abcde")]
    [DataRow("0123456789abcdeg")]
    public async Task Resume_NamingWhatIsNotARun_IsRefused_AndWritesNothing(string named)
    {
        // A run is named by its sixteen lowercase hex digits, and the name
        // becomes a folder's: nothing else may reach the path.
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var destination = Path.Combine(_harness.WorkPath, "restored");

        Assert.IsInstanceOfType<ServiceError>(await AskAsync(handler, destination, named), out var refused);

        Assert.AreEqual(ServiceErrorReason.InvalidArgument, refused.Reason);
        Assert.IsFalse(Directory.Exists(destination));
    }

    [TestMethod]
    public async Task Resume_UnderTheRenamePolicy_IsRefused()
    {
        // A copy written beside a kept file is named for the day it was
        // written, so a resume cannot tell its own from anybody's.
        await using var runtime = await BackUpAndStartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var destination = Path.Combine(_harness.WorkPath, "restored");
        var first = await RestoreAsync(handler, destination);
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(
                    await SnapshotIdAsync(handler), null, destination, Source: source.SourceId,
                    Existing: "rename", Resume: Path.GetFileName(first.OutputDirectory)),
                _timeout.Token),
            out var refused);

        Assert.AreEqual(ServiceErrorReason.InvalidArgument, refused.Reason);
        Assert.Contains("rename", refused.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    private async Task<RestoreResult> RestoreAsync(ServiceCommandHandler handler, string destination, string? resume = null)
    {
        var answer = await AskAsync(handler, destination, resume);
        Assert.IsInstanceOfType<RestoreResult>(answer, out var restored, (answer as ServiceError)?.Message);
        return restored;
    }

    private async Task<ServiceResult> AskAsync(ServiceCommandHandler handler, string destination, string? resume)
    {
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token);
        return await handler.ExecuteAsync(
            new RunRestoreCommand(await SnapshotIdAsync(handler), null, destination, Source: source.SourceId, Resume: resume),
            _timeout.Token);
    }

    private async Task<string> SnapshotIdAsync(ServiceCommandHandler handler)
    {
        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token), out var snapshots);
        return Assert.ContainsSingle(snapshots.Snapshots).SnapshotId;
    }

    private async Task<ServiceRuntime> BackUpAndStartAsync()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "hello");
        _harness.WriteSourceFile("nested/deeper.txt", "deeper");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");
        await _harness.SetupAsync();

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,

                // Asked at each run, so a test can say what is free between two.
                AvailableBytesProbe = _ => _available,
            },
            _timeout.Token);
    }
}
