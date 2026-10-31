using Bodu;
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
/// macOS, where the call itself says so.
/// Linux has no call that changes a link's permissions: it gives every link
/// <see cref="LinuxPermissions"/> and consults none. A link's owner and group
/// (<see cref="FileOwnership"/>), its extended attributes
/// (<see cref="ExtendedAttributes"/>) and its creation time on macOS
/// (<see cref="FileTimes"/>) are written by calls that already never follow
/// one. A link a restore makes on Windows (ADR-0181) is given none of these:
/// no call there sets a link's own times, and its attribute bits and
/// descriptor are written only onto files and folders.
/// </para>
/// <para>
/// The timespec both platforms take is two 64-bit words, so a 32-bit process
/// sets nothing rather than guess at a layout.
/// </para>
/// </remarks>
public static partial class SymlinkMetadata
{
    /// <summary>The permissions Linux gives every link, <c>rwxrwxrwx</c>, which nothing there changes or consults.</summary>
    public const uint LinuxPermissions = 0x1FF;

    /// <summary>Whether this platform has a call that sets a link's own times: Linux and macOS do, in a 64-bit process.</summary>
    public static bool CanSetTimes { get; } = (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && Environment.Is64BitProcess;

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

        // The access time comes first, as the call takes them.
        Span<TimeSpec> times = [TimeSpec.Of(accessedUtc), TimeSpec.Of(modifiedUtc)];
        return OperatingSystem.IsMacOS()
            ? Darwin.SetTimes(Darwin.CurrentDirectory, path, ref times[0], Darwin.NoFollow) == 0
            : Linux.SetTimes(Linux.CurrentDirectory, path, ref times[0], Linux.NoFollow) == 0;
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
