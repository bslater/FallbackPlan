using System.Globalization;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Repository.Index;

/// <summary>The persisted state of a writer's sequence space.</summary>
public sealed record SequenceState(ulong NextSequence, IReadOnlyList<ulong> PendingSequences, DeltaId? LastDeltaId)
{
    /// <summary>A fresh writer: the first allocation returns 1.</summary>
    public static readonly SequenceState Initial = new(1, [], null);
}

/// <summary>Durable storage for <see cref="WriterSequence"/> state.</summary>
public interface ISequenceStateStore
{
    /// <summary>Loads the persisted state, or <see cref="SequenceState.Initial"/> when none exists.</summary>
    SequenceState Load();

    /// <summary>Persists <paramref name="state"/> durably before the allocation it covers is used.</summary>
    void Save(SequenceState state);
}

/// <summary>A file-backed state store: one small text file, replaced atomically.</summary>
public sealed class FileSequenceStateStore : ISequenceStateStore
{
    private readonly string _path;

    /// <summary>Creates a store at <paramref name="path"/>.</summary>
    public FileSequenceStateStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <inheritdoc />
    public SequenceState Load()
    {
        if (!File.Exists(_path))
        {
            return SequenceState.Initial;
        }

        var next = SequenceState.Initial.NextSequence;
        var pending = new List<ulong>();
        DeltaId? lastDelta = null;

        foreach (var line in File.ReadAllLines(_path))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var key = line[..separator];
            var value = line[(separator + 1)..];

            switch (key)
            {
                case "next":
                    next = ulong.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "pending" when value.Length > 0:
                    pending.AddRange(value.Split(',').Select(item => ulong.Parse(item, CultureInfo.InvariantCulture)));
                    break;
                case "last-delta" when value.Length > 0:
                    lastDelta = DeltaId.FromBytes(Convert.FromHexString(value));
                    break;
                default:
                    break;
            }
        }

        return new SequenceState(next, pending, lastDelta);
    }

    /// <inheritdoc />
    public void Save(SequenceState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);

        var content =
            $"next={state.NextSequence.ToString(CultureInfo.InvariantCulture)}\n" +
            $"pending={string.Join(',', state.PendingSequences.Select(sequence => sequence.ToString(CultureInfo.InvariantCulture)))}\n" +
            $"last-delta={(state.LastDeltaId is { } delta ? Convert.ToHexStringLower(delta.ToArray()) : string.Empty)}\n";

        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, _path, overwrite: true);
    }
}

/// <summary>
/// The writer's <b>single</b> monotonic gapless sequence space
/// (specification 08 §2, 02 §4): journal records, index deltas, and blob
/// counters all draw from it, so a gap is detectable regardless of which
/// kind of object is missing. A number is <em>pending</em> from allocation
/// until its accounting object — a delta, a journal record, a void delta, or
/// a blob named by a durable intent — exists (ADR-0022 §Decision 7); numbers
/// still pending after a crash are void-delta obligations (07 §4).
/// </summary>
public sealed class WriterSequence : IBlobCounterAllocator
{
    private readonly ISequenceStateStore _store;
    private readonly Lock _gate = new();
    private ulong _next;
    private readonly List<ulong> _pending;
    private DeltaId? _lastDeltaId;

    /// <summary>Loads the writer's state; anything pending at load is a crash leftover.</summary>
    public WriterSequence(ISequenceStateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;

        var state = store.Load();
        _next = state.NextSequence;
        _pending = [.. state.PendingSequences];
        _lastDeltaId = state.LastDeltaId;
        RecoveredObligations = [.. state.PendingSequences];
    }

    /// <summary>
    /// Sequence numbers allocated by a previous run and never accounted for —
    /// each one MUST get a void delta so readers can distinguish "skipped"
    /// from "missing" (specification 07 §4).
    /// </summary>
    public IReadOnlyList<ulong> RecoveredObligations { get; }

    /// <summary>The writer's most recently published delta, for predecessor chaining.</summary>
    public DeltaId? LastDeltaId
    {
        get
        {
            lock (_gate)
            {
                return _lastDeltaId;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>The pending mark is durable <b>before</b> the number is returned — an allocation the disk never saw could not get its void delta.</remarks>
    public ulong AllocateNext()
    {
        lock (_gate)
        {
            var sequence = _next;
            _next++;
            _pending.Add(sequence);
            Persist();
            return sequence;
        }
    }

    /// <summary>
    /// Marks <paramref name="sequence"/> accounted for — its delta, journal
    /// record, void delta, or intent-covered blob is durable.
    /// </summary>
    public void MarkAccounted(ulong sequence, DeltaId? publishedDelta = null)
    {
        lock (_gate)
        {
            _pending.Remove(sequence);

            if (publishedDelta is { } delta)
            {
                _lastDeltaId = delta;
            }

            Persist();
        }
    }

    private void Persist() => _store.Save(new SequenceState(_next, [.. _pending], _lastDeltaId));
}
