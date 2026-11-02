using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FallbackPlan.Domain;

/// <summary>
/// What a restore makes a junction through (FR-RST-004; ADR-0183
/// Amendment 2), so a case can stand calls of its own in for the platform's.
/// </summary>
public interface IMountPoints
{
    /// <summary>
    /// Makes a junction at <paramref name="path"/> naming the folder
    /// <paramref name="target"/>.
    /// </summary>
    /// <param name="path">Where it goes; nothing may stand there.</param>
    /// <param name="target">The folder it names, by its drive's path.</param>
    /// <returns>Null when it was made, or why it was not; nothing is left at <paramref name="path"/> then.</returns>
    string? Make(string path, string target);
}

/// <summary>
/// Which links are junctions, and the platform's calls that make one
/// (ADR-0183 Amendments 1 and 2).
/// </summary>
/// <remarks>
/// <para>
/// A junction is a Windows mount point that names a folder by its drive's
/// path. Windows makes one as a folder carrying a mount-point reparse point,
/// set through <c>FSCTL_SET_REPARSE_POINT</c> on a handle opened on the
/// folder itself, which any account that may make the folder may do. The
/// reparse point names the folder twice: as the kernel resolves it, under
/// <c>\??\</c>, and as it is shown.
/// </para>
/// <para>
/// Windows alone makes one: these calls make nothing elsewhere, and say so.
/// Sits beside <see cref="AlternateStreams"/> for the reason it gives.
/// </para>
/// </remarks>
public static partial class MountPoints
{
    private const uint GenericWrite = 0x4000_0000;
    private const uint BackupSemantics = 0x0200_0000;
    private const uint OpenReparsePoint = 0x0020_0000;
    private const uint SetReparsePoint = 0x0009_00A4;
    private const uint MountPointTag = 0xA000_0003;

    /// <summary>The name prefix the kernel resolves a mount point's target under.</summary>
    private const string KernelPrefix = @"\??\";

    /// <summary>Whether this platform makes a junction: Windows alone does.</summary>
    public static bool CanMake { get; } = OperatingSystem.IsWindows();

    /// <summary>This platform's calls.</summary>
    public static IMountPoints OfThisPlatform { get; } = new Platform();

    /// <summary>
    /// Whether <paramref name="target"/> names a folder by its drive's path,
    /// as a junction's does (<c>C:\Users\Shared</c>): a letter, a colon and
    /// a separator. A volume's name, a share, and a relative path do not.
    /// </summary>
    public static bool NamesAFolderOnADrive(string? target) =>
        target is [>= 'A' and <= 'Z' or >= 'a' and <= 'z', ':', '\\', ..];

    /// <summary>
    /// Whether a link is a junction: one captured as a mount point, as
    /// <paramref name="form"/> keeps or <paramref name="diagnostics"/> name
    /// (<see cref="MetadataRules.LinkFormOf"/>), that names a folder by its
    /// drive's path.
    /// </summary>
    /// <param name="form">The form its manifest keeps in key 14.</param>
    /// <param name="diagnostics">Its capture diagnostics.</param>
    /// <param name="target">The path it names, or null where it names none.</param>
    public static bool IsJunction(LinkForm form, IEnumerable<string> diagnostics, string? target) =>
        MetadataRules.LinkFormOf(form, diagnostics) == LinkForm.MountPoint && NamesAFolderOnADrive(target);

    /// <summary>
    /// The reparse data a junction to <paramref name="target"/> carries:
    /// <c>REPARSE_DATA_BUFFER</c> in its mount-point form, the path the kernel
    /// resolves and then the path shown, each ending in a null.
    /// </summary>
    private static byte[] ReparseDataOf(string target)
    {
        var resolved = Encoding.Unicode.GetBytes(KernelPrefix + target);
        var shown = Encoding.Unicode.GetBytes(target);
        var pathBytes = resolved.Length + sizeof(char) + shown.Length + sizeof(char);

        // The tag, the data's length and a reserved word, then four offsets
        // and lengths, then the names.
        var data = new byte[8 + 8 + pathBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(data, MountPointTag);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), (ushort)(8 + pathBytes));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), (ushort)resolved.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), (ushort)(resolved.Length + sizeof(char)));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), (ushort)shown.Length);
        resolved.CopyTo(data, 16);
        shown.CopyTo(data, 16 + resolved.Length + sizeof(char));
        return data;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(
        string path, uint access, FileShare share, nint security, FileMode disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle device, uint code, ref byte input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);

    private sealed class Platform : IMountPoints
    {
        public string? Make(string path, string target)
        {
            if (!OperatingSystem.IsWindows())
            {
                return "this platform makes no junction";
            }

            if (Path.Exists(path))
            {
                return "something already stands at its name";
            }

            var data = ReparseDataOf(target);
            Directory.CreateDirectory(path);
            string? refused;
            using (var handle = CreateFile(path, GenericWrite, FileShare.None, 0, FileMode.Open, BackupSemantics | OpenReparsePoint, 0))
            {
                // A reparse point holds at most 16 KiB; a longer one is the
                // call's to refuse, as it refuses anything else.
                refused = !handle.IsInvalid && DeviceIoControl(handle, SetReparsePoint, ref data[0], (uint)data.Length, 0, 0, out _, 0)
                    ? null
                    : string.Create(CultureInfo.InvariantCulture, $"refused by the target (error {Marshal.GetLastPInvokeError()})");
            }

            if (refused is not null)
            {
                Directory.Delete(path);
            }

            return refused;
        }
    }
}
