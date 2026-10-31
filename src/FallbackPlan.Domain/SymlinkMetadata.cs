using Bodu;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FallbackPlan.Domain;

/// <summary>
/// The times and permissions a restore writes onto a symlink it made, onto
/// the link itself and never onto what it points at (specification 06 §4.1
/// keys 1, 3 and 4; FR-RST-004; ADR-0101).
/// </summary>
/// <remarks>
/// <para>
/// .NET's permission setter follows a link. Used on one, it would give
/// whatever the link points at, which may be outside the restore, the link's
/// captured permissions. So a link's permissions go through macOS's
/// <c>lchmod</c>. .NET's time setters act on the link itself on this
/// runtime, but promise nothing a restore could rest on, so a link's times
/// go through <c>utimensat</c> told not to follow a final link, on Linux and
/// macOS, where the call itself says so. On Windows they go through a handle
/// opened on the reparse point itself, which a handle's time setters then
/// write onto, a folder's link's as a file's (ADR-0181 Amendment 2).
/// Linux has no call that changes a link's permissions: it gives every link
/// <see cref="LinuxPermissions"/> and consults none. A link's owner and group
/// (<see cref="FileOwnership"/>), its extended attributes
/// (<see cref="ExtendedAttributes"/>) and its creation time on macOS
/// (<see cref="FileTimes"/>) are written by calls that already never follow
/// one. A link a restore makes on Windows (ADR-0181) is given its times and
/// its attribute bits (ADR-0181 Amendment 3), both through the handle on the
/// link, and its security descriptor through a handle of the same kind
/// (<see cref="WindowsSecurity"/>; ADR-0182 Amendment 1).
/// </para>
/// <para>
/// The timespec Linux and macOS take is two 64-bit words, so a 32-bit
/// process there sets nothing rather than guess at a layout.
/// </para>
/// </remarks>
public static partial class SymlinkMetadata
{
    /// <summary>The permissions Linux gives every link, <c>rwxrwxrwx</c>, which nothing there changes or consults.</summary>
    public const uint LinuxPermissions = 0x1FF;

