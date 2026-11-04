using System.Globalization;

namespace FallbackPlan.Filesystem.Local;

/// <summary>
/// Which folders a replay of a macOS volume's event history names, and what
/// in it is doubt (ADR-0191).
/// </summary>
/// <remarks>
/// It is a rule over the events a replay gives, not a platform call, so it
/// is the same everywhere and tested everywhere.
/// </remarks>
internal static class FsEventsRecords
{
    /// <summary>The history is done replaying; what follows is new.</summary>
    public const uint HistoryDone = 0x10;

    private const uint MustScanSubDirs = 0x01;
    private const uint UserDropped = 0x02;
    private const uint KernelDropped = 0x04;
    private const uint EventIdsWrapped = 0x08;
    private const uint RootChanged = 0x20;
    private const uint Mount = 0x40;
    private const uint Unmount = 0x80;
    private const uint ItemIsFile = 0x10000;
    private const uint ItemIsSymlink = 0x40000;
    private const uint ItemIsHardlink = 0x100000;

    private const uint LostTrack = MustScanSubDirs | UserDropped | KernelDropped | EventIdsWrapped | RootChanged | Mount | Unmount;

    /// <summary>
    /// A volume's history as a place carries it: its identity as the
    /// system spells it, and the identity's first eight bytes as the
    /// journal's.
    /// </summary>
    public static (string Volume, ulong Id) History(string uuid)
    {
        var hex = uuid.Replace("-", string.Empty, StringComparison.Ordinal);
        return ($"fsevents:{uuid}", ulong.Parse(hex.AsSpan(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The folders <paramref name="events"/> name under
    /// <paramref name="roots"/>, each a root as the set names it and as the
    /// system spells it; or null with <paramref name="doubt"/> saying why
    /// they cannot be trusted.
    /// </summary>
    /// <param name="events">Each path heard and its flags, in the order heard.</param>
    /// <param name="roots">Each root as the set names it, and as the system spells it.</param>
    /// <param name="marks">The folder this process writes its marks in, never named.</param>
    /// <param name="hasAnotherName">Whether a file has another name now.</param>
    /// <param name="spelledOnDisk">A path as its folders are spelled on disk, or null where it cannot be.</param>
    /// <param name="doubt">Why the events cannot be trusted, or null.</param>
    public static IReadOnlyList<ChangedFolder>? Named(
        IEnumerable<(string Path, uint Flags)> events,
        IReadOnlyList<(string Root, string Real)> roots,
        string marks,
        Func<string, bool> hasAnotherName,
        Func<string, string?> spelledOnDisk,
        out string? doubt)
    {
        var named = new HashSet<ChangedFolder>();
        var spelled = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (path, flags) in events)
        {
            if ((flags & LostTrack) != 0)
            {
                doubt = string.Create(CultureInfo.InvariantCulture, $"the history lost track at '{path}' (flags 0x{flags:X})");
                return null;
            }

            if (Below(marks, path) is not null)
            {
                continue;
            }

            var isFile = (flags & ItemIsFile) != 0;
            if (isFile && ((flags & ItemIsHardlink) != 0 || hasAnotherName(path)))
            {
                doubt = $"a file with another name changed: '{path}'";
                return null;
            }

            // A file or a link names the folder holding it; anything else
            // names itself, and a run walks every folder above a path it is
            // told of.
            var at = path.TrimEnd('/');
            if (isFile || (flags & ItemIsSymlink) != 0)
            {
                var slash = at.LastIndexOf('/');
                at = slash > 0 ? at[..slash] : "/";
            }

            if (!roots.Any(root => Below(root.Real, at) is not null))
            {
                continue;
            }

            // The run is told of a folder as the walk spells it, which is as
            // it is on disk, whatever case the history heard it in.
            if (!spelled.TryGetValue(at, out var onDisk))
            {
                onDisk = spelled[at] = spelledOnDisk(at);
            }

            if (onDisk is null)
            {
                doubt = $"'{at}' could not be spelled as it is on disk";
                return null;
            }

            var under = false;
            foreach (var (root, real) in roots)
            {
                if (Below(real, onDisk) is { } relative)
                {
                    named.Add(new ChangedFolder(root, relative));
                    under = true;
                }
            }

            if (!under)
            {
                doubt = $"'{at}' is spelled on disk outside every root, as '{onDisk}'";
                return null;
            }
        }

        doubt = null;
        return [.. named];
    }

    // Folders on the volumes macOS makes are named without regard to case,
    // as the history spells them; a sibling differing only in case on a
    // volume that does regard it is over-named, which costs a walk.
    private static string? Below(string root, string path)
    {
        var trimmed = path.TrimEnd('/');
        if (string.Equals(root, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var prefix = root.EndsWith('/') ? root : root + "/";
        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? trimmed[prefix.Length..] : null;
    }
}
