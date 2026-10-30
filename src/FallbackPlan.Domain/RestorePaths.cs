using System.Security.Cryptography;
using System.Text;
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

            // Windows does not refuse these; it writes them somewhere else, a
            // device or the name with its trailing period gone, and the
            // fence below cannot tell (ADR-0170).
            if (OperatingSystem.IsWindows() && !IsWindowsName(component, out why))
            {
                reason = $"the manifest path component cannot be made on Windows: {why}";
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
        // traverse it and escape though every component reads as plain. Follow
        // each folder below the root through its links and re-check: a real
        // path that leaves the root is refused before anything is written.
        if (EscapesThroughALink(root, resolved))
        {
            reason = "the path traverses a symlink that leaves the restore root";
            return false;
        }

        destination = resolved;
        return true;
    }

    /// <summary>
    /// Moves a verified <paramref name="spool"/> over whatever file stands at
    /// <paramref name="destination"/>, which a restore replaces whole. Windows
    /// refuses to replace a read-only file, and one an earlier restore gave
    /// its captured bits is one (ADR-0175), so the bit is cleared first; the
    /// file that lands takes its own bits back with the rest of its metadata.
    /// </summary>
    /// <param name="spool">The verified file, beside the destination.</param>
    /// <param name="destination">Where it lands.</param>
    public static void MoveOver(string spool, string destination)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(spool);
        ThrowHelper.ThrowIfNullOrWhiteSpace(destination);

        if (OperatingSystem.IsWindows()
            && File.Exists(destination)
            && File.GetAttributes(destination) is var standing
            && standing.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(destination, standing & ~FileAttributes.ReadOnly);
        }

        File.Move(spool, destination, overwrite: true);
    }

    /// <summary>
    /// Where a file that lands at <paramref name="destination"/> is written
    /// whole before it is moved there (ADR-0173): beside it, so the move is one
    /// rename, under a name of 33 characters whatever the destination's. The
    /// destination's own name may be as long as its target holds, so no name
    /// made by adding to it can be relied on to fit.
    /// </summary>
    /// <remarks>
    /// The name is made from the destination's own, so a destination has the
    /// same spool every time: one a run cut short left behind is the one the
    /// next run there writes over.
    /// </remarks>
    /// <param name="destination">The resolved path the file lands at.</param>
    /// <param name="owner">
    /// The writer, <c>restore</c> for the executor or <c>recover</c> for the
    /// recovery tool, so the two never share a spool.
    /// </param>
    /// <returns>The spool's path.</returns>
    public static string SpoolBeside(string destination, string owner)
    {
        ThrowHelper.ThrowIfNull(destination);
        ThrowHelper.ThrowIfNull(owner);

        return Path.Combine(Path.GetDirectoryName(destination)!, SpoolName(Path.GetFileName(destination), owner));
    }

    /// <summary>
    /// The name <see cref="SpoolBeside"/> gives the spool of a file named
    /// <paramref name="name"/>, in the file's own folder. It follows from the
    /// file's name alone, so an entry a snapshot names there is known before
    /// anything is written (ADR-0152 Amendment 4).
    /// </summary>
    /// <param name="name">The file's own name, one component.</param>
    /// <param name="owner">The writer, as <see cref="SpoolBeside"/> takes it.</param>
    /// <returns>The spool's name.</returns>
    public static string SpoolName(string name, string owner)
    {
        ThrowHelper.ThrowIfNull(name);
        ThrowHelper.ThrowIfNull(owner);

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(name), digest);
        return $".fbp-{owner}-{Convert.ToHexStringLower(digest[..8])}.tmp";
    }

    /// <summary>
    /// <paramref name="path"/> where it really is: each component that is a
    /// link on disk followed to where it leads, and the rest as written
    /// (ADR-0159).
    /// </summary>
    /// <remarks>
    /// A link's target is followed component by component in turn, so a
    /// target that itself passes through a link lands where that one leads.
    /// A chain longer than a platform follows is followed no further.
    /// </remarks>
    /// <param name="path">The path, absolute or relative to the working folder.</param>
    /// <returns>The path, followed through its links.</returns>
    public static string ThroughLinks(string path)
    {
        ThrowHelper.ThrowIfNull(path);

        var links = 0;
        return Follow(Path.GetFullPath(path), ref links);
    }

    /// <summary>The most links one path is followed through, as a platform's own walk bounds it.</summary>
    private const int MostLinksFollowed = 40;

    private static bool EscapesThroughALink(string root, string resolved)
    {
        // Lexical containment already proved `resolved` is under `root`. What is
        // left is a component between the root and the destination that exists
        // on disk as a link leading out of where the root really is. The root
        // is the person's own choice, so it is followed through its links
        // first and each component below it from there: a root that is itself
        // a link, or lies below one, fences where it really is (ADR-0159). A
        // component that leads out is refused though a later one leads back.
        var links = 0;
        var realRoot = Follow(Path.GetFullPath(root), ref links);
        var realFence = realRoot.EndsWith(Path.DirectorySeparatorChar) ? realRoot : realRoot + Path.DirectorySeparatorChar;

        var below = Path.GetRelativePath(root, Path.GetDirectoryName(resolved) ?? root);
        if (below == ".")
        {
            return false;
        }

        var followed = realRoot;
        foreach (var component in below.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            followed = Step(followed, component, ref links);
            if (!(followed + Path.DirectorySeparatorChar).StartsWith(realFence, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string Follow(string full, ref int links)
    {
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var followed = root;
        foreach (var component in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            followed = Step(followed, component, ref links);
        }

        return followed;
    }

    /// <summary>One component below a folder already followed, itself followed where it is a link.</summary>
    private static string Step(string followed, string component, ref int links)
    {
        var next = Path.Combine(followed, component);
        if (links >= MostLinksFollowed || LinkTargetOf(next) is not { } target)
        {
            return next;
        }

        links++;
        return Follow(Path.GetFullPath(target, followed), ref links);
    }

    private static string? LinkTargetOf(string path)
    {
        try
        {
            // A folder's link is read as a folder's, which Windows asks.
            return (Directory.Exists(path) ? new DirectoryInfo(path) : (FileSystemInfo)new FileInfo(path)).LinkTarget;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
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

    /// <summary>
    /// Whether Windows keeps <paramref name="component"/> as it is written
    /// (ADR-0170): not a device's name, with or without an extension; holding
    /// none of <c>&lt; &gt; : " / \ | ? *</c> or a control character; ending
    /// in neither a space nor a period; and no longer than 255 UTF-16 units.
    /// </summary>
    /// <remarks>
    /// Judged on any platform, so a plan made anywhere can say what a Windows
    /// target would not keep. A name another platform wrote can be any of
    /// these, and Windows refuses some and quietly alters the rest.
    /// </remarks>
    /// <param name="component">One component of a repository path. Untrusted.</param>
    /// <param name="why">Why it is not, in the code's own words.</param>
    public static bool IsWindowsName(string component, out string why)
    {
        ThrowHelper.ThrowIfNull(component);

        why = string.Empty;

        if (component.Length > MostWindowsComponentUnits)
        {
            why = $"the name is longer than the {MostWindowsComponentUnits} UTF-16 units Windows allows in one component";
            return false;
        }

        if (component.Any(character => character < ' ' || WindowsRefusedCharacters.Contains(character)))
        {
            why = "the name holds a character Windows refuses in a name";
            return false;
        }

        if (component.EndsWith(' ') || component.EndsWith('.'))
        {
            why = "the name ends in a space or a period, which Windows strips";
            return false;
        }

        // A device's name names the device whatever extension follows it.
        var stem = component.Split('.')[0].TrimEnd(' ');
        if (WindowsDeviceNames.Contains(stem))
        {
            why = "the name is reserved for a device on Windows";
            return false;
        }

        return true;
    }

    private const int MostWindowsComponentUnits = 255;

    private const string WindowsRefusedCharacters = "<>:\"/\\|?*";

    private static readonly HashSet<string> WindowsDeviceNames = new(
        ["CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³"],
        StringComparer.OrdinalIgnoreCase);
}
