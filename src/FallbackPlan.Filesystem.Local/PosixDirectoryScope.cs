using Microsoft.Win32.SafeHandles;

namespace FallbackPlan.Filesystem.Local;

/// <summary>
/// One open directory the scanner is walking, and every operation it
/// performs on that directory's children — listing, stat, descent, opening
/// content, reading a link target — expressed relative to the descriptor
/// rather than to a path.
/// </summary>
/// <remarks>
/// <para>
/// This is what closes the time-of-check-to-time-of-use gap architecture
/// 06 §4.1 records as owed. A path-based scanner stats an object under one
/// name and then re-opens it under the same name, and the two are not
/// guaranteed to be the same object: anything that can write to an ancestor
/// directory can substitute a symlink between them. Every call here starts
/// from a descriptor the scanner already holds and opens with
/// <c>O_NOFOLLOW</c>, so a substitution fails outright instead of
/// redirecting the read.
/// </para>
/// <para>
/// The descriptor also fixes what "revalidate" means. A file descriptor
/// names an inode for as long as it is open — no rename, unlink, or
/// replacement can move it to a different one — so stat'ing the handle
/// before and after a read compares the same object to itself. Stat'ing a
/// path twice does not.
/// </para>
/// </remarks>
internal sealed class PosixDirectoryScope : IDisposable
{
    private int _descriptor;

    private PosixDirectoryScope(int descriptor) => _descriptor = descriptor;

    /// <summary>The open descriptor; -1 once disposed.</summary>
    public int Descriptor => _descriptor;

    /// <summary>
    /// Opens the scan root, or returns <see langword="null"/> where the
    /// handle-relative walk is unavailable — on Windows, or when the root
    /// itself cannot be opened as a directory without following a link.
    /// </summary>
    public static PosixDirectoryScope? TryOpenRoot(string path)
    {
        if (!PosixHandleInterop.IsSupported)
        {
            return null;
        }

        var descriptor = PosixHandleInterop.OpenDirectory(path);
        return descriptor < 0 ? null : new PosixDirectoryScope(descriptor);
    }

    /// <summary>Descends into a child directory by its raw name bytes.</summary>
    public PosixDirectoryScope? TryOpenChild(ReadOnlySpan<byte> name)
    {
        var descriptor = PosixHandleInterop.OpenAt(_descriptor, name, directory: true);
        return descriptor < 0 ? null : new PosixDirectoryScope(descriptor);
    }

    /// <summary>
    /// Opens a child's content for reading, refusing to follow a link.
    /// </summary>
    /// <remarks>
    /// Only regular files are opened. A FIFO would block until a writer
    /// arrived, a device node would perform real device I/O, and neither
    /// carries content this format stores — they are captured as
    /// zero-content versions from their stat alone (ADR-0026 §Decision 4).
    /// </remarks>
    public SafeFileHandle? TryOpenContent(ReadOnlySpan<byte> name)
    {
        var descriptor = PosixHandleInterop.OpenAt(_descriptor, name, directory: false);
        return descriptor < 0 ? null : PosixHandleInterop.ToHandle(descriptor);
    }

    /// <summary>Stats a child without following a link.</summary>
    public bool TryStat(ReadOnlySpan<byte> name, out StatResult stat) =>
        OperatingSystem.IsMacOS()
            ? DarwinInterop.TryStatAt(_descriptor, name, out stat)
            : LinuxInterop.TryStatAt(_descriptor, name, out stat);

    /// <summary>Stats this directory itself.</summary>
    public bool TryStatSelf(out StatResult stat) => StatHandle(_descriptor, out stat);

    /// <summary>Stats the object an open descriptor names.</summary>
    public static bool StatHandle(int descriptor, out StatResult stat) =>
        OperatingSystem.IsMacOS()
            ? DarwinInterop.TryStatHandle(descriptor, out stat)
            : LinuxInterop.TryStatHandle(descriptor, out stat);

    /// <summary>Reads a child symlink's target as the bytes it holds.</summary>
    public byte[]? ReadLink(ReadOnlySpan<byte> name) => PosixHandleInterop.ReadLinkAt(_descriptor, name);

    /// <summary>Lists this directory's children as raw name bytes.</summary>
    public bool TryListNames(out List<byte[]> names) =>
        PosixDirectory.TryReadNamesFrom(_descriptor, out names);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_descriptor >= 0)
        {
            PosixHandleInterop.Close(_descriptor);
            _descriptor = -1;
        }
    }
}
