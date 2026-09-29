namespace FallbackPlan.Application.Tests;

/// <summary>
/// The pacing half of NFR-PERF-013's disk and network limits: one limiter per
/// configured rate, shared by everything that limit governs, and a stream that
/// charges what actually passes through it.
/// </summary>
/// <remarks>
/// <para>
/// Every case runs on a virtual clock whose waits complete at once and move
/// virtual time forward by what was asked — so a rate is proved by arithmetic
/// on the waits requested rather than by sleeping through them, which is the
/// same move the background window made with its clock.
/// </para>
/// <para>
/// The sharing case is the one a per-call limiter would pass everywhere else:
/// two syncs to one peer must get that peer's rate between them. A limiter
/// built per job would give each the full rate, and the uplink the operator
/// capped would carry twice what they wrote.
/// </para>
/// </remarks>
[TestClass]
public sealed class ByteRateLimiterTests
{
    private const long KiB = 1024;

    [TestMethod]
    public async Task AnIdleLimiter_LetsOneSecondsWorthThroughWithoutWaiting()
    {
        var clock = new VirtualPacing();
        var limiter = new ByteRateLimiter(Rate("64 KiB/s"), clock.Clock);

        await limiter.AcquireAsync(64 * KiB, CancellationToken.None);

        Assert.AreEqual(TimeSpan.Zero, clock.Waited, "a burst of one second's worth passes at once");
        Assert.AreEqual(64 * KiB, limiter.BytesPaced);
    }

    [TestMethod]
    public async Task BeyondTheBurst_EachByteWaitsItsShareOfASecond()
    {
        var clock = new VirtualPacing();
        var limiter = new ByteRateLimiter(Rate("64 KiB/s"), clock.Clock);

        for (var chunk = 0; chunk < 10; chunk++)
        {
            await limiter.AcquireAsync(64 * KiB, CancellationToken.None);
        }

        // Ten seconds' worth at the rate, one of them covered by the burst.
        AssertNear(TimeSpan.FromSeconds(9), clock.Waited);
        Assert.AreEqual(640 * KiB, limiter.BytesPaced);
    }

    [TestMethod]
    public async Task TwoConsumersOfOneLimiter_ShareItsRateRatherThanEachHavingIt()
    {
        var shared = new VirtualPacing();
        var limiter = new ByteRateLimiter(Rate("64 KiB/s"), shared.Clock);
        for (var chunk = 0; chunk < 10; chunk++)
        {
            await limiter.AcquireAsync(64 * KiB, CancellationToken.None); // one sync
            await limiter.AcquireAsync(64 * KiB, CancellationToken.None); // another, to the same peer
        }

        // Twenty seconds' worth between them, one covered by the burst.
        AssertNear(TimeSpan.FromSeconds(19), shared.Elapsed);

        // The control: two limiters at the same rate are the per-job mistake,
        // and they finish in half the time — which is what the assertion above
        // would also see if the limiter were not shared.
        var separate = new VirtualPacing();
        var first = new ByteRateLimiter(Rate("64 KiB/s"), separate.Clock);
        var second = new ByteRateLimiter(Rate("64 KiB/s"), separate.Clock);
        for (var chunk = 0; chunk < 10; chunk++)
        {
            await first.AcquireAsync(64 * KiB, CancellationToken.None);
            await second.AcquireAsync(64 * KiB, CancellationToken.None);
        }

        Assert.IsLessThan(shared.Elapsed, separate.Elapsed);
    }

    [TestMethod]
    public async Task AWaitThatIsCancelled_EndsAtOnceRatherThanServingItsTime()
    {
        // A capture the window parks, a job a preemption pauses and a service
        // that is stopping all cancel; none of them may sit out a pacing delay
        // first. The clock here never completes a wait on its own.
        var never = new PacingClock(() => 0, (wait, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        var limiter = new ByteRateLimiter(Rate("1 KiB/s"), never);
        await limiter.AcquireAsync(KiB, CancellationToken.None); // the burst

        using var cancel = new CancellationTokenSource();
        var waiting = limiter.AcquireAsync(64 * KiB, cancel.Token).AsTask();
        Assert.IsFalse(waiting.IsCompleted, "the second acquisition must wait");

        await cancel.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task APacedStream_ChargesWhatItReads_InChunksRatherThanOneDebt()
    {
        var clock = new VirtualPacing();
        var limiter = new ByteRateLimiter(Rate("256 KiB/s"), clock.Clock);
        var content = new byte[1024 * KiB];
        Random.Shared.NextBytes(content);

        await using var paced = new PacedStream(new MemoryStream(content), limiter);
        using var copy = new MemoryStream();
        var buffer = new byte[1024 * KiB];
        int read;
        while ((read = await paced.ReadAsync(buffer, CancellationToken.None)) > 0)
        {
            // A whole-object read is paid for as it streams: one read call
            // never takes more than a chunk, so a 64 MiB blob cannot run up a
            // single debt that stalls everything sharing the limiter behind it.
            Assert.IsLessThanOrEqualTo(PacedStream.ChunkBytes, read);
            copy.Write(buffer, 0, read);
        }

        CollectionAssert.AreEqual(content, copy.ToArray(), "pacing must not change a byte");
        Assert.AreEqual(1024 * KiB, limiter.BytesPaced);
        AssertNear(TimeSpan.FromSeconds(3), clock.Waited); // four seconds' worth, one covered by the burst
    }

    [TestMethod]
    public async Task APacedStream_ChargesWhatItWrites_AndPassesItAllThrough()
    {
        var clock = new VirtualPacing();
        var limiter = new ByteRateLimiter(Rate("256 KiB/s"), clock.Clock);
        var content = new byte[1024 * KiB];
        Random.Shared.NextBytes(content);

        using var sink = new MemoryStream();
        await using (var paced = new PacedStream(sink, limiter, leaveOpen: true))
        {
            await paced.WriteAsync(content, CancellationToken.None);
            await paced.FlushAsync(CancellationToken.None);
        }

        CollectionAssert.AreEqual(content, sink.ToArray());
        Assert.AreEqual(1024 * KiB, limiter.BytesPaced);
        AssertNear(TimeSpan.FromSeconds(3), clock.Waited);
    }

    [TestMethod]
    public void APacedStream_ClosesWhatItWraps_UnlessToldToLeaveItOpen()
    {
        var limiter = new ByteRateLimiter(Rate("1 MiB/s"), new VirtualPacing().Clock);

        var owned = new MemoryStream();
        new PacedStream(owned, limiter).Dispose();
        Assert.IsFalse(owned.CanRead, "an owned stream is closed with its wrapper");

        var borrowed = new MemoryStream();
        new PacedStream(borrowed, limiter, leaveOpen: true).Dispose();
        Assert.IsTrue(borrowed.CanRead, "a borrowed stream — a live session's — outlives the wrapper");
    }

    private static ByteRate Rate(string text)
    {
        Assert.IsTrue(ByteRate.TryParse(text, out var rate, out var defect), defect);
        return rate!;
    }

    private static void AssertNear(TimeSpan expected, TimeSpan actual) =>
        Assert.IsLessThan(
            TimeSpan.FromMilliseconds(50), (expected - actual).Duration(), $"expected about {expected}, got {actual}");

    /// <summary>
    /// A clock whose waits complete at once and advance virtual time by what
    /// was asked, so a rate is read off the requested waits.
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

        public TimeSpan Elapsed
        {
            get
            {
                lock (_gate)
                {
                    return TimeSpan.FromTicks(_now);
                }
            }
        }
    }
}
