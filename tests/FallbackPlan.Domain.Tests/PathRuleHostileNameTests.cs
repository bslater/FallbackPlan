using FallbackPlan.TestSupport;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// Rules against names chosen to be hostile to the regex the rules compile
/// into (specification 06 §7.1; ADR-0024).
/// </summary>
/// <remarks>
/// <para>
/// This class exists because of Duplicati's release notes. "Fixed an issue
/// with filenames containing a dollar sign" is a shipped fix there, and it
/// shipped <em>twice</em> — once in a canary, again in a later stable — as are
/// separate fixes for filter handling and for path comparison. A backup tool's
/// exclude list is a promise, and a filename is attacker-influenced input in
/// any shared directory; a name that slips past an exclude rule is a file
/// copied off the machine that the operator said never to copy.
/// </para>
/// <para>
/// Two distinct hazards are covered. A rule <em>containing</em> a
/// metacharacter must match that literal name and nothing else — the glob
/// compiler escapes, and this pins that it does. A <em>path</em> containing a
/// character the compiled regex treats specially must still be reached by a
/// rule that plainly covers it — which is where the newline cases below
/// belong, because <c>.</c> and <c>$</c> both have opinions about newlines
/// that filenames do not share.
/// </para>
/// </remarks>
[TestClass]
public sealed class PathRuleHostileNameTests
{
    private static PathRule Compile(string rule)
    {
        Assert.IsTrue(PathRule.TryCreate(rule, caseSensitive: true, out var compiled, out var defect), defect);
        return compiled!;
    }

    private static PathRuleSet CompileSet(string[] includes, string[] excludes)
    {
        Assert.IsTrue(
            PathRuleSet.TryCreate(includes, excludes, caseSensitive: true, out var rules, out var defects),
            string.Join("; ", defects));
        return rules!;
    }

    /// <summary>
    /// Every regex metacharacter that is legal in a POSIX filename, used as a
    /// literal in a glob rule. <c>*</c> and <c>?</c> are absent because they
    /// are the dialect's own wildcards; <c>/</c> and NUL are absent because
    /// they cannot appear in a name.
    /// </summary>
    [TestMethod]
    [DataRow("$")]
    [DataRow(".")]
    [DataRow("(")]
    [DataRow(")")]
    [DataRow("+")]
    [DataRow("|")]
    [DataRow("^")]
    [DataRow("{")]
    [DataRow("}")]
    [DataRow("[")]
    [DataRow("]")]
    [DataRow("\\")]
    [DataRow("#")]
    [DataRow(" ")]
    public void AGlobRuleHoldingAMetacharacter_MatchesThatNameAndNoOther(string metacharacter)
    {
        var rule = Compile($"a{metacharacter}b.txt");

        Assert.IsTrue(rule.Matches($"a{metacharacter}b.txt"), "the literal name the operator wrote");
        Assert.IsFalse(rule.Matches("axb.txt"), "the metacharacter was left live and matched something else");
        Assert.IsFalse(rule.Matches("ab.txt"), "the metacharacter was left live and matched something else");
    }

    /// <summary>
    /// The dollar sign specifically, in both directions and through the set —
    /// the name Duplicati shipped a fix for twice. A trailing dollar is the
    /// interesting placement: as a regex it is the end anchor.
    /// </summary>
    [TestMethod]
    public void ADollarSignInAName_IsALiteralInBothDirections()
    {
        var rules = CompileSet([], ["**/*$"]);

        Assert.IsTrue(rules.IsExcluded("home/notes$"), "an exclude naming a trailing $ must reach it");
        Assert.IsFalse(rules.IsCaptured("home/notes$"));
        Assert.IsTrue(rules.IsCaptured("home/notes"), "the $ must not evaporate into an end anchor");
        Assert.IsTrue(rules.IsCaptured("home/$notes"));
    }

