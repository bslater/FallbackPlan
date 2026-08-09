using System.Runtime.InteropServices;

namespace FallbackPlan.Filesystem.Local;

/// <summary>
/// The stat result the scanner consumes, platform-neutral: identity,
/// classification, times in Unix milliseconds, and POSIX ownership. Public
/// because pre/post-read revalidation (architecture 06 §1) is part of the
/// capture contract the publisher consumes.
/// </summary>
public readonly record struct StatResult(
    ulong Device,
    ulong FileId,
    uint LinkCount,
    uint Mode,
    uint Uid,
    uint Gid,
    long Size,
    ulong? ModifiedAtMs,
    ulong? CreatedAtMs,
    ulong? AccessedAtMs,
    uint RdevMajor,
    uint RdevMinor)
{
    private const uint TypeMask = 0xF000;

    public bool IsDirectory => (Mode & TypeMask) == 0x4000;

    public bool IsRegularFile => (Mode & TypeMask) == 0x8000;

    public bool IsSymlink => (Mode & TypeMask) == 0xA000;

    public bool IsFifo => (Mode & TypeMask) == 0x1000;

    public bool IsSocket => (Mode & TypeMask) == 0xC000;

    public bool IsCharDevice => (Mode & TypeMask) == 0x2000;

    public bool IsBlockDevice => (Mode & TypeMask) == 0x6000;

    /// <summary>The permission bits the manifest stores (key 4).</summary>
    public uint PermissionBits => Mode & 0x0FFF;
}

/// <summary>
/// The NUL-separated name list both platforms' xattr listing calls return.
/// </summary>
internal static class PosixXattrNames
{
    internal static List<string> Split(byte[] buffer, int size)
    {
        var names = new List<string>();
        var start = 0;
        for (var i = 0; i < size && i < buffer.Length; i++)
        {
            if (buffer[i] != 0)
            {
                continue;
            }

            if (i > start)
            {
                names.Add(System.Text.Encoding.UTF8.GetString(buffer, start, i - start));
            }

            start = i + 1;
        }

        return names;
    }
}

