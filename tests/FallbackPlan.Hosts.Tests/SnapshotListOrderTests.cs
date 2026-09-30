using FallbackPlan.Agent;
using FallbackPlan.Api;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The two orders the contract answers snapshots in, and why they differ:
/// <c>list_snapshots</c> gives each set's snapshots newest first, and
/// <c>open_restore_source</c> gives a source's oldest first, because the
/// restore wizard reads it as a timeline and takes the last row at or before
/// the date a person picked (FR-RST-001).
/// </summary>
/// <remarks>
/// Both orders were right and one of their comments was wrong. The result
/// record called <c>list_snapshots</c> oldest first, and the restore source
/// claimed to match it, so the console reversed a list that was already
/// newest first. These tests pin what each verb actually answers, so the
/// order is a stated contract and no longer a reading of the catalogue's
/// query.
/// </remarks>
[TestClass]
public sealed class SnapshotListOrderTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    [TestMethod]
    public async Task ListSnapshots_ASetsSnapshots_ComeNewestFirst()
    {
        await using var runtime = await TwoCapturesAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<SnapshotsResult>(
            await handler.ExecuteAsync(new ListSnapshotsCommand(), _timeout.Token), out var listed);

        Assert.HasCount(2, listed.Snapshots);
        Assert.IsTrue(
            listed.Snapshots[0].CapturedAt > listed.Snapshots[1].CapturedAt,
            "the newest capture comes first, as the CLI prints it and the snapshot browser's older/newer steps read it");
    }

    [TestMethod]
    public async Task OpenRestoreSource_ASourcesSnapshots_ComeOldestFirst()
    {
        await using var runtime = await TwoCapturesAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, _timeout.Token);

        Assert.HasCount(2, source.Snapshots);
        Assert.IsTrue(
            source.Snapshots[0].CapturedAt < source.Snapshots[1].CapturedAt,
            "the wizard takes the last snapshot at or before a date, and calls the first one the earliest");
    }

    /// <summary>The "docs" set with two captures, the second after its content changed.</summary>
    private async Task<ServiceRuntime> TwoCapturesAsync()
    {
        await _harness.CreateRepositoryAsync();
        _harness.WriteSourceFile("notes.txt", "the first version");
        await _harness.BackUpAsync();
        _harness.WriteSourceFile("notes.txt", "the second version");
        await _harness.BackUpAsync();
        _harness.WriteConfiguration("every 1h");

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                // The placement condition (ADR-0051) judges by volume, and the
                // fixture's every path shares one real volume — the vault is
                // told apart by name, the compliant install's shape.
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
            },
            _timeout.Token);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }
}
