using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// A restore writes back the Windows security descriptor captured with a file
/// or a folder, where the platform keeps one (FR-RST-004; ADR-0176): its
/// owner, its group and its DACL's entries as captured, protected as it was,
/// after the streams and times, since a DACL may take from the restoring
/// account the access they need, and before the attribute bits, since writing
/// one sets the archive bit. A descriptor that names an account this machine
/// does not know is not written, and the account is named, so a file is never
/// handed to a stranger's access. Where the account may not give the owner,
/// the DACL still lands.
/// </summary>
[TestClass]
public sealed class SecurityDescriptorWriteBackTests : IDisposable
{
    private const string Stranger = "S-1-5-21-1-2-3-4242";

    private static readonly DateTime Modified = new(2024, 8, 2, 11, 15, 30, 250, DateTimeKind.Utc);

    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-security-descriptor-tests", Guid.NewGuid().ToString("n"))).FullName;

    [TestMethod]
    public void ThisPlatform_WritesSecurityDescriptors_OnlyOnWindows() =>
        Assert.AreEqual(OperatingSystem.IsWindows(), MetadataTarget.ForLocalPlatform().SupportsSecurityDescriptors);

    [TestMethod]
    public void AnAccount_IsKnownOnlyWhereThisMachineNamesIt()
    {
        Assert.IsFalse(WindowsAccounts.Knows(Stranger));
        Assert.IsFalse(WindowsAccounts.Knows("not an account"));
        Assert.AreEqual(OperatingSystem.IsWindows(), WindowsAccounts.Knows("S-1-5-18"));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps a file's security descriptor")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void Write_OnWindows_GivesAFileTheDaclItWasCapturedWith_ProtectedAsItWas_AndKeepsItsTime()
    {
        var source = Landed("source.bin");
        var security = new FileInfo(source).GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        new FileInfo(source).SetAccessControl(security);
        var file = Landed("restored.bin");

        var written = Writer().Write(file, new EntryMetadata
        {
            ModifiedAt = (ulong)new DateTimeOffset(Modified).ToUnixTimeMilliseconds(),
            WindowsSecurityDescriptor = Captured(source),
        });

        Assert.AreEqual(Dacl(source), Dacl(file));
        Assert.IsTrue(new FileInfo(file).GetAccessControl().AreAccessRulesProtected);
        Assert.IsTrue(written.Applied.HasFlag(CapturedMetadata.SecurityDescriptor), written.Detail);
        Assert.AreEqual(Modified, File.GetLastWriteTimeUtc(file));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps a folder's security descriptor")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void Write_OnWindows_GivesAFolderItsDacl()
    {
        var source = new DirectoryInfo(Directory.CreateDirectory(Path.Combine(_root, "source")).FullName);
        var security = source.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        source.SetAccessControl(security);
        var folder = Directory.CreateDirectory(Path.Combine(_root, "restored")).FullName;

        var written = Writer().Write(
            folder,
            new EntryMetadata { WindowsSecurityDescriptor = source.GetAccessControl().GetSecurityDescriptorBinaryForm() },
            EntryKind.DirectoryPlaceholder);

        Assert.AreEqual(Dacl(source.GetAccessControl()), Dacl(new DirectoryInfo(folder).GetAccessControl()));
        Assert.IsTrue(written.Applied.HasFlag(CapturedMetadata.SecurityDescriptor), written.Detail);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps a file's security descriptor and attribute bits")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void Write_OnWindows_ADescriptorWrittenWithTheBits_LeavesNoArchiveBitTheCaptureDidNotHave()
    {
        var source = Landed("source.bin");
        var file = Landed("restored.bin");
        File.SetAttributes(file, FileAttributes.Normal);

        var written = Writer().Write(file, new EntryMetadata
        {
            WindowsSecurityDescriptor = Captured(source),
            FileAttributes = (uint)(FileAttributes.ReadOnly | FileAttributes.Hidden),
        });

        Assert.AreEqual(FileAttributes.ReadOnly | FileAttributes.Hidden, File.GetAttributes(file));
        Assert.IsTrue(written.Applied.HasFlag(CapturedMetadata.SecurityDescriptor), written.Detail);
        Assert.IsTrue(written.Applied.HasFlag(CapturedMetadata.FileAttributes), written.Detail);
        File.SetAttributes(file, FileAttributes.Normal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps a file's security descriptor")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void Write_OnWindows_ADescriptorNamingAnAccountThisMachineDoesNotKnow_IsNotWritten_AndNamesIt()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(Stranger), FileSystemRights.Read, AccessControlType.Allow));
        var file = Landed("stranger.bin");
        var before = Dacl(file);

        var written = Writer().Write(file, new EntryMetadata { WindowsSecurityDescriptor = security.GetSecurityDescriptorBinaryForm() });

        Assert.AreEqual(before, Dacl(file));
        Assert.IsFalse(written.Applied.HasFlag(CapturedMetadata.SecurityDescriptor));
        Assert.IsNotNull(written.Detail);
        Assert.Contains(Stranger, written.Detail, StringComparison.Ordinal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps a file's security descriptor")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void Write_OnWindows_AnOwnerTheAccountMayNotGive_StillLeavesTheDaclLanded()
    {
        // Local Service is an account every Windows machine names, and one a
        // process without SeRestorePrivilege enabled may not make an owner.
        var source = Landed("given-away.bin");
        var security = new FileInfo(source).GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(source).SetAccessControl(security);
        var captured = new FileInfo(source).GetAccessControl();
        captured.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null));
        var file = Landed("restored.bin");

        var written = Writer().Write(file, new EntryMetadata { WindowsSecurityDescriptor = captured.GetSecurityDescriptorBinaryForm() });

        Assert.AreEqual(Dacl(source), Dacl(file));
        if (!written.Applied.HasFlag(CapturedMetadata.SecurityDescriptor))
        {
            Assert.IsNotNull(written.Detail);
            Assert.Contains("owner and group not applied", written.Detail, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps a file's security descriptor")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void Write_OnWindows_ADescriptorThatDoesNotRead_IsNotWritten_AndSaysSo()
    {
        var written = Writer().Write(Landed("unread.bin"), new EntryMetadata { WindowsSecurityDescriptor = new byte[] { 1, 2, 3 } });

        Assert.IsFalse(written.Applied.HasFlag(CapturedMetadata.SecurityDescriptor));
        Assert.IsNotNull(written.Detail);
        Assert.Contains("does not read as one", written.Detail, StringComparison.Ordinal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "POSIX keeps no security descriptor")]
    [PlatformTrait(TestPlatforms.Posix)]
    [UnsupportedOSPlatform("windows")]
    public void Write_OnPosix_ATargetSaidToKeepDescriptors_WritesNone()
    {
        var file = Landed("posix.bin");
        var before = File.GetUnixFileMode(file);

        var written = new MetadataWriter(MetadataTarget.ForLocalPlatform() with { SupportsSecurityDescriptors = true, KnowsAccount = _ => true })
            .Write(file, new EntryMetadata
            {
                WindowsSecurityDescriptor = SecurityDescriptors.Build(null, null, SecurityDescriptors.Acl(
                    SecurityDescriptors.Ace(SecurityDescriptors.Allowed, 0, "S-1-1-0"))),
            });

        Assert.AreEqual(before, File.GetUnixFileMode(file));
        Assert.IsFalse(written.Applied.HasFlag(CapturedMetadata.SecurityDescriptor));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }

    private static MetadataWriter Writer() => new(MetadataTarget.ForLocalPlatform() with { SupportsSecurityDescriptors = true });

    [SupportedOSPlatform("windows")]
    private static byte[] Captured(string path) => new FileInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm();

    [SupportedOSPlatform("windows")]
    private static string Dacl(string path) => Dacl(new FileInfo(path).GetAccessControl());

    /// <summary>
    /// A DACL's protection and its entries, in order, each with whether its
    /// folder gave it. Windows does not keep, through the call a restore
    /// writes a descriptor with, the control bit that marks a DACL's
    /// inherited entries as given by automatic inheritance (ADR-0176), so the
    /// descriptor's own control bits are not compared.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string Dacl(FileSystemSecurity security) =>
        (security.AreAccessRulesProtected ? "protected: " : "inheriting: ")
        + string.Join("; ", security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => $"{rule.AccessControlType} {rule.IdentityReference} {rule.FileSystemRights} "
                + $"{rule.InheritanceFlags} {rule.PropagationFlags}{(rule.IsInherited ? " inherited" : string.Empty)}"));

    private string Landed(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "landed");
        return path;
    }
}
