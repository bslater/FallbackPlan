namespace FallbackPlan.Application.Tests;

/// <summary>
/// The pacing half of NFR-PERF-013's CPU limit (ADR-0200): one limiter for the
/// installation, charged with the processor time the service spent since it
/// last looked, and waiting whatever keeps that inside the configured share of
/// the machine.
/// </summary>
/// <remarks>
/// <para>
/// Every case runs on a virtual clock whose waits complete at once and move
/// virtual time forward, and on a meter the test moves itself, so a share is
/// proved by arithmetic on the waits requested. That is the move that makes
/// the limit provable here at all: the measurement is the thing a container
/// cannot settle, and the arithmetic over a measurement is not.
/// </para>
/// <para>
/// Two properties carry the most weight. Processor time spent while nothing
/// looked is spread over the time since the last look, so an idle service
/// with a little housekeeping does not hold the next chunk back. And what a
/// limiter remembers is bounded, so a person's long restore — never paced —
/// holds background work back for seconds afterwards rather than for as long
/// as the restore ran.
/// </para>
/// </remarks>
[TestClass]
public sealed class CpuLimiterTests
{
    [TestMethod]
    public async Task AnIdleLimiter_LetsOneSecondOfTheShareThroughWithoutWaiting()
    {
        var time = new VirtualPacing();
        var meter = new SteppedMeter(processorCount: 2);
        var limiter = new CpuLimiter(Share("50%"), meter.Meter, time.Clock); // one core's worth

        meter.Spend(TimeSpan.FromSeconds(1));
        await limiter.PaceAsync(CancellationToken.None);

        Assert.AreEqual(TimeSpan.Zero, time.Waited, "a burst of one second's share passes at once");
        Assert.AreEqual(TimeSpan.FromSeconds(1), limiter.ProcessorTimeCharged);
    }

    [TestMethod]
    public async Task BeyondTheBurst_ProcessorTimeWaitsItsShareOfASecond()
    {
        var time = new VirtualPacing();
        var meter = new SteppedMeter(processorCount: 2);
        var limiter = new CpuLimiter(Share("50%"), meter.Meter, time.Clock);

        for (var chunk = 0; chunk < 10; chunk++)
        {
            meter.Spend(TimeSpan.FromMilliseconds(500));
            await limiter.PaceAsync(CancellationToken.None);
        }

        // Five seconds of processor time at one core a second, one of them
        // covered by the burst.
        AssertNear(TimeSpan.FromSeconds(4), time.Waited);
        Assert.AreEqual(TimeSpan.FromSeconds(5), limiter.ProcessorTimeCharged);
    }

    [TestMethod]
    [DataRow("25%", 4, 4.0, 3.0)]
    [DataRow("25%", 1, 1.0, 3.0)]
    [DataRow("10%", 10, 3.0, 2.0)]
    public async Task TheShare_IsOfTheWholeMachine(string share, int processors, double spentSeconds, double waitedSeconds)
    {
        // 25% of four processors is one core's worth, so four seconds of
        // processor time take four seconds; on one processor a quarter of a
        // core, so one second does. Both less the one-second burst.
        var time = new VirtualPacing();
        var meter = new SteppedMeter(processors);
        var limiter = new CpuLimiter(Share(share), meter.Meter, time.Clock);

        meter.Spend(TimeSpan.FromSeconds(spentSeconds));
        await limiter.PaceAsync(CancellationToken.None);

        AssertNear(TimeSpan.FromSeconds(waitedSeconds), time.Waited);
    }

    [TestMethod]
    public async Task ProcessorTimeSpentWhileNothingLooked_IsSpreadOverTheTimeSinceTheLastLook()
    {
        // Three seconds of housekeeping over a hundred idle seconds is three
        // percent of a core. Charged as though it had all just happened, it
        // would hold the next background chunk back two seconds for nothing.
        var time = new VirtualPacing();
        var meter = new SteppedMeter(processorCount: 2);
        var limiter = new CpuLimiter(Share("50%"), meter.Meter, time.Clock);
        await limiter.PaceAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(100));
        meter.Spend(TimeSpan.FromSeconds(3));
        await limiter.PaceAsync(CancellationToken.None);

