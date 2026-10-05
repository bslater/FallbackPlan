using System.Runtime.InteropServices;

namespace FallbackPlan.TestSupport;

/// <summary>
/// Which account and group own a file, as its filesystem reports them: the
/// oracle a restore's ownership is checked against.
/// </summary>
/// <remarks>
/// Kept apart from the product's interop for the reason
/// <see cref="AllocatedSize"/> gives. Linux reads <c>statx</c>, whose layout
/// is one fixed kernel ABI on every architecture. macOS reads <c>stat</c>
/// against Darwin's 64-bit-inode layout. A Windows file's owner lives in its
/// security descriptor, which nothing here reads.
/// </remarks>
public static partial class FileOwner
{
    /// <summary>The user and group ids that own <paramref name="path"/>.</summary>
    /// <param name="path">An existing file.</param>
    public static (uint UserId, uint GroupId) Of(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("A Windows file's owner lives in its security descriptor.");
        }

        return OperatingSystem.IsMacOS() ? OfDarwin(path) : OfLinux(path);
    }

    private const int AtFdcwd = -100;
    private const uint StatxOwnership = 0x8 | 0x10;

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

    private static (uint, uint) OfLinux(string path)
    {
        if (NativeStatx(AtFdcwd, path, 0, StatxOwnership, out var buffer) != 0)
        {
            throw new IOException($"statx({path}) failed with errno {Marshal.GetLastPInvokeError()}.");
        }

        if ((buffer.Mask & StatxOwnership) != StatxOwnership)
        {
            throw new IOException($"statx({path}) did not report the file's owner and group.");
        }

        return (buffer.Uid, buffer.Gid);
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

    private static (uint, uint) OfDarwin(string path)
    {
        var status = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? NativeStatArm64(path, out var buffer)
            : NativeStatX64(path, out buffer);

        if (status != 0)
        {
            throw new IOException($"stat({path}) failed with errno {Marshal.GetLastPInvokeError()}.");
        }

        return (buffer.Uid, buffer.Gid);
    }
}
