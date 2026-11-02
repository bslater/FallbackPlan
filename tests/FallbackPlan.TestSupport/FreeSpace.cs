using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FallbackPlan.TestSupport;

/// <summary>
/// How many bytes the volume holding a directory has free for this process.
/// </summary>
/// <remarks>
/// On Windows it is asked of the directory itself, so a volume mounted on a
/// folder answers for itself: <see cref="DriveInfo"/> takes only a drive's
/// root there, and would answer for the drive the folder is on. On Linux and
/// macOS <see cref="DriveInfo"/> asks <c>statvfs</c> of the directory, which
/// answers for whatever volume holds it.
/// </remarks>
public static partial class FreeSpace
{
    /// <summary>The bytes free to this process on the volume holding <paramref name="directory"/>.</summary>
    /// <param name="directory">An existing directory.</param>
    public static long On(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);

        if (!OperatingSystem.IsWindows())
        {
            return new DriveInfo(directory).AvailableFreeSpace;
        }

        var asked = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
        return GetDiskFreeSpaceEx(asked, out var available, out _, out _)
            ? checked((long)available)
            : throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(
        string directoryName, out ulong freeBytesAvailable, out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);
}
