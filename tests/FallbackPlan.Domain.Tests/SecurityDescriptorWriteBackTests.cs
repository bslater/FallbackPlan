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
/// the DACL still lands. Where it holds SeRestorePrivilege, it gives an owner
/// it is not, with the privilege enabled for that write alone (ADR-0176
/// Amendment 1). A descriptor's audit list (SACL) lands with it where the
/// account holds SeSecurityPrivilege, enabled for that write alone; where it
/// does not, the audit list is named and the rest still lands (ADR-0176
/// Amendment 3).
/// </summary>
[TestClass]
public sealed class SecurityDescriptorWriteBackTests : IDisposable
{
    private const string Stranger = "S-1-5-21-1-2-3-4242";
    private const string Everyone = "S-1-1-0";

    private static readonly DateTime Modified = new(2024, 8, 2, 11, 15, 30, 250, DateTimeKind.Utc);

    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-security-descriptor-tests", Guid.NewGuid().ToString("n"))).FullName;

    [TestMethod]
    public void ThisPlatform_WritesSecurityDescriptors_OnlyOnWindows() =>
        Assert.AreEqual(OperatingSystem.IsWindows(), MetadataTarget.ForLocalPlatform().SupportsSecurityDescriptors);

    [TestMethod]
    public void ThisPlatform_HoldsTheRestorePrivilege_OnlyWhereItsAccountDoes()
    {
        Assert.AreEqual(WindowsPrivileges.Holds(WindowsPrivileges.Restore), MetadataTarget.ForLocalPlatform().HoldsRestorePrivilege);
        Assert.IsFalse(new MetadataTarget { SupportsPosixMetadata = false }.HoldsRestorePrivilege);
        if (!OperatingSystem.IsWindows())
        {
            Assert.IsFalse(MetadataTarget.ForLocalPlatform().HoldsRestorePrivilege);
        }
    }

    [TestMethod]
    public void ThisPlatform_HoldsTheSecurityPrivilege_OnlyWhereItsAccountDoes()
    {
        Assert.AreEqual(WindowsPrivileges.Holds(WindowsPrivileges.Security), MetadataTarget.ForLocalPlatform().HoldsSecurityPrivilege);
        Assert.IsFalse(new MetadataTarget { SupportsPosixMetadata = false }.HoldsSecurityPrivilege);
    }

