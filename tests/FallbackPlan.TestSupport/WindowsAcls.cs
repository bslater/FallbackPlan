using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace FallbackPlan.TestSupport;

/// <summary>
/// Real DACLs set on real files and folders, so a case on Windows can capture
/// a file kept to named accounts and restore it into a folder that would give
/// it more.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsAcls
{
    /// <summary>
    /// Keeps <paramref name="file"/> to the account running the case and
    /// <paramref name="accounts"/>, each given full control, its DACL taking
    /// nothing from its folder.
    /// </summary>
    public static void KeepTo(string file, params string[] accounts)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        ArgumentNullException.ThrowIfNull(accounts);

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var account in accounts.Select(sid => new SecurityIdentifier(sid)).Prepend(Me()))
        {
            security.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        new FileInfo(file).SetAccessControl(security);
    }

    /// <summary>
    /// Makes <paramref name="folder"/> one that gives each file made in it to
    /// the account running the case, with full control, and to
    /// <paramref name="reader"/>, to read and run, taking nothing from its
    /// own folder.
    /// </summary>
    /// <returns>The folder's full path.</returns>
    public static string Sharing(string folder, string reader)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentException.ThrowIfNullOrEmpty(reader);

        const InheritanceFlags Everything = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var made = Directory.CreateDirectory(folder);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            Me(), FileSystemRights.FullControl, Everything, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(reader), FileSystemRights.ReadAndExecute, Everything, PropagationFlags.None, AccessControlType.Allow));
        made.SetAccessControl(security);
        return made.FullName;
    }

    private static SecurityIdentifier Me()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!;
    }
}
