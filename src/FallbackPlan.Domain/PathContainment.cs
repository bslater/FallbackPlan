using Bodu;

namespace FallbackPlan.Domain;

/// <summary>
/// The lexical containment judgement behind the circular-capture guard
/// (FR-DEST-011): whether one filesystem path lies at or under another,
/// decided from the strings alone.
/// </summary>
/// <remarks>
/// <para>
/// The fence is <c>RestoreExecutor</c>'s: normalise both paths, append a
/// trailing separator to the ancestor, and compare prefixes — the separator
/// is what keeps <c>/data/docs-old</c> outside <c>/data/docs</c>. What is
/// deliberately NOT here is the symlink-resolving half that restore also
/// runs: this judgement is called from configuration boundaries that must
/// never touch the disk, so a layout that only a link makes circular is out
/// of scope and recorded as such in ADR-0034's amendment.
/// </para>
/// <para>
/// Comparison folds case, whatever the platform: the conservative direction
/// (the <c>MultiRootScan</c> precedent) never mistakes two spellings for two
/// folders. A false positive refuses a save with a stated reason; a false
/// negative captures a backup into itself.
/// </para>
/// </remarks>
public static class PathContainment
{
    /// <summary>
    /// Whether <paramref name="candidate"/> is <paramref name="ancestor"/> or
    /// lies under it. A path that is not rooted is never judged — its meaning
    /// depends on a working directory this method must not consult.
    /// </summary>
    /// <param name="ancestor">The path that would contain.</param>
    /// <param name="candidate">The path that would be contained.</param>
    public static bool IsAtOrUnder(string ancestor, string candidate)
    {
        ThrowHelper.ThrowIfNull(ancestor);
        ThrowHelper.ThrowIfNull(candidate);

        if (!Path.IsPathRooted(ancestor) || !Path.IsPathRooted(candidate))
        {
            return false;
        }

        // GetFullPath on already-rooted input is lexical normalisation only
        // (. and .. segments, separator shape) — no filesystem access.
        var fence = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ancestor))
            + Path.DirectorySeparatorChar;
        var resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate))
            + Path.DirectorySeparatorChar;

        return resolved.StartsWith(fence, StringComparison.OrdinalIgnoreCase);
    }
}
