using Bodu;
using Bodu.Collections.Generic.Concurrent;

namespace FallbackPlan.Diagnostics;

/// <summary>
/// The bounded, sequenced buffer a client reads recent diagnostics from
/// (ADR-0043 §6).
/// </summary>
/// <remarks>
/// <para>
/// Storage and thread-safety are
/// <see cref="ConcurrentCircularBuffer{T}"/>'s — a lock-free Vyukov MPMC ring
/// with <c>allowOverwrite</c>, which is exactly this shape: bounded, and
/// dropping the oldest record rather than the newest when it fills. Writers
/// here are genuinely many (the archive loop runs at the policy's concurrency,
/// the scheduler lane and both listeners all log), so the concurrent variant
/// is the correct one and no external lock is needed.
/// </para>
/// <para>
/// What this type adds is the part the buffer does not model: a
/// <b>monotonic sequence</b> per record and a <b>non-destructive read from a
/// cursor</b>. Those are what let a reader discover it fell behind — the same
/// drop-oldest-and-say-so contract the progress hub keeps, for the same reason.
/// A diagnostic buffer that silently loses records is worse than one that says
/// it did.
/// </para>
/// <para>
/// A client never receives a path to the log file. The service exposes no raw
/// filesystem access to clients (threat T-16), and a log reader is not the
/// place to make an exception — so this in-memory ring, not the file, is what
/// backs the contract's read verb.
/// </para>
/// </remarks>
public sealed class LogRing
{
    private readonly ConcurrentCircularBuffer<LogRecord> _records;
    private long _nextSequence;

    /// <summary>Creates a ring holding at least <paramref name="capacity"/> records.</summary>
    /// <param name="capacity">How many records to retain; rounded up to a power of two.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not positive.</exception>
    public LogRing(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        // Rounded up to a power of two, and not merely to be tidy: the ring's
        // slot mapping is a clean permutation across the position counter's
        // 2^32 wrap ONLY at a power-of-two capacity — otherwise one slot can
        // misalign once per 2^32 operations. An always-on service reaches
        // 2^32 log writes eventually, and the resulting fault would be
        // unreproducible and years late. Two is the protocol's own minimum.
        _records = new ConcurrentCircularBuffer<LogRecord>(RoundUpToPowerOfTwo(capacity), allowOverwrite: true);
    }

    /// <summary>How many records the ring holds.</summary>
    public int Capacity => _records.Capacity;

    /// <summary>The sequence the next added record will carry.</summary>
    public long NextSequence => Interlocked.Read(ref _nextSequence);

    /// <summary>
    /// The oldest sequence still held, or the next sequence when the ring is
    /// empty. A reader whose cursor is below this has missed records.
    /// </summary>
    public long OldestSequence
    {
        get
        {
            var snapshot = SnapshotBySequence();
            return snapshot.Count == 0 ? NextSequence : snapshot[0].Sequence;
        }
    }

    /// <summary>
    /// Stamps a sequence onto <paramref name="record"/> and stores it, evicting
    /// the oldest if the ring is full.
    /// </summary>
    /// <param name="record">The record to add; its sequence is replaced.</param>
    /// <returns>The stored record, carrying its assigned sequence.</returns>
    public LogRecord Add(LogRecord record)
    {
        ThrowHelper.ThrowIfNull(record);

        // Interlocked rather than a lock: the buffer is already safe for
        // concurrent producers, so the sequence is the only shared state left
        // to order, and ordering one counter does not need a critical section.
        var stamped = record with { Sequence = Interlocked.Increment(ref _nextSequence) - 1 };
        _records.Enqueue(stamped);
        return stamped;
    }

