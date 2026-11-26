using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// When the service stops, nothing it started in the background outlives the
/// stop (FR-SVC-017, ADR-0049 Amendment 2). A drill, scheduled or a
/// person's, runs inline rather than as a job on the queue, and a pass hands
/// back its drill phase unawaited, so draining the queue waited for neither:
/// a drill under way went on after the service had let go of the archives it
/// reads, and could open the set's archive again. Disposing the runtime now
/// tells them to stop and returns only once they have ended, and a drill the
/// stop cuts short states nothing.
/// </summary>
/// <remarks>
/// A drill is held at its first ask of the service, before it opens a source
/// to restore from, and a pass's drill phase at its start, before it asks
/// whether the set holds a snapshot: the only waits the stop made before were
/// for queued jobs and for a restore source in use. Held deaf to its token,
/// the work shows the stop waits for it; held on its token, that the stop
/// tells it to end.
/// </remarks>
[TestClass]
public sealed class BackgroundWorkAtStopTests : IAsyncDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));
    private readonly DateTimeOffset _start = DateTimeOffset.Now;
    private readonly SemaphoreSlim _underWay = new(0);
    private readonly SemaphoreSlim _phaseBegun = new(0);
    private bool _holdThePhase;
    private bool _holdOnToken;
    private readonly ManualResetEventSlim _release = new();

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    [TestMethod]
    public async Task StoppingTheService_WhileADrillIsUnderWay_ReturnsOnlyOnceItHasEnded_AndTheDrillStatesNothing()
    {
        var runtime = await StartAsync();
        try
        {
            await PassAsync(runtime, _start);
            var drilledBefore = Row(runtime).DrilledAt;
            Assert.IsNotNull(drilledBefore, "the premise: the pass drilled the vault");

            _ = HeldDrill(runtime);
            await _underWay.WaitAsync(Timeout);

            var drillEndedFirst = await StopAsync(runtime, () => !runtime.Drills.IsDrilling(_harness.DocsSetId, "vault"));

            Assert.IsTrue(drillEndedFirst, "the service let go of its archives with a drill still under way");
            Assert.AreEqual(drilledBefore, Row(runtime).DrilledAt, "a drill the stop cut short stated an outcome");
        }
        finally
        {
            _release.Set();
            await runtime.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task StoppingTheService_TellsADrillUnderWayToEnd()
    {
        // Held on its own token and never let go by the test: only the stop
        // can end it, and a stop that did not tell it would wait for ever.
        var runtime = await StartAsync();
        try
        {
            await PassAsync(runtime, _start);
            var drilledBefore = Row(runtime).DrilledAt;
            _holdOnToken = true;
            var drill = HeldDrill(runtime);
            await _underWay.WaitAsync(Timeout);

            await runtime.DisposeAsync().AsTask().WaitAsync(Bound, Timeout);

            Assert.IsTrue(drill.IsCompleted, "the drill was still under way when the stop returned");
            Assert.AreEqual(drilledBefore, Row(runtime).DrilledAt, "a drill the stop cut short stated an outcome");
        }
        finally
        {
            _release.Set();
            await runtime.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task StoppingTheService_WhileAPassesDrillPhaseIsUnderWay_ReturnsOnlyOnceThePhaseHasEnded()
    {
        var runtime = await StartAsync();
        try
        {
            _holdThePhase = true;
            var pass = await Scheduler.RunPassAsync(runtime, _start, Timeout);
            await _phaseBegun.WaitAsync(Timeout);

            var phaseEndedFirst = await StopAsync(runtime, () => pass.Drills.IsCompleted);

            Assert.IsTrue(phaseEndedFirst, "the service let go of its archives with a pass's drill phase still under way");
        }
        finally
        {
            _release.Set();
            await runtime.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task StoppingTheService_TellsAPassesDrillPhaseToEnd()
    {
        var runtime = await StartAsync();
        try
        {
            _holdThePhase = true;
            _holdOnToken = true;
            var pass = await Scheduler.RunPassAsync(runtime, _start, Timeout);
            await _phaseBegun.WaitAsync(Timeout);

            await runtime.DisposeAsync().AsTask().WaitAsync(Bound, Timeout);

            Assert.IsTrue(pass.Drills.IsCompleted, "the pass's drill phase was still under way when the stop returned");
        }
        finally
        {
            _release.Set();
            await runtime.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task StoppingTheService_WhileADrillsStepWaitsInTheQueue_EndsTheDrill_AndTheDrillStatesNothing()
    {
        // Every step of a drill queues on the reader lane, and the queue runs
        // nothing it had not started by the time it stops: a step left
        // waiting there is answered by the stop or by nothing. The lane is
        // held by a job that ends when the queue tells it to.
        var runtime = await StartAsync();
        try
        {
            await PassAsync(runtime, _start);
            var drilledBefore = Row(runtime).DrilledAt;
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.IsTrue(runtime.Queue.Enqueue(new QueuedJob(
                "hold-" + Guid.NewGuid().ToString("n"), JobLane.Reader, UserInitiated: true, "hold the reader lane",
                async token => await held.Task.WaitAsync(token))));

            var drill = RecoveryDrillJob.RunAsync(
                runtime,
                runtime.Configuration.BackupSets.Single(),
                "vault",
                (ulong)_start.AddDays(RecoveryDrillJob.DefaultIntervalDays + 1).ToUnixTimeMilliseconds(),
                userInitiated: false,
                Timeout);
            await WaitUntilAsync(() => runtime.Queue.ActiveCount == 2);

            await runtime.DisposeAsync().AsTask().WaitAsync(Bound, Timeout);

            Assert.IsTrue(drill.IsCompleted, "the drill was still waiting on its step when the stop returned");
            Assert.AreEqual(drilledBefore, Row(runtime).DrilledAt, "a drill the stop cut short stated an outcome");
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task StoppingTheService_AnswersACommandStillWaitingInTheQueue_Cancelled()
    {
        // A person's command waits in the queue as a drill's step does, and
        // the stop answers it rather than leaving the person waiting for
        // ever. Each of the writer lane's workers is held by a job that ends
        // when the queue tells it to.
        var runtime = await StartAsync();
        try
        {
            HoldEveryWriter(runtime);
            var asked = new ServiceCommandHandler(runtime, RemoteBindingState.Off)
                .ExecuteAsync(new RetentionCommand(Apply: false), Timeout).AsTask();
            await WaitUntilAsync(() => runtime.Queue.ActiveCount == runtime.BackupPoolWidth + 1);

            await runtime.DisposeAsync().AsTask().WaitAsync(Bound, Timeout);

            Assert.IsInstanceOfType<ServiceError>(await asked.WaitAsync(Bound, Timeout), out var answer);
            Assert.AreEqual(ServiceErrorReason.Cancelled, answer.Reason);
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task StoppingTheService_AnswersAPassWhoseBackupItNeverStarted_AndLeavesTheRowForTheNextStart()
    {
        // A pass waits for its backups, and the queue runs none it had not
        // started by the time it stops (ADR-0029 Amendment 7). The stop
        // answers the pass, which starts nothing beside the queue once the
        // stop has answered it. The backup's row is left as it was, for the
        // next start to settle as a run to retry and say so (ADR-0049
        // Decision 1): nobody cancelled it.
        var runtime = await StartAsync();
        try
        {
            HoldEveryWriter(runtime);
            var pass = Scheduler.RunPassAsync(runtime, _start, Timeout).AsTask();
            await WaitUntilAsync(() => runtime.Queue.ActiveCount == runtime.BackupPoolWidth + 1);

            await runtime.DisposeAsync().AsTask().WaitAsync(Bound, Timeout);

            var answered = Assert.ContainsSingle((await pass.WaitAsync(Bound, Timeout)).Sets);
            Assert.AreEqual("cancelled", answered.Outcome);
            Assert.AreEqual(Scheduler.StoppedBeforeStart, answered.Detail);
            Assert.AreEqual(0, runtime.KeptBackgroundWork, "the pass started work beside the queue after the stop had answered it");
            Assert.AreEqual(JobState.Pending, Assert.ContainsSingle(runtime.Jobs.Jobs).State, "the stop settled the row the next start's sweep is for");
        }
        finally
        {
            await runtime.DisposeAsync();
        }

        await using var again = await ServiceRuntime.StartAsync(Options(), Timeout);
        Assert.AreEqual(JobState.FailedRecoverable, Assert.ContainsSingle(again.Jobs.Jobs).State);
        Assert.IsTrue(
            again.Notices.Unacknowledged.Any(notice => notice.Message.Contains("interrupted", StringComparison.OrdinalIgnoreCase)),
            "the next start settled the backup the stop left without a notice saying so");
    }

    [TestMethod]
    public async Task ABackupAskedOnceTheQueueHasStopped_IsAnsweredCancelled_NotAlreadyRunning()
    {
        // A stopped queue takes no work. That refusal was read as the one a
        // job already active draws, so a backup asked during the stop was
        // answered as if another run of the set were under way.
        var runtime = await StartAsync();
        try
        {
            await runtime.Queue.DisposeAsync();

            var outcome = await Scheduler.Enqueue(runtime, runtime.Configuration.BackupSets.Single(), _start, userInitiated: true)
                .WaitAsync(Bound, Timeout);

            Assert.AreEqual("cancelled", outcome.Outcome, outcome.Detail);
            Assert.AreEqual(Scheduler.StoppedBeforeStart, outcome.Detail);
            var row = Assert.ContainsSingle(runtime.Jobs.Jobs);
            Assert.AreEqual(JobState.Cancelled, row.State);
            Assert.AreEqual(Scheduler.StoppedBeforeStart, row.Detail);
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task StoppingTheService_AnswersAPersonsSyncItNeverStarted_Cancelled_AndTheLedgerStatesNothing()
    {
        // A person's sync waits for its job, which the queue had not started
        // when it stopped (ADR-0029 Amendment 8). Fan-out keeps the
        // cooperative cancel path, so the stop answers the sync through a
        // callback of its own: the person is told cancelled, and the pair's
        // ledger row says nothing about a sync that never ran.
        var runtime = await StartAsync();
        try
        {
            await PassAsync(runtime, _start);
            var before = Row(runtime);
            HoldTheLane(runtime, JobLane.Transfer, workers: 1);
            var asked = new ServiceCommandHandler(runtime, RemoteBindingState.Off)
                .ExecuteAsync(new SyncCommand("docs", "vault"), Timeout).AsTask();
            await WaitUntilAsync(() => runtime.Queue.ActiveCount == 2);

            await runtime.DisposeAsync().AsTask().WaitAsync(Bound, Timeout);

            Assert.IsInstanceOfType<ServiceError>(await asked.WaitAsync(Bound, Timeout), out var answer);
            Assert.AreEqual(ServiceErrorReason.Cancelled, answer.Reason);
            var after = Row(runtime);
            Assert.AreEqual(before.State, after.State);
            Assert.AreEqual(before.LastSuccessAt, after.LastSuccessAt, "the ledger recorded a sync that never ran");
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ASyncAskedOnceTheQueueHasStopped_IsAnsweredCancelled_NotAlreadySyncing()
    {
        // A stopped queue takes no work. Fan-out read that refusal as the one
        // a pair whose sync is under way draws, so a person's sync asked
        // during the stop was told the pair was already syncing.
        var runtime = await StartAsync();
        try
        {
            await runtime.Queue.DisposeAsync();

            var answer = await new ServiceCommandHandler(runtime, RemoteBindingState.Off)
                .ExecuteAsync(new SyncCommand("docs", "vault"), Timeout).AsTask().WaitAsync(Bound, Timeout);

            Assert.IsInstanceOfType<ServiceError>(answer, out var refused, string.Join("; ", (answer as SyncResult)?.Lines ?? []));
            Assert.AreEqual(ServiceErrorReason.Cancelled, refused.Reason);
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task TheWorkAPassLeavesBeside_IsLetGoOnceItHasEnded()
    {
        // A service runs a pass every poll for months: what it keeps for the
        // stop to wait on must not grow with every pass that has finished.
        var runtime = await StartAsync();
        try
        {
            for (var pass = 0; pass < 3; pass++)
            {
                await PassAsync(runtime, _start.AddHours(pass));
            }

            Assert.IsLessThanOrEqualTo(1, runtime.KeptBackgroundWork, "the service kept every pass's finished work");
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    /// <summary>Longer than a stop that tells its work to end takes, far shorter than one that waits for ever.</summary>
    private static TimeSpan Bound => TimeSpan.FromSeconds(30);

    /// <summary>A drill of the vault through a service that holds its first ask until the test lets it go.</summary>
    private Task<RecoveryDrillJob.DrillOutcome> HeldDrill(ServiceRuntime runtime) =>
        RecoveryDrillJob.RunAsync(
            runtime,
            new HoldingService(
                new ServiceCommandHandler(runtime, RemoteBindingState.Off, CallerScope.Service) { RestoresForADrill = true },
                _underWay,
                _release,
                _holdOnToken,
                Timeout),
            runtime.Configuration.BackupSets.Single(),
            "vault",
            (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            budget: null,
            Random.Shared,
            Timeout);

    /// <summary>
    /// Disposes the runtime while the drill is held, lets the drill go a
    /// while later, and says whether <paramref name="ended"/> held at the
    /// moment disposal returned. A runtime that waits for its drill cannot
    /// return before the drill is let go; one that does not returns at once.
    /// </summary>
    private async Task<bool> StopAsync(ServiceRuntime runtime, Func<bool> ended)
    {
        var stopping = Task.Run(async () =>
        {
            await runtime.DisposeAsync();
            return ended();
        });
        await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(1), Timeout));
        _release.Set();
        return await stopping.WaitAsync(Timeout);
    }

    /// <summary>Holds each of the writer lane's workers with a job that ends only when the queue tells it to.</summary>
    private static void HoldEveryWriter(ServiceRuntime runtime) => HoldTheLane(runtime, JobLane.Writer, runtime.BackupPoolWidth);

    /// <summary>Holds each of a lane's workers with a job that ends only when the queue tells it to.</summary>
    private static void HoldTheLane(ServiceRuntime runtime, JobLane lane, int workers)
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        for (var worker = 0; worker < workers; worker++)
        {
            Assert.IsTrue(runtime.Queue.Enqueue(new QueuedJob(
                "hold-" + Guid.NewGuid().ToString("n"), lane, UserInitiated: true, $"hold the {lane} lane",
                async token => await held.Task.WaitAsync(token))));
        }
    }

    private async Task WaitUntilAsync(Func<bool> condition)
    {
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), Timeout);
        }
    }

    private async Task PassAsync(ServiceRuntime runtime, DateTimeOffset at)
    {
        var pass = await Scheduler.RunPassAsync(runtime, at, Timeout);
        Assert.AreEqual(1, pass.Ran, "the pass ran no backup");
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);
    }

    private DestinationSyncRecord Row(ServiceRuntime runtime) =>
        runtime.DestinationSync.Find(_harness.DocsSetId, "vault") ?? throw new AssertFailedException("no ledger row for the vault");

    private async Task<ServiceRuntime> StartAsync()
    {
        Directory.CreateDirectory(Vault);
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('2', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = Vault,
                },
            ],
            BackupSets =
            [
                new BackupSetConfiguration
                {
                    Id = _harness.DocsSetId,
                    Name = "docs",
                    Roots = [new BackupRootConfiguration { Path = _harness.SourceRoot }],
                    Schedule = "every 1h",
                    Destinations = [new SetDestinationReference { Ref = "vault" }],
                },
            ],
        }.Save(Path.Combine(_harness.StateDirectory, "config.json"));
        _harness.WriteSourceFile("docs/a.txt", "alpha");
        _harness.WriteSourceFile("docs/b.txt", new string('b', 40_000));

        await _harness.SetupAsync();
        return await ServiceRuntime.StartAsync(Options(), Timeout);
    }

    private ServiceOptions Options() => new()
    {
        ArchivesRoot = _harness.ArchivesRoot,
        StateDirectory = _harness.StateDirectory,
        VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
        DrillPhaseBegun = token =>
        {
            if (Volatile.Read(ref _holdThePhase))
            {
                _phaseBegun.Release();
                Hold(Volatile.Read(ref _holdOnToken) ? token : Timeout);
            }
        },
    };

    /// <summary>
    /// The service a drill asks, holding its first ask until the test lets it
    /// go, deaf to the drill's own token: a hold the stop could end would let
    /// the drill end before the stop had waited for it.
    /// </summary>
    private sealed class HoldingService(
        IFallbackPlanService inner,
        SemaphoreSlim underWay,
        ManualResetEventSlim release,
        bool onTheDrillsToken,
        CancellationToken timeout)
        : IFallbackPlanService
    {
        private int _asked;

        public async ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _asked) == 1)
            {
                underWay.Release();
                await Task.Run(() => Wait(onTheDrillsToken ? cancellationToken : timeout), CancellationToken.None);
            }

            return await inner.ExecuteAsync(command, cancellationToken);
        }

        public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
            inner.WatchAsync(cancellationToken);

        /// <summary>Waits to be let go, or for <paramref name="until"/>; a cancelled wait just ends.</summary>
        private void Wait(CancellationToken until)
        {
            try
            {
                release.Wait(until);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>Holds the caller until the test lets it go, or until <paramref name="until"/>.</summary>
    private void Hold(CancellationToken until)
    {
        try
        {
            _release.Wait(until);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _release.Set();
        _release.Dispose();
        _underWay.Dispose();
        _phaseBegun.Dispose();
        _timeout.Dispose();
        _harness.Dispose();
        return ValueTask.CompletedTask;
    }
}
