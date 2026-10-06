using System.Runtime.Versioning;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// Writing a restored file's or folder's extended attributes back
/// (FR-RST-004; ADR-0087; architecture 06 §3, "extended attributes:
/// preserve"). Each attribute is written alone, never through a link, and
/// a refusal is an answer rather than an exception: a restore lists what it
/// could not write back and goes on.
/// </summary>
/// <remarks>
/// On Linux an attribute's name carries its namespace. Any account may
/// write the user namespace of a file it may write; only root writes the
/// trusted and security namespaces. macOS has no namespaces. The privileged
/// and unprivileged halves each run where the suite runs as that, so a
/// container proves the one and a CI runner the other.
/// </remarks>
[TestClass]
public sealed class ExtendedAttributesTests : IDisposable
{
    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-extended-attributes-tests", Guid.NewGuid().ToString("n"))).FullName;

    [TestMethod]
    public void CanSet_IsTrueOnLinuxAndMacOsAlone() =>
        Assert.AreEqual(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), ExtendedAttributes.CanSet);

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "Linux and macOS write a file's extended attributes")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void TrySet_AnAttributeTheAccountMayWrite_ReadsBackAsWritten()
    {
        var path = Landed("tagged.bin");

        Assert.IsTrue(ExtendedAttributes.TrySet(path, "user.tag", "kept"u8));
        Assert.IsTrue(ExtendedAttributes.TrySet(path, "user.empty", []));

        CollectionAssert.AreEqual("kept"u8.ToArray(), Xattr.Get(path, "user.tag"));
        CollectionAssert.AreEqual(Array.Empty<byte>(), Xattr.Get(path, "user.empty"));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "Linux and macOS write a folder's extended attributes")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void TrySet_OnAFolder_ReadsBackAsWritten()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "folder")).FullName;

        Assert.IsTrue(ExtendedAttributes.TrySet(folder, "user.tag", "kept"u8));

        CollectionAssert.AreEqual("kept"u8.ToArray(), Xattr.Get(folder, "user.tag"));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "Linux and macOS write a file's extended attributes")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void TrySet_OnAPathThatIsNotThere_IsFalse() =>
        Assert.IsFalse(ExtendedAttributes.TrySet(Path.Combine(_root, "absent.bin"), "user.tag", "kept"u8));

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link needs no privilege to create only on a POSIX host")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void TrySet_OnALink_NeverReachesWhatItPointsAt()
    {
        // Linux refuses a user attribute on a link and macOS gives the link
        // its own. Neither may follow it: a restore would otherwise write
        // wherever a link planted in the tree points.
        var target = Landed("target.bin");
        var link = Path.Combine(_root, "link");
        File.CreateSymbolicLink(link, target);

        ExtendedAttributes.TrySet(link, "user.tag", "through"u8);

        Assert.IsNull(Xattr.Get(target, "user.tag"));
    }

    [TestMethod]
    [UnprivilegedPlatformCondition(TestPlatforms.Linux, "only root writes Linux's trusted namespace")]
    [PlatformTrait(TestPlatforms.Linux)]
    public void TrySet_ATrustedAttribute_IsRefusedWithoutPrivilege()
    {
        var path = Landed("trusted.bin");

        Assert.IsFalse(ExtendedAttributes.TrySet(path, "trusted.probe", "t"u8));

        Assert.DoesNotContain("trusted.probe", Xattr.Names(path));
    }

    [TestMethod]
    [PrivilegedPlatformCondition(TestPlatforms.Linux, "only root writes Linux's trusted namespace")]
    [PlatformTrait(TestPlatforms.Linux)]
    public void TrySet_ATrustedAttribute_LandsWithPrivilege()
    {
        var path = Landed("trusted.bin");

        Assert.IsTrue(ExtendedAttributes.TrySet(path, "trusted.probe", "t"u8));

        CollectionAssert.AreEqual("t"u8.ToArray(), Xattr.Get(path, "trusted.probe"));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "a POSIX ACL is an extended attribute on Linux")]
    [PlatformTrait(TestPlatforms.Linux)]
    [UnsupportedOSPlatform("windows")]
    public void TrySet_AnAcl_IsTheFilesAcl_AndItsMaskIsTheGroupsPermissions()
    {
        // user::rw-, user:54321:r--, group::r--, mask::r--, other::---. The
        // mask becomes the group's permission bits, which is why a restore
        // writes the permissions after the ACL and they agree.
        var path = Landed("shared.bin");
        var acl = Xattr.Acl(
            (Xattr.AclUserObject, 6, Xattr.AclUndefinedId),
            (Xattr.AclUser, 4, 54_321),
            (Xattr.AclGroupObject, 4, Xattr.AclUndefinedId),
            (Xattr.AclMask, 4, Xattr.AclUndefinedId),
            (Xattr.AclOther, 0, Xattr.AclUndefinedId));

        Assert.IsTrue(ExtendedAttributes.TrySet(path, "system.posix_acl_access", acl));

        CollectionAssert.AreEqual(acl, Xattr.Get(path, "system.posix_acl_access"));
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(path));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "Windows keeps no extended attributes a restore writes")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void OnWindows_NothingIsWritten() =>
        Assert.IsFalse(ExtendedAttributes.TrySet(Landed("windows.bin"), "user.tag", "kept"u8));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A straggling handle on a temp directory is not a test failure.
        }
    }

    private string Landed(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }
}
