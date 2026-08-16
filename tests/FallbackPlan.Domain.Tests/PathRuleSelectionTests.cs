namespace FallbackPlan.Domain.Tests;

/// <summary>
/// Which files a rule set selects, asked adversarially (specification 06 §7.1).
/// </summary>
/// <remarks>
/// <para>
/// Duplicati shipped "a case where purging a single file would purge all
/// <em>other</em> files, if the filesystem is case-sensitive" (2019-10-19) and,
/// three months later, "fix purge operation with simple filters and
/// case-sensitive filesystems" (2020-01-18). That is a selection <em>inversion</em>:
/// the rule set was asked which file to act on and answered with its
/// complement. In a purge it destroys everything the user meant to keep.
/// </para>
/// <para>
/// It shipped alongside "backing up a folder and a subfolder would lead to all
/// files being excluded" (2016-10-27) — the same shape from the other side, two
/// overlapping selections combining to select nothing instead of their union.
/// </para>
/// <para>
/// Neither can be caught by testing that a rule matches what it should. They
/// are caught by testing what a rule does to <em>everything else</em>, which is
/// what this class does: every case asserts both the intended member and a
/// non-member, so an inverted answer fails rather than passing half the
/// assertions.
/// </para>
/// </remarks>
[TestClass]
public sealed class PathRuleSelectionTests
{
    private static PathRuleSet Compile(string[] includes, string[] excludes, bool caseSensitive)
    {
        Assert.IsTrue(
            PathRuleSet.TryCreate(includes, excludes, caseSensitive, out var rules, out var defects),
            string.Join("; ", defects));
        return rules!;
    }

    /// <summary>
    /// A rule naming one file excludes that file and leaves every other one
    /// alone — under both case regimes, because the Duplicati defect appeared
    /// only on case-sensitive filesystems.
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void AnExcludeNamingOneFile_LeavesEveryOtherFileAlone(bool caseSensitive)
    {
        var rules = Compile([], ["work/report.txt"], caseSensitive);

        Assert.IsTrue(rules.IsExcluded("work/report.txt"), "the named file was not excluded");

        // The inversion: everything that is not the named file must survive.
        foreach (var survivor in new[]
                 {
                     "work/other.txt", "work/report.txt.bak", "work/sub/report.txt",
                     "report.txt", "elsewhere/report.txt", "work", "work/sub",
                 })
        {
            Assert.IsFalse(rules.IsExcluded(survivor), $"'{survivor}' was excluded by a rule naming another file");
            Assert.IsTrue(rules.IsCaptured(survivor), $"'{survivor}' was not captured");
        }
    }

    /// <summary>
    /// Case-insensitive matching widens what a rule reaches; it must not widen
    /// it to everything. A fold that went wrong in the permissive direction
    /// would show here and nowhere else.
    /// </summary>
    [TestMethod]
    public void ACaseInsensitiveExclude_ReachesTheCaseVariantsAndStopsThere()
    {
        var rules = Compile([], ["*.BAK"], caseSensitive: false);

        foreach (var excluded in new[] { "notes.bak", "notes.BAK", "notes.BaK", "deep/notes.bak" })
        {
            Assert.IsTrue(rules.IsExcluded(excluded), $"'{excluded}' escaped a case-insensitive exclude");
        }

        foreach (var survivor in new[] { "notes.txt", "notes.bakx", "bak", "notes.bak.txt", "backup/notes.txt" })
        {
            Assert.IsFalse(rules.IsExcluded(survivor), $"'{survivor}' was swept up by a case-insensitive exclude");
        }
    }

    /// <summary>
    /// And case-sensitive matching narrows it — to exactly the spelling given,
    /// and not to nothing and not to everything.
    /// </summary>
    [TestMethod]
    public void ACaseSensitiveExclude_ReachesOnlyTheExactSpelling()
    {
        var rules = Compile([], ["*.BAK"], caseSensitive: true);

        Assert.IsTrue(rules.IsExcluded("notes.BAK"));
        Assert.IsFalse(rules.IsExcluded("notes.bak"), "a case-sensitive rule matched a different spelling");
        Assert.IsFalse(rules.IsExcluded("notes.txt"));
    }

