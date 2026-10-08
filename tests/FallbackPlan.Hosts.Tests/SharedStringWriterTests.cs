using FallbackPlan.TestSupport;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The writer the agent-host tests read while a running service writes to it
/// (AgentHostTests, AgentDefaultLocationsTests).
/// </summary>
/// <remarks>
/// A <see cref="StringWriter"/> polled from one thread while another writes
/// can throw from <c>ToString</c>, which a test reads as its own failure
/// (TestSupport/SharedStringWriter says how). These cases hold the
/// replacement to the two things those tests need of it: a read during a
/// write never throws, and nothing written is lost.
/// </remarks>
[TestClass]
public sealed class SharedStringWriterTests
{
    private const int Lines = 20_000;

    // Enough that a writer whose reads race its writes fails here: at a
    // thousand, one whose reads skipped the lock passed one run in five.
    private const int ReadsDuringWrites = 3_000;

    private const int LinesPerRound = 200;

    [TestMethod]
    public async Task ReadWhileAnotherThreadWrites_NeverThrows_AndLosesNoLine()
    {
        // The writing goes on in rounds until enough reads have met it: a
        // fixed run of lines can be written before a reader on a busy machine
        // reads once, and then the case proves nothing. The reader has a
        // thread of its own for the same reason, not one from a pool the
        // suite keeps busy.
        SharedStringWriter? current = null;
        var reads = 0;
        using var stop = new CancellationTokenSource();
        var reader = Task.Factory.StartNew(
            () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    if (Volatile.Read(ref current) is not { } writer)
                    {
                        continue;
                    }

                    _ = writer.ToString();

                    // A read counts only if it ended before its round did.
                    if (ReferenceEquals(Volatile.Read(ref current), writer))
                    {
                        Interlocked.Increment(ref reads);
                    }
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        var deadline = Environment.TickCount64 + 30_000;
        var during = 0;
        while (during < ReadsDuringWrites && !reader.IsCompleted)
        {
            Assert.IsLessThan(deadline, Environment.TickCount64, $"only {during} reads met a write in 30 s");
            using var writer = new SharedStringWriter();
            var before = Volatile.Read(ref reads);
            Volatile.Write(ref current, writer);
            for (var line = 0; line < LinesPerRound; line++)
            {
                writer.WriteLine($"line {line} of the service's output, long enough to fill the builder's chunks");
            }

            Volatile.Write(ref current, null);
            during += Volatile.Read(ref reads) - before;
            Assert.HasCount(LinesPerRound, writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        }

        await stop.CancelAsync();

        // A read that threw ended the reader, and this rethrows it.
        await reader;
    }

    [TestMethod]
    public async Task LinesFromSeveralThreads_EachArriveWhole()
    {
        // A service logs from more than one thread, and a test looks for a
        // line it wrote: half of it beside half of another is not that line.
        // The writers have threads of their own and start together, so the
        // lines go in at once rather than one pool thread's after another's.
        using var writer = new SharedStringWriter();
        using var start = new ManualResetEventSlim();
        var threads = Enumerable.Range(0, 4).Select(thread => Task.Factory.StartNew(
            () =>
            {
                start.Wait();
                for (var line = 0; line < Lines / 4; line++)
                {
                    writer.WriteLine($"thread {thread} line {line}");
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default)).ToList();
        start.Set();
        await Task.WhenAll(threads);

        var written = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.HasCount(Lines, written);
        Assert.IsTrue(
            written.All(line => System.Text.RegularExpressions.Regex.IsMatch(line, @"^thread [0-3] line \d+$")),
            "a line arrived torn: " + written.FirstOrDefault(line =>
                !System.Text.RegularExpressions.Regex.IsMatch(line, @"^thread [0-3] line \d+$")));
    }
}
