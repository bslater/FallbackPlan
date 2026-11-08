using FallbackPlan.Agent;
using FallbackPlan.Api;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A restore's plan, asked of the service, names each file it would restore
/// as a version backed up before, standing in for one the backup could not
/// fetch from its cloud provider, with what the backup said of it; and names
/// none where the backup fetched every one (ADR-0194, FR-RST-003). The run
/// says how many it restored so, and its receipt names each with what the
/// backup said of it (ADR-0194 Amendment 1, FR-RST-004), read from the copy
/// that holds them where the set's own copy does not (FR-RST-007). The
/// service's listing of the receipts it keeps counts them (ADR-0194
/// Amendment 2). The plan names each file below a folder whose listing did
/// not come, kept as the backup before held it, with what the backup said of
/// the folder (ADR-0194 Amendment 3).
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

    [TestMethod]
    public async Task ThePlan_NamesEachFileBelowAFolderKeptFromBefore()
    {
        _harness.WriteConfiguration("every 1h");
        _harness.WriteSourceFile("drive.cloud/a.txt", "alpha");
        _harness.WriteSourceFile("drive.cloud/deep/b.txt", "bravo");
        _harness.WriteSourceFile("here.txt", "here");
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);

        _delivers = false;
        await BackUpAsync(runtime, start.AddHours(1));

        var earlier = (await PlanAsync(handler)).EarlierVersions!;
        CollectionAssert.AreEqual(new[] { "drive.cloud/a.txt", "drive.cloud/deep/b.txt" }, earlier.Select(version => version.Path).ToArray());
        foreach (var version in earlier)
        {
            Assert.AreEqual(
                $"its folder drive.cloud: online-only, and listing it failed: {CloudStandIn.TimedOut}; the folder as backed up earlier stands in its place",
                version.Detail);
        }
    }

    [TestMethod]
    public async Task TheRun_SaysHowManyItRestoredAsTheVersionBefore_AndItsReceiptNamesEachAndWhy()
    {
        _harness.WriteConfiguration("every 1h");
        _harness.WriteSourceFile("docs/a.cloud", "alpha");
        _harness.WriteSourceFile("docs/b.cloud", "bravo");
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);
        var clean = await RestoreAsync(handler, "clean");
        Assert.AreEqual(0, clean.Result.EarlierVersions, "a backup that fetched every one");
        Assert.IsNull(clean.Result.EarlierVersionsSample);
        Assert.IsTrue(clean.Receipt.Items.All(item => item.EarlierVersion is null));

        _harness.WriteSourceFile("docs/a.cloud", "alpha, changed");
        _delivers = false;
        await BackUpAsync(runtime, start.AddHours(1));

        var (result, receipt) = await RestoreAsync(handler, "earlier");
        Assert.AreEqual("complete", result.Outcome, "a file an earlier version stands in for restores");
        Assert.AreEqual(1, result.EarlierVersions);
        var line = Assert.ContainsSingle(result.EarlierVersionsSample!);
        Assert.StartsWith("docs/a.cloud — ", line, StringComparison.Ordinal);
        Assert.Contains(CloudStandIn.TimedOut, line, StringComparison.Ordinal);

        var restored = receipt.Items.Single(item => item.Path == "docs/a.cloud");
        Assert.Contains("stands in its place", restored.EarlierVersion!, StringComparison.Ordinal);
        Assert.IsNull(receipt.Items.Single(item => item.Path == "docs/b.cloud").EarlierVersion);
        Assert.IsFalse(receipt.EarlierVersionsUnread);
        var written = Assert.ContainsSingle(Directory.GetFiles(Path.Combine(_harness.WorkPath, "earlier"), "a.cloud", SearchOption.AllDirectories));
        Assert.AreEqual("alpha", await File.ReadAllTextAsync(written, Timeout), "the version backed up before");

        // The service's listing of the receipts it keeps counts them (ADR-0194 Amendment 2).
        Assert.IsInstanceOfType<RestoreReceiptsResult>(await handler.ExecuteAsync(new ListRestoreReceiptsCommand(), Timeout), out var kept);
        Assert.AreEqual(1L, kept.Receipts.Single(listed => listed.Run == Path.GetFileNameWithoutExtension(result.ReceiptPath!)).EarlierVersions);
        Assert.AreEqual(0L, kept.Receipts.Single(listed => listed.Run == Path.GetFileNameWithoutExtension(clean.Result.ReceiptPath!)).EarlierVersions);
    }

    [TestMethod]
    public async Task ARunWhoseOwnCopyLostTheRecordsThatSaySo_NamesThemFromTheCopyThatHoldsThem()
    {
        _harness.WriteConfiguration("every 1h");
        _harness.WriteSourceFile("docs/a.cloud", "alpha");
        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var start = DateTimeOffset.Now;
        await BackUpAsync(runtime, start);
        var before = MetadataBlobs();

        _harness.WriteSourceFile("docs/a.cloud", "alpha, changed");
        _delivers = false;
        await BackUpAsync(runtime, start.AddHours(1));
        Directory.CreateDirectory(Path.Combine(_harness.StateDirectory, "vault"));
        var pass = await Scheduler.RunPassAsync(runtime, start.AddHours(1), Timeout);
        await pass.Transfers.WaitAsync(Timeout);

        // The staging archive no longer holds what the second backup wrote of
        // its metadata, the snapshot's manifest and its failures among it; the
        // vault does, and the run reads it there as it reads around anything
        // its own copy will not serve (FR-RST-007).
        var lost = MetadataBlobs().Except(before, StringComparer.Ordinal).ToList();
        Assert.IsNotEmpty(lost, "the second backup wrote metadata of its own");
        lost.ForEach(File.Delete);

        var (result, receipt) = await RestoreAsync(handler, "around");
        Assert.AreEqual(1, result.EarlierVersions, string.Join(" | ", result.FailedSample ?? []));
        Assert.Contains("stands in its place", receipt.Items.Single(item => item.Path == "docs/a.cloud").EarlierVersion!, StringComparison.Ordinal);
        Assert.IsFalse(receipt.EarlierVersionsUnread);
    }

    private string[] MetadataBlobs()
    {
        var root = Path.Combine(_harness.RepositoryPath, "blobs", "meta");
        return Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories) : [];
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

    private async Task<(RestoreResult Result, Restore.RestoreReceipt Receipt)> RestoreAsync(ServiceCommandHandler handler, string folder)
    {
        Assert.IsInstanceOfType<SnapshotsResult>(await handler.ExecuteAsync(new ListSnapshotsCommand(), Timeout), out var snapshots);
        var latest = snapshots.Snapshots.MaxBy(snapshot => snapshot.CapturedAt)!;
        var source = await _harness.OpenGrantedSourceAsync(handler.ExecuteAsync, "docs", null, Timeout);
        Assert.IsInstanceOfType<RestoreResult>(
            await handler.ExecuteAsync(
                new RunRestoreCommand(latest.SnapshotId, null, Path.Combine(_harness.WorkPath, folder), Source: source.SourceId), Timeout),
            out var result);
        var kept = result.ReceiptPath is { } path
            ? Restore.RestoreReceipt.ReadKept(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path))
            : null;
        return (result, kept?.Receipt ?? throw new AssertFailedException($"the run kept a receipt that reads: {kept?.Problem}"));
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

                // The fixture's every path shares one real volume: the vault
                // is told apart by name, so the set may keep a copy there
                // (ADR-0051).
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
            },
            Timeout);
    }
}