    /// <summary>
    /// Two overlapping includes are a union, not an intersection. Duplicati's
    /// "a folder and a subfolder leads to all files being excluded" is what an
    /// intersection produces.
    /// </summary>
    [TestMethod]
    public void TwoIncludesWhereOneContainsTheOther_CaptureTheirUnion()
    {
        var rules = Compile(["docs/**", "docs/reports/**"], [], caseSensitive: true);

        foreach (var captured in new[]
                 {
                     "docs/notes.txt", "docs/reports/q1.txt", "docs/reports/deep/q2.txt", "docs/other/x.txt",
                 })
        {
            Assert.IsTrue(rules.IsCaptured(captured), $"'{captured}' was lost by two overlapping includes");
        }

        // And the union is still a boundary, not everything.
        Assert.IsFalse(rules.IsCaptured("music/song.mp3"));
    }

    /// <summary>
    /// The same shape for excludes: a subtree exclude plus a narrower one
    /// inside it removes the subtree, and nothing outside it.
    /// </summary>
    [TestMethod]
    public void TwoExcludesWhereOneContainsTheOther_RemoveTheirUnionAndNoMore()
    {
        var rules = Compile([], ["cache/**", "cache/inner/**"], caseSensitive: true);

        Assert.IsTrue(rules.IsExcluded("cache/a.tmp"));
        Assert.IsTrue(rules.IsExcluded("cache/inner/b.tmp"));
        Assert.IsFalse(rules.IsExcluded("cached.txt"), "a prefix that is not a component was swept up");
        Assert.IsTrue(rules.IsCaptured("docs/keep.txt"));
    }

    /// <summary>
    /// An include and an exclude naming the same path: exclude wins, and it
    /// wins only for that path. This is 06 §7.1's stated precedence, and the
    /// case where a wrong answer silently drops one file out of a set the
    /// operator believes is fully covered.
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void AnIncludeAndAnExcludeNamingTheSamePath_ExcludeWinsForThatPathOnly(bool caseSensitive)
    {
        var rules = Compile(["work/**"], ["work/secret.txt"], caseSensitive);

        Assert.IsFalse(rules.IsCaptured("work/secret.txt"), "exclude did not win");
        Assert.IsTrue(rules.IsCaptured("work/public.txt"), "exclude took a neighbour with it");
        Assert.IsTrue(rules.IsCaptured("work/sub/deep.txt"));
    }

    /// <summary>
    /// An empty include list captures everything not excluded. Stated because
    /// the opposite reading — an empty list captures nothing — turns a whole
    /// backup set into an empty one, and both readings are defensible until
    /// somebody writes it down.
    /// </summary>
    [TestMethod]
    public void AnEmptyIncludeList_CapturesEverythingNotExcluded()
    {
        var rules = Compile([], ["*.tmp"], caseSensitive: true);

        Assert.IsTrue(rules.IsCaptured("anything.txt"));
        Assert.IsTrue(rules.IsCaptured("deep/nested/thing.bin"));
        Assert.IsFalse(rules.IsCaptured("scratch.tmp"));
    }

    /// <summary>
    /// A rule set with no rules at all captures everything and excludes
    /// nothing — the degenerate end, where an inversion is total.
    /// </summary>
    [TestMethod]
    public void ARuleSetWithNoRules_CapturesEverything()
    {
        var rules = Compile([], [], caseSensitive: true);

        foreach (var path in new[] { "a", "a/b", "a/b/c.txt", "z.bin" })
        {
            Assert.IsTrue(rules.IsCaptured(path));
            Assert.IsFalse(rules.IsExcluded(path));
            Assert.IsTrue(rules.MayDescend(path));
        }
    }

    /// <summary>
    /// Excluding a directory prunes its subtree and stops at the boundary —
    /// `cache` must not take `cache-of-things` with it. A prefix match where a
    /// component match was meant is the quiet form of over-selection.
    /// </summary>
    [TestMethod]
    public void ExcludingADirectory_PrunesItsSubtreeAndStopsAtTheComponentBoundary()
    {
        var rules = Compile([], ["build"], caseSensitive: true);

        Assert.IsTrue(rules.IsExcluded("build"));
        Assert.IsTrue(rules.IsExcluded("build/out/app.exe"));
        Assert.IsFalse(rules.MayDescend("build"));

        foreach (var survivor in new[] { "builder", "build-output/x", "src/build.txt", "prebuild" })
        {
            Assert.IsFalse(rules.IsExcluded(survivor), $"'{survivor}' was pruned by a rule naming 'build'");
        }
    }
}
