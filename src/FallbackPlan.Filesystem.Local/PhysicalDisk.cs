namespace FallbackPlan.Filesystem.Local;

/// <summary>
/// Names the physical drive behind a path, where the platform can
/// (ADR-0051's "different physical hdd where possible"): on Linux the
/// answer comes from sysfs — a partition's block device resolves to its
/// parent disk — and everywhere it cannot be given honestly the answer is
/// null, never a guess: a tmpfs or network mount has no disk, a
/// device-mapper volume may span several, and other platforms wait for
/// their own probe.
/// </summary>
public static class PhysicalDisk
{
    /// <summary>The drive's stable name (e.g. <c>sda</c>, <c>nvme0n1</c>), or null when it cannot be named.</summary>
    /// <param name="path">Any path on the volume; the nearest existing ancestor is consulted.</param>
    public static string? Identify(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !OperatingSystem.IsLinux())
        {
            return null;
        }

        var probe = NearestExisting(path);
        if (probe is null || !LocalFileSystemSource.TryStat(probe, out var stat))
        {
            return null;
        }

        // Linux dev_t packing (glibc): major spans bits 8..19 and 32..43,
        // minor bits 0..7 and 20..31.
        var device = stat.Device;
        var major = (uint)(((device >> 8) & 0xfff) | ((device >> 32) & 0xfffff000));
        var minor = (uint)((device & 0xff) | ((device >> 12) & 0xffffff00));
        if (major == 0)
        {
            // Anonymous devices — tmpfs, network mounts — have no drive.
            return null;
        }

        try
        {
            var link = new DirectoryInfo($"/sys/dev/block/{major}:{minor}");
            if (link.LinkTarget is null)
            {
                return null;
            }

            var canonical = Path.GetFullPath(Path.Combine(link.Parent!.FullName, link.LinkTarget));

            // A partition's sysfs directory sits inside its disk's; the disk
            // directory is the name worth comparing. A whole-disk volume is
            // already there.
            return File.Exists(Path.Combine(canonical, "partition"))
                ? Path.GetFileName(Path.GetDirectoryName(canonical))
                : Path.GetFileName(canonical);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? NearestExisting(string path)
    {
        var current = Path.GetFullPath(path);
        while (current is not null && !Directory.Exists(current) && !File.Exists(current))
        {
            current = Path.GetDirectoryName(current);
        }

        return current;
    }
}
