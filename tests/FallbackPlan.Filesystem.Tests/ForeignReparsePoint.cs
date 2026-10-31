using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace FallbackPlan.Filesystem.Tests;

/// <summary>
/// A reparse point whose tag no filter on the machine keeps, set on a file
/// the test made. It is the one kind of non-link reparse point a test can
/// make anywhere: deduplication needs a server's volume, and the overlay
/// filter that <c>compact /exe</c> compresses through hides its reparse
/// point from what capture reads.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class ForeignReparsePoint
{
    /// <summary>
    /// A tag outside Microsoft's range that names no other path. Such a tag
    /// is set with a GUID, which says whose it is.
    /// </summary>
    public const uint Tag = 0x0000_7A51;

    private const uint GenericRead = 0x8000_0000;
    private const uint GenericWrite = 0x4000_0000;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x0020_0000;
    private const uint BackupSemantics = 0x0200_0000;
    private const uint SetReparsePoint = 0x0009_00A4;

    /// <summary>Sets a reparse point carrying <see cref="Tag"/> on the file at <paramref name="path"/>.</summary>
    public static void Set(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // REPARSE_GUID_DATA_BUFFER: the tag, the data's length, two reserved
        // bytes, the GUID, then the data.
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8];
        var buffer = new byte[24 + data.Length];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), Tag);
        BitConverter.TryWriteBytes(buffer.AsSpan(4, 2), (ushort)data.Length);
        new Guid("6d1b3f0e-8a42-4c7e-9f31-2b5a7c90e4d8").TryWriteBytes(buffer.AsSpan(8, 16));
        data.CopyTo(buffer, 24);

        using var handle = CreateFile(
            path, GenericRead | GenericWrite, FileShare.None, 0, OpenExisting, OpenReparsePoint | BackupSemantics, 0);
        if (handle.IsInvalid)
        {
            throw new IOException($"{path} did not open: {Marshal.GetLastPInvokeError()}");
        }

        if (!DeviceIoControl(handle, SetReparsePoint, buffer, (uint)buffer.Length, null, 0, out _, 0))
        {
            throw new IOException($"{path} took no reparse point: {Marshal.GetLastPInvokeError()}");
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(
        string path, uint access, FileShare share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle handle, uint code, byte[] input, uint inputLength, byte[]? output, uint outputLength, out uint returned, nint overlapped);
}
