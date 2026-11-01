using System.Runtime.InteropServices;
using Bodu;
using Microsoft.Win32.SafeHandles;

namespace FallbackPlan.Domain;

/// <summary>
/// How the volume a folder lies on takes names: whether it tells two names
/// that differ only in case apart, whether it takes a name's composed and
/// decomposed Unicode forms for one (FR-RST-003, ADR-0170 Amendment 5), and
/// whether it names files by Windows' rules (ADR-0170 Amendment 6).
/// </summary>
/// <param name="CaseSensitive">Whether names that differ only in case are two names.</param>
/// <param name="NormalisationInsensitive">Whether a name's composed and decomposed forms are one name.</param>
/// <param name="WindowsNames">
/// Whether the volume names files by Windows' rules: the characters Windows
/// refuses refused, a trailing period stripped, a device's name meaning the
/// device, and a name counted in UTF-16 units.
/// </param>
/// <remarks>
/// <para>
/// Read without writing to the volume, since a restore's plan writes
/// nothing. macOS says whether a volume distinguishes case, and its volumes
/// take the two forms for one. Windows says whether a folder distinguishes
/// case, which a folder made in one inherits, and keeps the two forms apart.
/// Linux says what kind of filesystem a folder is on, and whether the folder
/// folds case itself: FAT, exFAT and a share served over SMB or CIFS fold
/// case, and a casefolded folder folds both case and the two forms.
/// </para>
/// <para>
/// Windows' volumes, and a FAT or exFAT drive wherever it is mounted, name
/// files by Windows' rules. Linux's drivers for those two refuse most of what
/// Windows refuses and strip what it strips; the rest they write, and the
/// drive then holds a file Windows will not open by its name. A share's
/// client on Linux maps the characters Windows refuses to ones it takes, so
/// a name lands there as written.
/// </para>
/// <para>
/// A folder not yet made is judged by the nearest one that is, which is where
/// it would be made.
/// </para>
/// </remarks>
public readonly partial record struct VolumeNaming(bool CaseSensitive, bool NormalisationInsensitive, bool WindowsNames = false)
{
    private static readonly AsyncLocal<Func<string, VolumeNaming?>?> Flow = new();

    /// <summary>
    /// A test's word for how each folder's volume takes names, for this flow
    /// alone: null, the production value, asks the platform. An answer of
    /// null is a volume that says nothing.
    /// </summary>
    public static Func<string, VolumeNaming?>? OfInFlow
    {
        get => Flow.Value;
        set => Flow.Value = value;
    }

    /// <summary>
    /// How the volume <paramref name="directory"/> lies on, or would be made
    /// on, takes names; null where the platform does not say, or a file
    /// stands where a folder on the way to it would.
    /// </summary>
    /// <param name="directory">The folder.</param>
    /// <returns>How its volume takes names, or null.</returns>
    public static VolumeNaming? Of(string directory)
    {
        ThrowHelper.ThrowIfNullOrEmpty(directory);
        if (OfInFlow is { } stated)
        {
            return stated(directory);
        }

        try
        {
            if (NearestFolder(directory) is not { } folder)
            {
                return null;
            }

            if (OperatingSystem.IsMacOS())
            {
                return OfMacOs(folder);
            }

            if (OperatingSystem.IsWindows())
            {
                return OfWindows(folder);
            }

            return OperatingSystem.IsLinux() ? OfLinux(folder) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// How a Linux filesystem of the kind <paramref name="filesystemType"/>
    /// takes names in a folder that is, or is not, casefolded.
    /// </summary>
    /// <param name="filesystemType">The filesystem's kind, as <c>statfs</c> gives it.</param>
    /// <param name="casefolded">Whether the folder folds case itself.</param>
    /// <returns>How it takes names.</returns>
    public static VolumeNaming OfLinuxFilesystem(long filesystemType, bool casefolded) =>
        casefolded
            ? new VolumeNaming(CaseSensitive: false, NormalisationInsensitive: true)
            : (filesystemType & 0xFFFF_FFFF) switch
            {
                MsdosMagic or ExfatMagic =>
                    new VolumeNaming(CaseSensitive: false, NormalisationInsensitive: false, WindowsNames: true),
                CifsMagic or Smb2Magic or SmbMagic =>
                    new VolumeNaming(CaseSensitive: false, NormalisationInsensitive: false),
                _ => new VolumeNaming(CaseSensitive: true, NormalisationInsensitive: false),
            };

    private const long MsdosMagic = 0x4d44;
    private const long ExfatMagic = 0x2011BAB0;
    private const long CifsMagic = 0xFF534D42;
    private const long Smb2Magic = 0xFE534D42;
    private const long SmbMagic = 0x517B;

    /// <summary>The nearest folder at or above <paramref name="directory"/>, or null where a file stands in the way.</summary>
    private static string? NearestFolder(string directory)
    {
        var at = Path.GetFullPath(directory);
        while (!Directory.Exists(at))
        {
            if (File.Exists(at) || new FileInfo(at).LinkTarget is not null)
            {
                return null;
            }

            if (Path.GetDirectoryName(at) is not { } parent)
            {
                return null;
            }

            at = parent;
        }

        return at;
    }

    private const int CaseSensitiveName = 11;

    [LibraryImport("libc", EntryPoint = "pathconf", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint PathConf(string path, int name);

    private static VolumeNaming? OfMacOs(string folder) =>
        PathConf(folder, CaseSensitiveName) is var answer && answer >= 0
            ? new VolumeNaming(CaseSensitive: answer != 0, NormalisationInsensitive: true)
            : null;

    private const uint ReadAttributes = 0x80;
    private const uint BackupSemantics = 0x0200_0000;
    private const int FileCaseSensitiveInfo = 23;
    private const uint CaseSensitiveFolder = 1;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(
        string path, uint access, FileShare share, nint security, FileMode disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out uint information, uint size);

    private static VolumeNaming? OfWindows(string folder)
    {
        // Backup semantics is what lets a folder be opened at all.
        using var handle = CreateFile(
            folder, ReadAttributes, FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, BackupSemantics, 0);
        return !handle.IsInvalid && GetFileInformationByHandleEx(handle, FileCaseSensitiveInfo, out var flags, sizeof(uint))
            ? new VolumeNaming(CaseSensitive: (flags & CaseSensitiveFolder) != 0, NormalisationInsensitive: false, WindowsNames: true)
            : null;
    }

    private const int CasefoldFlag = 0x4000_0000;

    [LibraryImport("libc", EntryPoint = "statfs", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int StatFs(string path, nint buffer);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int Ioctl(int descriptor, nuint request, out int flags);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int descriptor);

    private static VolumeNaming? OfLinux(string folder)
    {
        // The filesystem's kind is the first field of struct statfs, a long.
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            return StatFs(folder, buffer) == 0
                ? OfLinuxFilesystem(Marshal.ReadIntPtr(buffer).ToInt64(), Casefolded(folder))
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Whether a Linux folder folds case itself; one whose filesystem keeps no such flag does not.</summary>
    private static bool Casefolded(string folder)
    {
        var descriptor = Open(folder, 0);
        if (descriptor < 0)
        {
            return false;
        }

        try
        {
            // FS_IOC_GETFLAGS: _IOR('f', 1, long), whose size the request encodes.
            var request = IntPtr.Size == 8 ? (nuint)0x8008_6601 : 0x8004_6601;
            return Ioctl(descriptor, request, out var flags) == 0 && (flags & CasefoldFlag) != 0;
        }
        finally
        {
            _ = Close(descriptor);
        }
    }
}
