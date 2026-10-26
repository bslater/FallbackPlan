using System.Runtime.Versioning;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// The writes a restore makes onto a symlink itself (FR-RST-004; ADR-0101):
/// its modification and access times on Linux and macOS, and its permissions
/// on macOS, which keeps a link's own. Every one lands on the link and never
/// on what it points at, so a link that points out of a restore, or at
/// nothing, is given its own and nothing else is touched.
/// </summary>
/// <remarks>
/// .NET's permission setter follows a link. Used on one, it would give
/// whatever the link points at the link's captured permissions, which a
/// restore as root could be any file on the machine. Its time setters act on
/// the link itself on this runtime, but promise nothing a restore could rest
/// on, so the no-follow is the call's own. Linux has no call that changes a
/// link's permissions: every link there is <c>rwxrwxrwx</c>, and none is
/// consulted.
/// </remarks>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class SymlinkMetadataTests : IDisposable
{
    private static readonly DateTime Modified = new(2023, 3, 9, 8, 15, 30, 250, DateTimeKind.Utc);

    private static readonly DateTime Accessed = new(2024, 2, 1, 12, 0, 5, 500, DateTimeKind.Utc);

    /// <summary>What the link's target carries: 2001-09-09, long before anything the test writes.</summary>
    private static readonly DateTime Before = new(2001, 9, 9, 1, 46, 40, DateTimeKind.Utc);

    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-symlink-metadata-tests", Guid.NewGuid().ToString("n"))).FullName;

    [TestMethod]
    public void CanSetTimes_IsTrueOnLinuxAndMacOsAlone() =>
        Assert.AreEqual((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && Environment.Is64BitProcess, SymlinkMetadata.CanSetTimes);

    [TestMethod]
    public void CanSetPermissions_IsTrueOnMacOsAlone() =>
        Assert.AreEqual(OperatingSystem.IsMacOS(), SymlinkMetadata.CanSetPermissions);

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link needs no privilege to create only on a POSIX host")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void TrySetTimes_WritesThemOntoTheLinkItself_AndWhatItPointsAtKeepsItsOwn()
    {
        var target = Target("target.bin");
        var link = Link("link", target);

        Assert.IsTrue(SymlinkMetadata.TrySetTimes(link, Modified, Accessed));

        var status = LinkStatus.Of(link);
        Assert.AreEqual(Modified, status.ModifiedUtc);
        Assert.AreEqual(Accessed, status.AccessedUtc);
        Assert.AreEqual(Before, File.GetLastWriteTimeUtc(target), "a link's times went through it");
        Assert.AreEqual(Before, File.GetLastAccessTimeUtc(target), "a link's times went through it");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link needs no privilege to create only on a POSIX host")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void TrySetTimes_OnALinkThatPointsAtNothing_StillWritesThem()
    {
        // A call that followed the link would find nothing there and fail.
        var link = Link("dangling", Path.Combine(_root, "not-there.bin"));

        Assert.IsTrue(SymlinkMetadata.TrySetTimes(link, Modified, Accessed));

        Assert.AreEqual(Modified, LinkStatus.Of(link).ModifiedUtc);
        Assert.IsFalse(File.Exists(Path.Combine(_root, "not-there.bin")));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link needs no privilege to create only on a POSIX host")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void TrySetTimes_WithOneTimeAlone_LeavesTheOtherAsItWas()
    {
        var link = Link("one", Target("one-target.bin"));
        Assert.IsTrue(SymlinkMetadata.TrySetTimes(link, Modified, Accessed));

        Assert.IsTrue(SymlinkMetadata.TrySetTimes(link, modifiedUtc: null, accessedUtc: Before));

        var status = LinkStatus.Of(link);
        Assert.AreEqual(Modified, status.ModifiedUtc);
        Assert.AreEqual(Before, status.AccessedUtc);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link needs no privilege to create only on a POSIX host")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void TrySetTimes_WhereNothingIsThere_AnswersFalse() =>
        Assert.IsFalse(SymlinkMetadata.TrySetTimes(Path.Combine(_root, "absent"), Modified, Accessed));

    [TestMethod]
    [PlatformCondition(TestPlatforms.MacOs, "macOS keeps a link's own permissions")]
    [PlatformTrait(TestPlatforms.MacOs)]
    public void TrySetPermissions_OnMacOs_WritesThemOntoTheLinkItself_AndWhatItPointsAtKeepsItsOwn()
    {
        var target = Target("guarded.bin");
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var link = Link("guarded-link", target);

        Assert.IsTrue(SymlinkMetadata.TrySetPermissions(link, 0x1ED));

        Assert.AreEqual(0x1EDu, LinkStatus.Of(link).Permissions);
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(target), "a link's permissions went through it");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Linux, "Linux keeps no permissions of a link's own")]
    [PlatformTrait(TestPlatforms.Linux)]
    public void TrySetPermissions_OnLinux_WritesNothing_AndEveryLinkKeepsTheOnesLinuxGivesIt()
    {
        var target = Target("linux-target.bin");
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var link = Link("linux-link", target);

        Assert.IsFalse(SymlinkMetadata.TrySetPermissions(link, 0x1ED));

        Assert.AreEqual(SymlinkMetadata.LinuxPermissions, LinkStatus.Of(link).Permissions);
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(target));
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

    private string Target(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        File.SetLastWriteTimeUtc(path, Before);
        File.SetLastAccessTimeUtc(path, Before);
        return path;
    }

    private string Link(string name, string target)
    {
        var path = Path.Combine(_root, name);
        File.CreateSymbolicLink(path, target);
        return path;
    }
}
