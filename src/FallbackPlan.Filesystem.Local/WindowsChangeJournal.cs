using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace FallbackPlan.Filesystem.Local;

/// <summary>
/// <c>FILE_ID_DESCRIPTOR</c> as <c>OpenFileById</c> reads it (winbase.h):
/// its size, the kind of identity, and the identity in a sixteen-byte union,
/// 24 bytes in all. It sits outside <see cref="WindowsChangeJournal"/> because
/// a layout is not a platform call, so the test that pins it runs everywhere.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FileIdDescriptor
{
    public uint Size;
    public int Type;
    public ulong FileId;
    public ulong Unused;
}

/// <summary>
/// What an NTFS volume's change journal says changed (ADR-0187). The journal
/// is the volume's own, kept whether or not anything is listening, so it
/// holds what changed while the service was not running too. A place in it
/// is answered only where the journal is the same one and still holds every
/// record since; each record's folder is then found by its identity, and a
/// changed file's every name is counted, since a file with several names
/// changes every folder that holds one.
/// </summary>
/// <remarks>
/// Reading a journal takes a handle to the volume, which an administrator
/// or the system account may open. Anything else is no journal, and the
/// caller walks in full.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsChangeJournal : IChangeJournal
{
    /// <summary>The most entries one read names before a walk is the cheaper way to find them.</summary>
    public const int MostEntriesNamed = 200_000;

    private const uint GenericRead = 0x8000_0000;
    private const uint ShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x0200_0000;
    private const uint QueryUsnJournal = 0x0009_00F4;
    private const uint ReadUsnJournal = 0x0009_00BB;
    private const uint EveryReason = 0xFFFF_FFFF;
    private const int ReadBufferBytes = 64 * 1024;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorDeletePending = 303;

    /// <inheritdoc />
    public JournalPosition? PositionOf(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!Directory.Exists(folder) || VolumeOf(folder) is not { } volume)
        {
            return null;
        }

        using var device = OpenVolume(volume);
        return device.IsInvalid || Query(device) is not { } journal
            ? null
            : new JournalPosition(volume, journal.Id, journal.Next);
    }

    /// <inheritdoc />
    public IReadOnlyList<ChangedFolder>? ChangedSince(JournalPosition since, IReadOnlyCollection<string> roots)
    {
        ArgumentNullException.ThrowIfNull(since);
        ArgumentNullException.ThrowIfNull(roots);
        if (roots.Count == 0)
        {
            return [];
        }

        // Each root as the volume spells it, so a short name or another case
        // is still the root.
        var spelled = new List<(string Root, string OnVolume)>();
        foreach (var root in roots)
        {
            if (!string.Equals(VolumeOf(root), since.Volume, StringComparison.OrdinalIgnoreCase)
                || FinalPathOf(root) is not { } onVolume)
            {
                return null;
            }

            spelled.Add((root, onVolume.TrimEnd('\\')));
        }

        using var device = OpenVolume(since.Volume);
        if (device.IsInvalid || Query(device) is not { } journal
            || !JournalCoverage.Covers(since, journal.Id, journal.LowestValid, journal.Next)
            || Read(device, since, journal.Next) is not { } changes
            || changes.Folders.Count + changes.Files.Count > MostEntriesNamed)
        {
            return null;
        }

        using var hint = OpenDirectory(spelled[0].OnVolume);
        if (hint.IsInvalid)
        {
            return null;
        }

        var named = new HashSet<ChangedFolder>();
        foreach (var folder in changes.Folders)
        {
            if (changes.Gone.Contains(folder))
            {
                continue;
            }

            if (PathOfId(hint, folder, out _) is not { } path)
            {
                return null;
            }

            Name(path);
        }

        foreach (var file in changes.Files)
        {
            if (changes.Gone.Contains(file))
            {
                continue;
            }

            if (FoldersHolding(hint, file) is not { } folders)
            {
                return null;
            }

            foreach (var folder in folders)
            {
                Name(folder);
            }
        }

        return [.. named];

        void Name(string found)
        {
            var path = found.TrimEnd('\\');
            foreach (var (root, onVolume) in spelled)
            {
                if (string.Equals(path, onVolume, StringComparison.OrdinalIgnoreCase))
                {
                    named.Add(new ChangedFolder(root, string.Empty));
                }
                else if (path.Length > onVolume.Length + 1
                    && path.StartsWith(onVolume, StringComparison.OrdinalIgnoreCase)
                    && path[onVolume.Length] == '\\')
                {
                    named.Add(new ChangedFolder(root, path[(onVolume.Length + 1)..].Replace('\\', '/')));
                }
            }
        }
    }

    /// <summary>Every record from <paramref name="since"/> to at least <paramref name="until"/>, or null where a read fails.</summary>
    private static unsafe JournalChanges? Read(SafeFileHandle device, JournalPosition since, long until)
    {
        var changes = new JournalChanges();
        var buffer = new byte[ReadBufferBytes];
        var request = new byte[40];
        for (var start = since.Usn; start < until;)
        {
            // READ_USN_JOURNAL_DATA_V0: from where, for every reason, without
            // waiting for more, in this journal and no other.
            BinaryPrimitives.WriteInt64LittleEndian(request, start);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), EveryReason);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(12), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(16), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(24), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(32), since.JournalId);

            uint returned;
            fixed (byte* input = request)
            fixed (byte* output = buffer)
            {
                if (!DeviceIoControl(device, ReadUsnJournal, input, (uint)request.Length, output, (uint)buffer.Length, out returned, 0))
                {
                    return null;
                }
            }

            if (UsnRecords.Parse(buffer.AsSpan(0, (int)returned)) is not { } read)
            {
                return null;
            }

            foreach (var record in read.Records)
            {
                changes.Add(record);
            }

            if (read.NextUsn <= start)
            {
                break;
            }

            start = read.NextUsn;
        }

        return changes;
    }

    /// <summary>The journal's identity, the oldest record it holds, and its next, or null where the volume keeps none.</summary>
    private static unsafe (ulong Id, long LowestValid, long Next)? Query(SafeFileHandle device)
    {
        // USN_JOURNAL_DATA_V0 is 56 bytes; a later version is longer and
        // begins the same way.
        var data = new byte[80];
        fixed (byte* output = data)
        {
            if (!DeviceIoControl(device, QueryUsnJournal, null, 0, output, (uint)data.Length, out var returned, 0) || returned < 32)
            {
                return null;
            }
        }

        return (
            BinaryPrimitives.ReadUInt64LittleEndian(data),
            BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(24)),
            BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(16)));
    }

    /// <summary>
    /// Every folder holding a name of the file whose identity is
    /// <paramref name="fileId"/>: none for a file gone since, and null where
    /// it cannot be told.
    /// </summary>
    private static List<string>? FoldersHolding(SafeFileHandle hint, ulong fileId)
    {
        if (PathOfId(hint, fileId, out var error) is not { } path)
        {
            return error is ErrorFileNotFound or ErrorPathNotFound or ErrorInvalidParameter or ErrorDeletePending ? [] : null;
        }

        if (VolumePathOf(path) is not { } volumeRoot)
        {
            return null;
        }

        // A name comes back from the volume's root, without the volume.
        var prefix = volumeRoot.TrimEnd('\\');
        var folders = new List<string>();
        foreach (var name in NamesOf(path))
        {
            if (Path.GetDirectoryName(prefix + name) is { } folder)
            {
                folders.Add(folder);
            }
        }

        return folders.Count == 0 ? null : folders;
    }

    /// <summary>Every name of the file at <paramref name="path"/>, each from its volume's root.</summary>
    private static unsafe List<string> NamesOf(string path)
    {
        var names = new List<string>();
        var buffer = new char[1024];
        var length = (uint)buffer.Length;
        nint find;
        fixed (char* name = buffer)
        {
            find = FindFirstFileName(path, 0, ref length, name);
        }

        if (find == -1)
        {
            return names;
        }

        try
        {
            do
            {
                names.Add(new string(buffer, 0, (int)Math.Max(0, length - 1)));
                length = (uint)buffer.Length;
                bool more;
                fixed (char* name = buffer)
                {
                    more = FindNextFileName(find, ref length, name);
                }

                if (!more)
                {
                    break;
                }
            }
            while (true);
        }
        finally
        {
            FindClose(find);
        }

        return names;
    }

    /// <summary>The path of the entry whose identity is <paramref name="id"/>, or null with the platform's error.</summary>
    private static string? PathOfId(SafeFileHandle hint, ulong id, out int error)
    {
        var descriptor = new FileIdDescriptor { Size = (uint)Marshal.SizeOf<FileIdDescriptor>(), Type = 0, FileId = id };
        using var handle = OpenFileById(hint, ref descriptor, 0, ShareAll, 0, FileFlagBackupSemantics);
        if (handle.IsInvalid)
        {
            error = Marshal.GetLastPInvokeError();
            return null;
        }

        error = 0;
        return FinalPathOf(handle);
    }

    private static string? FinalPathOf(string path)
    {
        using var handle = OpenDirectory(path);
        return handle.IsInvalid ? null : FinalPathOf(handle);
    }

    /// <summary>A handle's path as the volume spells it, with a drive letter and without the <c>\\?\</c> before it.</summary>
    private static unsafe string? FinalPathOf(SafeFileHandle handle)
    {
        var buffer = new char[512];
        while (true)
        {
            uint length;
            fixed (char* path = buffer)
            {
                length = GetFinalPathNameByHandle(handle, path, (uint)buffer.Length, 0);
            }

            if (length == 0)
            {
                return null;
            }

            if (length < buffer.Length)
            {
                var path = new string(buffer, 0, (int)length);
                return path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? null
                    : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..]
                    : path;
            }

            buffer = new char[length + 1];
        }
    }

    /// <summary>The volume holding <paramref name="path"/>, as <c>\\?\Volume{…}\</c>, or null.</summary>
    private static unsafe string? VolumeOf(string path)
    {
        if (VolumePathOf(path) is not { } mountPoint)
        {
            return null;
        }

        var buffer = new char[64];
        fixed (char* name = buffer)
        {
            if (!GetVolumeNameForVolumeMountPoint(mountPoint, name, (uint)buffer.Length))
            {
                return null;
            }
        }

        var volume = new string(buffer);
        var end = volume.IndexOf('\0', StringComparison.Ordinal);
        return end < 0 ? volume : volume[..end];
    }

    /// <summary>The folder <paramref name="path"/>'s volume is mounted at, such as <c>C:\</c>, or null.</summary>
    private static unsafe string? VolumePathOf(string path)
    {
        var buffer = new char[1024];
        fixed (char* name = buffer)
        {
            if (!GetVolumePathName(path, name, (uint)buffer.Length))
            {
                return null;
            }
        }

        var mountPoint = new string(buffer);
        var end = mountPoint.IndexOf('\0', StringComparison.Ordinal);
        return end < 0 ? mountPoint : mountPoint[..end];
    }

    /// <summary>The volume itself, which its journal is read through; its name without the trailing separator names the device.</summary>
    private static SafeFileHandle OpenVolume(string volume) =>
        CreateFile(volume.TrimEnd('\\'), GenericRead, ShareAll, 0, OpenExisting, 0, 0);

    private static SafeFileHandle OpenDirectory(string path) =>
        CreateFile(path, 0, ShareAll, 0, OpenExisting, FileFlagBackupSemantics, 0);

    [LibraryImport("kernel32", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode, void* inBuffer, uint inBufferSize,
        void* outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);

    [LibraryImport("kernel32", SetLastError = true)]
    private static partial SafeFileHandle OpenFileById(
        SafeFileHandle volumeHint, ref FileIdDescriptor fileId, uint desiredAccess, uint shareMode,
        nint securityAttributes, uint flagsAndAttributes);

    [LibraryImport("kernel32", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint length, uint flags);

    [LibraryImport("kernel32", EntryPoint = "GetVolumePathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumePathName(string fileName, char* volumePathName, uint length);

    [LibraryImport("kernel32", EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumeNameForVolumeMountPoint(string mountPoint, char* volumeName, uint length);

    [LibraryImport("kernel32", EntryPoint = "FindFirstFileNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial nint FindFirstFileName(string fileName, uint flags, ref uint length, char* linkName);

    [LibraryImport("kernel32", EntryPoint = "FindNextFileNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool FindNextFileName(nint find, ref uint length, char* linkName);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindClose(nint find);
}
