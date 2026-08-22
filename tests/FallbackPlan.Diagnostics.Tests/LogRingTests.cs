using FallbackPlan.Diagnostics;
using FallbackPlan.TestSupport;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Diagnostics.Tests;

/// <summary>
/// The buffer a client reads diagnostics from (ADR-0043 §6): bounded,
/// dropping the oldest rather than the newest, and — the part that matters —
/// able to tell a reader that it fell behind.
/// </summary>
/// <remarks>
/// Storage and thread-safety belong to Bodu's
/// <c>ConcurrentCircularBuffer&lt;T&gt;</c>; what is asserted here is the
/// contract this type adds on top, because that is what the contract's
/// <c>read_log</c> verb is specified against.
/// </remarks>
[TestClass]
public sealed class LogRingTests
{
    private static LogRecord Record(LogLevel level = LogLevel.Information, string message = "a thing happened") =>
        new(
            Sequence: 0,
            TimestampUnixMilliseconds: 1_722_600_000_000,
            Level: level,
            EventId: 4242,
            Category: "FallbackPlan.Tests",
            Values: [new KeyValuePair<string, object?>(LogRecord.OriginalFormatKey, message)],
            ExceptionType: null,
            ExceptionMessage: null);

    [TestMethod]
    public void LogRing_ACapacityThatIsNotAPowerOfTwo_IsRoundedUp()
    {
        // Not tidiness: the ring's slot mapping is a clean permutation across
        // the position counter's 2^32 wrap only at a power-of-two capacity.
        // An always-on service reaches 2^32 writes, and the resulting fault
        // would be unreproducible and years late.
        Assert.AreEqual(2048, new LogRing(2000).Capacity);
        Assert.AreEqual(1024, new LogRing(1024).Capacity);
        Assert.AreEqual(2, new LogRing(1).Capacity, "two is the ring protocol's own minimum");
    }

