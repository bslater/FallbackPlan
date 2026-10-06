using Bodu;

namespace FallbackPlan.Agent;

/// <summary>
/// Which generation of each set's capture settings — its roots and rules — is
/// current, and which a completed backup has captured (FR-SVC-009, ADR-0038
/// Amendment 2). A material edit moves the set on a generation; a run captures
/// the generation current when it was queued, because it captures the settings
/// it was queued with. The set-changed notice stands while the current
/// generation is uncaptured: a run queued before the edit cannot resolve it,
/// and a rescan that finishes after the run that captured its edit does not
/// raise it again.
/// </summary>
/// <remarks>
/// In memory only. After a restart every set starts again at generation zero,
/// and every run queued then captures the configuration as it stands — which
/// is what a standing notice is about.
/// </remarks>
internal sealed class SetSettingsGenerations
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _sets = new(StringComparer.Ordinal);

    /// <summary>Records a material edit: the set's settings move on a generation.</summary>
    /// <param name="setId">The set edited.</param>
    /// <returns>The generation the edit made current.</returns>
    public long Advance(string setId)
    {
        lock (_gate)
        {
            return ++EntryOf(setId).Current;
        }
    }

    /// <summary>The set's current generation: the one a run queued now captures.</summary>
    /// <param name="setId">The set.</param>
    /// <returns>The generation.</returns>
    public long CurrentOf(string setId)
    {
        lock (_gate)
        {
            return _sets.TryGetValue(setId, out var entry) ? entry.Current : 0;
        }
    }

    /// <summary>
    /// A run that captured <paramref name="generation"/> has committed: when
    /// that is still the current generation, <paramref name="resolve"/> runs,
    /// under the same lock a rescan's finding is judged by.
    /// </summary>
    /// <param name="setId">The set.</param>
    /// <param name="generation">The generation the run captured.</param>
    /// <param name="resolve">Resolves the set's notice.</param>
    public void Captured(string setId, long generation, Action resolve)
    {
        ThrowHelper.ThrowIfNull(resolve);
        lock (_gate)
        {
            var entry = EntryOf(setId);
            entry.Captured = Math.Max(entry.Captured, generation);
            if (generation >= entry.Current)
            {
                resolve();
            }
        }
    }

    /// <summary>
    /// A rescan of <paramref name="generation"/> has a finding:
    /// <paramref name="raise"/> runs only while that generation is current and
    /// no committed run has captured it. A rescan of superseded settings stays
    /// silent; the newer edit's rescan speaks for the set.
    /// </summary>
    /// <param name="setId">The set.</param>
    /// <param name="generation">The generation the rescan compared.</param>
    /// <param name="raise">Raises or refreshes the set's notice.</param>
    public void Found(string setId, long generation, Action raise)
    {
        ThrowHelper.ThrowIfNull(raise);
        lock (_gate)
        {
            var entry = EntryOf(setId);
            if (generation == entry.Current && entry.Captured < generation)
            {
                raise();
            }
        }
    }

    /// <summary>
    /// Asks for a run under the current settings once the set's run under way
    /// settles. Called under the scheduler's enqueue gate, which is how the
    /// request and the settling are kept in order.
    /// </summary>
    /// <param name="setId">The set.</param>
    public void RequestFollowUp(string setId)
    {
        lock (_gate)
        {
            EntryOf(setId).FollowUp = true;
        }
    }

    /// <summary>Takes the set's follow-up request, if one stands.</summary>
    /// <param name="setId">The set.</param>
    /// <returns>Whether a run under the current settings is owed.</returns>
    public bool TakeFollowUp(string setId)
    {
        lock (_gate)
        {
            if (!_sets.TryGetValue(setId, out var entry) || !entry.FollowUp)
            {
                return false;
            }

            entry.FollowUp = false;
            return true;
        }
    }

    private Entry EntryOf(string setId)
    {
        if (!_sets.TryGetValue(setId, out var entry))
        {
            entry = new Entry();
            _sets[setId] = entry;
        }

        return entry;
    }

    private sealed class Entry
    {
        public long Current { get; set; }

        public long Captured { get; set; }

        public bool FollowUp { get; set; }
    }
}
