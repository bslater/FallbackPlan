using Bodu;

namespace FallbackPlan.Domain;

/// <summary>
/// Where a restore writes a path a repository supplies, under the folder it
/// restores into: the rule every way of restoring holds to (ADR-0152). The
/// executor and the recovery tool both write names from a repository's
/// trees, and a repository is exactly as untrusted for the one as for the
/// other.
/// </summary>
/// <remarks>
/// It lives in Domain because the recovery tool may not reference the
/// restore project (architecture 11 §2), and two copies of a containment
/// rule drift apart.
/// </remarks>
public static class RestorePaths
{
    /// <summary>
    /// Resolves one repository path under <paramref name="root"/>, refusing
    /// anything that is not a sequence of plain name components.
    /// </summary>
    /// <param name="root">The already-resolved containment root.</param>
    /// <param name="path">The repository-supplied path. Untrusted.</param>
    /// <param name="destination">The resolved destination when this returns true.</param>
    /// <param name="reason">Why it was refused, in the code's own words.</param>
    /// <returns><see langword="true"/> when the path is safe to materialise.</returns>
    /// <remarks>
    /// Component validation is the boundary; the containment check underneath
    /// it is defence in depth, not the mechanism. A check that only compares
    /// the combined string against a prefix is defeated by anything the
    /// platform normalises differently from the checker — which is most of the
    /// interesting cases.
    /// </remarks>
    public static bool TryResolve(
        string root, string path, out string destination, out string reason)
    {
        ThrowHelper.ThrowIfNull(root);
        ThrowHelper.ThrowIfNull(path);
        destination = string.Empty;
        reason = string.Empty;

        if (path.Length == 0)
        {
            reason = "the manifest names an empty path";
            return false;
        }

        var built = root;
        foreach (var component in path.Split('/'))
        {
            if (!IsPlainComponent(component, out var why))
            {
                reason = why;
                return false;
            }

            built = Path.Combine(built, component);
        }

        var resolved = Path.GetFullPath(built);
        var fence = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        if (!resolved.StartsWith(fence, StringComparison.Ordinal))
        {
            reason = "the path resolves outside the restore root";
            return false;
        }

        // GetFullPath is lexical — it normalises `..` and `.` but does not
        // follow symlinks. A link inside the root pointing out of it (seeded by
        // an attacker, or restored by an earlier item) would let a later path
        // traverse it and escape though every component reads as plain. Resolve
        // the deepest existing ancestor through its links and re-check: a real
        // path that leaves the root is refused before anything is written.
        if (EscapesThroughALink(root, resolved))
        {
            reason = "the path traverses a symlink that leaves the restore root";
            return false;
        }

        destination = resolved;
        return true;
    }

    private static bool EscapesThroughALink(string root, string resolved)
    {
        // Lexical containment already proved `resolved` is under `root`. What is
        // left is a component *between* the root and the destination that exists
        // on disk as a symlink leaving the root — the only way a link can be
        // traversed by this write. The root itself is the executor's own
        // directory and is not suspect; anything at or above it is out of scope.
        var realRootFence = RealPath(root) + Path.DirectorySeparatorChar;
        var rootFence = (root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar);

        var component = Path.GetDirectoryName(resolved);
        while (!string.IsNullOrEmpty(component)
            && component.Length >= rootFence.Length
            && (component + Path.DirectorySeparatorChar).StartsWith(rootFence, StringComparison.Ordinal))
        {
            if (Directory.Exists(component) || File.Exists(component))
            {
                var real = RealPath(component) + Path.DirectorySeparatorChar;
                if (!real.StartsWith(realRootFence, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            component = Path.GetDirectoryName(component);
        }

        return false;
    }

    private static string RealPath(string path)
    {
        // ResolveLinkTarget with returnFinalTarget follows a chain of links to
        // the real object; a non-link returns null and the path stands.
        try
        {
            var resolved = Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName
                ?? File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
            return resolved is null ? Path.GetFullPath(path) : Path.GetFullPath(resolved);
        }
        catch (IOException)
        {
            return Path.GetFullPath(path);
        }
    }

    /// <summary>
    /// Whether <paramref name="component"/> is one plain name: not empty, not
    /// <c>.</c> or <c>..</c>, and holding no NUL, separator, drive marker or
    /// root.
    /// </summary>
    /// <param name="component">One component of a repository path. Untrusted.</param>
    /// <param name="why">Why it is not, in the code's own words.</param>
    public static bool IsPlainComponent(string component, out string why)
    {
        ThrowHelper.ThrowIfNull(component);

        why = string.Empty;

        if (component.Length == 0)
        {
            why = "the manifest path has an empty component";
            return false;
        }

        if (component is "." or "..")
        {
            why = $"the manifest path contains a '{component}' component";
            return false;
        }

        if (component.Contains('\0', StringComparison.Ordinal))
        {
            why = "the manifest path contains a NUL";
            return false;
        }

        // Separators, drive-relative and UNC forms cannot appear inside one
        // component. A backslash is a separator on Windows and a legal
        // filename byte on POSIX, so it is refused on both: a name that means
        // two different shapes on two platforms is one this cannot restore
        // predictably on either.
        if (component.Contains('/', StringComparison.Ordinal)
            || component.Contains('\\', StringComparison.Ordinal)
            || component.Contains(':', StringComparison.Ordinal))
        {
            why = "the manifest path component contains a separator or drive marker";
            return false;
        }

        if (Path.IsPathRooted(component))
        {
            why = "the manifest path component is rooted";
            return false;
        }

        return true;
    }
}
