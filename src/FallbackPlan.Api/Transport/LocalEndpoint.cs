using Bodu;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FallbackPlan.Api.Resources;

namespace FallbackPlan.Api.Transport;

/// <summary>
/// Where a service listens locally, and how a client finds it (ADR-0028 §5).
/// </summary>
/// <remarks>
/// <para>
/// A <b>Unix domain socket</b> on POSIX and a <b>named pipe</b> on Windows,
/// created where only the service account may write. Authentication is the
/// operating system's: filesystem permissions decide who may connect, and the
/// service reads peer credentials to identify them. No password, no token file,
/// no port.
/// </para>
/// <para>
/// Loopback TCP was rejected for the local binding on the prior art. One
/// widely-deployed desktop client authenticates to its own service with a
/// token file, and a stale one is among its most familiar failure modes — the
/// UI insisting it cannot reach an engine that is running perfectly well.
/// Another listens on a fixed loopback port behind a server password, so any
/// local process may attempt to connect and the user must manage a credential
/// to talk to their own machine. Both are artefacts of using a network
/// transport for a boundary that is not a network.
/// </para>
/// </remarks>
public static partial class LocalEndpoint
{
    /// <summary>
    /// The longest socket path the platform will accept. POSIX
    /// <c>sockaddr_un.sun_path</c> is 108 bytes on Linux and 104 on macOS,
    /// including the terminator — a limit that produces a baffling failure when
    /// it is met silently, so it is checked and named here instead.
    /// </summary>
    public const int MaximumSocketPathBytes = 103;

    /// <summary>The socket path or pipe name for a state directory.</summary>
    /// <param name="stateDirectory">The state directory whose service is addressed.</param>
    /// <returns>The address, in the platform's own form.</returns>
    /// <remarks>
    /// When <c>&lt;state&gt;/service.sock</c> fits the platform limit it is used
    /// as is. When it does not — macOS reaches the limit with nothing more
    /// exotic than its per-user temp root or a long username under
    /// <c>~/Library</c> — the socket falls back to a short path derived from
    /// the state directory alone: <c>/tmp/fbp-u&lt;uid&gt;/&lt;hash&gt;.sock</c>.
    /// Deterministic derivation is the point: the service and every client
    /// compute the same address independently, so there is no pointer file to
    /// go stale. The per-user directory is owner-only and refused rather than
    /// repaired when it is not — see <see cref="EnsureFallbackDirectory"/>.
    /// </remarks>
    public static string AddressFor(string stateDirectory)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);

        if (OperatingSystem.IsWindows())
        {
            // A pipe name cannot be a path, so it is derived from one. The hash
            // keeps distinct state directories on distinct pipes, which is what
            // makes several repositories on one machine independent.
            return $"fallbackplan-{ShortHash(Path.GetFullPath(stateDirectory))}";
        }

        var socketPath = Path.Combine(Path.GetFullPath(stateDirectory), "service.sock");
        if (Encoding.UTF8.GetByteCount(socketPath) <= MaximumSocketPathBytes)
        {
            return socketPath;
        }

        // The same hash-per-state-directory rule the Windows pipe uses, housed
        // under /tmp directly — never GetTempPath(), whose TMPDIR indirection
        // is exactly what made the standard path too long.
        var fallback = Path.Combine(
            EnsureFallbackDirectory(), $"{ShortHash(Path.GetFullPath(stateDirectory))}.sock");

        var bytes = Encoding.UTF8.GetByteCount(fallback);
        if (bytes > MaximumSocketPathBytes)
        {
            var length = bytes.ToString(CultureInfo.InvariantCulture);
            var limit = MaximumSocketPathBytes.ToString(CultureInfo.InvariantCulture);
            throw new IOException(Strings.FormatLocalEndpoint_SocketPathBytesPlatformAccepts(fallback, length, limit));
        }

        return fallback;
    }

    /// <summary>
    /// The per-user fallback directory, created owner-only and verified on
    /// every use — by clients as much as by the service, because both resolve
    /// addresses through it.
    /// </summary>
    /// <remarks>
    /// A directory in a world-writable place is only as trustworthy as its
    /// permissions, so an existing one that is a symlink or that admits other
    /// users is <b>refused, never repaired</b>: tightening the permissions of
    /// a directory somebody else prepared would be adopting their trap. A
    /// squatter who pre-creates the name with correct-looking permissions
    /// gains nothing but denial of service — the socket bind and connect fail
    /// on ownership either way, loudly.
    /// </remarks>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static string EnsureFallbackDirectory()
    {
        var directory = $"/tmp/fbp-u{GetEffectiveUserId().ToString(CultureInfo.InvariantCulture)}";
        const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        var info = new DirectoryInfo(directory);
        if (!info.Exists)
        {
            Directory.CreateDirectory(directory, OwnerOnly);
            info.Refresh();
        }

        if (info.LinkTarget is not null)
        {
            throw new IOException(
                Strings.FormatLocalEndpoint_FallbackDirectoryUnsafe(directory, "it is a symbolic link"));
        }

        if (info.UnixFileMode != OwnerOnly)
        {
            throw new IOException(
                Strings.FormatLocalEndpoint_FallbackDirectoryUnsafe(
                    directory, "its permissions admit other users"));
        }

        return directory;
    }

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();

    /// <summary>
    /// Prepares the directory the endpoint lives in, so that permissions are the
    /// authentication rather than an afterthought.
    /// </summary>
    /// <param name="stateDirectory">The state directory.</param>
    public static void PrepareDirectory(string stateDirectory)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);
        Directory.CreateDirectory(stateDirectory);

        if (!OperatingSystem.IsWindows())
        {
            // Owner only. On Windows the ACL is set on the pipe itself, because
            // there is no file to carry it.
            File.SetUnixFileMode(
                stateDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Whether a service appears to be listening for this state directory.</summary>
    /// <param name="stateDirectory">The state directory.</param>
    /// <returns><see langword="true"/> when an endpoint exists to try.</returns>
    /// <remarks>
    /// A hint, not a guarantee: the only proof a service is there is a
    /// connection, and a client must be written to fail gracefully when this
    /// says yes and the connection then refuses.
    /// </remarks>
    public static bool Exists(string stateDirectory)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(stateDirectory);

        if (OperatingSystem.IsWindows())
        {
            return Directory.Exists(@"\\.\pipe\")
                && Directory.EnumerateFiles(@"\\.\pipe\", AddressFor(stateDirectory)).Any();
        }

        try
        {
            return File.Exists(AddressFor(stateDirectory));
        }
        catch (IOException)
        {
            // An unusable address is not an endpoint.
            return false;
        }
    }

    private static string ShortHash(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant();
    }
}
