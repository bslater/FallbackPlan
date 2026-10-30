using Bodu;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace FallbackPlan.Domain;

/// <summary>
/// Whether this machine knows an account a captured Windows security
/// descriptor names (ADR-0176). A SID names the account it meant only where
/// the machine or its domain issued it: one a rebuilt machine's old local
/// account held names nobody on the new one.
/// </summary>
public static class WindowsAccounts
{
    /// <summary>
    /// Whether <paramref name="sid"/>, as <c>S-1-5-21-…</c>, names an account
    /// or group this machine can name. False off Windows, for text that is no
    /// SID, and where the lookup cannot be made, as with a domain out of reach.
    /// </summary>
    /// <param name="sid">The SID in its string form.</param>
    public static bool Knows(string sid)
    {
        ThrowHelper.ThrowIfNull(sid);

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            _ = new SecurityIdentifier(sid).Translate(typeof(NTAccount));
            return true;
        }
        catch (Exception exception) when (
            exception is IdentityNotMappedException or ArgumentException or Win32Exception
            || exception.GetType() == typeof(SystemException))
        {
            return false;
        }
    }
}

/// <summary>
/// The call a restore gives a file or a folder its captured security
/// descriptor back with (ADR-0176), on Windows.
/// </summary>
/// <remarks>
/// The descriptor is handed to <c>SetFileSecurityW</c> as captured, so the
/// DACL lands with the entries its folder had given it then, as a backup
/// restored in place needs, and with nothing walked beneath a folder. Its
/// protection is said to the call outright, so a DACL that took nothing from
/// its folder still takes nothing. Sits in Domain for the reason
/// <see cref="HardLinks"/> gives.
/// </remarks>
public static partial class WindowsSecurity
{
    private const uint OwnerInformation = 0x1;
    private const uint GroupInformation = 0x2;
    private const uint DaclInformation = 0x4;
    private const uint UnprotectedDaclInformation = 0x2000_0000;
    private const uint ProtectedDaclInformation = 0x8000_0000;

    /// <summary>
    /// Writes <paramref name="descriptor"/> onto <paramref name="path"/>: its
    /// DACL, and its owner and group where <paramref name="withOwner"/> says so.
    /// </summary>
    /// <param name="path">A file or a folder.</param>
    /// <param name="descriptor">The descriptor as captured, self-relative.</param>
    /// <param name="read">What it names (<see cref="WindowsSecurityDescriptor.Read"/>).</param>
    /// <param name="withOwner">Whether its owner and group are written too.</param>
    /// <param name="withRestorePrivilege">
    /// Whether the write is made with SeRestorePrivilege enabled, for it alone
    /// (<see cref="WindowsPrivileges.RunWith"/>), so an owner the account is
    /// not may be given. False where the account does not hold it.
    /// </param>
    /// <returns>Whether the platform took it: false wherever it refuses.</returns>
    [SupportedOSPlatform("windows")]
    public static bool TryWrite(
        string path, ReadOnlySpan<byte> descriptor, WindowsSecurityDescriptor read, bool withOwner, bool withRestorePrivilege = false)
    {
        ThrowHelper.ThrowIfNullOrEmpty(path);
        ThrowHelper.ThrowIfNull(read);

        var information = 0u;
        if (withOwner && read.Owner is not null)
        {
            information |= OwnerInformation;
        }

        if (withOwner && read.Group is not null)
        {
            information |= GroupInformation;
        }

        if (read.DaclPresent)
        {
            information |= DaclInformation | (read.DaclProtected ? ProtectedDaclInformation : UnprotectedDaclInformation);
        }

        var bytes = descriptor.ToArray();
        return information == 0
            || (withRestorePrivilege
                ? WindowsPrivileges.RunWith(WindowsPrivileges.Restore, () => SetFileSecurity(path, information, bytes))
                : SetFileSecurity(path, information, bytes));
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("advapi32.dll", EntryPoint = "SetFileSecurityW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileSecurity(string path, uint information, byte[] descriptor);
}
