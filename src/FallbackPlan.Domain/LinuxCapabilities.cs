using System.Globalization;

namespace FallbackPlan.Domain;

/// <summary>
/// The Linux capabilities this process holds, read from its effective set in
/// <c>/proc/self/status</c>. A restore asks before it gives a file away
/// (<see cref="FileOwnership"/>) and before it sets an immutable flag
/// (<see cref="FileFlags"/>), because root in a container need not hold
/// either, and an account that is not root may.
/// </summary>
internal static class LinuxCapabilities
{
    /// <summary>CAP_CHOWN, which lets a process give a file to any owner and group.</summary>
    public const ulong Chown = 1UL << 0;

    /// <summary>CAP_LINUX_IMMUTABLE, which lets a process set and clear the immutable and append-only flags.</summary>
    public const ulong Immutable = 1UL << 9;

    /// <summary>
    /// Whether this process's effective set holds <paramref name="capability"/>,
    /// a mask of one bit; false where the set cannot be read, as off Linux.
    /// </summary>
    public static bool Hold(ulong capability)
    {
        try
        {
            var effective = File.ReadLines("/proc/self/status")
                .FirstOrDefault(line => line.StartsWith("CapEff:", StringComparison.Ordinal));
            return effective is not null
                && ulong.TryParse(
                    effective.AsSpan("CapEff:".Length).Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                    out var capabilities)
                && (capabilities & capability) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