    [TestMethod]
    public void LogRing_ACapacityOfZeroOrLess_IsRefused()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LogRing(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LogRing(-1));
    }

    [TestMethod]
    public void LogRing_EveryAddedRecord_CarriesTheNextSequence()
    {
        var ring = new LogRing(8);

        Assert.AreEqual(0, ring.NextSequence);
        Assert.AreEqual(0, ring.Add(Record()).Sequence);
        Assert.AreEqual(1, ring.Add(Record()).Sequence);
        Assert.AreEqual(2, ring.Add(Record()).Sequence);
        Assert.AreEqual(3, ring.NextSequence);
    }

    [TestMethod]
    public void LogRing_ReadWithoutAnything_AnswersEmptyRatherThanFailing()
    {
        var page = new LogRing(8).Read(sinceSequence: 0, maximum: 10, LogLevel.Trace);

        Assert.IsEmpty(page.Records);
        Assert.IsFalse(page.Dropped);
        Assert.AreEqual(0, page.NextSequence);
    }

    [TestMethod]
    public void LogRing_MoreRecordsThanItHolds_DropsTheOldestAndKeepsTheNewest()
    {
        var ring = new LogRing(4);
        for (var index = 0; index < 10; index++)
        {
            ring.Add(Record());
        }

        var page = ring.Read(sinceSequence: 0, maximum: 100, LogLevel.Trace);

        // A diagnostic buffer keeps what just happened, not what happened
        // first: the newest four survive.
        SequenceAssert.AreEqual([6L, 7L, 8L, 9L], page.Records.Select(record => record.Sequence));
        Assert.AreEqual(10, page.NextSequence);
    }

    [TestMethod]
    public void LogRing_AReaderWhoseCursorWasEvicted_IsToldItMissedRecords()
    {
        var ring = new LogRing(4);
        for (var index = 0; index < 10; index++)
        {
            ring.Add(Record());
        }

        // Cursor 0 names a record the ring dropped long ago. Saying so is the
        // entire reason the sequence exists — a shorter history that claims to
        // be complete is worse than an admitted gap.
        Assert.IsTrue(ring.Read(sinceSequence: 0, maximum: 100, LogLevel.Trace).Dropped);

        // A cursor inside the retained window has missed nothing.
        Assert.IsFalse(ring.Read(sinceSequence: 6, maximum: 100, LogLevel.Trace).Dropped);
    }

    [TestMethod]
    public void LogRing_ReadingTwice_ReturnsTheSameRecords()
    {
        // Reading diagnostics must not consume them: two clients may be
        // watching, and neither owns the history.
        var ring = new LogRing(8);
        ring.Add(Record());
        ring.Add(Record());

        var first = ring.Read(0, 100, LogLevel.Trace);
        var second = ring.Read(0, 100, LogLevel.Trace);

        SequenceAssert.AreEqual(
            first.Records.Select(record => record.Sequence),
            second.Records.Select(record => record.Sequence));
    }

    [TestMethod]
    public void LogRing_PagingWithACursor_WalksEveryRecordExactlyOnce()
    {
        var ring = new LogRing(64);
        for (var index = 0; index < 20; index++)
        {
            ring.Add(Record());
        }

        var seen = new List<long>();
        long cursor = 0;

        for (var page = 0; page < 10; page++)
        {
            var read = ring.Read(cursor, maximum: 3, LogLevel.Trace);
            if (read.Records.Count == 0)
            {
                break;
            }

            seen.AddRange(read.Records.Select(record => record.Sequence));
            cursor = read.NextSequence;
        }

        SequenceAssert.AreEqual(Enumerable.Range(0, 20).Select(value => (long)value), seen);
    }

    [TestMethod]
    public void LogRing_APageBoundaryFallingOnAFilteredRecord_DoesNotSkipIt()
    {
        // The cursor advances over records the level filtered out, so a
        // narrowed read must not lose the records between pages. Warnings at
        // even sequences, debug at odd.
        var ring = new LogRing(64);
        for (var index = 0; index < 12; index++)
        {
            ring.Add(Record(index % 2 == 0 ? LogLevel.Warning : LogLevel.Debug));
        }

        var seen = new List<long>();
        long cursor = 0;

        for (var page = 0; page < 20; page++)
        {
            var read = ring.Read(cursor, maximum: 2, LogLevel.Warning);
            if (read.NextSequence == cursor)
            {
                break;
            }

            seen.AddRange(read.Records.Select(record => record.Sequence));
            cursor = read.NextSequence;
        }

        SequenceAssert.AreEqual([0L, 2L, 4L, 6L, 8L, 10L], seen);
    }

    [TestMethod]
    public void LogRing_ReadBelowTheMinimumLevel_SkipsThoseRecords()
    {
        var ring = new LogRing(16);
        ring.Add(Record(LogLevel.Trace));
        ring.Add(Record(LogLevel.Error));
        ring.Add(Record(LogLevel.Debug));

        var page = ring.Read(0, 100, LogLevel.Warning);

        var only = Assert.ContainsSingle(page.Records);
        Assert.AreEqual(LogLevel.Error, only.Level);
        Assert.AreEqual(3, page.NextSequence, "the cursor still advances past what was filtered");
    }

    [TestMethod]
    public void LogRing_AMaximumOfZeroOrLess_IsRefused()
    {
        var ring = new LogRing(8);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ring.Read(0, 0, LogLevel.Trace));
    }

    [TestMethod]
    public void LogRing_ManyThreadsLoggingAtOnce_AssignsEverySequenceExactlyOnce()
    {
        // The reason the concurrent buffer is the right variant: writers here
        // are genuinely many — the archive loop at the policy's concurrency,
        // the scheduler lane, and both listeners. A duplicated or skipped
        // sequence would silently break the gap detection every reader relies
        // on, and would do it only under load.
        const int Threads = 8;
        const int PerThread = 500;

        var ring = new LogRing(8192);
        var assigned = new System.Collections.Concurrent.ConcurrentBag<long>();

        Parallel.For(0, Threads, _ =>
        {
            for (var index = 0; index < PerThread; index++)
            {
                assigned.Add(ring.Add(Record()).Sequence);
            }
        });

        var sequences = assigned.OrderBy(value => value).ToArray();

        Assert.AreEqual(Threads * PerThread, sequences.Length);
        Assert.AreEqual(Threads * PerThread, ring.NextSequence);
        SequenceAssert.AreEqual(
            Enumerable.Range(0, Threads * PerThread).Select(value => (long)value),
            sequences);
    }

    [TestMethod]
    public void LogRing_ReadingWhileThreadsLog_NeverSeesADuplicateOrAnOutOfOrderRecord()
    {
        var ring = new LogRing(4096);
        using var writing = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var writers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!writing.IsCancellationRequested)
            {
                ring.Add(Record());
            }
        })).ToArray();

        // A reader paging the whole time must see each sequence at most once
        // and never go backwards, whatever the writers are doing.
        long cursor = 0;
        long previous = -1;
        var reads = 0;

        while (!writing.IsCancellationRequested && reads < 2_000)
        {
            var page = ring.Read(cursor, maximum: 64, LogLevel.Trace);
            foreach (var record in page.Records)
            {
                Assert.IsTrue(
                    record.Sequence > previous,
                    $"sequence {record.Sequence} arrived after {previous}");
                previous = record.Sequence;
            }

            cursor = page.NextSequence;
            reads++;
        }

        Task.WaitAll(writers);
        Assert.IsTrue(ring.NextSequence > 0, "the drill must actually have logged something");
    }
}
