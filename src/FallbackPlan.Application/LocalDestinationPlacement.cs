using Bodu;

namespace FallbackPlan.Application;

/// <summary>
/// What a placement judgement found: the first root the destination fails
/// against, and how — sharing its volume, or (a weaker but still refused
/// finding) sharing its physical drive across distinct volumes.
/// </summary>
/// <param name="Root">The set root the destination conflicts with.</param>
/// <param name="SamePhysicalDisk">
/// True when the volumes differ but the platform says they live on one
/// physical drive; false for the sharper same-volume finding.
/// </param>
public sealed record PlacementConflict(string Root, bool SamePhysicalDisk);

/// <summary>
/// The condition of choosing a local destination (ADR-0051, FR-DEST-017): a
/// backup on the drive the source lives on dies with it, so a local-path
/// destination must sit on a different volume than every root — and, where
/// the platform can name physical drives, on a different drive. Judged
/// purely with the platform probes injected; the command boundary supplies
/// the real ones and composes the refusal.
/// </summary>
public static class LocalDestinationPlacement
{
    /// <summary>Judges one destination path against a set's roots.</summary>
    /// <param name="roots">The set's root paths.</param>
    /// <param name="destinationPath">The local-path destination's directory.</param>
    /// <param name="volumeIdOf">The volume a path sits on, or null when the platform will not say.</param>
    /// <param name="diskIdOf">The physical drive behind a path's volume, or null when it cannot be named — a multi-device volume, a network mount, a platform without the probe.</param>
    /// <returns>The first conflict, or null when the placement is acceptable.</returns>
    /// <remarks>
    /// Unknowable answers never refuse: a volume the platform cannot
    /// identify is not refused on a guess (the status derivation stays
    /// conservative for it instead), and distinct volumes whose drives
    /// cannot be named are accepted — "a different physical drive where
    /// possible" is the condition, and volume separation is its hard core.
    /// </remarks>
    public static PlacementConflict? Judge(
        IReadOnlyList<string> roots,
        string destinationPath,
        Func<string, ulong?> volumeIdOf,
        Func<string, string?> diskIdOf)
    {
        ThrowHelper.ThrowIfNull(roots);
        ThrowHelper.ThrowIfNullOrWhiteSpace(destinationPath);
        ThrowHelper.ThrowIfNull(volumeIdOf);
        ThrowHelper.ThrowIfNull(diskIdOf);

        if (volumeIdOf(destinationPath) is not { } destinationVolume)
        {
            return null;
        }

        var destinationDisk = diskIdOf(destinationPath);

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || volumeIdOf(root) is not { } rootVolume)
            {
                continue;
            }

            if (rootVolume == destinationVolume)
            {
                return new PlacementConflict(root, SamePhysicalDisk: false);
            }

            if (destinationDisk is not null
                && diskIdOf(root) is { } rootDisk
                && string.Equals(rootDisk, destinationDisk, StringComparison.Ordinal))
            {
                return new PlacementConflict(root, SamePhysicalDisk: true);
            }
        }

        return null;
    }
}
