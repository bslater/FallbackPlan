using Bodu;

namespace FallbackPlan.Diagnostics;

/// <summary>
/// The bounded, sequenced buffer a client reads recent diagnostics from
/// (ADR-0043 §6).
/// </summary>
/// <remarks>
/// <para>
/// Modelled on the progress hub, and for the same reason: it is bounded, it
/// drops the oldest record when it is full, and every record carries a
/// monotonic <see cref="LogRecord.Sequence"/> so a reader can tell that it
/// missed something rather than quietly seeing a shorter history. A diagnostic
/// buffer that silently loses records is worse than one that says it did.
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
    private readonly LogRecord?[] _records;
    private readonly Lock _gate = new();
    private long _nextSequence;
    private int _head;
    private int _count;

    /// <summary>Creates a ring holding at most <paramref name="capacity"/> records.</summary>
    /// <param name="capacity">How many records to retain.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not positive.</exception>
    public LogRing(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _records = new LogRecord?[capacity];
    }

    /// <summary>How many records the ring holds.</summary>
    public int Capacity => _records.Length;

    /// <summary>The sequence the next added record will carry.</summary>
    public long NextSequence
    {
        get
        {
            lock (_gate)
            {
                return _nextSequence;
            }
        }
    }

    /// <summary>
    /// The oldest sequence still held, or the next sequence when the ring is
    /// empty. A reader whose cursor is below this has missed records.
    /// </summary>
    public long OldestSequence
    {
        get
        {
            lock (_gate)
            {
                return _count == 0 ? _nextSequence : _nextSequence - _count;
            }
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

        lock (_gate)
        {
            var stamped = record with { Sequence = _nextSequence++ };
            _records[_head] = stamped;
            _head = (_head + 1) % _records.Length;
            if (_count < _records.Length)
            {
                _count++;
            }

            return stamped;
        }
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

        lock (_gate)
        {
            var oldest = _count == 0 ? _nextSequence : _nextSequence - _count;
            var dropped = sinceSequence < oldest;
            var cursor = Math.Max(sinceSequence, oldest);

            var page = new List<LogRecord>();
            var sequence = cursor;

            while (sequence < _nextSequence && page.Count < maximum)
            {
                var slot = (int)(((_head - (_nextSequence - sequence)) % _records.Length + _records.Length)
                    % _records.Length);
                var held = _records[slot];
                if (held is not null && held.Level >= minimumLevel)
                {
                    page.Add(held);
                }

                sequence++;
            }

            return new LogPage(page, sequence, dropped);
        }
    }
}

/// <summary>One page of diagnostics read from a <see cref="LogRing"/>.</summary>
/// <param name="Records">The records in the page, oldest first.</param>
/// <param name="NextSequence">The cursor to pass to the next read.</param>
/// <param name="Dropped">
/// Whether records before this page had already been evicted — the reader fell
/// behind, and saying so is the whole point of the sequence.
/// </param>
public sealed record LogPage(IReadOnlyList<LogRecord> Records, long NextSequence, bool Dropped);
