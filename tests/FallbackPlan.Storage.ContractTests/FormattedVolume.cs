using System.Diagnostics;

namespace FallbackPlan.Storage.ContractTests;

/// <summary>
/// A volume of a test's own, mounted on an empty folder and gone again when
/// disposed: on Windows a virtual disk formatted with the file system it
/// names, on Linux a memory-backed one of the size it is given.
/// </summary>
/// <remarks>
/// Only a real volume shows how FAT32 or exFAT answers a call that asks a
/// folder for the link it holds, which it holds none of and cannot say so,
/// or how a volume with no room left refuses a write. Making one needs an
/// elevated process on Windows, and root on Linux.
/// </remarks>
internal sealed class FormattedVolume : IDisposable
{
    private readonly Action _release;

    private FormattedVolume(string mountedAt, Action release)
    {
        MountedAt = mountedAt;
        _release = release;
    }

    /// <summary>The folder the volume is mounted on.</summary>
    public string MountedAt { get; }

    /// <summary>Makes, formats and mounts a volume on Windows.</summary>
    /// <param name="fileSystem">What diskpart formats it as: <c>fat32</c>, <c>exfat</c> or <c>ntfs</c>.</param>
    /// <param name="megabytes">How large the volume is.</param>
    public static FormattedVolume Mount(string fileSystem, int megabytes = 256)
    {
        var name = $"fbp-{fileSystem}-" + Guid.NewGuid().ToString("n");
        var disk = Path.Combine(Path.GetTempPath(), name + ".vhd");
        var mountedAt = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), name)).FullName;

        // FAT32 takes no volume much below 33 MB; the disk grows only as
        // far as it is written.
        DiskPart(
            $"create vdisk file=\"{disk}\" maximum={megabytes} type=expandable",
            $"select vdisk file=\"{disk}\"",
            "attach vdisk",
            "create partition primary",
            $"format fs={fileSystem} quick label=FBP{fileSystem.ToUpperInvariant()}",
            $"assign mount=\"{mountedAt}\"");

        return new FormattedVolume(mountedAt, () =>
        {
            DiskPart($"select vdisk file=\"{disk}\"", "detach vdisk");
            File.Delete(disk);
            Directory.Delete(mountedAt);
        });
    }

    /// <summary>Mounts a memory-backed volume of <paramref name="megabytes"/> on Linux.</summary>
    /// <exception cref="InvalidOperationException">The process may not mount one, as a container's root need not.</exception>
    public static FormattedVolume Tmpfs(int megabytes)
    {
        var mountedAt = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "fbp-tmpfs-" + Guid.NewGuid().ToString("n"))).FullName;
        try
        {
            Run("mount", "-t", "tmpfs", "-o", $"size={megabytes}m", "tmpfs", mountedAt);
        }
        catch
        {
            Directory.Delete(mountedAt);
            throw;
        }

        return new FormattedVolume(mountedAt, () =>
        {
            Run("umount", mountedAt);
            Directory.Delete(mountedAt);
        });
    }

    /// <inheritdoc />
    public void Dispose() => _release();

    private static void Run(string command, params string[] arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(command, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException($"{command} did not start");

        var errors = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{command} exited {process.ExitCode}: {errors.GetAwaiter().GetResult()}");
        }
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
