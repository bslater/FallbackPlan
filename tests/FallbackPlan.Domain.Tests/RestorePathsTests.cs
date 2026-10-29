using FallbackPlan.TestSupport;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// Where a restore writes a path under the folder it restores into, judged
/// through links where that folder really is (FR-RST-005, ADR-0159). A folder
/// that is itself a link, or lies below one, takes every path below its top
/// level, and a link inside it that leads elsewhere inside it is taken by
/// either spelling. A link that leads out of it is refused at the folder that
/// leads out, as ADR-0152 has it, whatever the folder's own links.
/// </summary>
[TestClass]
public sealed class RestorePathsTests : IDisposable
{
    private readonly string _work = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "fbp-restore-paths", Guid.NewGuid().ToString("n"))).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link is made there without a privilege")]
    public void APathBelowTheTopLevelOfARootThatIsALink_IsTaken()
    {
        var real = Folder("real", "nested");
        var root = Link("root", real);

        Assert.IsTrue(RestorePaths.TryResolve(root, "nested/two.txt", out var destination, out var reason), reason);
        Assert.AreEqual(Path.Combine(root, "nested", "two.txt"), destination);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link is made there without a privilege")]
    public void APathBelowARootThatLiesBelowALink_IsTaken()
    {
        Folder("real", "root", "nested");
        var root = Path.Combine(Link("above", Path.Combine(_work, "real")), "root");

        Assert.IsTrue(RestorePaths.TryResolve(root, "nested/two.txt", out _, out var reason), reason);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link is made there without a privilege")]
    public void APathThroughALinkSpeltThroughTheRootsOwnLink_IsTaken()
    {
        // The link inside leads to a folder inside, named by the root's link.
        var real = Folder("real", "inside");
        var root = Link("root", real);
        Directory.CreateSymbolicLink(Path.Combine(real, "shortcut"), Path.Combine(root, "inside"));

        Assert.IsTrue(RestorePaths.TryResolve(root, "shortcut/two.txt", out _, out var reason), reason);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link is made there without a privilege")]
    public void APathThroughALinkLeadingElsewhereInsideTheRoot_IsTaken()
    {
        var root = Folder("root", "inside");
        Directory.CreateSymbolicLink(Path.Combine(root, "shortcut"), Path.Combine(root, "inside"));

        Assert.IsTrue(RestorePaths.TryResolve(root, "shortcut/two.txt", out _, out var reason), reason);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link is made there without a privilege")]
    public void APathThroughALinkLeadingOutOfTheRoot_IsRefused()
    {
        var root = Folder("root");
        var outside = Folder("outside");
        Directory.CreateSymbolicLink(Path.Combine(root, "out"), outside);

        Assert.IsFalse(RestorePaths.TryResolve(root, "out/two.txt", out _, out var reason));
        Assert.AreEqual("the path traverses a symlink that leaves the restore root", reason);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link is made there without a privilege")]
    public void APathThroughALinkLeadingOutOfARootThatIsALink_IsRefused()
    {
        var real = Folder("real");
        var root = Link("root", real);
        Directory.CreateSymbolicLink(Path.Combine(real, "out"), Folder("outside"));

        Assert.IsFalse(RestorePaths.TryResolve(root, "out/deeper/two.txt", out _, out var reason));
        Assert.AreEqual("the path traverses a symlink that leaves the restore root", reason);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link is made there without a privilege")]
    public void APathThroughALinkThatLeadsOutAndBackIn_IsRefused()
    {
        // It lands inside, but only by way of a folder outside.
        var root = Folder("root", "inside");
        var outside = Folder("outside");
        Directory.CreateSymbolicLink(Path.Combine(root, "out"), outside);
        Directory.CreateSymbolicLink(Path.Combine(outside, "back"), Path.Combine(root, "inside"));

        Assert.IsFalse(RestorePaths.TryResolve(root, "out/back/two.txt", out _, out _));
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link is made there without a privilege")]
    public void ThroughLinks_FollowsEveryComponentThatIsALink_AndKeepsTheRestAsWritten()
    {
        // The work folder may itself lie below a link, as a temporary folder
        // does on macOS, so the spelling through the link is held to the real
        // one's, both followed.
        var real = Folder("real", "inside");
        var link = Link("link", real);

        var followed = RestorePaths.ThroughLinks(Path.Combine(link, "inside", "not-yet", "file"));

        Assert.AreEqual(RestorePaths.ThroughLinks(Path.Combine(real, "inside", "not-yet", "file")), followed);
        Assert.EndsWith(Path.Combine("real", "inside", "not-yet", "file"), followed, StringComparison.Ordinal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link is made there without a privilege")]
    public void ThroughLinks_FollowsALinkWhoseTargetIsRelative_FromTheFolderItIsIn()
    {
        var real = Folder("real");
        Directory.CreateSymbolicLink(Path.Combine(_work, "link"), "real");

        var followed = RestorePaths.ThroughLinks(Path.Combine(_work, "link"));

        Assert.AreEqual(RestorePaths.ThroughLinks(real), followed);
        Assert.EndsWith(Path.DirectorySeparatorChar + "real", followed, StringComparison.Ordinal);
    }

    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "a link is made there without a privilege")]
    public void ThroughLinks_StopsAtALoop()
    {
        Directory.CreateSymbolicLink(Path.Combine(_work, "a"), Path.Combine(_work, "b"));
        Directory.CreateSymbolicLink(Path.Combine(_work, "b"), Path.Combine(_work, "a"));

        Assert.IsNotNull(RestorePaths.ThroughLinks(Path.Combine(_work, "a", "file")));
    }

    /// <summary>Makes the folders <paramref name="names"/> lays out under the work folder, and answers the outermost.</summary>
    private string Folder(params string[] names)
    {
        Directory.CreateDirectory(Path.Combine([_work, .. names]));
        return Path.Combine(_work, names[0]);
    }

    /// <summary>Makes a link named <paramref name="name"/> under the work folder leading to <paramref name="target"/>.</summary>
    private string Link(string name, string target)
    {
        var link = Path.Combine(_work, name);
        Directory.CreateSymbolicLink(link, target);
        return link;
    }
}
