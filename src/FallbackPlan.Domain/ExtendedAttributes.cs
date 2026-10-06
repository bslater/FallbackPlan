using Bodu;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace FallbackPlan.Domain;

/// <summary>
/// The extended attributes a restore writes back onto a file it landed or a
/// folder it made (specification 06 §4.1 key 8; FR-RST-004; ADR-0087).
/// </summary>
/// <remarks>
/// <para>
/// Each attribute is written alone, by its captured name, and a refusal is
/// answered as not set, so one refusal leaves the rest. Neither platform
/// follows a link: Linux's <c>lsetxattr</c> writes the link itself, and macOS
/// is asked not to follow one. On Linux the name's namespace decides who may
/// write it: any account the user namespace of a file it may write, root
/// alone the trusted and security namespaces, and the file's owner its
/// POSIX ACL, which the system namespace holds. macOS has no namespaces.
/// Windows keeps no extended attributes a restore writes.
/// </para>
/// <para>
/// Sits beside <see cref="FileOwnership"/> for the reason
/// <see cref="SparseFile"/> gives: the engine's restore and the standalone
/// recovery tool both write restored files, and Domain is the one assembly
/// both may reach.
/// </para>
/// </remarks>
public static unsafe partial class ExtendedAttributes
{
    /// <summary>Whether this platform has a call that writes an extended attribute: Linux and macOS do, Windows does not.</summary>
    public static bool CanSet { get; } = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>Gives the file or folder at <paramref name="path"/> the attribute <paramref name="name"/>.</summary>
    /// <param name="path">The file or folder; a link is never followed.</param>
    /// <param name="name">The attribute's name.</param>
    /// <param name="value">Its value, which may be empty.</param>
    /// <returns>Whether it now carries it: false on Windows, and wherever the platform refuses.</returns>
    public static bool TrySet(string path, string name, ReadOnlySpan<byte> value)
    {
        ThrowHelper.ThrowIfNull(name);
        return TrySet(path, Encoding.UTF8.GetBytes(name), value);
    }

    /// <summary>
    /// Gives the file or folder at <paramref name="path"/> the attribute whose
    /// name is the bytes <paramref name="name"/>, exactly as captured.
    /// </summary>
    /// <param name="path">The file or folder; a link is never followed.</param>
    /// <param name="name">The attribute's name, as its bytes. One that is empty or holds a NUL names nothing a platform takes.</param>
    /// <param name="value">Its value, which may be empty.</param>
    /// <returns>Whether it now carries it: false on Windows, and wherever the platform refuses.</returns>
    public static bool TrySet(string path, ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(path);

        if (!CanSet || name.IsEmpty || name.Contains((byte)0))
        {
            return false;
        }

        // The platform reads the name up to its NUL.
        var terminated = new byte[name.Length + 1];
        name.CopyTo(terminated);

        fixed (byte* named = terminated)
        fixed (byte* bytes = value)
        {
            if (OperatingSystem.IsMacOS())
            {
                return Darwin.Set(path, named, bytes, (nuint)value.Length) == 0;
            }

            return OperatingSystem.IsLinux() && Linux.Set(path, named, bytes, (nuint)value.Length) == 0;
        }
    }

    [SupportedOSPlatform("linux")]
    private static partial class Linux
    {
        public static int Set(string path, byte* name, byte* value, nuint size) => SetAttribute(path, name, value, size, 0);

        [LibraryImport("libc", EntryPoint = "lsetxattr", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial int SetAttribute(string path, byte* name, byte* value, nuint size, int flags);
    }

    [SupportedOSPlatform("macos")]
    private static partial class Darwin
    {
        /// <summary>The option that writes a link's own attribute rather than its target's.</summary>
        private const int NoFollow = 0x0001;

        public static int Set(string path, byte* name, byte* value, nuint size) => SetAttribute(path, name, value, size, 0, NoFollow);

        [LibraryImport("libSystem", EntryPoint = "setxattr", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial int SetAttribute(string path, byte* name, byte* value, nuint size, uint position, int options);
    }
}
