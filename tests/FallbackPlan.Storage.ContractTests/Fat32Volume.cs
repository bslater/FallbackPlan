using System.Diagnostics;

namespace FallbackPlan.Storage.ContractTests;

/// <summary>
/// A FAT32 volume of a test's own: a virtual disk, formatted and mounted on
/// an empty folder, gone again when disposed.
/// </summary>
/// <remarks>
/// Only a real volume shows how FAT32 answers a call that asks a folder for
/// the link it holds: it holds none and cannot say so, and refuses the call
/// as a function it does not have. Making one needs Windows and an elevated
/// process.
/// </remarks>
internal sealed class Fat32Volume : IDisposable
{
    private readonly string _disk;

    private Fat32Volume(string disk, string mountedAt)
    {
        _disk = disk;
        MountedAt = mountedAt;
    }

    /// <summary>The folder the volume is mounted on.</summary>
    public string MountedAt { get; }

    /// <summary>Makes, formats and mounts a FAT32 volume.</summary>
    public static Fat32Volume Mount()
    {
        var name = "fbp-fat32-" + Guid.NewGuid().ToString("n");
        var disk = Path.Combine(Path.GetTempPath(), name + ".vhd");
        var mountedAt = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), name)).FullName;

        // FAT32 takes no volume much below 33 MB; the disk grows only as
        // far as it is written.
        DiskPart(
            $"create vdisk file=\"{disk}\" maximum=256 type=expandable",
            $"select vdisk file=\"{disk}\"",
            "attach vdisk",
            "create partition primary",
            "format fs=fat32 quick label=FBPFAT32",
            $"assign mount=\"{mountedAt}\"");

        return new Fat32Volume(disk, mountedAt);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        DiskPart($"select vdisk file=\"{_disk}\"", "detach vdisk");
        File.Delete(_disk);
        Directory.Delete(MountedAt);
    }

    private static void DiskPart(params string[] commands)
    {
        var script = Path.Combine(Path.GetTempPath(), "fbp-diskpart-" + Guid.NewGuid().ToString("n") + ".txt");
        File.WriteAllLines(script, commands);

        try
        {
            using var process = Process.Start(new ProcessStartInfo("diskpart.exe", ["/s", script])
            {
                // diskpart writes its refusals to its output too.
                RedirectStandardOutput = true,
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("diskpart did not start");

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"diskpart exited {process.ExitCode} running:\n{string.Join('\n', commands)}\n{output}");
            }
        }
        finally
        {
            File.Delete(script);
        }
    }
}
