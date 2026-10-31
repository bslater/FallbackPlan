using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using FallbackPlan.TestSupport;
using static FallbackPlan.TestSupport.SecurityDescriptors;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// A file whose captured security descriptor a restore does not write takes
/// the access its new folder gives it, and the plan names the files that
/// gives more than their own did (FR-RST-003; ADR-0178; architecture 06 §3).
/// The access an account has to an entry is what its DACL's allowing entries
/// grant less what its denying ones take, inherit-only entries apart and
/// generic rights read as a file's; a null DACL grants everyone everything.
/// A file made in a folder takes the folder's object-inheriting entries, the
/// creator's own apart. An account is given wider access where it gets a
/// right the entry did not give it, synchronising apart. A file made below
/// folders the restore makes takes what passes down through them: an entry
/// marked not to propagate goes no further than the first (ADR-0178
/// Amendment 3). A folder the restore makes takes, as its own, what passes
/// to folders; one that stands is nothing to judge (ADR-0178 Amendment 4).
/// </summary>
[TestClass]
public sealed class WindowsAccessTests : IDisposable
{
    private const string Ana = "S-1-5-21-1-2-3-1001";
    private const string Users = "S-1-5-32-545";
    private const string Everyone = "S-1-1-0";
    private const string LocalSystem = "S-1-5-18";
    private const string Administrators = "S-1-5-32-544";
    private const string CreatorOwner = "S-1-3-0";

    private const uint FullControl = 0x1F01FF;
    private const uint Read = 0x120089;
    private const uint ReadAndExecute = 0x1200A9;
    private const uint WriteData = 0x2;
    private const uint Synchronize = 0x100000;
    private const uint GenericRead = 0x8000_0000;
    private const uint GenericAll = 0x1000_0000;

    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-windows-access-tests", Guid.NewGuid().ToString("n"))).FullName;

    [TestMethod]
    public void Read_EachAllowingAndDenyingEntry_WithItsMaskAndFlags_InOrder()
    {
        var read = WindowsSecurityDescriptor.Read(Build(Ana, Users, Acl(
            Ace(Denied, 0, Everyone, WriteData),
            Ace(Allowed, 0, Ana, FullControl),
            Ace(AllowedCallback, 0, Sid(Users)),
            Ace(Allowed, (byte)(Inherited | ObjectInherit), LocalSystem, GenericAll))));

        Assert.IsNotNull(read);
        CollectionAssert.AreEqual(
            new[]
            {
                new AccessEntry(Everyone, Allows: false, WriteData, 0),
                new AccessEntry(Ana, Allows: true, FullControl, 0),
                new AccessEntry(LocalSystem, Allows: true, GenericAll, (byte)(Inherited | ObjectInherit)),
            },
            read.Entries.ToArray());
        Assert.IsFalse(read.NullDacl);
    }

    [TestMethod]
    public void Of_GrantsWhatAllowingEntriesGive_LessWhatDenyingOnesTake_InheritOnlyEntriesApart()
    {
        var access = WindowsAccess.Of(
        [
            new AccessEntry(Users, Allows: false, WriteData, 0),
            new AccessEntry(Users, Allows: true, Read | WriteData, 0),
            new AccessEntry(Ana, Allows: true, FullControl, 0),
            new AccessEntry(Everyone, Allows: true, FullControl, InheritOnly | ObjectInherit),
        ]);

        Assert.HasCount(2, access);
        Assert.AreEqual(Read, access[Users]);
        Assert.AreEqual(FullControl, access[Ana]);
    }

    [TestMethod]
    public void Of_ReadsGenericRightsAsAFiles()
    {
        var access = WindowsAccess.Of([new AccessEntry(Users, Allows: true, GenericRead, 0), new AccessEntry(Ana, Allows: true, GenericAll, 0)]);

        Assert.AreEqual(Read, access[Users]);
        Assert.AreEqual(FullControl, access[Ana]);
    }

