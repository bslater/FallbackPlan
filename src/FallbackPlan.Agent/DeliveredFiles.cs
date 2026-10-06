using System.Collections.Concurrent;
using FallbackPlan.Repository.Catalogue;

namespace FallbackPlan.Agent;

/// <summary>
/// What each destination holds of its set's newest backup, worked out from
/// the ledger's watermark and remembered until either moves (ADR-0088
/// Amendment 1). A status poll comes every few seconds, while the answer
/// changes only when a backup is published or a sync succeeds.
/// </summary>
public sealed class DeliveredFiles
{
    private readonly ConcurrentDictionary<(string SetId, string Destination), (string Snapshot, ulong Synced, FilesDelivered Files)> _known = new();

    /// <summary>The answer for one destination, from memory when nothing it rests on has moved.</summary>
    /// <param name="setId">The set.</param>
    /// <param name="destination">The destination.</param>
    /// <param name="snapshot">The set's newest snapshot, by lowercase hex identity.</param>
    /// <param name="synced">The destination's watermark.</param>
    /// <param name="work">Works the answer out when it is not remembered.</param>
    public FilesDelivered Get(
        string setId, string destination, string snapshot, ulong synced, Func<FilesDelivered> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (_known.TryGetValue((setId, destination), out var known)
            && known.Synced == synced && string.Equals(known.Snapshot, snapshot, StringComparison.Ordinal))
        {
            return known.Files;
        }

        var files = work();
        _known[(setId, destination)] = (snapshot, synced, files);
        return files;
    }
}
