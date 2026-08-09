using FallbackPlan.TestSupport;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// The rules-v1 dialect directly (specification 06 §7.1; ADR-0024) — the
/// semantics the conformance vectors freeze, plus properties no enumerated
/// vector can cover. Cross-language agreement on the committed cases is
/// <c>PathRulesConformanceTests</c>' territory.
/// </summary>
[TestClass]
public sealed class PathRulesTests
{
    private static PathRule Compile(string rule, bool caseSensitive = true)
    {
        Assert.IsTrue(PathRule.TryCreate(rule, caseSensitive, out var compiled, out var defect), defect);
        return compiled!;
    }

    [TestMethod]
    [DataRow("*.log", "a.log", true)]
    [DataRow("*.log", "dir/a.log", true)]
    [DataRow("*.log", "a.log/inner", false)]
    [DataRow("src/**", "src", false)]
    [DataRow("src/**", "src/deep/file", true)]
    [DataRow("a/**/b", "a/b", true)]
    [DataRow("a/**/b", "a/x/y/b", true)]
    [DataRow("?", "x", true)]
    [DataRow("?", "xy", false)]
    public void Glob_semantics_hold(string rule, string path, bool expected) =>
        Assert.AreEqual(expected, Compile(rule).Matches(path));

    [TestMethod]
    [DataRow("")]
    [DataRow("/leading")]
    [DataRow("trailing/")]
    [DataRow("a//b")]
    [DataRow("a**b")]
    [DataRow("**.log")]
    [DataRow("re:^anchored")]
    [DataRow(@"re:\d+")]
    [DataRow("re:(?:group)")]
    [DataRow(@"re:(a)\1")]
    [DataRow("re:brace{x")]
    public void Invalid_rules_are_refused_by_name(string rule)
    {
        Assert.IsFalse(PathRule.TryCreate(rule, caseSensitive: true, out _, out var defect));
        Assert.IsFalse(string.IsNullOrEmpty(defect));
    }

    [TestMethod]
    public void PathRuleSet_ExcludeAndIncludeBothMatch_ExcludesAndPrunesTheSubtree()
    {
        Assert.IsTrue(PathRuleSet.TryCreate(
            includeRules: ["build/keep/**"], excludeRules: ["build"], caseSensitive: true,
            out var rules, out _));

        Assert.IsTrue(rules!.IsExcluded("build/keep/artefact"));
        Assert.IsFalse(rules.IsCaptured("build/keep/artefact"));
        Assert.IsFalse(rules.MayDescend("build"));
    }

    [TestMethod]
    public void TryCreate_WhenSeveralRulesAreInvalid_ShouldReportEachByRuleAndReason()
    {
        Assert.IsFalse(PathRuleSet.TryCreate(
            includeRules: ["ok/**", "a**b"], excludeRules: ["re:^bad"], caseSensitive: true,
            out var rules, out var defects));

        Assert.IsNull(rules);
        Assert.AreEqual(2, defects.Count);
        Assert.Contains(defect => defect.Contains("a**b", StringComparison.Ordinal), defects);
        Assert.Contains(defect => defect.Contains("re:^bad", StringComparison.Ordinal), defects);
    }

    /// <summary>
    /// The no-slash shorthand is exactly <c>**/&lt;rule&gt;</c> — the property
    /// version of the anchoring rule, over generated component paths.
    /// </summary>
    [TestMethod]
    public void PathRule_WrittenWithoutASlash_MatchesAsItsExplicitExpansion() =>
        PropertyCheck.Holds(this, maxTest: 200);

    public static bool PathRule_WrittenWithoutASlash_MatchesAsItsExplicitExpansionProperty(byte[]? seeds, byte nameSeed)
    {
        var name = "f" + (char)('a' + nameSeed % 26) + ".txt";
        var components = (seeds ?? [])
            .Take(6)
            .Select(seed => ((char)('a' + seed % 26)).ToString())
            .ToArray();
        var path = string.Join('/', components.Append(name));

        var shorthand = Compile(name);
        var explicitRule = Compile("**/" + name);

        return shorthand.Matches(path) == explicitRule.Matches(path)
            && shorthand.Matches(path);
    }

    [TestMethod]
    public void PathRuleSet_CaseInsensitiveMatching_EqualsCaseSensitiveMatchingAfterFolding() =>
        PropertyCheck.Holds(this, maxTest: 200);

    public static bool PathRuleSet_CaseInsensitiveMatching_EqualsCaseSensitiveMatchingAfterFoldingProperty(byte[]? seeds)
    {
        var name = string.Concat((seeds ?? [1]).Take(8).Select(seed => (char)('A' + seed % 26)));
        if (name.Length == 0)
        {
            return true;
        }

        var insensitive = Compile(name, caseSensitive: false);

        return insensitive.Matches(name.ToLowerInvariant())
            && insensitive.Matches(name.ToUpperInvariant());
    }
}