    [TestMethod]
    public void Of_ADescriptor_IsItsDaclsAccess_ANullDaclGrantsEveryoneEverything_AndNoDaclIsNotKnown()
    {
        var granted = WindowsAccess.Of(WindowsSecurityDescriptor.Read(Build(Ana, Users, Acl(Ace(Allowed, 0, Ana, FullControl))))!);
        var open = WindowsAccess.Of(WindowsSecurityDescriptor.Read(Build(Ana, Users, dacl: null))!);
        var unknown = WindowsAccess.Of(WindowsSecurityDescriptor.Read(Build(Ana, Users, dacl: null, daclPresent: false))!);

        Assert.AreEqual(FullControl, Assert.ContainsSingle(granted!).Value);
        Assert.AreEqual(Everyone, Assert.ContainsSingle(open!).Key);
        Assert.AreEqual(FullControl, open![Everyone]);
        Assert.IsNull(unknown);
    }

    [TestMethod]
    public void InheritedByAFile_TakesTheFoldersObjectInheritingEntries_TheCreatorsApart()
    {
        var inherited = WindowsAccess.InheritedByAFile(
        [
            new AccessEntry(Users, Allows: true, ReadAndExecute, ObjectInherit | ContainerInherit),
            new AccessEntry(Everyone, Allows: true, Read, ContainerInherit),
            new AccessEntry(CreatorOwner, Allows: true, GenericAll, ObjectInherit | ContainerInherit | InheritOnly),
            new AccessEntry(Administrators, Allows: true, FullControl, 0),
            new AccessEntry(LocalSystem, Allows: true, GenericAll, ObjectInherit | InheritOnly),
        ]);

        Assert.HasCount(2, inherited);
        Assert.AreEqual(ReadAndExecute, inherited[Users]);
        Assert.AreEqual(FullControl, inherited[LocalSystem]);
    }

    [TestMethod]
    public void Wider_NamesEachAccountGivenARightTheEntryDidNotHave_InOrder_SynchronisingApart()
    {
        var own = new Dictionary<string, uint> { [Ana] = FullControl, [LocalSystem] = FullControl, [Users] = Read };

        CollectionAssert.AreEqual(
            new[] { Users, Administrators },
            WindowsAccess.Wider(own, new Dictionary<string, uint>
            {
                [Users] = ReadAndExecute,
                [LocalSystem] = FullControl,
                [Administrators] = FullControl,
                [Everyone] = Synchronize,
            }).ToArray());
        Assert.IsEmpty(WindowsAccess.Wider(own, new Dictionary<string, uint> { [LocalSystem] = FullControl, [Users] = Read }));
    }

