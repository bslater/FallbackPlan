using System.Globalization;
using System.Runtime.InteropServices;
using Bodu;

namespace FallbackPlan.Domain;

/// <summary>
/// What a restore gives a file's, a folder's and a link's flags back through
/// (FR-RST-004; ADR-0180 Amendment 4), so a case can stand calls of its own
/// in for the platform's.
/// </summary>
public interface IFileFlags
{
    /// <summary>
    /// Adds <paramref name="flags"/> to those of what stands at
    /// <paramref name="path"/>, each as far as the platform and the account
    /// let it, and answers which it now carries.
    /// </summary>
    /// <param name="path">A file the restore landed, a folder it made, or a link it made, which is changed itself.</param>
    /// <param name="flags">The flags it was captured with.</param>
    /// <param name="kind">What stands there.</param>
    /// <returns>Those of <paramref name="flags"/> it carries now.</returns>
    FileFlagBits Apply(string path, FileFlagBits flags, EntryKind kind);
}

/// <summary>The platform's rule and calls for a file's, a folder's and a link's flags.</summary>
/// <remarks>
/// <para>
/// Linux keeps a file's and a folder's flags as inode flags, set through
/// <c>FS_IOC_SETFLAGS</c> on a descriptor open on it. Its owner may set
/// no-dump; immutable and append-only need <c>CAP_LINUX_IMMUTABLE</c>, which
/// a container's root need not hold, so it is read from the process's
/// effective set rather than assumed of root. A link cannot be opened
/// without following it, so a link's own flags are set by nothing.
/// Compressed is the volume's to give, and encrypted comes only with a key,
/// so neither is set by a flag.
/// </para>
/// <para>
/// macOS keeps them in <c>st_flags</c>, and its owner may set its user
/// immutable, append-only, no-dump and hidden flags; <c>lchflags</c> sets a
/// link's own. Compressed is the system's to set. Windows keeps none of them.
/// </para>
/// <para>
/// Sits beside <see cref="AlternateStreams"/> for the reason it gives, and
/// reads a macOS file's flags by the layout capture reads them by.
/// </para>
/// </remarks>
public static partial class FileFlags
{
    private const FileFlagBits Privileged = FileFlagBits.Immutable | FileFlagBits.AppendOnly;

    private const ulong LinuxImmutableCapability = 1UL << 9;

    private const int LinuxNoDump = 0x40;
    private const int LinuxImmutable = 0x10;
    private const int LinuxAppendOnly = 0x20;

    private const int LinuxNoFollow = 0x20000;
    private const int LinuxNonBlock = 0x800;
    private const int LinuxCloseOnExec = 0x80000;

    private const uint DarwinNoDump = 0x1;
    private const uint DarwinImmutable = 0x2;
    private const uint DarwinAppendOnly = 0x4;
    private const uint DarwinHidden = 0x8000;

    /// <summary>
    /// What Linux lets a restore set on <paramref name="kind"/>: no-dump on a
    /// file or a folder, and immutable and append-only too for a process
    /// that <paramref name="holdsImmutableCapability"/>; nothing on a link.
    /// </summary>
    public static FileFlagBits SettableOnLinux(EntryKind kind, bool holdsImmutableCapability) =>
        kind == EntryKind.Symlink
            ? FileFlagBits.None
            : FileFlagBits.NoDump | (holdsImmutableCapability ? Privileged : FileFlagBits.None);

    /// <summary>
    /// What macOS lets a restore set on <paramref name="kind"/>: its owner's
    /// immutable, append-only, no-dump and hidden flags, on a link too.
    /// </summary>
    public static FileFlagBits SettableOnMacOs(EntryKind kind) =>
        FileFlagBits.Immutable | FileFlagBits.AppendOnly | FileFlagBits.NoDump | FileFlagBits.Hidden;

    /// <summary>Whether this process may set Linux's immutable and append-only flags: false off Linux.</summary>
    public static bool HoldsImmutableCapability { get; } = OperatingSystem.IsLinux() && ReadsImmutableCapability();

    /// <summary>
    /// The flags this platform sets only for an account this one is not:
    /// Linux's immutable and append-only, without <c>CAP_LINUX_IMMUTABLE</c>.
    /// </summary>
    public static FileFlagBits NeedingPrivilege { get; } =
        OperatingSystem.IsLinux() && !HoldsImmutableCapability ? Privileged : FileFlagBits.None;

    /// <summary>What this platform lets a restore, as this process, set on <paramref name="kind"/>.</summary>
    public static FileFlagBits Settable(EntryKind kind) =>
        OperatingSystem.IsLinux() ? SettableOnLinux(kind, HoldsImmutableCapability)
        : OperatingSystem.IsMacOS() ? SettableOnMacOs(kind)
        : FileFlagBits.None;

    /// <summary>This platform's calls.</summary>
    public static IFileFlags OfThisPlatform { get; } = new Platform();

