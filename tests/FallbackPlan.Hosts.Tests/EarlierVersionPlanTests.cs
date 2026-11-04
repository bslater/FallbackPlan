using FallbackPlan.Agent;
using FallbackPlan.Api;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A restore's plan, asked of the service, names each file it would restore
/// as a version backed up before, standing in for one the backup could not
/// fetch from its cloud provider, with what the backup said of it; and names
/// none where the backup fetched every one (ADR-0194, FR-RST-003).
/// </summary>
/// <remarks>
/// No runner has a cloud provider, so a stand-in source marks the files
/// named <c>.cloud</c> online-only and fails their fetch while told to.
/// </remarks>
[TestClass]
public sealed class EarlierVersionPlanTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));
    private bool _delivers = true;

    private CancellationToken Timeout => _timeout.Token;

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task ThePlan_NamesEachFileAnEarlierVersionStandsIn_AndWhy()
    {
        _harness.WriteConfiguration("every 1h");
        _harness.WriteSourceFile("docs/a.cloud", "alpha");
        _harness.WriteSourceFile("docs/b.cloud", "bravo");
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);
        Assert.IsNull((await PlanAsync(handler)).EarlierVersions, "a backup that fetched every one");

        _harness.WriteSourceFile("docs/a.cloud", "alpha, changed");
        _harness.WriteSourceFile("docs/new.cloud", "new, and never fetched");
        _delivers = false;
        await BackUpAsync(runtime, start.AddHours(1));

        var plan = await PlanAsync(handler);
        var earlier = Assert.ContainsSingle(plan.EarlierVersions!);
        Assert.AreEqual("docs/a.cloud", earlier.Path);
        Assert.Contains(CloudStandIn.TimedOut, earlier.Detail, StringComparison.Ordinal);
        Assert.Contains("stands in its place", earlier.Detail, StringComparison.Ordinal);
    }

    private static ulong Ms(DateTimeOffset at) => (ulong)at.ToUnixTimeMilliseconds();

    private async Task<RestorePlanResult> PlanAsync(ServiceCommandHandler handler)
    {
        Assert.IsInstanceOfType<SnapshotsResult>(await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var snapshots);
        var latest = snapshots.Snapshots.MaxBy(snapshot => snapshot.CapturedAt)!;
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, Timeout);
        Assert.IsInstanceOfType<RestorePlanResult>(
            await handler.ExecuteAsync(new PlanRestoreCommand(latest.SnapshotId, null, Source: source.SourceId), Timeout),
            out var plan);
        return plan;
    }

    private async Task BackUpAsync(ServiceRuntime runtime, DateTimeOffset now)
    {
        var set = runtime.Configuration.BackupSets.Single();
        var job = runtime.Jobs.Begin(set.Id, Ms(now));
        var outcome = await BackupRunner.RunAsync(
            runtime, set, job.Id, now, userInitiated: true, full: false, cancellationToken: Timeout);
        Assert.AreEqual("ran", outcome.Outcome, outcome.Detail);
    }

    private async Task<ServiceRuntime> StartAsync()
    {
        await _harness.SetupAsync();
        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                ChangeJournal = null,
                WrapSource = source => new CloudStandIn(source, () => _delivers),
            },
            Timeout);
    }
}