/// <summary>
/// Linux interop: <c>statx</c> (a fixed kernel ABI, identical on every
/// architecture — the reason it is used instead of <c>struct stat</c>,
/// whose layout varies), xattrs, and hole enumeration. Confined to this
/// class; everything above consumes <see cref="StatResult"/>.
/// </summary>
internal static partial class LinuxInterop
{
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
        public uint RdevMajor;
        public uint RdevMinor;
        public uint DevMajor;
        public uint DevMinor;
        public ulong MountId;
        public uint DioMemAlign;
        public uint DioOffsetAlign;
        public fixed ulong Spare[12];
    }

    private const int AtFdcwd = -100;
    private const int AtSymlinkNofollow = 0x100;
    private const uint StatxBasicStats = 0x7FF;
    private const uint StatxBtime = 0x800;

    private const int AtEmptyPath = 0x1000;

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int NativeStatx(int dirfd, string pathname, int flags, uint mask, out Statx buf);

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static unsafe partial int NativeStatxBytes(int dirfd, byte* pathname, int flags, uint mask, out Statx buf);

    /// <summary>Stats a child by name bytes, relative to an open directory.</summary>
    public static unsafe bool TryStatAt(int directoryFd, ReadOnlySpan<byte> name, out StatResult result)
    {
        var terminated = new byte[name.Length + 1];
        name.CopyTo(terminated);

        int status;
        Statx stx;
        fixed (byte* pathname = terminated)
        {
            status = NativeStatxBytes(
                directoryFd, pathname, AtSymlinkNofollow, StatxBasicStats | StatxBtime, out stx);
        }

        result = status == 0 ? ToResult(stx) : default;
        return status == 0;
    }

    /// <summary>
    /// Stats the object an open descriptor names — the one stat that cannot
    /// be raced, because a descriptor names an inode and nothing can move it
    /// to another one.
    /// </summary>
    public static unsafe bool TryStatHandle(int fd, out StatResult result)
    {
        var empty = new byte[1];

        int status;
        Statx stx;
        fixed (byte* pathname = empty)
        {
            status = NativeStatxBytes(
                fd, pathname, AtEmptyPath | AtSymlinkNofollow, StatxBasicStats | StatxBtime, out stx);
        }

        result = status == 0 ? ToResult(stx) : default;
        return status == 0;
    }

    [LibraryImport("libc", EntryPoint = "llistxattr", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint NativeListXattr(string path, byte[]? list, nuint size);

    [LibraryImport("libc", EntryPoint = "lgetxattr", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint NativeGetXattr(string path, string name, byte[]? value, nuint size);

    [LibraryImport("libc", EntryPoint = "flistxattr", SetLastError = true)]
    private static partial nint NativeListXattrAt(int fd, byte[]? list, nuint size);

    [LibraryImport("libc", EntryPoint = "fgetxattr", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint NativeGetXattrAt(int fd, string name, byte[]? value, nuint size);

    /// <summary>Lists extended attribute names on an open descriptor.</summary>
    public static IReadOnlyList<string> ListXattrNamesAt(int fd)
    {
        var size = NativeListXattrAt(fd, null, 0);
        if (size <= 0)
        {
            return [];
        }

        var buffer = new byte[size];
        size = NativeListXattrAt(fd, buffer, (nuint)buffer.Length);
        return size <= 0 ? [] : PosixXattrNames.Split(buffer, (int)size);
    }

    /// <summary>Reads one extended attribute from an open descriptor; null on failure.</summary>
    public static byte[]? GetXattrAt(int fd, string name)
    {
        var size = NativeGetXattrAt(fd, name, null, 0);
        if (size < 0)
        {
            return null;
        }

        var buffer = new byte[size];
        return NativeGetXattrAt(fd, name, buffer, (nuint)buffer.Length) == size ? buffer : null;
    }

    public static bool TryStat(string path, out StatResult result)
    {
        if (NativeStatx(AtFdcwd, path, AtSymlinkNofollow, StatxBasicStats | StatxBtime, out var stx) != 0)
        {
            result = default;
            return false;
        }

        result = ToResult(stx);
        return true;
    }

    private static StatResult ToResult(in Statx stx) => new(
        Device: ((ulong)stx.DevMajor << 32) | stx.DevMinor,
        FileId: stx.Inode,
        LinkCount: stx.LinkCount,
        Mode: stx.Mode,
        Uid: stx.Uid,
        Gid: stx.Gid,
        Size: (long)stx.Size,
        ModifiedAtMs: ToMilliseconds(stx.ModifyTime),
        CreatedAtMs: (stx.Mask & StatxBtime) != 0 ? ToMilliseconds(stx.BirthTime) : null,
        AccessedAtMs: ToMilliseconds(stx.AccessTime),
        RdevMajor: stx.RdevMajor,
        RdevMinor: stx.RdevMinor);

    private static ulong? ToMilliseconds(StatxTimestamp timestamp) =>
        timestamp.Seconds < 0
            ? null
            : (ulong)timestamp.Seconds * 1_000 + timestamp.Nanoseconds / 1_000_000;

    /// <summary>Lists extended attribute names; empty on any failure — degrade, never abort.</summary>
    public static IReadOnlyList<string> ListXattrNames(string path)
    {
        var size = NativeListXattr(path, null, 0);
        if (size <= 0)
        {
            return [];
        }

        var buffer = new byte[size];
        size = NativeListXattr(path, buffer, (nuint)buffer.Length);
        if (size <= 0)
        {
            return [];
        }

        return PosixXattrNames.Split(buffer, (int)size);
    }

    /// <summary>Reads one extended attribute value; null on failure.</summary>
    public static byte[]? GetXattr(string path, string name)
    {
        var size = NativeGetXattr(path, name, null, 0);
        if (size < 0)
        {
            return null;
        }

        var buffer = new byte[size];
        return NativeGetXattr(path, name, buffer, (nuint)buffer.Length) == size ? buffer : null;
    }
}

/// <summary>
/// macOS interop: <c>lstat</c> against Darwin's stable 64-bit-inode layout
/// (the <c>$INODE64</c> entry point on x64; plain on arm64, where 64-bit
/// inodes are the only ABI), and xattrs with the no-follow option.
/// </summary>
internal static partial class DarwinInterop
{
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

    private const int XattrNofollow = 0x0001;
    private const int AtSymlinkNofollow = 0x0020;

    [LibraryImport("libSystem", EntryPoint = "lstat$INODE64", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int NativeLstatX64(string path, out DarwinStat buf);

    [LibraryImport("libSystem", EntryPoint = "fstatat$INODE64", SetLastError = true)]
    private static unsafe partial int NativeFstatatX64(int dirfd, byte* path, out DarwinStat buf, int flag);

    [LibraryImport("libSystem", EntryPoint = "fstatat", SetLastError = true)]
    private static unsafe partial int NativeFstatatArm64(int dirfd, byte* path, out DarwinStat buf, int flag);

    [LibraryImport("libSystem", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static partial int NativeFstatX64(int fd, out DarwinStat buf);

    [LibraryImport("libSystem", EntryPoint = "fstat", SetLastError = true)]
    private static partial int NativeFstatArm64(int fd, out DarwinStat buf);

    /// <summary>Stats a child by name bytes, relative to an open directory.</summary>
    public static unsafe bool TryStatAt(int directoryFd, ReadOnlySpan<byte> name, out StatResult result)
    {
        var terminated = new byte[name.Length + 1];
        name.CopyTo(terminated);

        int status;
        DarwinStat stat;
        fixed (byte* path = terminated)
        {
            status = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? NativeFstatatArm64(directoryFd, path, out stat, AtSymlinkNofollow)
                : NativeFstatatX64(directoryFd, path, out stat, AtSymlinkNofollow);
        }

        result = status == 0 ? ToResult(stat) : default;
        return status == 0;
    }

    /// <summary>Stats the object an open descriptor names.</summary>
    public static bool TryStatHandle(int fd, out StatResult result)
    {
        var status = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? NativeFstatArm64(fd, out var stat)
            : NativeFstatX64(fd, out stat);

        result = status == 0 ? ToResult(stat) : default;
        return status == 0;
    }

    [LibraryImport("libSystem", EntryPoint = "lstat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int NativeLstatArm64(string path, out DarwinStat buf);

    [LibraryImport("libSystem", EntryPoint = "listxattr", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint NativeListXattr(string path, byte[]? list, nuint size, int options);

    [LibraryImport("libSystem", EntryPoint = "getxattr", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint NativeGetXattr(string path, string name, byte[]? value, nuint size, uint position, int options);

    [LibraryImport("libSystem", EntryPoint = "flistxattr", SetLastError = true)]
    private static partial nint NativeListXattrAt(int fd, byte[]? list, nuint size, int options);

    [LibraryImport("libSystem", EntryPoint = "fgetxattr", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint NativeGetXattrAt(int fd, string name, byte[]? value, nuint size, uint position, int options);

    /// <summary>Lists extended attribute names on an open descriptor.</summary>
    public static IReadOnlyList<string> ListXattrNamesAt(int fd)
    {
        var size = NativeListXattrAt(fd, null, 0, 0);
        if (size <= 0)
        {
            return [];
        }

        var buffer = new byte[size];
        size = NativeListXattrAt(fd, buffer, (nuint)buffer.Length, 0);
        return size <= 0 ? [] : PosixXattrNames.Split(buffer, (int)size);
    }

    /// <summary>Reads one extended attribute from an open descriptor; null on failure.</summary>
    public static byte[]? GetXattrAt(int fd, string name)
    {
        var size = NativeGetXattrAt(fd, name, null, 0, 0, 0);
        if (size < 0)
        {
            return null;
        }

        var buffer = new byte[size];
        return NativeGetXattrAt(fd, name, buffer, (nuint)buffer.Length, 0, 0) == size ? buffer : null;
    }

    public static bool TryStat(string path, out StatResult result)
    {
        var status = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? NativeLstatArm64(path, out var stat)
            : NativeLstatX64(path, out stat);

        if (status != 0)
        {
            result = default;
            return false;
        }

        result = ToResult(stat);
        return true;
    }

    private static StatResult ToResult(in DarwinStat stat) => new(
        Device: unchecked((ulong)(uint)stat.Dev),
        FileId: stat.Inode,
        LinkCount: stat.LinkCount,
        Mode: stat.Mode,
        Uid: stat.Uid,
        Gid: stat.Gid,
        Size: stat.Size,
        ModifiedAtMs: ToMilliseconds(stat.ModifySeconds, stat.ModifyNanoseconds),
        CreatedAtMs: ToMilliseconds(stat.BirthSeconds, stat.BirthNanoseconds),
        AccessedAtMs: ToMilliseconds(stat.AccessSeconds, stat.AccessNanoseconds),
        RdevMajor: (uint)((stat.Rdev >> 24) & 0xFF),
        RdevMinor: (uint)(stat.Rdev & 0xFFFFFF));

    private static ulong? ToMilliseconds(long seconds, long nanoseconds) =>
        seconds < 0 ? null : (ulong)seconds * 1_000 + (ulong)nanoseconds / 1_000_000;

    /// <summary>Lists extended attribute names without following symlinks.</summary>
    public static IReadOnlyList<string> ListXattrNames(string path)
    {
        var size = NativeListXattr(path, null, 0, XattrNofollow);
        if (size <= 0)
        {
            return [];
        }

        var buffer = new byte[size];
        size = NativeListXattr(path, buffer, (nuint)buffer.Length, XattrNofollow);
        if (size <= 0)
        {
            return [];
        }

        return PosixXattrNames.Split(buffer, (int)size);
    }

    /// <summary>Reads one extended attribute value; null on failure.</summary>
    public static byte[]? GetXattr(string path, string name)
    {
        var size = NativeGetXattr(path, name, null, 0, 0, XattrNofollow);
        if (size < 0)
        {
            return null;
        }

        var buffer = new byte[size];
        return NativeGetXattr(path, name, buffer, (nuint)buffer.Length, 0, XattrNofollow) == size ? buffer : null;
    }
}

/// <summary>
/// POSIX name lookups shared by Linux and macOS: uid/gid to names via
/// <c>getpwuid</c>/<c>getgrgid</c>, reading only the first field
/// (<c>pw_name</c>/<c>gr_name</c> lead both structs on both platforms).
/// Cached — the scanner is single-threaded over one uid space.
/// </summary>
internal static partial class PosixNames
{
    private static readonly Dictionary<uint, string?> Users = [];
    private static readonly Dictionary<uint, string?> Groups = [];

    [LibraryImport("libc", EntryPoint = "getpwuid", SetLastError = true)]
    private static partial nint NativeGetPwUid(uint uid);

    [LibraryImport("libc", EntryPoint = "getgrgid", SetLastError = true)]
    private static partial nint NativeGetGrGid(uint gid);

    public static string? UserName(uint uid)
    {
        if (!Users.TryGetValue(uid, out var name))
        {
            Users[uid] = name = FirstStringField(NativeGetPwUid(uid));
        }

        return name;
    }

    public static string? GroupName(uint gid)
    {
        if (!Groups.TryGetValue(gid, out var name))
        {
            Groups[gid] = name = FirstStringField(NativeGetGrGid(gid));
        }

        return name;
    }

    private static string? FirstStringField(nint entry) =>
        entry == 0 ? null : Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(entry));
}
