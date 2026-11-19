namespace FallbackPlan.Agent;

/// <summary>
/// The blobs a restore of one snapshot reads, by their store names, crossed
/// off as a sync finds each at its destination
/// ([ADR-0198](../../docs/adr/0198-a-sync-copies-only-from-a-source-that-holds-the-newest-backup-whole.md)
/// Amendment 2). A destination is whole for the snapshot once none is left.
/// </summary>
/// <remarks>
/// <para>
/// Fed what the destination declared holding before the sync and each key it
/// took during it, as the file count is, so a destination already holding
/// most of the backup is judged from where it stands.
/// </para>
/// <para>
/// A blob is matched by its keyed store name alone, the last segment of its
/// store key, and not by the class segment before it: a catalogue rebuilt
/// from the index does not always know a blob's class, and the name is the
/// blob's own either way.
/// </para>
/// </remarks>
internal sealed class SnapshotClosure
{
    private const string BlobPrefix = "blobs/";

    private readonly HashSet<string> _lacking;

    /// <summary>Starts with every blob a restore of the snapshot reads lacking.</summary>
    /// <param name="names">The keyed store names of the blobs the snapshot needs.</param>
    public SnapshotClosure(IEnumerable<string> names) =>
        _lacking = new HashSet<string>(names, StringComparer.Ordinal);

    /// <summary>How many of the snapshot's blobs the destination has not been found holding.</summary>
    public int Lacking => _lacking.Count;

    /// <summary>The first of them by name, or null when none is lacking.</summary>
    public string? FirstLacking => _lacking.Count == 0 ? null : _lacking.Min(StringComparer.Ordinal);

    /// <summary>The destination holds <paramref name="key"/>.</summary>
    /// <param name="key">A store key it holds, whether or not the snapshot needs it.</param>
    public void Landed(string key)
    {
        if (key.StartsWith(BlobPrefix, StringComparison.Ordinal))
        {
            _lacking.Remove(key[(key.LastIndexOf('/') + 1)..]);
        }
    }
}
