using FallbackPlan.Agent;
using FallbackPlan.Api;
using FallbackPlan.Application;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The CPU limit of NFR-PERF-013 in a running service (ADR-0200):
/// <c>background_cpu_limit</c> paces every background job at the seams the
/// byte limits already pace — a capture's source reads, a sync's transfers, a
/// sweep's reads and a drill's — whether or not any byte limit is set, and never a
/// person's work. A job waiting on it does not hold the service's stop
/// (FR-SVC-017).
/// </summary>
/// <remarks>
/// <para>
/// The runtime measures on a meter the test moves
/// (<see cref="ServiceOptions.CpuMeter"/>) and paces on a virtual clock whose
/// waits complete at once and are summed, so the share is proved by the waits
/// it asked for. Each reading of the meter here costs fifty milliseconds of
/// processor time, which at 10% of one processor is half a second of wall
/// time. That stands in for the work a chunk costs; what it proves is that
/// every chunk is looked at and every look is paid for.
/// </para>
/// <para>
/// What it cannot prove is a figure measured on a real machine, which is the
/// one thing a container's processor time does not settle: measured agent
/// CPU at or under 30% over sixty seconds under a 25% limit stays measured
/// nowhere. That is the caveat throughput carries too, and this suite
/// does not establish NFR-PERF-007.
/// </para>
/// <para>
/// Does not establish FR-DRL-002 — the drill is observed as a background
/// reader of a destination, and nothing about what it proves is tested.
/// </para>
/// </remarks>
[TestClass]
public sealed class BackgroundCpuLimitTests : IDisposable
{
    private const string Limit = "10%";

    private static readonly TimeSpan PerReading = TimeSpan.FromMilliseconds(50);

    private readonly HostHarness _harness = new();
    private readonly VirtualPacing _pacing = new();
    private readonly HoldingPacing _holding = new();
    private readonly SteppedMeter _meter = new();