    [TestMethod]
    public void InheritedByAFile_BelowFoldersARestoreMakes_TakesWhatPassesDownThem_AndNotWhatStopsAtTheFirst()
    {
        // A folder made below passes on what inherits to folders as its own,
        // and what inherits to files alone as inherit-only, for the files
        // below it; what is marked not to propagate stops with it.
        AccessEntry[] standing =
        [
            new(LocalSystem, Allows: true, FullControl, ObjectInherit | ContainerInherit),
            new(Users, Allows: true, ReadAndExecute, ObjectInherit),
            new(Everyone, Allows: true, Read, ContainerInherit),
            new(Administrators, Allows: true, FullControl, ObjectInherit | ContainerInherit | NoPropagate),
            new(Ana, Allows: true, Read, ObjectInherit | NoPropagate),
            new(CreatorOwner, Allows: true, GenericAll, ObjectInherit | ContainerInherit | InheritOnly),
        ];

        CollectionAssert.AreEquivalent(
            new[] { LocalSystem, Users, Administrators, Ana }, WindowsAccess.InheritedByAFile(standing).Keys.ToArray());
        foreach (var made in new[] { 1, 3 })
        {
            var below = WindowsAccess.InheritedByAFile(standing, made);
            CollectionAssert.AreEquivalent(new[] { LocalSystem, Users }, below.Keys.ToArray(), $"{made} folder(s) down");
            Assert.AreEqual(ReadAndExecute, below[Users]);
            Assert.AreEqual(FullControl, below[LocalSystem]);
        }

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => WindowsAccess.InheritedByAFile(standing, -1));
    }

    [TestMethod]
    public void InheritedByAFolder_TakesWhatPassesToFolders_AsItsOwn_AndNothingThatPassesToFilesAlone()
    {
        AccessEntry[] standing =
        [
            new(LocalSystem, Allows: true, FullControl, ObjectInherit | ContainerInherit),
            new(Users, Allows: true, ReadAndExecute, ObjectInherit),
            new(Everyone, Allows: true, Read, ContainerInherit),
            new(Administrators, Allows: true, FullControl, ObjectInherit | ContainerInherit | NoPropagate),
            new(CreatorOwner, Allows: true, GenericAll, ObjectInherit | ContainerInherit | InheritOnly),
            new(Ana, Allows: true, Read, 0),
        ];

        var made = WindowsAccess.InheritedByAFolder(standing);
        CollectionAssert.AreEquivalent(new[] { LocalSystem, Everyone, Administrators }, made.Keys.ToArray());
        Assert.AreEqual(Read, made[Everyone]);

        var below = WindowsAccess.InheritedByAFolder(standing, 2);
        CollectionAssert.AreEquivalent(new[] { LocalSystem, Everyone }, below.Keys.ToArray(), "what stops at the first goes no further");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => WindowsAccess.InheritedByAFolder(standing, -1));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "only Windows keeps a folder's DACL")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void InheritedByAFileAt_IsNotKnownOffWindows() => Assert.IsNull(WindowsAccess.InheritedByAFileAt(_root));

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "only Windows keeps a folder's DACL")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void InheritedByAFolderAt_IsNotKnownOffWindows() =>
        Assert.IsNull(WindowsAccess.InheritedByAFolderAt(Path.Combine(_root, "not-made")));

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps a folder's DACL")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void InheritedByAFolderAt_OnWindows_IsNothingForAFolderThatStands_AndReadsTheNearestAboveOneMade()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "shared")).FullName;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(Users), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(Everyone), FileSystemRights.Read,
            InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(folder).SetAccessControl(security);

        Assert.IsNull(WindowsAccess.InheritedByAFolderAt(folder), "a folder that stands keeps its own, so nothing is judged");

        var inherited = WindowsAccess.InheritedByAFolderAt(Path.Combine(folder, "made"));
        Assert.IsNotNull(inherited);
        Assert.AreEqual(folder, inherited.Folder);
        Assert.AreEqual(Read, inherited.Access[Everyone]);
        Assert.IsFalse(inherited.Access.ContainsKey(Users), "what passes to files alone is not the folder's own");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps a folder's DACL")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void InheritedByAFileAt_OnWindows_ReadsTheNearestFolderThatStands()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "shared")).FullName;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(LocalSystem), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(Users), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(Everyone), FileSystemRights.Read,
            InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(Administrators), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.NoPropagateInherit, AccessControlType.Allow));
        new DirectoryInfo(folder).SetAccessControl(security);

        var inherited = WindowsAccess.InheritedByAFileAt(Path.Combine(folder, "not", "made", "yet"));

        Assert.IsNotNull(inherited);
        Assert.AreEqual(folder, inherited.Folder, "the folder its access comes from");
        Assert.AreEqual(FullControl, inherited.Access[LocalSystem]);
        Assert.AreEqual(ReadAndExecute, inherited.Access[Users]);
        Assert.IsFalse(inherited.Access.ContainsKey(Everyone));
        Assert.IsFalse(inherited.Access.ContainsKey(Administrators), "an entry that does not propagate stops at the first folder made");

        var direct = WindowsAccess.InheritedByAFileAt(folder);
        Assert.IsNotNull(direct);
        Assert.AreEqual(FullControl, direct.Access[Administrators], "a file made in the folder itself takes it");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows keeps a folder's DACL")]
    [PlatformTrait(TestPlatforms.Windows)]
    [SupportedOSPlatform("windows")]
    public void InheritedByAFileAt_OnWindows_ReadsAFolderThatStandsBelowAnother_NotTheOneAboveIt()
    {
        var shared = Directory.CreateDirectory(Path.Combine(_root, "shared")).FullName;
        var sharing = new DirectorySecurity();
        sharing.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sharing.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        sharing.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(Users), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(shared).SetAccessControl(sharing);
        var kept = Directory.CreateDirectory(Path.Combine(shared, "kept")).FullName;
        var keeping = new DirectorySecurity();
        keeping.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        keeping.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(kept).SetAccessControl(keeping);

        var inherited = WindowsAccess.InheritedByAFileAt(kept);

        Assert.IsNotNull(inherited);
        Assert.AreEqual(kept, inherited.Folder);
        Assert.IsFalse(inherited.Access.ContainsKey(Users), "the folder above it shares; the one it lands in does not");
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
}
