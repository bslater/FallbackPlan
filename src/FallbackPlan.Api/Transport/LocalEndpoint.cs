using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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
/// Loopback TCP was rejected for the local binding on the prior art. CrashPlan's
/// desktop client authenticates to its service with a token file, and a stale
/// one is among that product's most familiar failure modes — the UI insisting
/// it cannot reach an engine that is running perfectly well. Duplicati listens
/// on <c>localhost:8200</c> behind a server password, so any local process may
/// attempt to connect and the user must manage a credential to talk to their
/// own machine. Both are artefacts of using a network transport for a boundary
/// that is not a network.
/// </para>
/// </remarks>
public static class LocalEndpoint
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
    public static string AddressFor(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);

        if (OperatingSystem.IsWindows())
        {
            // A pipe name cannot be a path, so it is derived from one. The hash
            // keeps distinct state directories on distinct pipes, which is what
            // makes several repositories on one machine independent.
            return $"fallbackplan-{ShortHash(Path.GetFullPath(stateDirectory))}";
        }

        var socketPath = Path.Combine(Path.GetFullPath(stateDirectory), "service.sock");
        var bytes = Encoding.UTF8.GetByteCount(socketPath);
        if (bytes > MaximumSocketPathBytes)
        {
            var length = bytes.ToString(CultureInfo.InvariantCulture);
            var limit = MaximumSocketPathBytes.ToString(CultureInfo.InvariantCulture);
            throw new IOException(
                $"The socket path '{socketPath}' is {length} bytes, and the platform accepts at most {limit}. "
                + "Choose a shorter state directory — this limit is the operating system's, not this program's.");
        }

        return socketPath;
    }

    /// <summary>
    /// Prepares the directory the endpoint lives in, so that permissions are the
    /// authentication rather than an afterthought.
    /// </summary>
    /// <param name="stateDirectory">The state directory.</param>
    public static void PrepareDirectory(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
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
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);

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