    private static bool ReadsImmutableCapability()
    {
        try
        {
            var effective = File.ReadLines("/proc/self/status")
                .FirstOrDefault(line => line.StartsWith("CapEff:", StringComparison.Ordinal));
            return effective is not null
                && ulong.TryParse(
                    effective.AsSpan("CapEff:".Length).Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                    out var capabilities)
                && (capabilities & LinuxImmutableCapability) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int ToLinux(FileFlagBits flags) =>
        (flags.HasFlag(FileFlagBits.NoDump) ? LinuxNoDump : 0)
        | (flags.HasFlag(FileFlagBits.Immutable) ? LinuxImmutable : 0)
        | (flags.HasFlag(FileFlagBits.AppendOnly) ? LinuxAppendOnly : 0);

    private static FileFlagBits FromLinux(int flags) =>
        ((flags & LinuxNoDump) != 0 ? FileFlagBits.NoDump : FileFlagBits.None)
        | ((flags & LinuxImmutable) != 0 ? FileFlagBits.Immutable : FileFlagBits.None)
        | ((flags & LinuxAppendOnly) != 0 ? FileFlagBits.AppendOnly : FileFlagBits.None);

    private static uint ToDarwin(FileFlagBits flags) =>
        (flags.HasFlag(FileFlagBits.NoDump) ? DarwinNoDump : 0)
        | (flags.HasFlag(FileFlagBits.Immutable) ? DarwinImmutable : 0)
        | (flags.HasFlag(FileFlagBits.AppendOnly) ? DarwinAppendOnly : 0)
        | (flags.HasFlag(FileFlagBits.Hidden) ? DarwinHidden : 0);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int Ioctl(int descriptor, nuint request, ref int flags);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int descriptor);

    [LibraryImport("libSystem", EntryPoint = "lchflags", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Lchflags(string path, uint flags);

    [LibraryImport("libSystem", EntryPoint = "lstat$INODE64", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int LstatX64(string path, out DarwinStat stat);

    [LibraryImport("libSystem", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int LstatArm64(string path, out DarwinStat stat);

    /// <summary>The flags of the file at <paramref name="path"/>, its own for a link, or null where it will not say.</summary>
    private static uint? DarwinFlagsOf(string path)
    {
        var status = RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? LstatX64(path, out var stat)
            : LstatArm64(path, out stat);
        return status == 0 ? stat.Flags : null;
    }

    private static FileFlagBits OnLinux(string path, FileFlagBits asked)
    {
        // FS_IOC_GETFLAGS and FS_IOC_SETFLAGS: _IOR and _IOW('f', 1|2, long),
        // whose size the request encodes.
        var get = IntPtr.Size == 8 ? (nuint)0x8008_6601 : 0x8004_6601;
        var set = IntPtr.Size == 8 ? (nuint)0x4008_6602 : 0x4004_6602;

        // Non-blocking, so a pipe put in the file's place is not waited on.
        var descriptor = Open(path, LinuxNoFollow | LinuxNonBlock | LinuxCloseOnExec);
        if (descriptor < 0)
        {
            return FileFlagBits.None;
        }

        try
        {
            var current = 0;
            if (Ioctl(descriptor, get, ref current) != 0)
            {
                return FileFlagBits.None;
            }

            // The owner's flag first and the privileged ones apart, so a
            // refusal of those leaves it set.
            foreach (var part in new[] { asked & ~Privileged, asked & Privileged })
            {
                var wanted = current | ToLinux(part);
                if (wanted != current && Ioctl(descriptor, set, ref wanted) == 0)
                {
                    current = wanted;
                }
            }

            var after = 0;
            return Ioctl(descriptor, get, ref after) == 0 ? FromLinux(after) & asked : FileFlagBits.None;
        }
        finally
        {
            _ = Close(descriptor);
        }
    }

    private static FileFlagBits OnMacOs(string path, FileFlagBits asked)
    {
        if (DarwinFlagsOf(path) is not { } current)
        {
            return FileFlagBits.None;
        }

        // One call sets them all; where it is refused, each is tried alone.
        if (Lchflags(path, current | ToDarwin(asked)) != 0)
        {
            foreach (var flag in Enum.GetValues<FileFlagBits>().Where(flag => flag != FileFlagBits.None && asked.HasFlag(flag)))
            {
                if (Lchflags(path, current | ToDarwin(flag)) == 0)
                {
                    current |= ToDarwin(flag);
                }
            }
        }

        return DarwinFlagsOf(path) is { } after ? MetadataRules.FileFlagsFromDarwin(after) & asked : FileFlagBits.None;
    }

    /// <summary>Darwin's 64-bit-inode <c>struct stat</c>, as capture reads it.</summary>
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

    private sealed class Platform : IFileFlags
    {
        public FileFlagBits Apply(string path, FileFlagBits flags, EntryKind kind)
        {
            ThrowHelper.ThrowIfNullOrWhiteSpace(path);

            var asked = flags & Settable(kind);
            return asked == FileFlagBits.None ? FileFlagBits.None
                : OperatingSystem.IsLinux() ? OnLinux(path, asked)
                : OperatingSystem.IsMacOS() ? OnMacOs(path, asked)
                : FileFlagBits.None;
        }
    }
}
