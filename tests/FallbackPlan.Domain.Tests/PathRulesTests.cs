using FsCheck.Xunit;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// The rules-v1 dialect directly (specification 06 §7.1; ADR-0024) — the
/// semantics the conformance vectors freeze, plus properties no enumerated
/// vector can cover. Cross-language agreement on the committed cases is
/// <c>PathRulesConformanceTests</c>' territory.
/// </summary>
public sealed class PathRulesTests
{
    private static PathRule Compile(string rule, bool caseSensitive = true)
    {
        Assert.True(PathRule.TryCreate(rule, caseSensitive, out var compiled, out var defect), defect);
        return compiled!;
    }

    [Theory]
    [InlineData("*.log", "a.log", true)]
    [InlineData("*.log", "dir/a.log", true)]
    [InlineData("*.log", "a.log/inner", false)]
    [InlineData("src/**", "src", false)]
    [InlineData("src/**", "src/deep/file", true)]
    [InlineData("a/**/b", "a/b", true)]
    [InlineData("a/**/b", "a/x/y/b", true)]
    [InlineData("?", "x", true)]
    [InlineData("?", "xy", false)]
    public void Glob_semantics_hold(string rule, string path, bool expected) =>
        Assert.Equal(expected, Compile(rule).Matches(path));

    [Theory]
    [InlineData("")]
    [InlineData("/leading")]
    [InlineData("trailing/")]
    [InlineData("a//b")]
    [InlineData("a**b")]
    [InlineData("**.log")]
    [InlineData("re:^anchored")]
    [InlineData(@"re:\d+")]
    [InlineData("re:(?:group)")]
    [InlineData(@"re:(a)\1")]
    [InlineData("re:brace{x")]
    public void Invalid_rules_are_refused_by_name(string rule)
    {
        Assert.False(PathRule.TryCreate(rule, caseSensitive: true, out _, out var defect));
        Assert.False(string.IsNullOrEmpty(defect));
    }

    [Fact]
    public void Exclude_wins_over_include_and_prunes_the_subtree()
    {
        Assert.True(PathRuleSet.TryCreate(
            includeRules: ["build/keep/**"], excludeRules: ["build"], caseSensitive: true,
            out var rules, out _));

        Assert.True(rules!.IsExcluded("build/keep/artefact"));
        Assert.False(rules.IsCaptured("build/keep/artefact"));
        Assert.False(rules.MayDescend("build"));
    }

    [Fact]
    public void A_rule_set_with_defects_reports_every_one_by_rule_and_reason()
    {
        Assert.False(PathRuleSet.TryCreate(
            includeRules: ["ok/**", "a**b"], excludeRules: ["re:^bad"], caseSensitive: true,
            out var rules, out var defects));

        Assert.Null(rules);
        Assert.Equal(2, defects.Count);
        Assert.Contains(defects, defect => defect.Contains("a**b", StringComparison.Ordinal));
        Assert.Contains(defects, defect => defect.Contains("re:^bad", StringComparison.Ordinal));
    }

    /// <summary>
    /// The no-slash shorthand is exactly <c>**/&lt;rule&gt;</c> — the property
    /// version of the anchoring rule, over generated component paths.
    /// </summary>
    [Property(MaxTest = 200)]
    public bool A_slashless_rule_is_equivalent_to_its_explicit_expansion(byte[]? seeds, byte nameSeed)
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

    [Property(MaxTest = 200)]
    public bool Case_insensitive_matching_is_case_sensitive_matching_after_folding(byte[]? seeds)
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