    /// <summary>Whether this platform has a call that sets a link's own times: Windows does, and Linux and macOS in a 64-bit process.</summary>
    public static bool CanSetTimes { get; } =
        OperatingSystem.IsWindows() || ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && Environment.Is64BitProcess);

    /// <summary>Whether this platform has a call that sets a link's own creation time: macOS and Windows do.</summary>
    public static bool CanSetCreationTime { get; } = OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();

    /// <summary>Whether this platform keeps a link's own attribute bits and has a call that sets them: Windows does.</summary>
    [SupportedOSPlatformGuard("windows")]
    public static bool CanSetAttributes { get; } = OperatingSystem.IsWindows();

    /// <summary>
    /// Whether this platform keeps a link's own security descriptor and has a
    /// call that writes it onto the link: Windows does
    /// (<see cref="WindowsSecurity.TryWrite"/>, ADR-0182 Amendment 1).
    /// </summary>
    [SupportedOSPlatformGuard("windows")]
    public static bool CanSetSecurityDescriptor { get; } = OperatingSystem.IsWindows();

    /// <summary>Whether this platform keeps a link's own permissions and has a call that sets them: macOS does.</summary>
    [SupportedOSPlatformGuard("macos")]
    public static bool CanSetPermissions { get; } = OperatingSystem.IsMacOS();

    /// <summary>
    /// Sets the modification and access times of the link at
    /// <paramref name="path"/> itself, never following it.
    /// </summary>
    /// <param name="path">The link, which may point at nothing.</param>
    /// <param name="modifiedUtc">Its modification time, or null to leave it.</param>
    /// <param name="accessedUtc">Its access time, or null to leave it.</param>
    /// <returns>Whether it now carries them: false where the platform has no such call, and wherever it refuses.</returns>
    public static bool TrySetTimes(string path, DateTime? modifiedUtc, DateTime? accessedUtc)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(path);

        if (!CanSetTimes)
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return Windows.OntoTheLink(path, link =>
            {
                if (modifiedUtc is { } modified)
                {
                    File.SetLastWriteTimeUtc(link, modified);
                }

                if (accessedUtc is { } accessed)
                {
                    File.SetLastAccessTimeUtc(link, accessed);
                }
            });
        }

        // The access time comes first, as the call takes them.
        Span<TimeSpec> times = [TimeSpec.Of(accessedUtc), TimeSpec.Of(modifiedUtc)];
        return OperatingSystem.IsMacOS()
            ? Darwin.SetTimes(Darwin.CurrentDirectory, path, ref times[0], Darwin.NoFollow) == 0
            : Linux.SetTimes(Linux.CurrentDirectory, path, ref times[0], Linux.NoFollow) == 0;
    }

    /// <summary>
    /// Sets the creation time of the link at <paramref name="path"/> itself,
    /// never following it.
    /// </summary>
    /// <param name="path">The link, which may point at nothing.</param>
    /// <param name="createdUtc">Its creation time.</param>
    /// <returns>Whether it now carries it: false where the platform has no such call, and wherever it refuses.</returns>
    public static bool TrySetCreationTime(string path, DateTime createdUtc)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(path);

        if (OperatingSystem.IsWindows())
        {
            return Windows.OntoTheLink(path, link => File.SetCreationTimeUtc(link, createdUtc));
        }

        return OperatingSystem.IsMacOS() && FileTimes.TrySetCreationTime(path, createdUtc);
    }

    /// <summary>
    /// Gives the link at <paramref name="path"/> itself the attribute bits of
    /// <paramref name="captured"/> a restore writes, set as captured and
    /// cleared where not (<see cref="MetadataRules.FileAttributesToSet"/>),
    /// never following it. The bits that make it a link, and a folder's, stay
    /// as making it left them, which Windows will not take as written bits.
    /// </summary>
    /// <param name="path">The link, which may point at nothing.</param>
    /// <param name="captured">Its captured attribute bits.</param>
    /// <returns>Whether it now carries them: false on any platform but Windows, and wherever Windows refuses.</returns>
    public static bool TrySetAttributes(string path, uint captured)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(path);
        return CanSetAttributes && Windows.OntoTheLink(path, link => File.SetAttributes(
            link,
            (FileAttributes)MetadataRules.FileAttributesToSet((uint)File.GetAttributes(link) & ~MetadataRules.EntryFileAttributes, captured)));
    }

    /// <summary>
    /// Sets the permissions of the link at <paramref name="path"/> itself,
    /// never following it.
    /// </summary>
    /// <param name="path">The link, which may point at nothing.</param>
    /// <param name="mode">Its permission, set-id and sticky bits.</param>
    /// <returns>Whether it now carries them: false on any platform but macOS, and wherever macOS refuses.</returns>
    public static bool TrySetPermissions(string path, uint mode)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(path);
        return CanSetPermissions && Darwin.SetMode(path, (ushort)(mode & 0xFFF)) == 0;
    }

    /// <summary>
    /// One time as the call takes it: seconds and nanoseconds since the Unix
    /// epoch, or the platform's marker for a time to leave as it is.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct TimeSpec
    {
        public long Seconds;
        public long Nanoseconds;

        public static TimeSpec Of(DateTime? utc)
        {
            if (utc is not { } instant)
            {
                return new TimeSpec { Nanoseconds = OperatingSystem.IsMacOS() ? Darwin.Omit : Linux.Omit };
            }

            var ticks = instant.Ticks - DateTime.UnixEpoch.Ticks;
            return new TimeSpec
            {
                Seconds = Math.DivRem(ticks, TimeSpan.TicksPerSecond, out var remainder) - (remainder < 0 ? 1 : 0),
                Nanoseconds = (remainder < 0 ? remainder + TimeSpan.TicksPerSecond : remainder) * 100,
            };
        }
    }

    private static partial class Linux
    {
        public const int CurrentDirectory = -100;

        public const int NoFollow = 0x100;

        public const long Omit = (1L << 30) - 2;

        [LibraryImport("libc", EntryPoint = "utimensat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        public static partial int SetTimes(int directory, string path, ref TimeSpec times, int flags);
    }

    [SupportedOSPlatform("windows")]
    private static partial class Windows
    {
        /// <summary>Reading a link's attribute bits and writing them and its times, and nothing of what it holds.</summary>
        private const uint ReadAndWriteAttributes = 0x0080 | 0x0100;

        private const uint OpenExisting = 3;

        /// <summary>Opens a link itself, and never what it points at.</summary>
        private const uint OpenReparsePoint = 0x0020_0000;

        /// <summary>Lets a folder's link open, since Windows opens a folder only when asked to open one.</summary>
        private const uint BackupSemantics = 0x0200_0000;

        /// <summary>
        /// Opens the link at <paramref name="path"/> itself for its
        /// attributes alone and makes <paramref name="write"/> onto it.
        /// </summary>
        /// <returns>Whether the link opened and took the write.</returns>
        public static bool OntoTheLink(string path, Action<SafeFileHandle> write)
        {
            using var link = CreateFile(
                path, ReadAndWriteAttributes, FileShare.ReadWrite | FileShare.Delete, 0, OpenExisting, OpenReparsePoint | BackupSemantics, 0);
            if (link.IsInvalid)
            {
                return false;
            }

            try
            {
                write(link);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial SafeFileHandle CreateFile(
            string path, uint access, FileShare share, nint security, uint disposition, uint flags, nint template);
    }

    [SupportedOSPlatform("macos")]
    private static partial class Darwin
    {
        public const int CurrentDirectory = -2;

        public const int NoFollow = 0x0020;

        public const long Omit = -2;

        [LibraryImport("libSystem", EntryPoint = "utimensat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        public static partial int SetTimes(int directory, string path, ref TimeSpec times, int flags);

        [LibraryImport("libSystem", EntryPoint = "lchmod", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        public static partial int SetMode(string path, ushort mode);
    }
}