        Assert.AreEqual(TimeSpan.Zero, time.Waited);
        Assert.AreEqual(TimeSpan.FromSeconds(3), limiter.ProcessorTimeCharged, "it is still charged, only not owed");
    }

    [TestMethod]
    public async Task IdleTime_BanksNoMoreThanTheBurst_OnceTheLimiterIsLookingAgain()
    {
        // The spreading covers only the interval the time was spent in. Once
        // background work is looking at every chunk, a run of heavy chunks
        // after a long idle spell is paced as though the idle spell had not
        // happened.
        var time = new VirtualPacing();
        var meter = new SteppedMeter(processorCount: 2);
        var limiter = new CpuLimiter(Share("50%"), meter.Meter, time.Clock);
        await limiter.PaceAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(100));
        await limiter.PaceAsync(CancellationToken.None);

        for (var chunk = 0; chunk < 6; chunk++)
        {
            meter.Spend(TimeSpan.FromMilliseconds(500));
            await limiter.PaceAsync(CancellationToken.None);
        }

        AssertNear(TimeSpan.FromSeconds(2), time.Waited);
    }

    [TestMethod]
    public async Task FarMoreThanTheShare_WhileNothingLooked_HoldsTheNextPaceBackOnlyAsLongAsTheLimiterRemembers()
    {
        // A person's ten-minute restore on four busy cores is forty minutes of
        // processor time, and a person is never paced. Owed at one core a
        // second, it would hold the next background chunk back half an hour
        // after the restore had ended. The limiter remembers ten seconds.
        var time = new VirtualPacing();
        var meter = new SteppedMeter(processorCount: 4);
        var limiter = new CpuLimiter(Share("25%"), meter.Meter, time.Clock);
        await limiter.PaceAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(10));
        meter.Spend(TimeSpan.FromMinutes(40));
        await limiter.PaceAsync(CancellationToken.None);

        AssertNear(CpuLimiter.Memory - TimeSpan.FromSeconds(1), time.Waited);
    }

    [TestMethod]
    public async Task TwoConsumersOfOneLimiter_AreChargedTheServicesTimeOnce()
    {
        // The meter reads the whole service, so two background jobs pacing
        // through one limiter must not each be charged what the other spent.
        var shared = new VirtualPacing();
        var meter = new SteppedMeter(processorCount: 2);
        var limiter = new CpuLimiter(Share("50%"), meter.Meter, shared.Clock);

        meter.Spend(TimeSpan.FromSeconds(3));
        await limiter.PaceAsync(CancellationToken.None); // one capture
        await limiter.PaceAsync(CancellationToken.None); // another, at once

        AssertNear(TimeSpan.FromSeconds(2), shared.Waited);
        Assert.AreEqual(TimeSpan.FromSeconds(3), limiter.ProcessorTimeCharged);

        // The control: a limiter per job charges the same three seconds to
        // each of them.
        var separate = new VirtualPacing();
        var together = new SteppedMeter(processorCount: 2);
        var first = new CpuLimiter(Share("50%"), together.Meter, separate.Clock);
        var second = new CpuLimiter(Share("50%"), together.Meter, separate.Clock);
        together.Spend(TimeSpan.FromSeconds(3));
        await first.PaceAsync(CancellationToken.None);
        await second.PaceAsync(CancellationToken.None);
        Assert.AreEqual(TimeSpan.FromSeconds(6), first.ProcessorTimeCharged + second.ProcessorTimeCharged);
    }

    [TestMethod]
    public async Task AWaitThatIsCancelled_EndsAtOnceRatherThanServingItsTime()
    {
        // A capture the window parks, a job a preemption pauses and a service
        // that is stopping all cancel; none may sit out a pacing delay first.
        var never = new PacingClock(() => 0, (wait, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        var meter = new SteppedMeter(processorCount: 1);
        var limiter = new CpuLimiter(Share("10%"), meter.Meter, never);

        meter.Spend(TimeSpan.FromSeconds(5));
        using var cancel = new CancellationTokenSource();
        var waiting = limiter.PaceAsync(cancel.Token).AsTask();
        Assert.IsFalse(waiting.IsCompleted, "fifty seconds' worth must wait");

        await cancel.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task APacedStream_UnderTheCpuLimitAlone_LooksAtEveryChunkAndChangesNoByte()
    {
        var time = new VirtualPacing();
        var meter = new SteppedMeter(processorCount: 1) { PerReading = TimeSpan.FromMilliseconds(100) };
        var limiter = new CpuLimiter(Share("10%"), meter.Meter, time.Clock);
        var content = new byte[256 * 1024];
        Random.Shared.NextBytes(content);

        await using var paced = new PacedStream(new MemoryStream(content), new BackgroundPace(null, limiter));
        using var copy = new MemoryStream();
        await paced.CopyToAsync(copy);

        CollectionAssert.AreEqual(content, copy.ToArray(), "pacing must not change a byte");

        // Four chunks and the read that finds the end, each a reading, each a
        // tenth of a second of processor time that takes a second at a tenth
        // of one core; one second is the burst.
        Assert.AreEqual(TimeSpan.FromMilliseconds(500), limiter.ProcessorTimeCharged);
        AssertNear(TimeSpan.FromSeconds(4), time.Waited);
    }

    [TestMethod]
    public async Task APace_WithBothLimits_ChargesEach()
    {
        var time = new VirtualPacing();
        var meter = new SteppedMeter(processorCount: 1) { PerReading = TimeSpan.FromMilliseconds(10) };
        Assert.IsTrue(ByteRate.TryParse("64 KiB/s", out var rate, out var defect), defect);
        var bytes = new ByteRateLimiter(rate!, time.Clock);
        var cpu = new CpuLimiter(Share("50%"), meter.Meter, time.Clock);
        var pace = new BackgroundPace(bytes, cpu);

        await pace.AcquireAsync(64 * 1024, CancellationToken.None);

        Assert.AreEqual(64 * 1024L, bytes.BytesPaced);
        Assert.AreEqual(TimeSpan.FromMilliseconds(10), cpu.ProcessorTimeCharged);
    }

    [TestMethod]
    public void APace_IsNoneWhenNothingIsLimited()
    {
        Assert.IsNull(BackgroundPace.Of(null, null));
        var limiter = new CpuLimiter(Share("50%"), new SteppedMeter(1).Meter, new VirtualPacing().Clock);
        Assert.AreSame(limiter, BackgroundPace.Of(null, limiter)!.Cpu);
    }

    [TestMethod]
    public void TheSystemMeter_ReadsThisProcess()
    {
        // The one reading nothing here can fake: the process's own processor
        // time grows as it works, and the machine has at least one processor.
        var before = CpuMeter.System.ProcessorTime();
        var spin = 0L;
        var until = Environment.TickCount64 + 50;
        while (Environment.TickCount64 < until)
        {
            spin++;
        }

        Assert.IsGreaterThan(0L, spin);
        Assert.IsGreaterThan(before, CpuMeter.System.ProcessorTime());
        Assert.IsGreaterThanOrEqualTo(1, CpuMeter.System.ProcessorCount);
    }

    private static CpuShare Share(string text)
    {
        Assert.IsTrue(CpuShare.TryParse(text, out var share, out var defect), defect);
        return share!;
    }

    private static void AssertNear(TimeSpan expected, TimeSpan actual) =>
        Assert.IsLessThan(
            TimeSpan.FromMilliseconds(50), (expected - actual).Duration(), $"expected about {expected}, got {actual}");

    /// <summary>
    /// A meter the test moves: <see cref="Spend"/> adds processor time, and
    /// each reading adds <see cref="PerReading"/>, which is how a stream's
    /// chunks are made to cost something.
    /// </summary>
    private sealed class SteppedMeter
    {
        private long _ticks;

        public SteppedMeter(int processorCount) =>
            Meter = new CpuMeter(
                () => TimeSpan.FromTicks(Interlocked.Add(ref _ticks, PerReading.Ticks)),
                processorCount);

        public CpuMeter Meter { get; }

        public TimeSpan PerReading { get; init; }

        public void Spend(TimeSpan time) => Interlocked.Add(ref _ticks, time.Ticks);
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

        public void Advance(TimeSpan time)
        {
            lock (_gate)
            {
                _now += time.Ticks;
            }
        }
    }
}