    [TestMethod]
    [PrivilegedPlatformCondition(TestPlatforms.Windows, "only an elevated process holds the privilege an audit list is written with")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void Write_OnWindows_ADescriptorWithAnAuditList_LandsWithIt_WhereTheAccountHoldsTheSecurityPrivilege()
    {
        var source = Audited("audited.bin");
        var file = Landed("restored.bin");

        var written = WriterHoldingSecurity(true).Write(file, new EntryMetadata { WindowsSecurityDescriptor = CapturedWithAudit(source) });

        Assert.IsTrue(written.Applied.HasFlag(CapturedMetadata.SecurityDescriptor), written.Detail);
        Assert.AreEqual(Dacl(source), Dacl(file));
        Assert.AreEqual(Audits(source), Audits(file));
        Assert.Contains(Everyone, Audits(file), StringComparison.Ordinal);
    }

    [TestMethod]
    [PrivilegedPlatformCondition(TestPlatforms.Windows, "only an elevated process can give a file an audit list to capture")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void Write_OnWindows_AnAuditList_IsNamed_AndTheRestLands_WhereTheAccountHoldsNoSecurityPrivilege()
    {
        var source = Audited("audited.bin");
        var file = Landed("restored.bin");

        var written = WriterHoldingSecurity(false).Write(file, new EntryMetadata { WindowsSecurityDescriptor = CapturedWithAudit(source) });

        Assert.IsFalse(written.Applied.HasFlag(CapturedMetadata.SecurityDescriptor));
        Assert.IsNotNull(written.Detail);
        Assert.Contains("audit list", written.Detail, StringComparison.Ordinal);
        Assert.Contains("SeSecurityPrivilege", written.Detail, StringComparison.Ordinal);
        Assert.AreEqual(Dacl(source), Dacl(file));
        Assert.DoesNotContain(Everyone, Audits(file), StringComparison.Ordinal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps a file's security descriptor")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void Write_OnWindows_AnAuditListNamingAnAccountThisMachineDoesNotKnow_IsNamed_AndTheRestLands()
    {
        // Said to hold the privilege, so only the stranger keeps the audit
        // list back: were it tried, a process that does not hold it would
        // be refused the whole write.
        var source = Landed("source.bin");
        var security = new FileInfo(source).GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(source).SetAccessControl(security);
        var captured = new FileInfo(source).GetAccessControl();
        captured.AddAuditRule(new FileSystemAuditRule(new SecurityIdentifier(Stranger), FileSystemRights.Delete, AuditFlags.Success));
        var file = Landed("restored.bin");

        var written = WriterHoldingSecurity(true).Write(
            file, new EntryMetadata { WindowsSecurityDescriptor = captured.GetSecurityDescriptorBinaryForm() });

        Assert.IsFalse(written.Applied.HasFlag(CapturedMetadata.SecurityDescriptor));
        Assert.IsNotNull(written.Detail);
        Assert.Contains("audit list not applied", written.Detail, StringComparison.Ordinal);
        Assert.Contains(Stranger, written.Detail, StringComparison.Ordinal);
        Assert.AreEqual(Dacl(source), Dacl(file));
    }

    [TestMethod]
    public void AnAccount_IsKnownOnlyWhereThisMachineNamesIt()
    {
        Assert.IsFalse(WindowsAccounts.Knows(Stranger));
        Assert.IsFalse(WindowsAccounts.Knows("not an account"));
        Assert.AreEqual(OperatingSystem.IsWindows(), WindowsAccounts.Knows("S-1-5-18"));
    }

    [TestMethod]
    public void AnOwner_IsOneTheAccountMayGive_NeitherWhereItIsAStranger_NorWhereItIsNoAccount()
    {
        Assert.IsFalse(WindowsAccounts.MayOwn(Stranger));
        Assert.IsFalse(WindowsAccounts.MayOwn("not an account"));

        // Local Service is an account every Windows machine names, and one no
        // test runs as.
        Assert.IsFalse(WindowsAccounts.MayOwn("S-1-5-19"));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows names an account by SID")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void AnOwner_OnWindows_MayBeTheAccountItself()
    {
        using var self = WindowsIdentity.GetCurrent();

        Assert.IsTrue(WindowsAccounts.MayOwn(self.User!.Value));
    }

    [TestMethod]
    [PrivilegedPlatformCondition(TestPlatforms.Windows, "only an elevated process may own by the administrators' group")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void AnOwner_InAnElevatedProcess_MayBeTheAdministratorsGroup() =>
        Assert.IsTrue(WindowsAccounts.MayOwn(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value));

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
    public void Write_OnWindows_AnOwnerTheAccountMayNotGive_LeavesTheDaclLanded_AndSaysSo()
    {
        // Local Service is an account every Windows machine names, and one a
        // process may not make an owner without SeRestorePrivilege enabled.
        var (source, descriptor) = GivenAway("given-away.bin");
        var file = Landed("restored.bin");
        var before = new FileInfo(file).GetAccessControl().GetOwner(typeof(SecurityIdentifier));

        var written = Writer(holdsRestorePrivilege: false).Write(file, new EntryMetadata { WindowsSecurityDescriptor = descriptor });

        Assert.IsFalse(written.Applied.HasFlag(CapturedMetadata.SecurityDescriptor));
        Assert.IsNotNull(written.Detail);
        Assert.Contains("owner and group not applied", written.Detail, StringComparison.Ordinal);
        Assert.AreEqual(Dacl(source), Dacl(file));
        Assert.AreEqual(before, new FileInfo(file).GetAccessControl().GetOwner(typeof(SecurityIdentifier)));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps a file's security descriptor")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void Write_OnWindows_AnOwnerTheAccountMayNotGive_WithAnAuditList_LeavesTheDaclLanded_AndSaysNeitherLanded()
    {
        var (source, descriptor) = GivenAway("given-away.bin", audited: true);
        var file = Landed("restored.bin");
        var writer = new MetadataWriter(MetadataTarget.ForLocalPlatform() with
        {
            SupportsSecurityDescriptors = true, HoldsRestorePrivilege = false, HoldsSecurityPrivilege = false,
        });

        var written = writer.Write(file, new EntryMetadata { WindowsSecurityDescriptor = descriptor });

        Assert.IsFalse(written.Applied.HasFlag(CapturedMetadata.SecurityDescriptor));
        Assert.IsNotNull(written.Detail);
        Assert.Contains("owner and group not applied", written.Detail, StringComparison.Ordinal);
        Assert.Contains("nor its audit list", written.Detail, StringComparison.Ordinal);
        Assert.AreEqual(Dacl(source), Dacl(file));
    }

    [TestMethod]
    [PrivilegedPlatformCondition(TestPlatforms.Windows, "only an elevated process holds the privilege a restore gives an owner with")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void Write_OnWindows_AnAccountHoldingTheRestorePrivilege_GivesAnOwnerItIsNot_AndLeavesThePrivilegeAsItWas()
    {
        var (source, descriptor) = GivenAway("given-away.bin");
        var file = Landed("restored.bin");
        var enabledBefore = WindowsPrivileges.IsEnabled(WindowsPrivileges.Restore);

        var written = Writer(holdsRestorePrivilege: true).Write(file, new EntryMetadata { WindowsSecurityDescriptor = descriptor });

        Assert.IsTrue(written.Applied.HasFlag(CapturedMetadata.SecurityDescriptor));
        Assert.IsNull(written.Detail);
        Assert.AreEqual(
            new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null),
            new FileInfo(file).GetAccessControl().GetOwner(typeof(SecurityIdentifier)));
        Assert.AreEqual(Dacl(source), Dacl(file));
        Assert.AreEqual(enabledBefore, WindowsPrivileges.IsEnabled(WindowsPrivileges.Restore));
        Assert.IsNull(WindowsIdentity.GetCurrent(ifImpersonating: true));
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

    private static MetadataWriter Writer(bool holdsRestorePrivilege) =>
        new(MetadataTarget.ForLocalPlatform() with { SupportsSecurityDescriptors = true, HoldsRestorePrivilege = holdsRestorePrivilege });

    private static MetadataWriter WriterHoldingSecurity(bool holdsSecurityPrivilege) =>
        new(MetadataTarget.ForLocalPlatform() with { SupportsSecurityDescriptors = true, HoldsSecurityPrivilege = holdsSecurityPrivilege });

    /// <summary>
    /// A file whose protected DACL grants this account everything, and whose
    /// audit list records Everyone deleting it. .NET reads and writes an
    /// audit list with SeSecurityPrivilege enabled for the call.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private string Audited(string name)
    {
        var path = Landed(name);
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Audit);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAuditRule(new FileSystemAuditRule(new SecurityIdentifier(Everyone), FileSystemRights.Delete, AuditFlags.Success));
        new FileInfo(path).SetAccessControl(security);
        return path;
    }

    [SupportedOSPlatform("windows")]
    private static byte[] CapturedWithAudit(string path) =>
        new FileInfo(path).GetAccessControl(AccessControlSections.All).GetSecurityDescriptorBinaryForm();

    /// <summary>A file's audit entries, each with its account, rights and flags.</summary>
    [SupportedOSPlatform("windows")]
    private static string Audits(string path) =>
        string.Join("; ", new FileInfo(path).GetAccessControl(AccessControlSections.Audit)
            .GetAuditRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAuditRule>()
            .Select(rule => $"{rule.IdentityReference} {rule.FileSystemRights} {rule.AuditFlags}"));

    /// <summary>
    /// A file whose protected DACL grants this account everything, and a
    /// descriptor captured from it that names Local Service as its owner,
    /// though the file itself stays this account's, and, where
    /// <paramref name="audited"/> says so, carries an audit list built in
    /// memory, which needs no privilege.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private (string Source, byte[] Descriptor) GivenAway(string name, bool audited = false)
    {
        var source = Landed(name);
        var security = new FileInfo(source).GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(source).SetAccessControl(security);
        var captured = new FileInfo(source).GetAccessControl();
        captured.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null));
        if (audited)
        {
            captured.AddAuditRule(new FileSystemAuditRule(new SecurityIdentifier(Everyone), FileSystemRights.Delete, AuditFlags.Success));
        }

        return (source, captured.GetSecurityDescriptorBinaryForm());
    }

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
