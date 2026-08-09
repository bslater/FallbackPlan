using Bodu;
using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FallbackPlan.Api.Transport;

/// <summary>
/// Who is on the other end of a local connection, as the operating system
/// reports it ([T-16](../../../docs/threat-model.md)).
/// </summary>
/// <remarks>
/// This is identification layered on top of authentication, not a substitute
/// for it. The endpoint lives in a directory only the service account may
/// write, so the filesystem has already decided <i>whether</i> a caller may
/// connect; this answers <i>who</i> connected. Where the platform will not say,
/// the answer is <see cref="PeerIdentity.Unknown"/> — an invented identity
/// would be worse than an absent one.
/// </remarks>
public static partial class PeerCredentials
{
    private const int SoPeerCredLinux = 17;

    /// <summary>Reads the caller's identity from a connected socket.</summary>
    /// <param name="socket">The accepted socket.</param>
    /// <returns>The caller's identity, or <see cref="PeerIdentity.Unknown"/>.</returns>
    public static PeerIdentity Read(Socket socket)
    {
        ThrowHelper.ThrowIfNull(socket);

        if (OperatingSystem.IsLinux())
        {
            return ReadLinux(socket);
        }

        return OperatingSystem.IsMacOS() ? ReadMacOs(socket) : PeerIdentity.Unknown;
    }

    /// <summary>Reads the caller's identity from a connected named pipe.</summary>
    /// <param name="pipe">The connected server pipe.</param>
    /// <returns>The caller's identity, or <see cref="PeerIdentity.Unknown"/>.</returns>
    [SupportedOSPlatform("windows")]
    public static PeerIdentity Read(NamedPipeServerStream pipe)
    {
        ThrowHelper.ThrowIfNull(pipe);

        try
        {
            return new PeerIdentity(true, -1, -1, pipe.GetImpersonationUserName());
        }
        catch (IOException)
        {
            return PeerIdentity.Unknown;
        }
        catch (InvalidOperationException)
        {
            return PeerIdentity.Unknown;
        }
    }

    [SupportedOSPlatform("linux")]
    private static PeerIdentity ReadLinux(Socket socket)
    {
        // struct ucred { pid_t pid; uid_t uid; gid_t gid; } — three 32-bit
        // fields, read through the raw option accessor because .NET names no
        // SocketOptionName for SO_PEERCRED.
        var buffer = new byte[12];
        try
        {
            socket.GetSocketOption(SocketOptionLevel.Socket, (SocketOptionName)SoPeerCredLinux, buffer);
        }
        catch (SocketException)
        {
            return PeerIdentity.Unknown;
        }

        var processId = BitConverter.ToInt32(buffer, 0);
        var userId = BitConverter.ToInt32(buffer, 4);
        return new PeerIdentity(
            true,
            userId,
            processId,
            string.Create(CultureInfo.InvariantCulture, $"uid {userId}, pid {processId}"));
    }

    [SupportedOSPlatform("macos")]
    private static PeerIdentity ReadMacOs(Socket socket)
    {
        // getpeereid is the documented API and avoids reasoning about the
        // layout of struct xucred, which has changed shape historically.
        try
        {
            if (GetPeerEid(socket.Handle, out var effectiveUser, out _) != 0)
            {
                return PeerIdentity.Unknown;
            }

            return new PeerIdentity(
                true,
                effectiveUser,
                -1,
                string.Create(CultureInfo.InvariantCulture, $"uid {effectiveUser}"));
        }
        catch (EntryPointNotFoundException)
        {
            return PeerIdentity.Unknown;
        }
        catch (DllNotFoundException)
        {
            return PeerIdentity.Unknown;
        }
    }

    [SupportedOSPlatform("macos")]
    [LibraryImport("libc", EntryPoint = "getpeereid", SetLastError = true)]
    private static partial int GetPeerEid(nint socket, out uint effectiveUser, out uint effectiveGroup);
}
