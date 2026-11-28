using System.Collections;

namespace FallbackPlan.Repository;

/// <summary>
/// A file's segment references, or their content identifiers, as its archive
/// collects them: at most <see cref="ArchiveSession.ReferencesHeldPerFile"/>
/// in memory, each full chunk before them in the session's spill
/// (ADR-0201 Amendment 3). Read in order, it reads the spill a chunk at a
/// time, so walking a file of any size holds one chunk.
/// </summary>
/// <remarks>
/// One writer adds while the file is archived (the session's barrier), and
/// readers read once it has been. Readable while the session runs: a chunk
/// in the spill is gone once the session has ended, as every spilled list is.
/// </remarks>
internal sealed class SpooledList<T> : IReadOnlyList<T>
{
    private readonly Func<IReadOnlyList<T>, IReadOnlyList<T>> _spill;
    private readonly int _chunk;
    private readonly List<IReadOnlyList<T>> _spilled = [];
    private readonly List<int> _starts = [];
    private List<T> _pending = [];
    private int _spilledCount;

    /// <param name="spill">Writes a chunk to the spill and returns it as read back from there.</param>
    /// <param name="chunk">How many are held before they are spilled.</param>
    public SpooledList(Func<IReadOnlyList<T>, IReadOnlyList<T>> spill, int chunk)
    {
        ArgumentNullException.ThrowIfNull(spill);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunk);
        _spill = spill;
        _chunk = chunk;
    }

    /// <inheritdoc />
    public int Count => _spilledCount + _pending.Count;

    /// <summary>Whether nothing of it is held in memory.</summary>
    public bool AllSpilled => _pending.Count == 0;

    /// <inheritdoc />
    public T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            if (index >= _spilledCount)
            {
                return _pending[index - _spilledCount];
            }

            // The chunk whose start is the last at or before the index; a
            // chunk spilled short is the last, so the search finds it too.
            var found = _starts.BinarySearch(index);
            var chunk = found >= 0 ? found : ~found - 1;
            return _spilled[chunk][index - _starts[chunk]];
        }
    }

    /// <summary>Adds the next one, spilling the chunk it fills.</summary>
    public void Add(T item)
    {
        _pending.Add(item);
        if (_pending.Count == _chunk)
        {
            Spill();
        }
    }

    /// <summary>Spills what is held, so that nothing of it stays in memory.</summary>
    public void Spill()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        _starts.Add(_spilledCount);
        _spilled.Add(_spill(_pending));
        _spilledCount += _pending.Count;

        // A new list, not a cleared one: a cleared list keeps its capacity,
        // which is the chunk this exists not to hold.
        _pending = [];
    }

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator()
    {
        foreach (var chunk in _spilled)
        {
            foreach (var item in chunk)
            {
                yield return item;
            }
        }

        foreach (var item in _pending)
        {
            yield return item;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
