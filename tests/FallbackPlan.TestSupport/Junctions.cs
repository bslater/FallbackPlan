using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FallbackPlan.TestSupport;

/// <summary>
/// Makes a Windows directory junction, the mount point an account needs no
/// privilege to make, and tells one from a symbolic link.
/// </summary>
/// <remarks>
/// Kept apart from the product's interop for the reason
/// <see cref="AllocatedSize"/> gives: the oracle a junction a restore made
/// is checked against must not be the code that made it.
/// </remarks>
public static partial class Junctions
{
    private const uint BackupSemantics = 0x0200_0000;
    private const uint OpenReparsePoint = 0x0020_0000;
    private const uint ReparsePointBit = 0x400;
    private const uint MountPointTag = 0xA000_0003;
    private const int AttributeTagInfo = 9;

    /// <summary>
    /// Makes a junction at <paramref name="path"/> naming the folder
    /// <paramref name="target"/>, and answers whether it was made.
    /// </summary>
    /// <remarks>
    /// <c>mklink</c> is a <c>cmd</c> built-in, so it is run through the
    /// shell. No managed call makes a mount point: .NET's
    /// <see cref="Directory.CreateSymbolicLink"/> makes a folder's symbolic
    /// link, which needs a privilege a junction does not.
    /// </remarks>
    /// <param name="path">Where the junction goes; nothing may stand there.</param>
    /// <param name="target">The absolute path of the folder it names.</param>
    public static bool Make(string path, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            ArgumentList = { "/c", "mklink", "/J", path, target },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });

        if (process is null)
        {
            return false;
        }

        process.WaitForExit(TimeSpan.FromSeconds(30));
        return process.ExitCode == 0 && Directory.Exists(path);
    }

    /// <summary>
    /// Whether the entry at <paramref name="path"/> is a mount point, read
    /// through a handle on its own reparse point: false for a symbolic link,
    /// a folder, anything else, and off Windows.
    /// </summary>
    public static bool IsMountPoint(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var handle = CreateFile(path, 0, 7 /* read|write|delete share */, 0, 3 /* OPEN_EXISTING */, BackupSemantics | OpenReparsePoint, 0);
        return !handle.IsInvalid
            && GetFileInformationByHandleEx(handle, AttributeTagInfo, out var information, (uint)Marshal.SizeOf<AttributeTag>())
            && (information.Attributes & ReparsePointBit) != 0
            && information.Tag == MountPointTag;
    }

    /// <summary><c>FILE_ATTRIBUTE_TAG_INFO</c>: an entry's attribute bits and its reparse tag.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag
    {
        public uint Attributes;
        public uint Tag;
    }

    [LibraryImport("kernel32", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out AttributeTag information, uint size);
}
