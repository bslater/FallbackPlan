using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace FallbackPlan.Filesystem.Local;

/// <summary>
/// Windows interop: stable file identity and link counts via
/// <c>GetFileInformationByHandle</c>, alternate-stream enumeration via
/// <c>FindFirstStreamW</c>, and the self-relative security descriptor via
/// <c>GetFileSecurityW</c>. Exercised by the CI matrix; on other platforms
/// nothing here is reachable.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsInterop
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct FindStreamData
    {
        public long StreamSize;
        public fixed char StreamName[296];

        public readonly string Name
        {
            get
            {
                fixed (char* name = StreamName)
                {
                    var span = new ReadOnlySpan<char>(name, 296);
                    var end = span.IndexOf('\0');
                    return new string(span[..(end < 0 ? 296 : end)]);
                }
            }
        }
    }

    private const uint FileFlagBackupSemantics = 0x0200_0000;
    private const uint FileFlagOpenReparsePoint = 0x0020_0000;
    private const uint OwnerSecurityInformation = 0x1;
    private const uint GroupSecurityInformation = 0x2;
    private const uint DaclSecurityInformation = 0x4;

    [LibraryImport("kernel32", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);

    [LibraryImport("kernel32", EntryPoint = "FindFirstStreamW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindFirstStream(string fileName, int infoLevel, out FindStreamData data, uint flags);

    [LibraryImport("kernel32", EntryPoint = "FindNextStreamW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindNextStream(nint handle, out FindStreamData data);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindClose(nint handle);

    [LibraryImport("advapi32", EntryPoint = "GetFileSecurityW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileSecurity(
        string fileName, uint requestedInformation, byte[]? descriptor, uint length, out uint lengthNeeded);

    /// <summary>Identity via volume serial + file index; false when the handle cannot open.</summary>
    public static bool TryGetIdentity(string path, out ulong device, out ulong fileId, out uint linkCount)
    {
        device = 0;
        fileId = 0;
        linkCount = 0;

        using var handle = CreateFile(
            path, 0, 7 /* read|write|delete share */, 0, 3 /* OPEN_EXISTING */,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);

        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information))
        {
            return false;
        }

        device = information.VolumeSerialNumber;
        fileId = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        linkCount = information.NumberOfLinks;
        return true;
    }

    /// <summary>Enumerates named alternate data streams (the unnamed <c>::$DATA</c> stream is skipped).</summary>
    public static IReadOnlyList<(string Name, long Length)> ListAlternateStreams(string path)
    {
        var streams = new List<(string, long)>();
        var handle = FindFirstStream(path, 0, out var data, 0);
        if (handle == -1)
        {
            return streams;
        }

        try
        {
            do
            {
                // Names arrive as ":name:$DATA"; the unnamed stream is "::$DATA".
                var name = data.Name;
                if (name.Length > 1 && name[0] == ':' && name[1] != ':')
                {
                    var end = name.IndexOf(':', 1);
                    if (end > 1)
                    {
                        streams.Add((name[1..end], data.StreamSize));
                    }
                }
            }
            while (FindNextStream(handle, out data));
        }
        finally
        {
            FindClose(handle);
        }

        return streams;
    }

    /// <summary>The self-relative security descriptor (owner, group, DACL); null when unreadable — degrade, never abort.</summary>
    public static byte[]? GetSecurityDescriptor(string path)
    {
        const uint requested = OwnerSecurityInformation | GroupSecurityInformation | DaclSecurityInformation;

        if (GetFileSecurity(path, requested, null, 0, out var needed) || needed == 0)
        {
            return null;
        }

        var descriptor = new byte[needed];
        return GetFileSecurity(path, requested, descriptor, needed, out _) ? descriptor : null;
    }
}
