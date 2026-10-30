using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Bodu;

namespace FallbackPlan.Domain;

/// <summary>
/// The privileges this process's Windows account holds, and the one way a
/// restore uses one: enabled for a single call, on the thread that makes it
/// (ADR-0176 Amendment 1).
/// </summary>
/// <remarks>
/// A privilege enabled in the process's own token would hold for every call
/// any thread makes. SeRestorePrivilege so enabled lets every open made with
/// backup intent, as .NET makes one to set a folder's times, write past the
/// DACL of what it opens. So it is enabled in a copy of the token the calling
/// thread alone runs under, and that copy is dropped once the call returns.
/// </remarks>
public static partial class WindowsPrivileges
{
    /// <summary>
    /// The privilege that lets an account give a file an owner it is not,
    /// which Windows grants to administrators and backup operators.
    /// </summary>
    public const string Restore = "SeRestorePrivilege";

    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const int TokenPrivilegesClass = 3;
    private const uint PrivilegeEnabled = 0x2;
    private const int SecurityImpersonation = 2;
    private const int ErrorNotAllAssigned = 1300;
    private const int LuidAndAttributesLength = 12;

    /// <summary>
    /// Whether this process's token holds <paramref name="privilege"/>,
    /// enabled or not. False off Windows, and for a name no privilege has.
    /// </summary>
    /// <param name="privilege">The privilege's name, as <see cref="Restore"/>.</param>
    public static bool Holds(string privilege)
    {
        ThrowHelper.ThrowIfNullOrEmpty(privilege);

        return OperatingSystem.IsWindows()
            && LookupPrivilegeValue(null, privilege, out var luid)
            && OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token)
            && Closing(token, AttributesOf(token, luid)) is not null;
    }

    /// <summary>
    /// Whether <paramref name="privilege"/> is enabled in the token the
    /// calling thread runs under: its own where it impersonates, the
    /// process's where it does not.
    /// </summary>
    /// <param name="privilege">The privilege's name, as <see cref="Restore"/>.</param>
    public static bool IsEnabled(string privilege)
    {
        ThrowHelper.ThrowIfNullOrEmpty(privilege);

        if (!OperatingSystem.IsWindows() || !LookupPrivilegeValue(null, privilege, out var luid))
        {
            return false;
        }

        if (!OpenThreadToken(GetCurrentThread(), TokenQuery, openAsSelf: true, out var token)
            && !OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
        {
            return false;
        }

        return Closing(token, AttributesOf(token, luid)) is { } attributes && (attributes & PrivilegeEnabled) != 0;
    }

    /// <summary>
    /// Makes <paramref name="call"/> on this thread under a copy of this
    /// process's token with <paramref name="privilege"/> enabled, and gives
    /// back what it answers. False, without making it, where the token does
    /// not hold the privilege, where the thread already runs under a token
    /// of its own, which reverting would end, or where no copy can be made.
    /// </summary>
    /// <param name="privilege">The privilege's name, as <see cref="Restore"/>.</param>
    /// <param name="call">A synchronous call, which must not leave this thread.</param>
    public static bool RunWith(string privilege, Func<bool> call)
    {
        ThrowHelper.ThrowIfNullOrEmpty(privilege);
        ThrowHelper.ThrowIfNull(call);

        if (!OperatingSystem.IsWindows() || !LookupPrivilegeValue(null, privilege, out var luid))
        {
            return false;
        }

        if (OpenThreadToken(GetCurrentThread(), TokenQuery, openAsSelf: true, out var standing))
        {
            CloseHandle(standing);
            return false;
        }

        if (!ImpersonateSelf(SecurityImpersonation))
        {
            return false;
        }

        try
        {
            return OpenThreadToken(GetCurrentThread(), TokenAdjustPrivileges | TokenQuery, openAsSelf: true, out var copy)
                && Closing(copy, Enable(copy, luid))
                && call();
        }
        finally
        {
            // A thread left under the copy would carry the privilege, enabled,
            // into whatever work it is given next.
            if (!RevertToSelf())
            {
                Environment.FailFast("A thread could not drop the token it enabled a privilege in.");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static T Closing<T>(nint token, T answer)
    {
        CloseHandle(token);
        return answer;
    }

    [SupportedOSPlatform("windows")]
    private static unsafe bool Enable(nint token, long luid)
    {
        Span<byte> state = stackalloc byte[4 + LuidAndAttributesLength];
        BinaryPrimitives.WriteUInt32LittleEndian(state, 1);
        BinaryPrimitives.WriteInt64LittleEndian(state[4..], luid);
        BinaryPrimitives.WriteUInt32LittleEndian(state[12..], PrivilegeEnabled);

        fixed (byte* pointer = state)
        {
            // The call succeeds where the token lacks the privilege, and says
            // so only through the last error.
            return AdjustTokenPrivileges(token, disableAll: false, pointer, 0, 0, 0)
                && Marshal.GetLastPInvokeError() != ErrorNotAllAssigned;
        }
    }

    [SupportedOSPlatform("windows")]
    private static unsafe uint? AttributesOf(nint token, long luid)
    {
        _ = GetTokenInformation(token, TokenPrivilegesClass, null, 0, out var needed);
        if (needed < 4)
        {
            return null;
        }

        var buffer = new byte[needed];
        fixed (byte* pointer = buffer)
        {
            if (!GetTokenInformation(token, TokenPrivilegesClass, pointer, needed, out _))
            {
                return null;
            }
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        for (var at = 4; count > 0 && at + LuidAndAttributesLength <= buffer.Length; count--, at += LuidAndAttributesLength)
        {
            if (BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(at)) == luid)
            {
                return BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(at + 8));
            }
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValue(string? system, string name, out long luid);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [SupportedOSPlatform("windows")]
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [SupportedOSPlatform("windows")]
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenThreadToken(nint thread, uint access, [MarshalAs(UnmanagedType.Bool)] bool openAsSelf, out nint token);

    [SupportedOSPlatform("windows")]
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetTokenInformation(nint token, int informationClass, byte* information, uint length, out uint returned);

    [SupportedOSPlatform("windows")]
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool AdjustTokenPrivileges(
        nint token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, byte* newState, uint previousLength, nint previousState, nint returned);

    [SupportedOSPlatform("windows")]
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ImpersonateSelf(int level);

    [SupportedOSPlatform("windows")]
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RevertToSelf();

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
