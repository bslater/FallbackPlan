using System.Runtime.InteropServices;

namespace FallbackPlan.Filesystem.Local;

/// <summary>
/// Reads directory entries as the **bytes** the filesystem reported
/// (specification 06 §2, §4).
/// </summary>
/// <remarks>
/// <para>
/// A POSIX filename is a sequence of bytes that is not NUL and not <c>/</c>.
/// It is under no obligation to be valid UTF-8, and on real systems it
/// sometimes is not — a file copied from a Latin-1 machine, an archive
/// unpacked with the wrong locale, a name a program generated from binary
/// data.
/// </para>
/// <para>
/// <see cref="Directory.EnumerateFileSystemEntries(string)"/> hands back
/// <see cref="string"/>, and .NET has no surrogate-escape scheme, so those
/// bytes are already gone by the time managed code sees them. Re-encoding the
/// string to UTF-8 then produces different bytes from the ones on disk — which
/// makes the format's promise of "the entry name as the source filesystem
/// reported it, raw bytes" unkeepable, and makes the file unrestorable under
/// its own name. So the bytes are read here, from <c>readdir</c>, and the
/// string is derived from them rather than the other way round.
/// </para>
/// </remarks>
internal static partial class PosixDirectory
{
    /// <summary>Whether this platform has the native reader.</summary>
    public static bool IsSupported => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>
    /// Offset of <c>d_name</c> within <c>struct dirent</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Linux, every architecture — <c>d_ino</c> (8), <c>d_off</c> (8),
    /// <c>d_reclen</c> (2), <c>d_type</c> (1) = 19. This matches the
    /// <c>getdents64</c> kernel ABI, which is stable by contract.
    /// </para>
    /// <para>
    /// macOS with the 64-bit inode ABI, which is the only one available to a
    /// modern build — <c>d_ino</c> (8), <c>d_seekoff</c> (8),
    /// <c>d_reclen</c> (2), <c>d_namlen</c> (2), <c>d_type</c> (1) = 21.
    /// </para>
    /// <para>
    /// A wrong constant here would silently mangle every filename, so it is not
    /// left to trust: <c>LocalScanTests</c> compares the names this returns
    /// against the managed enumeration for an all-ASCII directory, on both
    /// platforms, in CI.
    /// </para>
    /// </remarks>
    private static int NameOffset => OperatingSystem.IsLinux() ? 19 : 21;

    private const int ReclenOffset = 16;

    [LibraryImport("libc", EntryPoint = "opendir", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint NativeOpenDir(string name);

    [LibraryImport("libc", EntryPoint = "readdir", SetLastError = true)]
    private static partial nint NativeReadDir(nint dir);

    [LibraryImport("libc", EntryPoint = "closedir", SetLastError = true)]
    private static partial int NativeCloseDir(nint dir);

    /// <summary>Reads one directory's entry names as raw bytes.</summary>
    /// <param name="directory">The directory to read.</param>
    /// <param name="names"><c>.</c> and <c>..</c> excluded, in readdir order.</param>
    /// <returns><see langword="false"/> when the directory could not be opened.</returns>
    public static bool TryReadNames(string directory, out List<byte[]> names)
    {
        names = [];

        var handle = NativeOpenDir(directory);
        if (handle == nint.Zero)
        {
            return false;
        }

        try
        {
            var nameOffset = NameOffset;
            while (true)
            {
                var entry = NativeReadDir(handle);
                if (entry == nint.Zero)
                {
                    // End of directory, or an error. Both end the walk; an
                    // error part-way is indistinguishable from the end without
                    // clearing errno first, and a short listing is reported by
                    // the caller's own comparison rather than guessed at here.
                    return true;
                }

                var reclen = (int)(ushort)Marshal.ReadInt16(entry, ReclenOffset);
                var name = ReadName(entry, nameOffset, reclen);

                // "." and ".." are the two entries every directory has and
                // nothing wants.
                if (name.Length == 0
                    || (name.Length == 1 && name[0] == (byte)'.')
                    || (name.Length == 2 && name[0] == (byte)'.' && name[1] == (byte)'.'))
                {
                    continue;
                }

                names.Add(name);
            }
        }
        finally
        {
            NativeCloseDir(handle);
        }
    }

    /// <summary>Reads the NUL-terminated name, bounded by the record length.</summary>
    private static byte[] ReadName(nint entry, int nameOffset, int reclen)
    {
        // Bounded by d_reclen so a malformed record cannot walk off the end of
        // the buffer the C library owns. A name is NUL-terminated within its
        // record; if the terminator is missing the record's own length ends it.
        var maximum = reclen > nameOffset ? reclen - nameOffset : 0;
        var length = 0;
        while (length < maximum && Marshal.ReadByte(entry, nameOffset + length) != 0)
        {
            length++;
        }

        var name = new byte[length];
        Marshal.Copy(entry + nameOffset, name, 0, length);
        return name;
    }
}
