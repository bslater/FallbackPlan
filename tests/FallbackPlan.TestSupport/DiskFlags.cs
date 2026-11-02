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

    /// <summary>Linux's immutable inode flag, which only a process holding <c>CAP_LINUX_IMMUTABLE</c> may set or clear.</summary>
    public const int LinuxImmutable = 0x10;

    /// <summary>Linux's append-only inode flag, which only a process holding <c>CAP_LINUX_IMMUTABLE</c> may set or clear.</summary>
    public const int LinuxAppendOnly = 0x20;

    /// <summary>macOS's user immutable flag, which the file's owner may set and clear.</summary>
    public const uint DarwinImmutable = 0x2;

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

    /// <summary>The Linux inode flags of the file or folder at <paramref name="path"/>.</summary>
    public static int OnLinux(string path)
    {
        var descriptor = Open(path, 0);
        if (descriptor < 0)
        {
            throw new IOException($"open failed on '{path}' (errno {Marshal.GetLastPInvokeError()}).");
        }

        try
        {
            var current = 0;
            return Ioctl(descriptor, FsIocGetFlags, ref current) == 0
                ? current
                : throw new IOException($"FS_IOC_GETFLAGS failed on '{path}' (errno {Marshal.GetLastPInvokeError()}).");
        }
        finally
        {
            _ = Close(descriptor);
        }
    }

    /// <summary>
    /// The macOS flags of the file, folder or link at <paramref name="path"/>,
    /// as <c>stat -f %f</c> gives them, the link's own for a link.
    /// </summary>
    public static uint OnMacOs(string path)
    {
        using var stat = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/bin/stat", ["-f", "%f", path])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var output = stat.StandardOutput.ReadToEnd().Trim();
        stat.WaitForExit();
        return stat.ExitCode == 0
            ? uint.Parse(output, System.Globalization.CultureInfo.InvariantCulture)
            : throw new IOException($"stat -f %f failed on '{path}'.");
    }

    /// <summary>
    /// Clears every flag that would keep the tree at <paramref name="root"/>
    /// from being deleted, as a test that restored flags must before it
    /// cleans up. Best effort: an entry whose flags cannot be read is passed
    /// over.
    /// </summary>
    public static void ClearUnder(string root)
    {
        if (!Directory.Exists(root) || OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Append(root))
        {
            if (OperatingSystem.IsMacOS())
            {
                _ = Lchflags(entry, 0);
                continue;
            }

            if (new FileInfo(entry).LinkTarget is not null)
            {
                continue;
            }

            var descriptor = Open(entry, 0x800);
            if (descriptor < 0)
            {
                continue;
            }

            try
            {
                var current = 0;
                if (Ioctl(descriptor, FsIocGetFlags, ref current) == 0 && (current & (LinuxImmutable | LinuxAppendOnly)) != 0)
                {
                    current &= ~(LinuxImmutable | LinuxAppendOnly);
                    _ = Ioctl(descriptor, FsIocSetFlags, ref current);
                }
            }
            finally
            {
                _ = Close(descriptor);
            }
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

    [LibraryImport("libSystem", EntryPoint = "lchflags", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Lchflags(string path, uint flags);
}
