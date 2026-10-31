using Bodu;

namespace FallbackPlan.Domain;

/// <summary>
/// Whether, and how, a restore makes a symlink a link (FR-RST-004; ADR-0181;
/// architecture 06 §3, "preserve, privilege permitting"). Linux and macOS let
/// any account make one. Windows lets an account make one where it holds the
/// privilege to, or where the machine is in developer mode, and asks of each
/// link whether it is a folder's or a file's.
/// </summary>
public static class SymbolicLinks
{
    /// <summary>A Windows attribute bit: the entry is a folder, or a link made as a folder's.</summary>
    private const uint FolderAttribute = 0x10;

    private static readonly Lazy<bool> Creatable = new(Probe);

    /// <summary>
    /// Whether this process may make a link: everywhere but Windows, and
    /// there where making one in the temporary folder succeeds. The answer is
    /// the account's, so it is asked once.
    /// </summary>
    public static bool CanCreate => Creatable.Value;

    /// <summary>
    /// <paramref name="target"/> with each separator Windows' own. A link
    /// captured on Linux or macOS names its target with forward slashes,
    /// which Windows does not resolve in a link, and which no Windows name
    /// may hold, so each is a separator.
    /// </summary>
    public static string TargetOnWindows(string target)
    {
        ThrowHelper.ThrowIfNull(target);
        return target.Replace('/', '\\');
    }

    /// <summary>
    /// Whether a link is made as a folder's: as its captured Windows
    /// attribute bits say where Windows captured it, since a link there is
    /// one or the other whatever it names; otherwise as
    /// <paramref name="namesAFolder"/> answers of what it names.
    /// </summary>
    /// <param name="capturedAttributes">The link's captured Windows attribute bits, or null where none were captured.</param>
    /// <param name="namesAFolder">Whether what the link names, made by the restore or standing, is a folder.</param>
    public static bool PointsAtAFolder(uint? capturedAttributes, Func<bool> namesAFolder)
    {
        ThrowHelper.ThrowIfNull(namesAFolder);
        return capturedAttributes is { } bits ? (bits & FolderAttribute) != 0 : namesAFolder();
    }

    /// <summary>
    /// The full path the link at <paramref name="link"/> names by
    /// <paramref name="target"/>: from the folder the link is in, or where a
    /// target from the root says.
    /// </summary>
    public static string PathNamed(string link, string target)
    {
        ThrowHelper.ThrowIfNull(link);
        ThrowHelper.ThrowIfNull(target);
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(link)!, target));
    }

    /// <summary>
    /// Makes the link <paramref name="path"/> to <paramref name="target"/>,
    /// as a folder's where <paramref name="folder"/> says so, which only
    /// Windows distinguishes.
    /// </summary>
    public static void Create(string path, string target, bool folder)
    {
        if (folder && OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(path, target);
        }
        else
        {
            File.CreateSymbolicLink(path, target);
        }
    }

    private static bool Probe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        var probe = Path.Combine(Path.GetTempPath(), "fallbackplan-link-probe-" + Guid.NewGuid().ToString("n"));
        try
        {
            File.CreateSymbolicLink(probe, "nowhere");
            File.Delete(probe);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
