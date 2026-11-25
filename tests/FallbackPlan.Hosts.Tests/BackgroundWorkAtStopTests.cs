using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;

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
/// Each test holds a drill at its first ask of the service, before it opens
/// a source to restore from: the only waits the stop made before were for
/// queued jobs and for a restore source in use.
/// </remarks>
[TestClass]
public sealed class BackgroundWorkAtStopTests : IAsyncDisposable
{
    private readonly HostHarness _harness = new();
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));
    private readonly DateTimeOffset _start = DateTimeOffset.Now;
    private readonly SemaphoreSlim _underWay = new(0);
    private readonly SemaphoreSlim _joined = new(0);
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
    public async Task StoppingTheService_WhileAPassWaitsOnADrill_ReturnsOnlyOnceThePassHasEnded()
    {
        var runtime = await StartAsync();
        try
        {
            await PassAsync(runtime, _start);
            _ = HeldDrill(runtime);
            await _underWay.WaitAsync(Timeout);

            // A pass that finds the vault due a drill joins the one under way.
            var pass = await Scheduler.RunPassAsync(runtime, _start.AddDays(RecoveryDrillJob.DefaultIntervalDays + 1), Timeout);
            await pass.Transfers.WaitAsync(Timeout);
            await _joined.WaitAsync(Timeout);

            var passEndedFirst = await StopAsync(runtime, () => pass.Drills.IsCompleted);

            Assert.IsTrue(passEndedFirst, "the service let go of its archives with a pass's drill phase still under way");
        }
        finally
        {
            _release.Set();
            await runtime.DisposeAsync();
        }
    }

    /// <summary>A drill of the vault through a service that holds its first ask until the test lets it go.</summary>
    private Task<RecoveryDrillJob.DrillOutcome> HeldDrill(ServiceRuntime runtime) =>
        RecoveryDrillJob.RunAsync(
            runtime,
            new HoldingService(
                new ServiceCommandHandler(runtime, RemoteBindingState.Off, CallerScope.Service) { RestoresForADrill = true },
                _underWay,
                _release,
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
        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
                DrillJoined = (_, _) => _joined.Release(),
            },
            Timeout);
    }

    /// <summary>
    /// The service a drill asks, holding its first ask until the test lets it
    /// go, deaf to the drill's own token: a hold the stop could end would let
    /// the drill end before the stop had waited for it.
    /// </summary>
    private sealed class HoldingService(
        IFallbackPlanService inner, SemaphoreSlim underWay, ManualResetEventSlim release, CancellationToken timeout)
        : IFallbackPlanService
    {
        private int _asked;

        public async ValueTask<ServiceResult> ExecuteAsync(ServiceCommand command, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _asked) == 1)
            {
                underWay.Release();
                await Task.Run(() => release.Wait(timeout), CancellationToken.None);
            }

            return await inner.ExecuteAsync(command, cancellationToken);
        }

        public IAsyncEnumerable<JobProgressEvent> WatchAsync(CancellationToken cancellationToken) =>
            inner.WatchAsync(cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _release.Set();
        _release.Dispose();
        _underWay.Dispose();
        _joined.Dispose();
        _timeout.Dispose();
        _harness.Dispose();
        return ValueTask.CompletedTask;
    }
}
