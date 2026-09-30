using Bodu;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Catalogue;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Repository;

/// <summary>
/// Traces damaged blobs, named by the store keys a deep sweep or a restore
/// reports them under, to what needs them: the snapshots a restore of which
/// would meet the damage, and the paths in them (FR-VER-005, specification
/// 04 §7).
/// </summary>
/// <remarks>
/// A store key is the keyed rendering of a blob's identity (specification 02
/// §4.3), and a catalogue rebuilt from the index plane records no store key
/// for most blobs, so each located blob's key is derived here and compared.
/// A key that matches no blob the catalogue locates anything in is untraced
/// rather than ignored: the blob could be garbage, or what another writer's
/// snapshot needs, and not knowing which is not evidence that nothing needs
/// it.
/// </remarks>
public static class DamageScope
{
    /// <summary>How many paths a reach names when the caller does not say.</summary>
    public const int DefaultSample = 5;

    /// <summary>What the blobs stored under <paramref name="damagedKeys"/> reach.</summary>
    /// <param name="catalogue">The set's catalogue, read here and never written but for a scratch table.</param>
    /// <param name="keys">The set's keys, for the store keys' derivation.</param>
    /// <param name="damagedKeys">The store keys of the damaged blobs, as <c>blobs/&lt;class&gt;/&lt;shard&gt;/&lt;key&gt;</c>.</param>
    /// <param name="sampleLimit">How many paths to name.</param>
    public static DamageReach Trace(
        Catalogue.Catalogue catalogue, RepositoryKeySet keys, IEnumerable<string> damagedKeys, int sampleLimit = DefaultSample)
    {
        ThrowHelper.ThrowIfNull(catalogue);
        ThrowHelper.ThrowIfNull(keys);
        ThrowHelper.ThrowIfNull(damagedKeys);

        var wanted = new HashSet<string>(damagedKeys.Select(Rendered), StringComparer.Ordinal);
        if (wanted.Count == 0)
        {
            return DamageReach.None;
        }

        using var derive = new StoreBlobKeyDeriver(keys.KeyIdKey);
        var blobs = new List<BlobId>();
        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var blob in catalogue.LocatedBlobs())
        {
            var rendered = derive.Derive(blob).ToBase32();
            if (wanted.Contains(rendered))
            {
                blobs.Add(blob);
                matched.Add(rendered);
            }
        }

        var reach = catalogue.ReachOf(blobs, sampleLimit);
        return reach with { Untraced = reach.Untraced + (wanted.Count - matched.Count) };
    }

    /// <summary>A store key's last segment: the blob key's own rendering, whichever class and shard it is filed under.</summary>
    private static string Rendered(string storeKey) => storeKey[(storeKey.LastIndexOf('/') + 1)..];
}
