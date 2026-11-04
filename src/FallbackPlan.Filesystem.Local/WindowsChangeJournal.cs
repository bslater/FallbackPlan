using System.Buffers.Binary;
using System.Globalization;
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

    /// <summary>How many times the journal is read for one answer, while a folder it names is gone as it is followed.</summary>
    private const int ReadingsForAFolderGone = 4;

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
    private const uint FileReadAttributes = 0x80;
    private const int FileHardLinkInformation = 46;
    private const int StatusBufferOverflow = unchecked((int)0x8000_0005);
    private const int StatusBufferTooSmall = unchecked((int)0xC000_0023);

    /// <inheritdoc />
    public JournalPosition? PositionOf(string folder) => Locate(folder, out _);

    /// <summary>
    /// Why the volume holding <paramref name="folder"/> gives no place: the
    /// step that failed and the platform's error, or null where it gives one.
    /// Error 1179 is a volume that keeps no journal.
    /// </summary>
    internal static string? WhyNoPosition(string folder)
    {
        Locate(folder, out var why);
        return why;
    }

    private static JournalPosition? Locate(string folder, out string? why)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!Directory.Exists(folder))
        {
            why = "no folder there";
            return null;
        }

        if (VolumeOf(folder) is not { } volume)
        {
            why = Failed("naming its volume");
            return null;
        }

        using var device = OpenVolume(volume);
        if (device.IsInvalid)
        {
            why = Failed($"opening {volume}");
            return null;
        }

        if (Query(device) is not { } journal)
        {
            why = Failed($"asking {volume} for its journal");
            return null;
        }

        why = null;
        return new JournalPosition(volume, journal.Id, journal.Next);
    }

    /// <summary>A step that failed, with the error the last platform call raised.</summary>
    private static string Failed(string step) =>
        string.Create(CultureInfo.InvariantCulture, $"{step}: error {Marshal.GetLastPInvokeError()}");

    /// <inheritdoc />
    public IReadOnlyList<ChangedFolder>? ChangedSince(JournalPosition since, IReadOnlyCollection<string> roots) =>
        ChangedSince(since, roots, out _);

    /// <summary>
    /// As <see cref="ChangedSince(JournalPosition, IReadOnlyCollection{string})"/>,
    /// and where there is no answer, why: the step that doubted, with the
    /// platform's error where one was raised. <paramref name="afterReading"/>
    /// runs once the records are first read and before they are followed, so
    /// a test can delete a folder between the two as another program would.
    /// </summary>
    internal static IReadOnlyList<ChangedFolder>? ChangedSince(
        JournalPosition since, IReadOnlyCollection<string> roots, out string? doubt, Action? afterReading = null)
    {
        ArgumentNullException.ThrowIfNull(since);
        ArgumentNullException.ThrowIfNull(roots);
        doubt = null;
        if (roots.Count == 0)
        {
            return [];
        }

        // Each root as the volume spells it, so a short name or another case
        // is still the root.
        var spelled = new List<(string Root, string OnVolume)>();
        foreach (var root in roots)
        {
            if (!string.Equals(VolumeOf(root), since.Volume, StringComparison.OrdinalIgnoreCase))
            {
                doubt = $"{root} is not on {since.Volume}";
                return null;
            }

            if (FinalPathOf(root) is not { } onVolume)
            {
                doubt = Failed($"finding {root} on its volume");
                return null;
            }

            spelled.Add((root, onVolume.TrimEnd('\\')));
        }

        using var device = OpenVolume(since.Volume);
        if (device.IsInvalid)
        {
            doubt = Failed($"opening {since.Volume}");
            return null;
        }

        using var hint = OpenDirectory(spelled[0].OnVolume);
        if (hint.IsInvalid)
        {
            doubt = Failed($"opening {spelled[0].OnVolume}");
            return null;
        }

        // A folder another program deletes after its records are read, and
        // before they are followed, cannot be opened, and its deletion is not
        // among them: the journal is read again, and holds it then.
        for (var reading = 1; ; reading++)
        {
            if (Query(device) is not { } journal)
            {
                doubt = Failed($"asking {since.Volume} for its journal");
                return null;
            }

            if (!JournalCoverage.Covers(since, journal.Id, journal.LowestValid, journal.Next))
            {
                doubt = "the journal does not hold every record since the place";
                return null;
            }

            if (Read(device, since, journal.Next) is not { } changes)
            {
                doubt = Failed("reading the journal");
                return null;
            }

            if (changes.Folders.Count + changes.Files.Count > MostEntriesNamed)
            {
                doubt = "it names more entries than a walk would cost";
                return null;
            }

            if (reading == 1)
            {
                afterReading?.Invoke();
            }

            var named = Follow(changes, hint, spelled, out doubt, out var vanished);
            if (named is not null || !vanished || reading == ReadingsForAFolderGone)
            {
                return named;
            }

            Thread.Sleep(50 * reading);
        }
    }

    /// <summary>
    /// The folders in the set that <paramref name="changes"/> name, or null
    /// with why; <paramref name="vanished"/> says a folder they name was gone
    /// by the time it was followed.
    /// </summary>
    private static List<ChangedFolder>? Follow(
        JournalChanges changes, SafeFileHandle hint, List<(string Root, string OnVolume)> spelled,
        out string? doubt, out bool vanished)
    {
        doubt = null;
        vanished = false;

        // Folders first, so that only a file changed in one of the set's
        // folders is opened to find its other names: a file another program
        // is writing elsewhere on the volume is never held open under it.
        var named = new HashSet<ChangedFolder>();
        var inTheSet = new HashSet<ulong>();
        foreach (var folder in changes.Folders)
        {
            if (changes.Gone.Contains(folder))
            {
                continue;
            }

            if (PathOfId(hint, folder, out var error) is not { } path)
            {
                vanished = Vanished(error);
                doubt = string.Create(CultureInfo.InvariantCulture, $"finding folder {folder:X}: error {error}");
                return null;
            }

            if (Name(path))
            {
                inTheSet.Add(folder);
            }
        }

        // A file's every name is in a folder its records may not name.
        foreach (var file in changes.FilesIn(inTheSet.Contains).ToList())
        {
            if (changes.Gone.Contains(file))
            {
                continue;
            }

            if (LinkParentsOf(hint, file, out doubt) is not { } parents)
            {
                return null;
            }

            foreach (var folder in parents.Where(parent => !inTheSet.Contains(parent)))
            {
                if (PathOfId(hint, folder, out var error) is not { } path)
                {
                    vanished = Vanished(error);
                    doubt = string.Create(CultureInfo.InvariantCulture, $"finding folder {folder:X}: error {error}");
                    return null;
                }

                Name(path);
            }
        }

        return [.. named];

        // Whether the folder is in the set: under one of its roots.
        bool Name(string found)
        {
            var path = found.TrimEnd('\\');
            var inside = false;
            foreach (var (root, onVolume) in spelled)
            {
                if (string.Equals(path, onVolume, StringComparison.OrdinalIgnoreCase))
                {
                    named.Add(new ChangedFolder(root, string.Empty));
                    inside = true;
                }
                else if (path.Length > onVolume.Length + 1
                    && path.StartsWith(onVolume, StringComparison.OrdinalIgnoreCase)
                    && path[onVolume.Length] == '\\')
                {
                    named.Add(new ChangedFolder(root, path[(onVolume.Length + 1)..].Replace('\\', '/')));
                    inside = true;
                }
            }

            return inside;
        }
    }

    // The errors an id gives once what it named is gone, or going.
    private static bool Vanished(int error) =>
        error is ErrorFileNotFound or ErrorPathNotFound or ErrorInvalidParameter or ErrorDeletePending;

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
    /// The folders holding the names of a file with several, by identity:
    /// none for a file with one, whose folder is its record's, or one gone
    /// since; and null, with why, where they cannot be told.
    /// </summary>
    private static unsafe List<ulong>? LinkParentsOf(SafeFileHandle hint, ulong fileId, out string? doubt)
    {
        doubt = null;
        var descriptor = new FileIdDescriptor { Size = (uint)Marshal.SizeOf<FileIdDescriptor>(), Type = 0, FileId = fileId };
        using var handle = OpenFileById(hint, ref descriptor, FileReadAttributes, ShareAll, 0, FileFlagBackupSemantics);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            if (Vanished(error))
            {
                return [];
            }

            doubt = string.Create(CultureInfo.InvariantCulture, $"opening file {fileId:X}: error {error}");
            return null;
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            doubt = Failed(string.Create(CultureInfo.InvariantCulture, $"counting the names of file {fileId:X}"));
            return null;
        }

        if (information.NumberOfLinks <= 1)
        {
            return [];
        }

        var buffer = new byte[4096];
        for (var attempt = 0; ; attempt++)
        {
            int status;
            fixed (byte* links = buffer)
            {
                status = NtQueryInformationFile(handle, out _, links, (uint)buffer.Length, FileHardLinkInformation);
            }

            if ((status == StatusBufferOverflow || status == StatusBufferTooSmall) && attempt < 4)
            {
                buffer = new byte[Math.Max(BinaryPrimitives.ReadUInt32LittleEndian(buffer), (uint)buffer.Length * 2)];
                continue;
            }

            if (status < 0 || HardLinks.ParentsOf(buffer) is not { } parents)
            {
                doubt = string.Create(CultureInfo.InvariantCulture, $"listing the names of file {fileId:X}: status 0x{status:X8}");
                return null;
            }

            return [.. parents];
        }
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

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);

    [LibraryImport("ntdll")]
    private static unsafe partial int NtQueryInformationFile(
        SafeFileHandle handle, out IoStatusBlock ioStatus, void* information, uint length, int informationClass);

    /// <summary><c>IO_STATUS_BLOCK</c>: a status or pointer, and a count, each pointer-wide.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public nint Status;
        public nint Information;
    }
}
