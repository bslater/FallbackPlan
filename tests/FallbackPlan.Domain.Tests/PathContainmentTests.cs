namespace FallbackPlan.Domain.Tests;

/// <summary>
/// The lexical containment judgement behind the circular-capture guard
/// (FR-DEST-011): whether one filesystem path lies at or under another,
/// decided from the strings alone — it must be callable from configuration
/// validation paths that are forbidden to touch the disk.
/// </summary>
[TestClass]
public sealed class PathContainmentTests
{
    [TestMethod]
    public void IsAtOrUnder_AChildPath_IsTrue()
    {
        Assert.IsTrue(PathContainment.IsAtOrUnder(P("/data"), P("/data/vault")));
        Assert.IsTrue(PathContainment.IsAtOrUnder(P("/data"), P("/data/deeper/vault")));
    }

    [TestMethod]
    public void IsAtOrUnder_ASiblingSharingANamePrefix_IsFalse()
    {
        // The classic prefix trap: '/data/docs-old' starts with '/data/docs'
        // as a string and is unrelated as a path. The trailing-separator
        // fence is what keeps these apart (the RestoreExecutor fence, and the
        // bug docs/review/2026-08 recorded).
        Assert.IsFalse(PathContainment.IsAtOrUnder(P("/data/docs"), P("/data/docs-old")));
        Assert.IsFalse(PathContainment.IsAtOrUnder(P("/data/docs-old"), P("/data/docs")));
    }

    [TestMethod]
    public void IsAtOrUnder_TheSamePath_IsTrue()
    {
        // Equality is the degenerate containment: a destination that IS the
        // root is captured in full.
        Assert.IsTrue(PathContainment.IsAtOrUnder(P("/data/vault"), P("/data/vault")));
    }

    [TestMethod]
    public void IsAtOrUnder_PathsDifferingOnlyInCase_IsTrue()
    {
        // Conservative folding, the MultiRootScan precedent: the direction
        // that never mistakes two spellings for two folders. A false positive
        // here refuses a save with a stated reason; a false negative captures
        // a backup into itself.
        Assert.IsTrue(PathContainment.IsAtOrUnder(P("/data"), P("/DATA/vault")));
    }

    [TestMethod]
    public void IsAtOrUnder_ARelativePath_IsNeverJudged()
    {
        // A relative path's meaning depends on a working directory this
        // judgement must not consult. Refusing to judge keeps configs
        // carrying the pre-normalisation defect loadable; the address defect
        // reports those.
        Assert.IsFalse(PathContainment.IsAtOrUnder("vault", P("/data/vault")));
        Assert.IsFalse(PathContainment.IsAtOrUnder(P("/data"), "vault"));
    }

    [TestMethod]
    public void IsAtOrUnder_ATrailingSeparator_IsIgnored()
    {
        Assert.IsTrue(PathContainment.IsAtOrUnder(P("/data") + Path.DirectorySeparatorChar, P("/data/vault")));
        Assert.IsTrue(PathContainment.IsAtOrUnder(P("/data"), P("/data/vault") + Path.DirectorySeparatorChar));
    }

    [TestMethod]
    public void IsAtOrUnder_LexicalDotSegments_AreNormalisedBeforeJudging()
    {
        Assert.IsTrue(PathContainment.IsAtOrUnder(P("/data"), P("/data/x/../vault")));
        Assert.IsFalse(PathContainment.IsAtOrUnder(P("/data"), P("/data/../elsewhere")));
    }

    /// <summary>A POSIX-spelled fixture path in the platform's own spelling.</summary>
    private static string P(string posix) =>
        OperatingSystem.IsWindows()
            ? "C:" + posix.Replace('/', Path.DirectorySeparatorChar)
            : posix;
}
