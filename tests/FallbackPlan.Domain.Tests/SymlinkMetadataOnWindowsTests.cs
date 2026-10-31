using FallbackPlan.TestSupport;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// The times a restore writes onto a symlink it made on Windows (FR-RST-004;
/// ADR-0181 Amendment 2): its modification, access and creation times, each
/// through a handle opened on the link itself, so a link to a file, to a
/// folder or to nothing is given its own and what it points at keeps its
/// own.
/// </summary>
/// <remarks>
/// The oracle is the times Windows reports for the link's path, which it
/// reports for a link itself and never for what the link points at. A
/// write that went through the link would leave the link's own as they were
/// and move its target's, and both are checked.
/// </remarks>
[TestClass]
public sealed class SymlinkMetadataOnWindowsTests : IDisposable
{
    private static readonly DateTime Modified = new(2023, 3, 9, 8, 15, 30, 250, DateTimeKind.Utc);

    private static readonly DateTime Accessed = new(2024, 2, 1, 12, 0, 5, 500, DateTimeKind.Utc);

    private static readonly DateTime Created = new(2020, 5, 20, 17, 45, 0, 125, DateTimeKind.Utc);

    /// <summary>What the link's target carries: 2001-09-09, long before anything the test writes.</summary>
    private static readonly DateTime Before = new(2001, 9, 9, 1, 46, 40, DateTimeKind.Utc);

    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fbp-symlink-windows-tests", Guid.NewGuid().ToString("n"))).FullName;

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "a link's own times are written through Windows' own handle on it")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void TrySetTimes_OnWindows_WritesThemOntoTheLinkItself_AndWhatItPointsAtKeepsItsOwn()
    {
        var target = Target("target.bin");
        var link = Link("link", target, folder: false);

        Assert.IsTrue(SymlinkMetadata.TrySetTimes(link, Modified, Accessed));

        var own = new FileInfo(link);
        Assert.AreEqual((Modified, Accessed), (own.LastWriteTimeUtc, own.LastAccessTimeUtc));
        Assert.AreEqual(Before, File.GetLastWriteTimeUtc(target), "a link's times went through it");
        Assert.AreEqual(Before, File.GetLastAccessTimeUtc(target), "a link's times went through it");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "a link's own times are written through Windows' own handle on it")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void TrySetTimes_OnWindows_OnAFoldersLink_WritesThemOntoTheLink_AndTheFolderKeepsItsOwn()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "folder")).FullName;
        Directory.SetLastWriteTimeUtc(folder, Before);
        var link = Link("folder-link", folder, folder: true);

        Assert.IsTrue(SymlinkMetadata.TrySetTimes(link, Modified, Accessed));

        Assert.AreEqual(Modified, new DirectoryInfo(link).LastWriteTimeUtc);
        Assert.AreEqual(Before, Directory.GetLastWriteTimeUtc(folder), "a link's times went through it");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "a link's own times are written through Windows' own handle on it")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void TrySetTimes_OnWindows_OnALinkThatPointsAtNothing_StillWritesThem()
    {
        // A call that followed the link would find nothing there and fail.
        var link = Link("dangling", Path.Combine(_root, "not-there.bin"), folder: false);

        Assert.IsTrue(SymlinkMetadata.TrySetTimes(link, Modified, Accessed));

        Assert.AreEqual(Modified, new FileInfo(link).LastWriteTimeUtc);
        Assert.IsFalse(File.Exists(Path.Combine(_root, "not-there.bin")));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "a link's own times are written through Windows' own handle on it")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void TrySetTimes_OnWindows_WithOneTimeAlone_LeavesTheOtherAsItWas()
    {
        var link = Link("one", Target("one-target.bin"), folder: false);
        Assert.IsTrue(SymlinkMetadata.TrySetTimes(link, Modified, Accessed));

        Assert.IsTrue(SymlinkMetadata.TrySetTimes(link, modifiedUtc: null, accessedUtc: Before));

        var own = new FileInfo(link);
        Assert.AreEqual((Modified, Before), (own.LastWriteTimeUtc, own.LastAccessTimeUtc));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "a link's own times are written through Windows' own handle on it")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void TrySetCreationTime_OnWindows_WritesItOntoTheLinkItself_AndWhatItPointsAtKeepsItsOwn()
    {
        var target = Target("born.bin");
        var link = Link("born-link", target, folder: false);

        Assert.IsTrue(SymlinkMetadata.TrySetCreationTime(link, Created));

        Assert.AreEqual(Created, new FileInfo(link).CreationTimeUtc);
        Assert.AreEqual(Before, File.GetCreationTimeUtc(target), "a link's creation time went through it");
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Windows, "a link's own times are written through Windows' own handle on it")]
    [PlatformTrait(TestPlatforms.Windows)]
    public void TrySetTimes_AndTrySetCreationTime_OnWindows_WhereNothingIsThere_AnswerFalse()
    {
        var absent = Path.Combine(_root, "absent");

        Assert.IsFalse(SymlinkMetadata.TrySetTimes(absent, Modified, Accessed));
        Assert.IsFalse(SymlinkMetadata.TrySetCreationTime(absent, Created));
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
        File.SetCreationTimeUtc(path, Before);
        File.SetLastWriteTimeUtc(path, Before);
        File.SetLastAccessTimeUtc(path, Before);
        return path;
    }

    private string Link(string name, string target, bool folder)
    {
        Assert.IsTrue(SymbolicLinks.CanCreate, "the premise: this account may make a link");
        var path = Path.Combine(_root, name);
        SymbolicLinks.Create(path, target, folder);
        return path;
    }
}