    // A hang guard, not a performance claim: the virtual clock makes every
    // paced wait instant, and what remains is real captures of small trees.
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(5));

    private CancellationToken Timeout => _timeout.Token;

    private string Vault => Path.Combine(_harness.WorkPath, "vault");

    public void Dispose()
    {
        _timeout.Dispose();
        _harness.Dispose();
    }

    [TestMethod]
    public async Task AScheduledCapture_UnderTheCpuLimit_IsPaced_AndAManualRunIsNot()
    {
        await using var runtime = await StartAsync(cpuLimit: Limit);
        var set = runtime.Configuration.BackupSets.Single();

        var scheduled = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: false).WaitAsync(Timeout);
        Assert.AreEqual("ran", scheduled.Outcome, scheduled.Detail);

        var limiter = runtime.Pacing.ForCpu(runtime.Configuration);
        Assert.IsNotNull(limiter);
        AssertPaced(limiter);

        // A person's run of the same set, after a change, is not looked at.
        var charged = limiter.ProcessorTimeCharged;
        var waited = _pacing.Waited;
        WriteRandomSourceFile("docs/later.bin", 96 * 1024);
        var manual = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", manual.Outcome, manual.Detail);

        Assert.AreEqual(charged, limiter.ProcessorTimeCharged);
        Assert.AreEqual(waited, _pacing.Waited);
    }

    [TestMethod]
    public async Task AScheduledCapture_OfAnUnchangedTree_IsPacedAsItWalks_AndAPersonsIsNot()
    {
        // A run over a tree where nothing changed reads no content: every
        // file is reused on its stat, so its processor time is the walk's.
        // A limit that looked only at content would never be consulted by
        // the incremental backup a machine runs most often.
        for (var file = 0; file < 256; file++)
        {
            _harness.WriteSourceFile($"docs/tree/{file / 32:D2}/note-{file:D3}.txt", $"note {file}");
        }

        await using var runtime = await StartAsync(cpuLimit: Limit);
        var set = runtime.Configuration.BackupSets.Single();
        var first = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", first.Outcome, first.Detail);
        var limiter = runtime.Pacing.ForCpu(runtime.Configuration)!;

        // The control: a person's run of the unchanged tree walks it too,
        // and is not looked at.
        var person = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", person.Outcome, person.Detail);
        Assert.AreEqual(TimeSpan.Zero, limiter.ProcessorTimeCharged, "a person's walk is not looked at");

        var scheduled = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: false).WaitAsync(Timeout);
        Assert.AreEqual("ran", scheduled.Outcome, scheduled.Detail);
        AssertPaced(limiter);
    }

    [TestMethod]
    public async Task AScheduledSync_IsPacedByTheCpuLimit_WithNoTransferLimitOnItsDestination()
    {
        // The CPU limit is the installation's and rides every seam on its own:
        // a destination nobody gave a transfer limit is still a background
        // job's to pace.
        await using var runtime = await StartAsync(cpuLimit: Limit);
        var set = runtime.Configuration.BackupSets.Single();
        var captured = await Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: true).WaitAsync(Timeout);
        Assert.AreEqual("ran", captured.Outcome, captured.Detail);
        Assert.IsNull(runtime.Pacing.ForDestination(VaultIn(runtime)), "the fixture limits no destination's bytes");
        Assert.AreEqual(0, _meter.Readings, "a person's capture is not looked at");
        var limiter = runtime.Pacing.ForCpu(runtime.Configuration)!;

        var sync = FanOut.Enqueue(runtime, set, "vault", DateTimeOffset.Now, userInitiated: false);
        Assert.IsNotNull(sync, "the pair had a sync to run");
        await sync.WaitAsync(Timeout);

        Assert.IsNotNull(
            runtime.DestinationSync.Find(_harness.DocsSetId, "vault")?.LastSuccessAt, "a paced sync still finishes");
        AssertPaced(limiter);
    }

    [TestMethod]
    public async Task APersonsSync_UnderTheCpuLimit_IsNotPaced()
    {
        await using var runtime = await StartAsync(cpuLimit: Limit);

        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout, userInitiated: true);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);

        // The control first: the person's pass did capture and copy, so
        // "nothing was paced" is about the person and not an empty fixture.
        Assert.AreEqual(1, pass.Ran);
        Assert.IsNotNull(runtime.DestinationSync.Find(_harness.DocsSetId, "vault")?.LastSuccessAt);

        Assert.AreEqual(0, _meter.Readings, "nothing a person started looked at the meter");
        Assert.AreEqual(TimeSpan.Zero, _pacing.Waited);
    }

    [TestMethod]
    public async Task ADrillUnderTheCpuLimit_IsPaced_AndAPersonsIsNot()
    {
        await using var runtime = await StartAsync(cpuLimit: Limit);
        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout, userInitiated: true);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);
        var limiter = runtime.Pacing.ForCpu(runtime.Configuration)!;
        var set = runtime.Configuration.BackupSets.Single();
        var nowMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var person = await RecoveryDrillJob.RunAsync(runtime, set, "vault", nowMs, userInitiated: true, Timeout);
        Assert.IsNull(person.Failure, person.Failure);
        Assert.IsGreaterThan(0, person.Files, "the control: the drill had files to restore");
        Assert.AreEqual(TimeSpan.Zero, limiter.ProcessorTimeCharged, "a person's drill is not looked at");

        var scheduled = await RecoveryDrillJob.RunAsync(runtime, set, "vault", nowMs, userInitiated: false, Timeout);
        Assert.IsNull(scheduled.Failure, scheduled.Failure);
        AssertPaced(limiter);
    }

    [TestMethod]
    public async Task ASweepUnderTheCpuLimit_IsPaced_AndAPersonsIsNot()
    {
        // The sweep reads and hashes every blob of a replica, which is as
        // CPU-bound as background work gets after the capture itself.
        await using var runtime = await StartAsync(cpuLimit: Limit);
        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout, userInitiated: true);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);
        var limiter = runtime.Pacing.ForCpu(runtime.Configuration)!;
        var set = runtime.Configuration.BackupSets.Single();
        var nowMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var person = await ReplicaSweepJob.RunAsync(runtime, set, "vault", nowMs, userInitiated: true, Timeout);
        Assert.IsGreaterThan(0, person.Examined, "the control: the sweep had objects to read");
        Assert.AreEqual(TimeSpan.Zero, limiter.ProcessorTimeCharged, "a person's deep verification is not looked at");

        var scheduled = await ReplicaSweepJob.RunAsync(runtime, set, "vault", nowMs, userInitiated: false, Timeout);
        Assert.IsGreaterThan(0, scheduled.Examined);
        AssertPaced(limiter);
    }

    [TestMethod]
    public async Task StoppingTheService_WhileAScheduledCaptureWaitsOnTheCpuLimit_EndsIt()
    {
        await using var runtime = await StartAsync(cpuLimit: Limit, clock: _holding.Clock);
        var set = runtime.Configuration.BackupSets.Single();

        var capture = Scheduler.Enqueue(runtime, set, DateTimeOffset.Now, userInitiated: false);
        await _holding.Waiting.WaitAsync(Timeout);

        await runtime.DisposeAsync().AsTask().WaitAsync(Bound, Timeout);

        Assert.IsTrue(capture.IsCompleted, "the capture was still waiting on its limit when the stop returned");
    }

    [TestMethod]
    public async Task AnInstallationWithNoCpuLimit_NeverReadsTheMeter()
    {
        // Every file written before schema 12 says "unlimited" by not
        // mentioning the limit, and an installation that sets none should
        // not pay even for looking.
        await using var runtime = await StartAsync();

        var pass = await Scheduler.RunPassAsync(runtime, DateTimeOffset.Now, Timeout);
        await pass.Transfers.WaitAsync(Timeout);
        await pass.Drills.WaitAsync(Timeout);

        Assert.AreEqual(1, pass.Ran);
        Assert.IsNull(runtime.Pacing.ForCpu(runtime.Configuration));
        Assert.AreEqual(0, _meter.Readings);
        Assert.AreEqual(TimeSpan.Zero, _pacing.Waited);
    }

    [TestMethod]
    public async Task TheCpuLimiter_IsOneForTheInstallation_UntilTheLimitChanges()
    {
        // One limiter, because the meter reads the whole service: two
        // background jobs with a limiter each would each be charged what both
        // spent (the arithmetic is Application.Tests/CpuLimiterTests').
        await using var runtime = await StartAsync(cpuLimit: Limit);

        var first = runtime.Pacing.ForCpu(runtime.Configuration);
        Assert.IsNotNull(first);
        Assert.AreSame(first, runtime.Pacing.ForCpu(runtime.Configuration));

        // Read afresh, as the window and the byte limits are: an edit takes
        // effect without a restart.
        WriteConfiguration(cpuLimit: "40%");
        var changed = runtime.Pacing.ForCpu(runtime.Configuration);
        Assert.IsNotNull(changed);
        Assert.AreNotSame(first, changed);
        Assert.AreEqual(40, changed.Share.Percent);

        WriteConfiguration(cpuLimit: null);
        Assert.IsNull(runtime.Pacing.ForCpu(runtime.Configuration));
    }

    [TestMethod]
    public async Task TheStatus_CarriesTheCpuLimitInForce()
    {
        await using var runtime = await StartAsync(cpuLimit: "25%");
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);

        var result = await handler.ExecuteAsync(new GetStatusCommand(), Timeout);

        Assert.IsInstanceOfType<StatusResult>(result, out var status);
        var limits = status.BackgroundLimits;
        Assert.IsNotNull(limits, "a CPU limit alone is a limit in force");
        Assert.AreEqual("25%", limits.CpuLimit?.Text);
        Assert.AreEqual(25, limits.CpuLimit?.Percent);
        Assert.IsNull(limits.ReadLimit);
        Assert.IsEmpty(limits.TransferLimits);
    }

    /// <summary>
    /// The limiter looked, and the virtual time it asked to wait covers what
    /// it was charged at the share, less the one second's burst.
    /// </summary>
    private void AssertPaced(CpuLimiter limiter)
    {
        var charged = limiter.ProcessorTimeCharged;
        Assert.IsGreaterThan(TimeSpan.Zero, charged, "the limiter never looked");
        var owed = TimeSpan.FromTicks(charged.Ticks * 100 / limiter.Share.Percent) - TimeSpan.FromSeconds(1);
        Assert.IsGreaterThan(TimeSpan.Zero, owed, "the fixture must cost more than one second's share");
        Assert.IsGreaterThanOrEqualTo(
            owed - TimeSpan.FromMilliseconds(100), _pacing.Waited, $"{charged} of processor time at {Limit} was not paced");
    }

    private static DestinationConfiguration VaultIn(ServiceRuntime runtime) =>
        runtime.Configuration.FindDestination("vault")
        ?? throw new AssertFailedException("the fixture declares a destination named vault");

    private void WriteRandomSourceFile(string relativePath, int length)
    {
        var path = _harness.WriteSourceFile(relativePath, string.Empty);
        var content = new byte[length];
        Random.Shared.NextBytes(content);
        File.WriteAllBytes(path, content);
    }

    private void WriteConfiguration(string? cpuLimit = null) =>
        new ClientConfiguration
        {
            SchemaVersion = ClientConfiguration.CurrentSchemaVersion,
            BackgroundCpuLimit = cpuLimit,
            Destinations =
            [
                new DestinationConfiguration
                {
                    Id = new string('d', 32), Name = "vault", Kind = DestinationKind.LocalPath, Path = Vault,
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

    /// <summary>Longer than a stop that ends a paced wait takes, far shorter than one that waits it out.</summary>
    private static TimeSpan Bound => TimeSpan.FromSeconds(30);

    private async Task<ServiceRuntime> StartAsync(string? cpuLimit = null, PacingClock? clock = null)
    {
        Directory.CreateDirectory(Vault);
        WriteRandomSourceFile("docs/content.bin", 256 * 1024);
        _harness.WriteSourceFile("docs/notes.txt", "a file small enough to ride in the burst");
        WriteConfiguration(cpuLimit);

        await _harness.SetupAsync();

        return await ServiceRuntime.StartAsync(
            new ServiceOptions
            {
                ArchivesRoot = _harness.ArchivesRoot,
                StateDirectory = _harness.StateDirectory,
                PacingClock = clock ?? _pacing.Clock,
                CpuMeter = _meter.Meter,
                // The placement condition (ADR-0051) judges by volume, and the
                // fixture's every path shares one real volume — the vault is
                // told apart by name, the compliant install's shape.
                VolumeIdentityOverride = path => path.Contains("vault", StringComparison.Ordinal) ? 2UL : 1UL,
            },
            Timeout);
    }

    /// <summary>
    /// One processor, and fifty milliseconds of processor time spent between
    /// any two readings: each look a background job takes costs the same.
    /// </summary>
    private sealed class SteppedMeter
    {
        private long _ticks;
        private int _readings;

        public SteppedMeter() =>
            Meter = new CpuMeter(
                () =>
                {
                    Interlocked.Increment(ref _readings);
                    return TimeSpan.FromTicks(Interlocked.Add(ref _ticks, PerReading.Ticks));
                },
                processorCount: 1);

        public CpuMeter Meter { get; }

        public int Readings => Volatile.Read(ref _readings);
    }

    /// <summary>
    /// A clock that stands still and never ends a wait on its own: the first
    /// second's burst passes, and the first wait past it is held until its
    /// token is cancelled.
    /// </summary>
    private sealed class HoldingPacing
    {
        private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HoldingPacing() =>
            Clock = new PacingClock(
                static () => 0,
                async (wait, token) =>
                {
                    _waiting.TrySetResult();
                    await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
                });

        public PacingClock Clock { get; }

        /// <summary>Completes once a paced job is waiting on its limit.</summary>
        public Task Waiting => _waiting.Task;
    }

    /// <summary>
    /// A clock whose waits complete at once and advance virtual time by what
    /// was asked, summed, so a share is read off the requested waits.
    /// </summary>
    private sealed class VirtualPacing
    {
        private readonly Lock _gate = new();
        private long _now;
        private long _waited;

        public VirtualPacing() =>
            Clock = new PacingClock(
                () =>
                {
                    lock (_gate)
                    {
                        return _now;
                    }
                },
                (wait, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    lock (_gate)
                    {
                        _now += wait.Ticks;
                        _waited += wait.Ticks;
                    }

                    return Task.CompletedTask;
                });

        public PacingClock Clock { get; }

        public TimeSpan Waited
        {
            get
            {
                lock (_gate)
                {
                    return TimeSpan.FromTicks(_waited);
                }
            }
        }
    }
}
