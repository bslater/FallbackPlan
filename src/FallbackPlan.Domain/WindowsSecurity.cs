using Bodu;
using Microsoft.Win32.SafeHandles;
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

    /// <summary>
    /// Whether this process's account may make <paramref name="sid"/> a
    /// file's owner without SeRestorePrivilege: its own account, or a group
    /// its token marks as one it may own by (ADR-0176 Amendment 2). False off
    /// Windows, and for text that is no SID.
    /// </summary>
    /// <param name="sid">The SID in its string form.</param>
    public static bool MayOwn(string sid)
    {
        ThrowHelper.ThrowIfNull(sid);

        return OperatingSystem.IsWindows() && WindowsPrivileges.Owners().Contains(sid);
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
    private const uint SaclInformation = 0x8;
    private const uint UnprotectedSaclInformation = 0x1000_0000;
    private const uint UnprotectedDaclInformation = 0x2000_0000;
    private const uint ProtectedSaclInformation = 0x4000_0000;
    private const uint ProtectedDaclInformation = 0x8000_0000;
    private const uint WriteDac = 0x0004_0000;
    private const uint WriteOwner = 0x0008_0000;
    private const uint AccessSystemSecurity = 0x0100_0000;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x0020_0000;
    private const uint BackupSemantics = 0x0200_0000;

    /// <summary>
    /// Writes <paramref name="descriptor"/> onto <paramref name="path"/>: its
    /// DACL, its owner and group where <paramref name="withOwner"/> says so,
    /// and its audit list where <paramref name="withAuditList"/> does.
    /// </summary>
    /// <param name="path">A file or a folder.</param>
    /// <param name="descriptor">The descriptor as captured, self-relative.</param>
    /// <param name="read">What it names (<see cref="WindowsSecurityDescriptor.Read"/>).</param>
    /// <param name="withOwner">Whether its owner and group are written too.</param>
    /// <param name="withRestorePrivilege">
    /// Whether the write is made with SeRestorePrivilege enabled, for it alone
    /// (<see cref="WindowsPrivileges.RunWith(string, Func{bool})"/>), so an owner the account is
    /// not may be given. False where the account does not hold it.
    /// </param>
    /// <param name="withAuditList">
    /// Whether its audit list (SACL) is written too, with SeSecurityPrivilege
    /// enabled for the write alone (ADR-0176 Amendment 3). False where the
    /// account does not hold it.
    /// </param>
    /// <param name="link">
    /// Whether <paramref name="path"/> is a link, whose own descriptor is
    /// written through a handle opened on it (ADR-0182 Amendment 1):
    /// <c>SetFileSecurityW</c> opens what a link points at.
    /// </param>
    /// <returns>Whether the platform took it: false wherever it refuses.</returns>
    [SupportedOSPlatform("windows")]
    public static bool TryWrite(
        string path,
        ReadOnlySpan<byte> descriptor,
        WindowsSecurityDescriptor read,
        bool withOwner,
        bool withRestorePrivilege = false,
        bool withAuditList = false,
        bool link = false)
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

        List<string> privileges = withRestorePrivilege ? [WindowsPrivileges.Restore] : [];
        if (withAuditList && read.SaclPresent)
        {
            information |= SaclInformation | (read.SaclProtected ? ProtectedSaclInformation : UnprotectedSaclInformation);
            privileges.Add(WindowsPrivileges.Security);
        }

        var bytes = descriptor.ToArray();
        return information == 0
            || (privileges.Count > 0
                ? WindowsPrivileges.RunWith(privileges, Set)
                : Set());

        bool Set() => link ? SetOnTheLink(path, information, bytes) : SetFileSecurity(path, information, bytes);
    }

    /// <summary>
    /// Writes a descriptor onto the link at <paramref name="path"/> itself,
    /// through a handle on the reparse point opened for no more than the
    /// write asks: the owner's right for an owner or a group, the DACL's for
    /// a DACL, and the audit list's, which SeSecurityPrivilege grants only
    /// while enabled, for an audit list. The privileges a write runs with are
    /// enabled while the handle is opened, which is when they are asked for.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool SetOnTheLink(string path, uint information, byte[] descriptor)
    {
        var access = ((information & (OwnerInformation | GroupInformation)) != 0 ? WriteOwner : 0)
            | ((information & DaclInformation) != 0 ? WriteDac : 0)
            | ((information & SaclInformation) != 0 ? AccessSystemSecurity : 0);
        using var link = CreateFile(
            path, access, FileShare.ReadWrite | FileShare.Delete, 0, OpenExisting, OpenReparsePoint | BackupSemantics, 0);
        return !link.IsInvalid && SetKernelObjectSecurity(link, information, descriptor);
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(
        string path, uint access, FileShare share, nint security, uint disposition, uint flags, nint template);

    [SupportedOSPlatform("windows")]
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetKernelObjectSecurity(SafeFileHandle handle, uint information, byte[] descriptor);

    [SupportedOSPlatform("windows")]
    [LibraryImport("advapi32.dll", EntryPoint = "SetFileSecurityW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileSecurity(string path, uint information, byte[] descriptor);
}
