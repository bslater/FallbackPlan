using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace FallbackPlan.TestSupport;

/// <summary>
/// A link's own security descriptor on Windows, read without following it:
/// the oracle for a descriptor written onto a link. .NET reads a path's
/// descriptor by its name, which opens what a link points at, so this opens
/// the reparse point itself and reads through the handle.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class LinkSecurity
{
    private const uint ReadControl = 0x0002_0000;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x0020_0000;
    private const uint BackupSemantics = 0x0200_0000;

    /// <summary>Owner, group and DACL.</summary>
    private const uint Requested = 0x1 | 0x2 | 0x4;

    /// <summary>The owner, group and DACL of the link at <paramref name="link"/> itself.</summary>
    public static FileSecurity Of(string link)
    {
        ArgumentException.ThrowIfNullOrEmpty(link);

        using var handle = CreateFile(
            link, ReadControl, FileShare.ReadWrite | FileShare.Delete, 0, OpenExisting, OpenReparsePoint | BackupSemantics, 0);
        if (handle.IsInvalid)
        {
            throw new IOException($"{link} did not open as a link: {Marshal.GetLastPInvokeError()}");
        }

        GetKernelObjectSecurity(handle, Requested, null, 0, out var needed);
        var descriptor = new byte[needed];
        if (needed == 0 || !GetKernelObjectSecurity(handle, Requested, descriptor, needed, out _))
        {
            throw new IOException($"{link}'s descriptor did not read: {Marshal.GetLastPInvokeError()}");
        }

        var security = new FileSecurity();
        security.SetSecurityDescriptorBinaryForm(descriptor);
        return security;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(
        string path, uint access, FileShare share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetKernelObjectSecurity(
        SafeFileHandle handle, uint requested, byte[]? descriptor, uint length, out uint needed);
}
