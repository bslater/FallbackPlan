using FallbackPlan.TestSupport;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// A junction a restore makes again (FR-RST-004; ADR-0183 Amendment 2): a
/// link whose manifest names it a mount point, in key 14 from format 6 or
/// in its capture diagnostics before it, and which names a folder by its
/// drive's path. Windows makes one through a mount-point reparse point,
/// which needs no privilege; no other platform makes one, and says so.
/// </summary>
[TestClass]
public sealed class MountPointsTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "fbp-mount-points", Guid.NewGuid().ToString("n"))).FullName;

    [TestMethod]
    [DataRow(@"C:\Users\Shared\Projects")]
    [DataRow(@"d:\")]
    public void AFolderOnADrive_IsNamedByItsDrivesPath(string target) =>
        Assert.IsTrue(MountPoints.NamesAFolderOnADrive(target));

    [TestMethod]
    [DataRow(@"Volume{0b3c7a52-1f6e-4c1d-9a2b-6c8e2f4d7a10}\")]
    [DataRow("")]
    [DataRow(@"Projects\Shared")]
    [DataRow(@"\\server\share\Projects")]
    [DataRow(@"C:Projects")]
    [DataRow(@"1:\Projects")]
    [DataRow("/home/shared/projects")]
    public void APathThatIsNoDrivesFolder_IsNot(string target) =>
        Assert.IsFalse(MountPoints.NamesAFolderOnADrive(target));

    [TestMethod]
    public void TheFormALinkWasCapturedWith_IsWhatKeyFourteenKeeps_OrWhatItsDiagnosticsName()
    {
        // A manifest written at format 6 keeps the form in the key, one
        // written before names it; a reader asks both, so it reads either.
        Assert.AreEqual(LinkForm.MountPoint, MetadataRules.LinkFormOf(LinkForm.MountPoint, []));
        Assert.AreEqual(LinkForm.MountPoint, MetadataRules.LinkFormOf(LinkForm.SymbolicLink, ["captured-inconsistent: 2", "link-form: mount-point"]));
        Assert.AreEqual(LinkForm.SymbolicLink, MetadataRules.LinkFormOf(LinkForm.SymbolicLink, ["captured-inconsistent: 2"]));
        Assert.AreEqual(LinkForm.SymbolicLink, MetadataRules.LinkFormOf(LinkForm.SymbolicLink, ["link-form: sideways"]));
    }

    [TestMethod]
    public void AJunction_IsAMountPointNamingADrivesFolder_HoweverItsFormWasKept()
    {
        Assert.IsTrue(MountPoints.IsJunction(LinkForm.MountPoint, [], @"C:\Users\Shared"));
        Assert.IsTrue(MountPoints.IsJunction(LinkForm.SymbolicLink, ["link-form: mount-point"], @"C:\Users\Shared"));

        Assert.IsFalse(MountPoints.IsJunction(LinkForm.SymbolicLink, [], @"C:\Users\Shared"), "a symbolic link to a drive's folder");
        Assert.IsFalse(MountPoints.IsJunction(LinkForm.MountPoint, [], "Projects/Shared"), "a mount point naming no drive's folder");
        Assert.IsFalse(MountPoints.IsJunction(LinkForm.MountPoint, [], target: null), "a mount point naming nothing");
    }

    [TestMethod]
    public void OnlyWindows_MakesOne() =>
        Assert.AreEqual(OperatingSystem.IsWindows(), MountPoints.CanMake);

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "Windows makes junctions")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void OffWindows_NoneIsMade_AndTheCallSaysSo()
    {
        var path = Path.Combine(_root, "junction");

        var refused = MountPoints.OfThisPlatform.Make(path, @"C:\Users\Shared");

        Assert.AreEqual("this platform makes no junction", refused);
        Assert.IsFalse(Path.Exists(path));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows makes a junction")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void OnWindows_AJunctionIsMade_NamingItsFolder_AndWhatTheFolderHoldsIsSeenThroughIt()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "projects")).FullName;
        File.WriteAllText(Path.Combine(folder, "kept.txt"), "through a junction");
        var path = Path.Combine(_root, "junction");

        var refused = MountPoints.OfThisPlatform.Make(path, folder);

        Assert.IsNull(refused);
        Assert.IsTrue(Junctions.IsMountPoint(path), "a mount point, not a folder's symbolic link");
        Assert.AreEqual(folder, new DirectoryInfo(path).LinkTarget);
        Assert.AreEqual("through a junction", File.ReadAllText(Path.Combine(path, "kept.txt")));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows makes a junction")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void OnWindows_AJunctionToAFolderNotThere_IsMadeStill()
    {
        // As a link to nothing is: what it names may come later, or be on a
        // drive this machine does not have.
        var path = Path.Combine(_root, "junction");
        var absent = Path.Combine(_root, "not-there");

        Assert.IsNull(MountPoints.OfThisPlatform.Make(path, absent));
        Assert.IsTrue(Junctions.IsMountPoint(path));
        Assert.AreEqual(absent, new DirectoryInfo(path).LinkTarget);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows makes a junction")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void OnWindows_WhereSomethingStandsAtItsName_NoneIsMade_AndWhatStandsIsLeft()
    {
        var path = Path.Combine(_root, "taken");
        File.WriteAllText(path, "already here");

        var refused = MountPoints.OfThisPlatform.Make(path, _root);

        Assert.IsNotNull(refused);
        Assert.AreEqual("already here", File.ReadAllText(path));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "only Windows makes a junction")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void OnWindows_ATargetTooLongForAReparsePoint_IsRefused_AndNothingIsLeftAtItsName()
    {
        // A reparse point holds at most 16 KiB, so a path of 9,000
        // characters, twice over in UTF-16, cannot be kept in one.
        var path = Path.Combine(_root, "junction");

        var refused = MountPoints.OfThisPlatform.Make(path, @"C:\" + new string('a', 9_000));

        Assert.IsNotNull(refused);
        Assert.Contains("refused by the target", refused, StringComparison.Ordinal);
        Assert.IsFalse(Path.Exists(path));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A stale temp directory, never a failed assertion.
        }
    }
}
