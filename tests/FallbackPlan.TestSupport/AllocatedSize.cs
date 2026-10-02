using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FallbackPlan.TestSupport;

/// <summary>
/// How many bytes of storage a file occupies, as its filesystem reports it —
/// the measure that tells a file whose holes stayed holes from one whose
/// zeroes were written, which no read of its content can.
/// </summary>
/// <remarks>
/// <para>
/// Kept apart from the product's own stat interop on purpose: an oracle that
/// shared the subject's code could share its mistakes. Linux uses
/// <c>statx</c>, whose layout is one fixed kernel ABI on every architecture;
/// macOS uses <c>stat</c> against Darwin's 64-bit-inode layout; Windows uses
/// <c>GetCompressedFileSizeW</c>, which reports a sparse file's allocated
/// clusters rather than its length.
/// </para>
/// <para>
/// On Linux and macOS the file is written back first, with <c>fsync</c> on a
/// descriptor of its own. A filesystem that defers allocation reports only
/// what it has allocated so far, and APFS fills a hole it will not keep with
/// zeroes, and allocates them, only when it writes the file back. A file
/// measured before then looks sparser than it is. The descriptor is opened
/// with the system call, not through .NET, whose emulated share modes would
/// refuse a file a restore still holds exclusively.
/// </para>
/// </remarks>
public static partial class AllocatedSize
{
    /// <summary>The bytes <paramref name="path"/> occupies on its filesystem.</summary>
    /// <param name="path">An existing file.</param>
    public static long Of(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (OperatingSystem.IsWindows())
        {
            return OfWindows(path);
        }

        if (OperatingSystem.IsMacOS())
        {
            WriteBack(path, DarwinOpen, DarwinFsync, DarwinClose);
            return OfDarwin(path);
        }

        WriteBack(path, LinuxOpen, LinuxFsync, LinuxClose);
        return OfLinux(path);
    }

    private const int ReadOnly = 0;

    private static void WriteBack(
        string path, Func<string, int, int> open, Func<int, int> fsync, Func<int, int> close)
    {
        var descriptor = open(path, ReadOnly);
        if (descriptor < 0)
        {
            throw new IOException($"open({path}) failed with errno {Marshal.GetLastPInvokeError()}.");
        }

        try
        {
            if (fsync(descriptor) != 0)
            {
                throw new IOException($"fsync({path}) failed with errno {Marshal.GetLastPInvokeError()}.");
            }
        }
        finally
        {
            _ = close(descriptor);
        }
    }

    // open's mode argument is variadic, and passed only with O_CREAT, so the
    // two fixed arguments are the whole call on every architecture.
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LinuxOpen(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int LinuxFsync(int descriptor);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int LinuxClose(int descriptor);

    [LibraryImport("libSystem", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int DarwinOpen(string path, int flags);

    [LibraryImport("libSystem", EntryPoint = "fsync", SetLastError = true)]
    private static partial int DarwinFsync(int descriptor);

    [LibraryImport("libSystem", EntryPoint = "close", SetLastError = true)]
    private static partial int DarwinClose(int descriptor);

    private const int AtFdcwd = -100;
    private const uint StatxBlocks = 0x400;

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Statx
    {
        public uint Mask;
        public uint BlockSize;
        public ulong Attributes;
        public uint LinkCount;
        public uint Uid;
        public uint Gid;
        public ushort Mode;
        public ushort Spare0;
        public ulong Inode;
        public ulong Size;
        public ulong Blocks;
        public fixed byte Rest[200];
    }

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int NativeStatx(int directoryFd, string path, int flags, uint mask, out Statx buffer);

    private static long OfLinux(string path)
    {
        if (NativeStatx(AtFdcwd, path, 0, StatxBlocks, out var buffer) != 0)
        {
            throw new IOException($"statx({path}) failed with errno {Marshal.GetLastPInvokeError()}.");
        }

        if ((buffer.Mask & StatxBlocks) == 0)
        {
            throw new IOException($"statx({path}) did not report the file's block count.");
        }

        return checked((long)buffer.Blocks * 512);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DarwinStat
    {
        public int Dev;
        public ushort Mode;
        public ushort LinkCount;
        public ulong Inode;
        public uint Uid;
        public uint Gid;
        public int Rdev;
        public long AccessSeconds;
        public long AccessNanoseconds;
        public long ModifySeconds;
        public long ModifyNanoseconds;
        public long ChangeSeconds;
        public long ChangeNanoseconds;
        public long BirthSeconds;
        public long BirthNanoseconds;
        public long Size;
        public long Blocks;
        public int BlockSize;
        public uint Flags;
        public uint Generation;
        public int Spare;
        public long Qspare0;
        public long Qspare1;
    }

    [LibraryImport("libSystem", EntryPoint = "stat$INODE64", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int NativeStatX64(string path, out DarwinStat buffer);

    [LibraryImport("libSystem", EntryPoint = "stat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int NativeStatArm64(string path, out DarwinStat buffer);

    private static long OfDarwin(string path)
    {
        var status = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? NativeStatArm64(path, out var buffer)
            : NativeStatX64(path, out buffer);

        if (status != 0)
        {
            throw new IOException($"stat({path}) failed with errno {Marshal.GetLastPInvokeError()}.");
        }

        return checked(buffer.Blocks * 512);
    }

    private const uint InvalidFileSize = 0xFFFF_FFFF;

    [LibraryImport("kernel32", EntryPoint = "GetCompressedFileSizeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [SupportedOSPlatform("windows")]
    private static partial uint GetCompressedFileSize(string fileName, out uint fileSizeHigh);

    [SupportedOSPlatform("windows")]
    private static long OfWindows(string path)
    {
        var low = GetCompressedFileSize(path, out var high);
        if (low == InvalidFileSize && Marshal.GetLastPInvokeError() is var error and not 0)
        {
            throw new IOException($"GetCompressedFileSizeW({path}) failed with error {error}.");
        }

        return ((long)high << 32) | low;
    }
}
