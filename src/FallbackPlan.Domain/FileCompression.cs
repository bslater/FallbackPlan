using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FallbackPlan.Domain;

/// <summary>
/// What a restore gives a file's and a folder's NTFS compression back
/// through (FR-RST-004; ADR-0175 Amendment 2), so a case can stand calls of
/// its own in for the platform's.
/// </summary>
public interface IFileCompression
{
    /// <summary>
    /// Makes <paramref name="path"/> compressed, or not, as
    /// <paramref name="compressed"/> says.
    /// </summary>
    /// <param name="path">A file the restore landed or a folder it made.</param>
    /// <param name="compressed">Whether it was captured compressed.</param>
    /// <returns>Null when it stands so, or why it does not.</returns>
    string? Apply(string path, bool compressed);
}

/// <summary>The platform's calls for a file's and a folder's compression.</summary>
/// <remarks>
/// <para>
/// NTFS compresses a file or a folder through <c>FSCTL_SET_COMPRESSION</c>
/// on a handle open to read and write its data, which a read-only file
/// refuses. A folder's compression is what each file made in it later starts
/// with, and changes none it already holds. An entry already standing as
/// asked is left, so the call is made only where the two differ.
/// </para>
/// <para>
/// Windows alone keeps it: these calls set nothing elsewhere, and say so.
/// Sits beside <see cref="AlternateStreams"/> for the reason it gives.
/// </para>
/// </remarks>
public static partial class FileCompression
{
    private const uint GenericRead = 0x8000_0000;
    private const uint GenericWrite = 0x4000_0000;
    private const uint BackupSemantics = 0x0200_0000;
    private const uint SetCompression = 0x0009_C040;
    private const ushort NoCompression = 0;
    private const ushort DefaultCompression = 1;

    /// <summary>Whether this platform keeps a file's compression: Windows alone does.</summary>
    public static bool CanSet { get; } = OperatingSystem.IsWindows();

    /// <summary>This platform's calls.</summary>
    public static IFileCompression OfThisPlatform { get; } = new Platform();

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(
        string path, uint access, FileShare share, nint security, FileMode disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle device, uint code, ref ushort input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);

    private static string Refused() =>
        string.Create(CultureInfo.InvariantCulture, $"refused by the target (error {Marshal.GetLastPInvokeError()})");

    private sealed class Platform : IFileCompression
    {
        public string? Apply(string path, bool compressed)
        {
            if (!OperatingSystem.IsWindows())
            {
                return "this platform keeps no compression";
            }

            try
            {
                if (File.GetAttributes(path).HasFlag(FileAttributes.Compressed) == compressed)
                {
                    return null;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return "refused by the target";
            }

            // Backup semantics is what lets a folder be opened at all.
            using var handle = CreateFile(
                path, GenericRead | GenericWrite, FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, BackupSemantics, 0);
            if (handle.IsInvalid)
            {
                return Refused();
            }

            var format = compressed ? DefaultCompression : NoCompression;
            return DeviceIoControl(handle, SetCompression, ref format, sizeof(ushort), 0, 0, out _, 0) ? null : Refused();
        }
    }
}
