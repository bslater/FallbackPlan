using Bodu;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FallbackPlan.Domain;

/// <summary>
/// The times a restore writes back onto a file it landed (specification 06
/// §4.1 keys 1 to 3; FR-RST-004).
/// </summary>
/// <remarks>
/// <para>
/// Modification and access times need nothing here: .NET sets both on every
/// platform. A creation time does. Windows sets one through .NET too. macOS
/// sets one through <c>setattrlist</c>, called here because .NET's setter
/// writes the modification time instead whenever a volume keeps no creation
/// times. Linux has no call that sets a creation time, and .NET's setter there
/// writes the modification time as well. Either substitution would silently
/// replace the time a restore had just put back, so a time that cannot be set
/// is answered as not set.
/// </para>
/// <para>
/// Sits beside <see cref="SparseFile"/> for the same reason: the engine's
/// restore and the standalone recovery tool both write restored files, and
/// Domain is the one assembly both may reach.
/// </para>
/// </remarks>
public static partial class FileTimes
{
    /// <summary>The last millisecond a file's time can carry here, at the end of the year 9999.</summary>
    private static readonly ulong LastMillisecond = (ulong)DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    /// <summary>Whether this platform has a call that sets a file's creation time: Windows and macOS do, Linux does not.</summary>
    public static bool CanSetCreationTime { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>
    /// A captured time, in milliseconds since the Unix epoch, as a UTC instant;
    /// or null when there is none, or when it lies beyond the last instant a
    /// file can carry. A manifest is untrusted input, so an impossible time is
    /// nothing to apply rather than an exception.
    /// </summary>
    /// <param name="milliseconds">The captured time.</param>
    public static DateTime? FromUnixMilliseconds(ulong? milliseconds) =>
        milliseconds is { } value && value <= LastMillisecond
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)value).UtcDateTime
            : null;

    /// <summary>Sets the creation time of the file at <paramref name="path"/>.</summary>
    /// <param name="path">The file.</param>
    /// <param name="utc">The time, in UTC.</param>
    /// <returns>
    /// Whether the file now carries it. False on Linux, which has no call that
    /// sets one; on a macOS volume that keeps none; and wherever the
    /// filesystem refuses the write.
    /// </returns>
    public static bool TrySetCreationTime(string path, DateTime utc)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(path);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                File.SetCreationTimeUtc(path, utc);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        return OperatingSystem.IsMacOS() && DarwinCreationTime.Set(path, utc);
    }

    [SupportedOSPlatform("macos")]
    private static partial class DarwinCreationTime
    {
        private const ushort AttributeBitmapCount = 5;

        private const uint CommonCreationTime = 0x00000200;

        private const uint NoFollow = 0x00000001;

        private static readonly long EpochSeconds = DateTime.UnixEpoch.Ticks / TimeSpan.TicksPerSecond;

        public static bool Set(string path, DateTime utc)
        {
            var list = new AttributeList { BitmapCount = AttributeBitmapCount, CommonAttributes = CommonCreationTime };
            var time = new TimeSpec
            {
                Seconds = (utc.Ticks / TimeSpan.TicksPerSecond) - EpochSeconds,
                Nanoseconds = utc.Ticks % TimeSpan.TicksPerSecond * 100,
            };

            return SetAttributeList(path, ref list, ref time, (nuint)Marshal.SizeOf<TimeSpec>(), NoFollow) == 0;
        }

        // struct attrlist: the bitmap count, a reserved half-word, then one
        // attribute group per kind. Only the common group's creation time is
        // asked for, so the buffer is that one timespec.
        [StructLayout(LayoutKind.Sequential)]
        private struct AttributeList
        {
            public ushort BitmapCount;
            public ushort Reserved;
            public uint CommonAttributes;
            public uint VolumeAttributes;
            public uint DirectoryAttributes;
            public uint FileAttributes;
            public uint ForkAttributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TimeSpec
        {
            public long Seconds;
            public long Nanoseconds;
        }

        [LibraryImport("libSystem", EntryPoint = "setattrlist", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial int SetAttributeList(
            string path, ref AttributeList list, ref TimeSpec buffer, nuint size, uint options);
    }
}
