using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A restore drill run because a person asked (FR-DRL-003, ADR-0054
/// Amendment 6): the same drill the schedule runs, at once and inside the
/// pair's interval, recorded on the pair's row and announced exactly as a
/// scheduled one is, so a passing drill clears the notice a failed one
/// raised. A pair with nothing there to restore is said and not drilled; a
/// pair being drilled already is joined rather than drilled twice; and the
/// drill belongs to the service, so the person who asked for it giving up
/// does not cut it short.
/// </summary>
/// <remarks>
/// Every set here is write-only, the only shape setup produces, so a drill
/// that passes states the sealed-content limit (ADR-0054 Amendment 2).
/// </remarks>
[TestClass]
public sealed class DrillNowTests : IDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(5));

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task DrillNow_AConvergedDestination_IsDrilledAtOnce_InsideItsInterval_AndRecordedAsTheScheduleRecordsIt()
    {
        await using var runtime = await StartDrilledAsync();
        var before = Pair(runtime);

        // The pass drilled the pair moments ago, so the schedule would leave
        // it for thirty days. A person asking is the one reason not to wait.
        var drilled = await DrillAsync(runtime, new RunDrillCommand("docs", "vault"));

        var after = Pair(runtime);
        Assert.IsGreaterThan(before.DrilledAt!.Value, after.DrilledAt!.Value, "a person's drill runs inside the interval");
        Assert.IsNull(after.DrillFailure, after.DrillFailure);
        Assert.IsGreaterThan(0, after.DrillFiles, "a drill that reached no file has proved nothing");
        Assert.AreEqual(0, drilled.Failed);
        Assert.AreEqual(0, drilled.NotDrilled);

        var line = Assert.ContainsSingle(drilled.Lines);
        Assert.Contains("docs -> vault", line, StringComparison.Ordinal);
        Assert.Contains("sealed", line, StringComparison.OrdinalIgnoreCase);
        Assert.IsEmpty(DrillNotices(runtime));

        // Recorded where the schedule records it, so every surface that
        // reports a drill reports this one.
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<StatusResult>(await handler.ExecuteAsync(new GetStatusCommand(), Timeout), out var status);
        var row = Assert.ContainsSingle(Assert.ContainsSingle(status.Sets).Destinations);
        Assert.AreEqual(after.DrilledAt, row.DrilledAt);
        Assert.AreEqual(after.DrillLimit, row.DrillLimit);
    }

    [TestMethod]
    public async Task DrillNow_APassingDrill_ClearsTheNoticeAFailedOneRaised_AndEndsItsCount()
    {
        // ADR-0054 Amendment 4 named the gap: with no drill-now verb, a fault
        // that passed in a minute left its notice standing until the
        // schedule came round again. The failure here is a drill's own,
        // recorded and announced a few minutes ago; nothing has synced since,
        // so the schedule would wait its whole interval before trying again.
        await using var runtime = await StartDrilledAsync();
        var earlier = (ulong)DateTimeOffset.Now.AddMinutes(-5).ToUnixTimeMilliseconds();
        runtime.DestinationSync.RecordDrill(
            _harness.DocsSetId, "vault", files: 0, bytes: 0, "the replica would not open: the disk was unplugged.",
            limit: null, earlier);
        runtime.Notices.Raise(
            $"drill-failed:{_harness.DocsSetId}:vault",
            "A restore drill against 'vault' could not bring back a file from set 'docs': "
            + "the replica would not open: the disk was unplugged.",
            earlier);
        Assert.AreEqual(1, Pair(runtime).ConsecutiveFailedDrills, "the premise: one failed drill on the row");
        Assert.IsNotEmpty(DrillNotices(runtime));

        var drilled = await DrillAsync(runtime, new RunDrillCommand("docs", "vault"));

        Assert.AreEqual(0, drilled.Failed, string.Join(" | ", drilled.Lines));
        var record = Pair(runtime);
        Assert.IsGreaterThan(earlier, record.DrilledAt!.Value);
        Assert.IsNull(record.DrillFailure, record.DrillFailure);
        Assert.AreEqual(0, record.ConsecutiveFailedDrills, "a drill that passes ends the count");
        Assert.IsEmpty(DrillNotices(runtime), "and its notice clears");
    }

    [TestMethod]
    public async Task DrillNow_AReplicaThatWillNotRestore_FailsAndIsRecordedAndAnnounced_InTheDrillsOwnWords()
    {
        await using var runtime = await StartDrilledAsync();

        // Length-preserving rot in the content plane of the only copy there is.
        TamperEveryDataBlob(Assert.ContainsSingle(Directory.GetDirectories(Vault)));

        // Every set, every destination: the verb's widest form.
        var drilled = await DrillAsync(runtime, new RunDrillCommand(null, null));

        Assert.AreEqual(1, drilled.Failed, "the count an exit code reads, never the prose");
        Assert.AreEqual(0, drilled.NotDrilled);
        var record = Pair(runtime);
        Assert.IsNotNull(record.DrillFailure, "a drill that restored nothing is not a drill that passed");
        Assert.AreEqual(0, record.DrillFiles);

        var line = Assert.ContainsSingle(drilled.Lines);
        Assert.Contains("could not restore", line, StringComparison.Ordinal);
        Assert.Contains(record.DrillFailure, line, StringComparison.Ordinal);
        Assert.ContainsSingle(DrillNotices(runtime));
    }

    [TestMethod]
    public async Task DrillNow_ASetWithNoBackupYet_IsNotDrilled_AndNothingIsRecorded()
    {
        // ADR-0054 Amendment 5's rule holds for a person's ask too: there is
        // nothing there to restore, and drilling would record a failure about
        // an absence that is correct.
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: true);
        _harness.WriteSourceFile("docs/content.txt", "not captured yet");

        await using var runtime = await StartAsync();

        var drilled = await DrillAsync(runtime, new RunDrillCommand("docs", "vault"));

        Assert.AreEqual(0, drilled.Failed);
        Assert.AreEqual(1, drilled.NotDrilled, "said, and counted apart from a failure");
        var line = Assert.ContainsSingle(drilled.Lines);
        Assert.Contains("not drilled", line, StringComparison.Ordinal);
        Assert.Contains("no backup", line, StringComparison.Ordinal);
        Assert.IsNull(runtime.DestinationSync.Find(_harness.DocsSetId, "vault")?.DrilledAt);
        Assert.IsEmpty(DrillNotices(runtime));
    }

    [TestMethod]
    public async Task DrillNow_ADestinationNothingHasReached_IsNotDrilled_AndIsNotBlamed_WhileItsSiblingIs()
    {
        await using var runtime = await StartDrilledAsync();

        // A second destination joins the set after its backup, and no pass
        // has copied anything there yet.
        var spare = Path.Combine(_harness.WorkPath, "spare");
        Directory.CreateDirectory(spare);
        WriteConfiguration(directShip: true, spare: spare);

        var drilled = await DrillAsync(runtime, new RunDrillCommand("docs", null));

        Assert.HasCount(2, drilled.Lines);
        Assert.AreEqual(0, drilled.Failed, string.Join(" | ", drilled.Lines));
        Assert.AreEqual(1, drilled.NotDrilled);
        var spareLine = Assert.ContainsSingle(drilled.Lines.Where(line => line.Contains("-> spare", StringComparison.Ordinal)));
        Assert.Contains("not drilled", spareLine, StringComparison.Ordinal);
        Assert.Contains("nothing has been copied there", spareLine, StringComparison.Ordinal);
        Assert.IsNull(runtime.DestinationSync.Find(_harness.DocsSetId, "spare")?.DrilledAt);
        Assert.IsEmpty(DrillNotices(runtime));
    }

    [TestMethod]
    public async Task DrillNow_WhileThePairIsBeingDrilled_JoinsThatDrill_RatherThanStartingAnother()
    {
        // One drill per pair at a time. The drill under way is the schedule's,
        // held at its first step and then refused there, so its answer is
        // one no fresh drill of this clean replica could give: a person whose
        // ask joined it is told that answer, and the row holds one drill.
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = await StartDrilledAsync((_, _) => joined.TrySetResult());

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var at = DateTimeOffset.Now;
        var scheduled = RecoveryDrillJob.RunAsync(
            runtime,
            new Interposed(
                new ServiceCommandHandler(runtime, RemoteBindingState.Off, CallerScope.Service),
                command => command is OpenRestoreSourceCommand,
                async () =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(Timeout);
                    return new ServiceError(ServiceErrorReason.Failed, "held at the gate, then refused");
                }),
            runtime.Configuration.BackupSets[0], "vault", (ulong)at.ToUnixTimeMilliseconds(),
            budget: null, Random.Shared, Timeout);
        await entered.Task.WaitAsync(Timeout);

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var asked = handler.ExecuteAsync(new RunDrillCommand("docs", "vault"), Timeout).AsTask();
        await joined.Task.WaitAsync(Timeout);
        release.SetResult();

        Assert.IsInstanceOfType<DrillResult>(await asked, out var drilled);
        var outcome = await scheduled;
        Assert.Contains("held at the gate", outcome.Failure!, StringComparison.Ordinal);
        Assert.AreEqual(1, drilled.Failed, "the person is told the answer of the drill they joined");
        Assert.Contains("held at the gate", Assert.ContainsSingle(drilled.Lines), StringComparison.Ordinal);

        var record = Pair(runtime);
        Assert.AreEqual((ulong)at.ToUnixTimeMilliseconds(), record.DrilledAt, "one drill, stamped by the drill under way");
        Assert.AreEqual(1, record.ConsecutiveFailedDrills, "and counted once");
        Assert.ContainsSingle(DrillNotices(runtime));
    }

    [TestMethod]
    public async Task DrillNow_ThePersonWhoAskedGivingUp_DoesNotCutTheDrillShort()
    {
        // The drill belongs to the service, as an on-demand sync does: the
        // person stops waiting and is answered cancelled, and the drill they
        // started finishes and is recorded. Every step of a drill queues on
        // the reader lane, so holding that lane holds the drill under way.
        await using var runtime = await StartDrilledAsync();
        var before = Pair(runtime);

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.IsTrue(runtime.Queue.Enqueue(new QueuedJob(
            "hold-" + Guid.NewGuid().ToString("n"), JobLane.Reader, UserInitiated: true, "hold the reader lane",
            async token => await release.Task.WaitAsync(token))));

        using var asker = CancellationTokenSource.CreateLinkedTokenSource(Timeout);
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var asked = handler.ExecuteAsync(new RunDrillCommand("docs", "vault"), asker.Token).AsTask();
        await WaitUntilAsync(() => runtime.Drills.IsDrilling(_harness.DocsSetId, "vault"));

        await asker.CancelAsync();
        Assert.IsInstanceOfType<ServiceError>(await asked, out var gaveUp);
        Assert.AreEqual(ServiceErrorReason.Cancelled, gaveUp.Reason);

        release.SetResult();
        await WaitUntilAsync(() => !runtime.Drills.IsDrilling(_harness.DocsSetId, "vault"));

        var after = Pair(runtime);
        Assert.IsGreaterThan(before.DrilledAt!.Value, after.DrilledAt!.Value, "the drill finished and was recorded");
        Assert.IsNull(after.DrillFailure, after.DrillFailure);
    }

    [TestMethod]
    public async Task DrillNow_WhileTheServiceStops_IsAnsweredCancelled_AndStatesNothing()
    {
        // ADR-0054 Amendments 1 and 4: a drill cut short by the service
        // stopping says nothing about the replica, whoever asked for it.
        await using var runtime = await StartDrilledAsync();
        var before = Pair(runtime);
        await runtime.Queue.DisposeAsync();

        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new RunDrillCommand("docs", "vault"), Timeout), out var refused);

        Assert.AreEqual(ServiceErrorReason.Cancelled, refused.Reason);
        var after = Pair(runtime);
        Assert.AreEqual(before.DrilledAt, after.DrilledAt, "the last completed drill's answer stands");
        Assert.AreEqual(before.DrillFailure, after.DrillFailure);
        Assert.IsEmpty(DrillNotices(runtime));
    }

    [TestMethod]
    public async Task DrillNow_AnUnknownSetOrDestination_IsNotFound()
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: true);

        await using var runtime = await StartAsync();
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new RunDrillCommand("nonesuch", null), Timeout), out var noSet);
        Assert.AreEqual(ServiceErrorReason.NotFound, noSet.Reason);
        Assert.Contains("nonesuch", noSet.Message, StringComparison.Ordinal);

        Assert.IsInstanceOfType<ServiceError>(
            await handler.ExecuteAsync(new RunDrillCommand("docs", "nowhere"), Timeout), out var noDestination);
        Assert.AreEqual(ServiceErrorReason.NotFound, noDestination.Reason);
        Assert.Contains("nowhere", noDestination.Message, StringComparison.Ordinal);
    }

    private async Task<DrillResult> DrillAsync(ServiceRuntime runtime, RunDrillCommand command)
    {
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        var answer = await handler.ExecuteAsync(command, Timeout);
        Assert.IsInstanceOfType<DrillResult>(answer, out var drilled, (answer as ServiceError)?.Message);
        return drilled;
    }

    private async Task WaitUntilAsync(Func<bool> condition)
    {
        while (!condition())
        {
            await Task.Delay(20, Timeout);
        }
    }

    private DestinationSyncRecord Pair(ServiceRuntime runtime) =>
        runtime.DestinationSync.Find(_harness.DocsSetId, "vault")!;

    private static IEnumerable<Notice> DrillNotices(ServiceRuntime runtime) =>
        runtime.Notices.Unacknowledged.Where(notice => notice.Key.StartsWith("drill-failed:", StringComparison.Ordinal));

    private static void TamperEveryDataBlob(string replicaRoot)
    {
        var files = Directory.GetFiles(
            Path.Combine(replicaRoot, "blobs", "data"), "*", SearchOption.AllDirectories);
        Assert.IsNotEmpty(files, "the destination must hold data blobs for this to test anything");

        foreach (var path in files)
        {
            var bytes = File.ReadAllBytes(path);
            for (var i = 200; i < bytes.Length; i++)
            {
                bytes[i] ^= 0xFF;
            }

            File.WriteAllBytes(path, bytes);
        }
    }

    private void WriteConfiguration(bool directShip, string? spare = null)
    {
        List<DestinationConfiguration> destinations =
        [
            new() { Id = new string('d', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = Vault },
        ];
        List<SetDestinationReference> references = [new() { Ref = "vault" }];
        if (spare is not null)
        {
            destinations.Add(new() { Id = new string('e', 32), Name = "spare", Kind = DestinationKind.LocalPath, Path = spare });
            references.Add(new() { Ref = "spare" });
        }

        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations = destinations,
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = _harness.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                    Schedule = "every 1h",
                    Destinations = references,
                    DirectShip = directShip,
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));
    }

    /// <summary>A set-up installation whose one pair has converged and passed a clean scheduled drill.</summary>
    private async Task<ServiceRuntime> StartDrilledAsync(Action<string, string>? drillJoined = null)
    {
        Directory.CreateDirectory(Vault);
        WriteConfiguration(directShip: true);
        _harness.WriteSourceFile("docs/content.txt", new string('c', 90_000) + "the bytes a restore needs");

        var runtime = await StartAsync(drillJoined);
        try
        {
            var first = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
            await first.Transfers.WaitAsync(Timeout);
            await first.Drills.WaitAsync(Timeout);

            var drilled = Pair(runtime);
            Assert.IsNotNull(drilled.DrilledAt, $"the clean drill must pass first: {drilled.DrillFailure}");
            Assert.IsNull(drilled.DrillFailure);
            return runtime;
        }
        catch
        {
            await runtime.DisposeAsync();
            throw;
        }
    }

    private async Task<ServiceRuntime> StartAsync(Action<string, string>? drillJoined = null)
    {
        await _harness.SetupAsync();

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                DrillJoined = drillJoined,
            },
            Timeout);
    }

    /// <summary>
    /// The service a drill talks to, with a fault in the way of the first
    /// command the predicate picks; everything else goes through.
    /// </summary>
    private sealed class Interposed(
        IFallbackPlanService service, Func<ServiceCommand, bool> picks, Func<ValueTask<ServiceResult>> fault)
        : IFallbackPlanService
    {
        private int _struck;

        public ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken) =>
            picks(command) && Interlocked.Exchange(ref _struck, 1) == 0
                ? fault()
                : service.ExecuteAsync(command, cancellationToken);

        public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
            service.WatchAsync(cancellationToken);
    }
}