    /// <summary>
    /// A newline is legal in a POSIX filename and is the classic way to hide
    /// one. A subtree exclude must reach it: <c>secrets/**</c> means every
    /// descendant, with no exception carved out for names the pattern
    /// language finds awkward.
    /// </summary>
    [TestMethod]
    [DataRow("secrets/ssh\nkey")]
    [DataRow("secrets/inner/ssh\nkey")]
    [DataRow("secrets/two\nline\nname")]
    [DataRow("secrets/trailing\n")]
    public void ASubtreeExcludeReachesADescendantWithANewlineInItsName(string path)
    {
        var rules = CompileSet([], ["secrets/**"]);

        Assert.IsTrue(rules.IsExcluded(path), $"'{path.Replace("\n", "\\n", StringComparison.Ordinal)}' escaped the exclude");
        Assert.IsFalse(rules.IsCaptured(path));
    }

    /// <summary>
    /// The same hole seen through a wildcard rather than a subtree: a name
    /// whose extension is excluded is excluded whatever else is in the name.
    /// </summary>
    [TestMethod]
    public void AWildcardExcludeReachesANameWithANewlineInIt()
    {
        var rules = CompileSet([], ["*.key"]);

        Assert.IsTrue(rules.IsExcluded("host\nname.key"));
        Assert.IsTrue(rules.IsExcluded("dir/host\nname.key"));
    }

    /// <summary>
    /// The other side of the newline problem: a rule must match the whole
    /// path, and a regex end anchor is happy to stop one character early when
    /// that character is a newline. Two different files must not be one file
    /// to the rule set.
    /// </summary>
    [TestMethod]
    [DataRow("keep.txt", "keep.txt\n")]
    [DataRow("dir/keep.txt", "dir/keep.txt\n")]
    public void ARuleMatchesTheWholeName_NotAPrefixEndingBeforeANewline(string rule, string longerName)
    {
        var compiled = Compile(rule);

        Assert.IsTrue(compiled.Matches(rule));
        Assert.IsFalse(
            compiled.Matches(longerName),
            "a name with a trailing newline is a different name; the end anchor let it through");
    }

    /// <summary>
    /// An include list is a scope, and the same anchor slip widens it: a rule
    /// naming one file must not draw in a neighbour whose name merely starts
    /// with it.
    /// </summary>
    [TestMethod]
    public void AnIncludeNamingOneFile_DoesNotDrawInANeighbourEndingInANewline()
    {
        var rules = CompileSet(["work/report.txt"], []);

        Assert.IsTrue(rules.IsCaptured("work/report.txt"));
        Assert.IsFalse(rules.IsCaptured("work/report.txt\n"));
    }

    /// <summary>
    /// Case-insensitive matching is Unicode simple case folding (06 §7.1),
    /// which is a fixed mapping — not the operator's locale. In Turkish
    /// <c>I</c> lowercases to the dotless <c>ı</c>, so a locale-sensitive fold
    /// would stop an exclude reaching a file on a Turkish machine and start it
    /// reaching a different one. The rule set is compiled once and shared, so
    /// which machine ran it must not be part of the answer.
    /// </summary>
    [TestMethod]
    public void CaseInsensitiveMatchingFoldsTheSameWay_WhateverTheOperatorsLocale()
    {
        foreach (var culture in CultureScope.Hostile)
        {
            using var scope = new CultureScope(culture);

            Assert.IsTrue(
                PathRule.TryCreate("INFO.LOG", caseSensitive: false, out var rule, out var defect), defect);

            Assert.IsTrue(rule!.Matches("info.log"), $"ASCII case folding broke under {culture}");
            Assert.IsTrue(rule.Matches("InFo.LoG"), culture);

            // The dotless ı is a different letter, not a lowercase I. Only a
            // Turkish fold would call these the same file.
            Assert.IsFalse(rule.Matches("ınfo.log"), $"a Turkish fold leaked in under {culture}");

            // And the dotted capital İ likewise.
            Assert.IsFalse(rule.Matches("İnfo.log"), $"a Turkish fold leaked in under {culture}");
        }
    }
}
