using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FallbackPlan.Filesystem.Local;

/// <summary>
/// The handle-relative POSIX surface the scanner walks on: <c>openat</c>,
/// <c>fstatat</c>, <c>readlinkat</c> and the fd-based xattr calls, all
/// taking a name as **bytes** rather than as a path string.
/// </summary>
/// <remarks>
/// <para>
/// Two problems disappear together here. A path-based scanner stats an
/// object and then re-opens it by name, so anything may be substituted
/// between the two — the ordinary time-of-check-to-time-of-use gap, and the
/// reason architecture 06 §4.1 says this is owed. And a path is a string,
/// which a POSIX name is not: a name that is not valid UTF-8 cannot be put
/// back together into a path that opens the file.
/// </para>
/// <para>
/// Working from a directory descriptor solves both. The name never becomes
/// a string, and the object opened is the object stat'd — a file descriptor
/// names an inode, and no rename or replacement can move it to another one.
/// </para>
/// <para>
/// The flag constants are the asm-generic values Linux uses on x86-64,
/// AArch64 and RISC-V, and Darwin's on macOS. They are not universal — a
/// few Linux architectures (alpha, mips, sparc) differ — and this is the
/// one place they appear.
/// </para>
/// </remarks>
internal static partial class PosixHandleInterop
{
    /// <summary>Whether this platform has the handle-relative walk.</summary>
    public static bool IsSupported => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    private const int LinuxONofollow = 0x20000;
    private const int LinuxODirectory = 0x10000;
    private const int LinuxOCloexec = 0x80000;
    private const int LinuxONonblock = 0x800;

    private const int DarwinONofollow = 0x0100;
    private const int DarwinODirectory = 0x100000;
    private const int DarwinOCloexec = 0x1000000;
    private const int DarwinONonblock = 0x0004;

    /// <summary>Open read-only, refusing to follow a final symlink.</summary>
    private static int NoFollowReadFlags => OperatingSystem.IsLinux()
        ? LinuxONofollow | LinuxOCloexec | LinuxONonblock
        : DarwinONofollow | DarwinOCloexec | DarwinONonblock;

    /// <summary>The same, and the object must be a directory.</summary>
    private static int NoFollowDirectoryFlags => OperatingSystem.IsLinux()
        ? LinuxONofollow | LinuxODirectory | LinuxOCloexec
        : DarwinONofollow | DarwinODirectory | DarwinOCloexec;

    /// <summary>The <c>dirfd</c> value meaning "relative to the working directory".</summary>
    public const int AtFdCwd = -100;

    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static unsafe partial int LinuxOpenAt(int dirfd, byte* pathname, int flags);

    [LibraryImport("libSystem", EntryPoint = "openat", SetLastError = true)]
    private static unsafe partial int DarwinOpenAt(int dirfd, byte* pathname, int flags);

    [LibraryImport("libc", EntryPoint = "readlinkat", SetLastError = true)]
    private static unsafe partial nint LinuxReadLinkAt(int dirfd, byte* pathname, byte* buffer, nuint size);

    [LibraryImport("libSystem", EntryPoint = "readlinkat", SetLastError = true)]
    private static unsafe partial nint DarwinReadLinkAt(int dirfd, byte* pathname, byte* buffer, nuint size);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int LinuxClose(int fd);

    [LibraryImport("libSystem", EntryPoint = "close", SetLastError = true)]
    private static partial int DarwinClose(int fd);

    [LibraryImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static partial int LinuxDup(int fd);

    [LibraryImport("libSystem", EntryPoint = "dup", SetLastError = true)]
    private static partial int DarwinDup(int fd);

    /// <summary>Opens a child by name bytes, without following a link.</summary>
    /// <param name="directoryFd">The parent directory's descriptor.</param>
    /// <param name="name">The child's raw name bytes, without a terminator.</param>
    /// <param name="directory">Require the child to be a directory.</param>
    /// <returns>The descriptor, or -1.</returns>
    public static unsafe int OpenAt(int directoryFd, ReadOnlySpan<byte> name, bool directory)
    {
        var flags = directory ? NoFollowDirectoryFlags : NoFollowReadFlags;
        var terminated = Terminate(name);

        fixed (byte* pathname = terminated)
        {
            return OperatingSystem.IsLinux()
                ? LinuxOpenAt(directoryFd, pathname, flags)
                : DarwinOpenAt(directoryFd, pathname, flags);
        }
    }

    /// <summary>Opens an absolute path as a directory, without following a final link.</summary>
    public static int OpenDirectory(string path) =>
        OpenAt(AtFdCwd, System.Text.Encoding.UTF8.GetBytes(path), directory: true);

    /// <summary>Reads a symlink's target as the bytes it actually holds.</summary>
    /// <remarks>
    /// A link target is a byte string under exactly the same rules as a
    /// name, and <see cref="FileSystemInfo.LinkTarget"/> has already decoded
    /// it. Reading it here keeps a target that is not valid UTF-8 intact.
    /// </remarks>
    public static unsafe byte[]? ReadLinkAt(int directoryFd, ReadOnlySpan<byte> name)
    {
        var terminated = Terminate(name);
        var size = 256;

        while (size <= 65_536)
        {
            var buffer = new byte[size];
            nint written;

            fixed (byte* pathname = terminated)
            fixed (byte* target = buffer)
            {
                written = OperatingSystem.IsLinux()
                    ? LinuxReadLinkAt(directoryFd, pathname, target, (nuint)size)
                    : DarwinReadLinkAt(directoryFd, pathname, target, (nuint)size);
            }

            if (written < 0)
            {
                return null;
            }

            // A result that exactly fills the buffer may have been truncated —
            // readlink does not say — so it is retried with more room.
            if (written < size)
            {
                return buffer[..(int)written];
            }

            size *= 2;
        }

        return null;
    }

    /// <summary>Closes a raw descriptor.</summary>
    public static void Close(int fd)
    {
        if (fd < 0)
        {
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            LinuxClose(fd);
        }
        else
        {
            DarwinClose(fd);
        }
    }

    /// <summary>Duplicates a descriptor, so one owner can be handed away.</summary>
    public static int Duplicate(int fd) =>
        OperatingSystem.IsLinux() ? LinuxDup(fd) : DarwinDup(fd);

    /// <summary>Wraps a descriptor as an owning handle the BCL can stream from.</summary>
    public static SafeFileHandle ToHandle(int fd) => new(fd, ownsHandle: true);

    /// <summary>NUL-terminates a name for the C calls, which take C strings.</summary>
    private static byte[] Terminate(ReadOnlySpan<byte> name)
    {
        var terminated = new byte[name.Length + 1];
        name.CopyTo(terminated);
        return terminated;
    }
}
