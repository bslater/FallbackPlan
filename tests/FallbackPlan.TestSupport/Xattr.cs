using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace FallbackPlan.TestSupport;

/// <summary>
/// A file's extended attributes as its filesystem reports them, read and
/// written without following a link: the oracle a restore's extended
/// attributes are checked against, and how a test gives a source file some.
/// </summary>
/// <remarks>
/// Kept apart from the product's interop for the reason
/// <see cref="AllocatedSize"/> gives. Linux names the no-follow calls
/// <c>lgetxattr</c> and <c>lsetxattr</c>; macOS takes an option on the plain
/// calls. Windows keeps no extended attributes a restore writes.
/// </remarks>
public static partial class Xattr
{
    private const int XattrNofollow = 0x0001;

    /// <summary>A POSIX ACL entry's tag for the file's owner.</summary>
    public const ushort AclUserObject = 0x01;

    /// <summary>A POSIX ACL entry's tag for an account named by number.</summary>
    public const ushort AclUser = 0x02;

    /// <summary>A POSIX ACL entry's tag for the file's group.</summary>
    public const ushort AclGroupObject = 0x04;

    /// <summary>A POSIX ACL entry's tag for a group named by number.</summary>
    public const ushort AclGroup = 0x08;

    /// <summary>A POSIX ACL entry's tag for the mask.</summary>
    public const ushort AclMask = 0x10;

    /// <summary>A POSIX ACL entry's tag for everyone else.</summary>
    public const ushort AclOther = 0x20;

    /// <summary>The id an ACL entry carries when it names no account.</summary>
    public const uint AclUndefinedId = uint.MaxValue;

    /// <summary>
    /// A Linux POSIX ACL as <c>system.posix_acl_access</c> holds it: version
    /// 2, then each entry's tag, permissions and id, little-endian.
    /// </summary>
    /// <param name="entries">Each entry's tag, permission bits (read 4, write 2, execute 1) and id.</param>
    public static byte[] Acl(params (ushort Tag, ushort Permissions, uint Id)[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var acl = new byte[4 + (entries.Length * 8)];
        BinaryPrimitives.WriteUInt32LittleEndian(acl.AsSpan(0, 4), 2);
        for (var index = 0; index < entries.Length; index++)
        {
            var at = 4 + (index * 8);
            BinaryPrimitives.WriteUInt16LittleEndian(acl.AsSpan(at, 2), entries[index].Tag);
            BinaryPrimitives.WriteUInt16LittleEndian(acl.AsSpan(at + 2, 2), entries[index].Permissions);
            BinaryPrimitives.WriteUInt32LittleEndian(acl.AsSpan(at + 4, 4), entries[index].Id);
        }

        return acl;
    }

    /// <summary>The value of <paramref name="name"/> on <paramref name="path"/>, or null where it has none.</summary>
    /// <param name="path">The file, folder or link, never followed.</param>
    /// <param name="name">The attribute's name.</param>
    public static byte[]? Get(string path, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ThrowOnWindows();

        var size = OperatingSystem.IsMacOS()
            ? DarwinGet(path, name, null, 0, 0, XattrNofollow)
            : LinuxGet(path, name, null, 0);
        if (size < 0)
        {
            return null;
        }

        var value = new byte[size];
        var read = OperatingSystem.IsMacOS()
            ? DarwinGet(path, name, value, (nuint)value.Length, 0, XattrNofollow)
            : LinuxGet(path, name, value, (nuint)value.Length);
        return read == size ? value : throw new IOException($"{name} on {path} changed while it was read.");
    }

    /// <summary>Gives <paramref name="path"/> the attribute <paramref name="name"/>, or throws.</summary>
    /// <param name="path">The file, folder or link, never followed.</param>
    /// <param name="name">The attribute's name.</param>
    /// <param name="value">Its value.</param>
    public static void Set(string path, string name, byte[] value)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);
        ThrowOnWindows();

        var status = OperatingSystem.IsMacOS()
            ? DarwinSet(path, name, value, (nuint)value.Length, 0, XattrNofollow)
            : LinuxSet(path, name, value, (nuint)value.Length, 0);
        if (status != 0)
        {
            throw new IOException($"Setting {name} on {path} failed with errno {Marshal.GetLastPInvokeError()}.");
        }
    }

    /// <summary>Every attribute name <paramref name="path"/> carries.</summary>
    /// <param name="path">The file, folder or link, never followed.</param>
    public static IReadOnlyList<string> Names(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ThrowOnWindows();

        var size = OperatingSystem.IsMacOS() ? DarwinList(path, null, 0, XattrNofollow) : LinuxList(path, null, 0);
        if (size <= 0)
        {
            return [];
        }

        var list = new byte[size];
        size = OperatingSystem.IsMacOS()
            ? DarwinList(path, list, (nuint)list.Length, XattrNofollow)
            : LinuxList(path, list, (nuint)list.Length);
        return Encoding.UTF8.GetString(list, 0, (int)Math.Max(size, 0))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static void ThrowOnWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("A restore writes no extended attributes on Windows.");
        }
    }

    [LibraryImport("libc", EntryPoint = "lgetxattr", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint LinuxGet(string path, string name, byte[]? value, nuint size);

    [LibraryImport("libc", EntryPoint = "lsetxattr", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LinuxSet(string path, string name, byte[] value, nuint size, int flags);

    [LibraryImport("libc", EntryPoint = "llistxattr", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint LinuxList(string path, byte[]? list, nuint size);

    [LibraryImport("libSystem", EntryPoint = "getxattr", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint DarwinGet(string path, string name, byte[]? value, nuint size, uint position, int options);

    [LibraryImport("libSystem", EntryPoint = "setxattr", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int DarwinSet(string path, string name, byte[] value, nuint size, uint position, int options);

    [LibraryImport("libSystem", EntryPoint = "listxattr", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint DarwinList(string path, byte[]? list, nuint size, int options);
}
