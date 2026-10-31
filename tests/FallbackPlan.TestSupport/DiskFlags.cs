using System.Runtime.InteropServices;

namespace FallbackPlan.TestSupport;

/// <summary>
/// Sets a file's flags as its platform keeps them, so a test can give a
/// source file some: Linux's inode flags through <c>FS_IOC_SETFLAGS</c>, and
/// macOS's <c>st_flags</c> through <c>chflags</c>.
/// </summary>
/// <remarks>
/// Kept apart from the product's interop for the reason
/// <see cref="AllocatedSize"/> gives. The no-dump flag is the one a file's
/// owner may set on either platform, and the one that leaves the file free
/// to delete.
/// </remarks>
public static partial class DiskFlags
{
    /// <summary>Linux's no-dump inode flag.</summary>
    public const int LinuxNoDump = 0x40;

    /// <summary>macOS's no-dump flag.</summary>
    public const uint DarwinNoDump = 0x1;

    /// <summary>macOS's hidden flag.</summary>
    public const uint DarwinHidden = 0x8000;

    private const nuint FsIocGetFlags = 0x80086601;
    private const nuint FsIocSetFlags = 0x40086602;

    /// <summary>
    /// Adds <paramref name="flags"/> to the Linux inode flags of the file or
    /// folder at <paramref name="path"/>.
    /// </summary>
    public static void AddOnLinux(string path, int flags)
    {
        // open(2) rather than a file handle, which refuses a folder.
        var descriptor = Open(path, 0);
        if (descriptor < 0)
        {
            throw new IOException($"open failed on '{path}' (errno {Marshal.GetLastPInvokeError()}).");
        }

        try
        {
            var current = 0;
            if (Ioctl(descriptor, FsIocGetFlags, ref current) != 0)
            {
                throw new IOException($"FS_IOC_GETFLAGS failed on '{path}' (errno {Marshal.GetLastPInvokeError()}).");
            }

            current |= flags;
            if (Ioctl(descriptor, FsIocSetFlags, ref current) != 0)
            {
                throw new IOException($"FS_IOC_SETFLAGS failed on '{path}' (errno {Marshal.GetLastPInvokeError()}).");
            }
        }
        finally
        {
            _ = Close(descriptor);
        }
    }

    /// <summary>Gives the file at <paramref name="path"/> the macOS flags <paramref name="flags"/>.</summary>
    public static void SetOnMacOs(string path, uint flags)
    {
        if (Chflags(path, flags) != 0)
        {
            throw new IOException($"chflags failed on '{path}' (errno {Marshal.GetLastPInvokeError()}).");
        }
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int descriptor);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int Ioctl(int descriptor, nuint request, ref int flags);

    [LibraryImport("libSystem", EntryPoint = "chflags", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Chflags(string path, uint flags);
}
