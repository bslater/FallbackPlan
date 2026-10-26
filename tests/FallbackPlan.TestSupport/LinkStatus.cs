using System.Runtime.InteropServices;

namespace FallbackPlan.TestSupport;

/// <summary>
/// What a symlink itself carries, never what it points at: the oracle a
/// restore's writes onto a link are checked against.
/// </summary>
/// <remarks>
/// Kept apart from the product's interop for the reason
/// <see cref="AllocatedSize"/> gives. .NET reports a link's permissions from
/// what the link points at and has no call for a link's owner, so it cannot
/// tell a write onto a link from one through it. Linux reads <c>statx</c>
/// without following a final link, whose layout is one fixed kernel ABI on
/// every architecture.
/// macOS reads <c>lstat</c> against Darwin's 64-bit-inode layout. Windows
/// restores no links.
/// </remarks>
/// <param name="ModifiedUtc">The link's own modification time.</param>
/// <param name="AccessedUtc">The link's own access time.</param>
/// <param name="CreatedUtc">The link's own creation time, where the filesystem keeps one.</param>
/// <param name="Permissions">The link's own permission, set-id and sticky bits.</param>
/// <param name="UserId">The account that owns the link.</param>
/// <param name="GroupId">The group that owns the link.</param>
public sealed partial record LinkStatus(
    DateTime ModifiedUtc, DateTime AccessedUtc, DateTime? CreatedUtc, uint Permissions, uint UserId, uint GroupId)
{
    /// <summary>What the link at <paramref name="path"/> carries itself.</summary>
    /// <param name="path">An existing link, which may point at nothing.</param>
    public static LinkStatus Of(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows restores no links.");
        }

        return OperatingSystem.IsMacOS() ? OfDarwin(path) : OfLinux(path);
    }

    private const int AtFdcwd = -100;
    private const int AtSymlinkNofollow = 0x100;
    private const uint StatxBasicStats = 0x7FF;
    private const uint StatxBirthTime = 0x800;
    private const uint ModeBits = 0xFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct StatxTimestamp
    {
        public long Seconds;
        public uint Nanoseconds;
        public int Reserved;
    }

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
        public ulong AttributesMask;
        public StatxTimestamp AccessTime;
        public StatxTimestamp BirthTime;
        public StatxTimestamp ChangeTime;
        public StatxTimestamp ModifyTime;
        public fixed byte Rest[128];
    }

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int NativeStatx(int directoryFd, string path, int flags, uint mask, out Statx buffer);

    private static LinkStatus OfLinux(string path)
    {
        if (NativeStatx(AtFdcwd, path, AtSymlinkNofollow, StatxBasicStats | StatxBirthTime, out var buffer) != 0)
        {
            throw new IOException($"statx({path}) failed with errno {Marshal.GetLastPInvokeError()}.");
        }

        return new LinkStatus(
            Utc(buffer.ModifyTime.Seconds, buffer.ModifyTime.Nanoseconds),
            Utc(buffer.AccessTime.Seconds, buffer.AccessTime.Nanoseconds),
            (buffer.Mask & StatxBirthTime) != 0 ? Utc(buffer.BirthTime.Seconds, buffer.BirthTime.Nanoseconds) : null,
            buffer.Mode & ModeBits,
            buffer.Uid,
            buffer.Gid);
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

    [LibraryImport("libSystem", EntryPoint = "lstat$INODE64", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int NativeLstatX64(string path, out DarwinStat buffer);

    [LibraryImport("libSystem", EntryPoint = "lstat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int NativeLstatArm64(string path, out DarwinStat buffer);

    private static LinkStatus OfDarwin(string path)
    {
        var status = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? NativeLstatArm64(path, out var buffer)
            : NativeLstatX64(path, out buffer);

        if (status != 0)
        {
            throw new IOException($"lstat({path}) failed with errno {Marshal.GetLastPInvokeError()}.");
        }

        return new LinkStatus(
            Utc(buffer.ModifySeconds, buffer.ModifyNanoseconds),
            Utc(buffer.AccessSeconds, buffer.AccessNanoseconds),
            Utc(buffer.BirthSeconds, buffer.BirthNanoseconds),
            buffer.Mode & ModeBits,
            buffer.Uid,
            buffer.Gid);
    }

    private static DateTime Utc(long seconds, long nanoseconds) =>
        DateTime.UnixEpoch.AddTicks((seconds * TimeSpan.TicksPerSecond) + (nanoseconds / 100));
}