    /// <summary>
    /// Reads up to <paramref name="maximum"/> records from
    /// <paramref name="sinceSequence"/> onward, at or above
    /// <paramref name="minimumLevel"/>.
    /// </summary>
    /// <param name="sinceSequence">The cursor; pass 0 to start at the oldest held record.</param>
    /// <param name="maximum">The page size — the contract caps a frame at 8 MiB, so a read is always bounded.</param>
    /// <param name="minimumLevel">Records below this level are skipped.</param>
    /// <returns>
    /// The page, the cursor to pass next, and whether records before the page
    /// had already been evicted.
    /// </returns>
    public LogPage Read(long sinceSequence, int maximum, Microsoft.Extensions.Logging.LogLevel minimumLevel)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);

        // One snapshot, taken without disturbing the buffer — reading
        // diagnostics must never consume them, since two clients may be
        // watching and neither owns the history.
        //
        // Sorted by sequence, and that is not belt-and-braces. Add stamps the
        // sequence and enqueues in two steps, so two producers can interleave
        // between them: thread A takes 5, thread B takes 6, B enqueues first,
        // and the buffer's FIFO order is 6 then 5. Paging a snapshot in that
        // order would advance the cursor past 6 and then silently drop 5 —
        // a record missing from the feed with nothing to say it went. Sorting
        // costs O(n log n) over at most Capacity records on a read that
        // happens when a human asks, and it buys back the ordering the
        // lock-free enqueue cannot promise.
        var snapshot = SnapshotBySequence();

        if (snapshot.Count == 0)
        {
            return new LogPage([], Math.Max(sinceSequence, NextSequence), Dropped: false);
        }

        // The reader fell behind if its cursor names a record the ring has
        // already evicted. Derived from the snapshot rather than from Count,
        // which this buffer documents as approximate.
        var dropped = sinceSequence < snapshot[0].Sequence;

        var page = new List<LogRecord>();
        var cursor = Math.Max(sinceSequence, snapshot[0].Sequence);

        foreach (var record in snapshot)
        {
            if (record.Sequence < cursor)
            {
                continue;
            }

            if (page.Count == maximum)
            {
                // Stop at the page boundary and report the first sequence NOT
                // returned, so the next read resumes exactly here — including
                // over records this call filtered out by level.
                return new LogPage(page, record.Sequence, dropped);
            }

            if (record.Level >= minimumLevel)
            {
                page.Add(record);
            }

            cursor = record.Sequence + 1;
        }

        return new LogPage(page, cursor, dropped);
    }

    /// <summary>
    /// A snapshot of the held records, oldest sequence first, with unpublished
    /// slots discarded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two corrections to a raw <c>ToArray</c>, both of them consequences of
    /// reading a lock-free ring while producers are writing to it.
    /// </para>
    /// <para>
    /// <b>Nulls.</b> A snapshot can observe a slot a producer has claimed but
    /// not yet published, and the buffer's <c>where T : class?</c> lets that
    /// arrive as <see langword="null"/> — so the compiler's non-null
    /// annotation on the element type is not a promise the runtime keeps. A
    /// record that is not published yet is simply not in this page; its
    /// sequence is above the cursor, so the next read returns it. Discarding
    /// it here is what keeps a diagnostic read from throwing under exactly the
    /// load that makes somebody want to read diagnostics.
    /// </para>
    /// <para>
    /// <b>Order.</b> <see cref="Add"/> stamps the sequence and enqueues in two
    /// steps, so two producers can interleave between them — thread A takes 5,
    /// thread B takes 6, B enqueues first, and the ring's FIFO order is 6 then
    /// 5. Paging in that order would advance the cursor past 6 and silently
    /// drop 5: a record missing from the feed with nothing to say it went.
    /// </para>
    /// </remarks>
    private List<LogRecord> SnapshotBySequence()
    {
        var raw = _records.ToArray();
        var held = new List<LogRecord>(raw.Length);

        foreach (var record in raw)
        {
            if (record is not null)
            {
                held.Add(record);
            }
        }

        held.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));
        return held;
    }

    /// <summary>
    /// The next power of two at or above <paramref name="value"/>, floored at
    /// the ring protocol's minimum of two.
    /// </summary>
    private static int RoundUpToPowerOfTwo(int value) =>
        value <= 2 ? 2 : (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)value);
}

/// <summary>One page of diagnostics read from a <see cref="LogRing"/>.</summary>
/// <param name="Records">The records in the page, oldest first.</param>
/// <param name="NextSequence">The cursor to pass to the next read.</param>
/// <param name="Dropped">
/// Whether records before this page had already been evicted — the reader fell
/// behind, and saying so is the whole point of the sequence.
/// </param>
public sealed record LogPage(IReadOnlyList<LogRecord> Records, long NextSequence, bool Dropped);
