using System.Text.Json;
using FallbackPlan.Domain;
using Xunit;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// Drives every <c>path-rules.json</c> case through the C# rules-v1
/// implementation (specification 06 §7.1; ADR-0024). The vectors are
/// produced by an independent pure-Python matcher inside the generator, so
/// green here means two implementations in two languages agree on every
/// committed case — the property the dialect exists to guarantee.
/// </summary>
public sealed class PathRulesConformanceTests
{
    private static JsonDocument Vectors { get; } =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "path-rules.json")));

    [Fact]
    public void Every_single_rule_match_case_agrees()
    {
        foreach (var vectorCase in Vectors.RootElement.GetProperty("match_cases").EnumerateArray())
        {
            var rule = vectorCase.GetProperty("rule").GetString()!;
            var path = vectorCase.GetProperty("path").GetString()!;
            var caseSensitive = vectorCase.GetProperty("case_sensitive").GetBoolean();

            Assert.True(
                PathRule.TryCreate(rule, caseSensitive, out var compiled, out var defect),
                $"rule '{rule}' failed to compile: {defect}");

            Assert.True(
                vectorCase.GetProperty("matches").GetBoolean() == compiled!.Matches(path),
                $"rule '{rule}' vs '{path}': expected {vectorCase.GetProperty("matches").GetBoolean()}");
        }
    }

    [Fact]
    public void Every_invalid_rule_is_refused_with_a_named_defect()
    {
        foreach (var vectorCase in Vectors.RootElement.GetProperty("invalid_cases").EnumerateArray())
        {
            var rule = vectorCase.GetProperty("rule").GetString()!;

            Assert.False(
                PathRule.TryCreate(rule, caseSensitive: true, out _, out var defect),
                $"invalid rule '{rule}' ({vectorCase.GetProperty("reason").GetString()}) unexpectedly compiled");
            Assert.False(string.IsNullOrEmpty(defect), $"invalid rule '{rule}' produced no named defect");
        }
    }

    [Fact]
    public void Every_evaluation_scenario_agrees_on_excluded_and_captured()
    {
        foreach (var scenario in Vectors.RootElement.GetProperty("evaluation_scenarios").EnumerateArray())
        {
            var name = scenario.GetProperty("name").GetString();
            var includes = scenario.GetProperty("includes").EnumerateArray().Select(rule => rule.GetString()!).ToList();
            var excludes = scenario.GetProperty("excludes").EnumerateArray().Select(rule => rule.GetString()!).ToList();
            var caseSensitive = scenario.GetProperty("case_sensitive").GetBoolean();

            Assert.True(
                PathRuleSet.TryCreate(includes, excludes, caseSensitive, out var rules, out var defects),
                $"scenario '{name}' rules failed: {string.Join("; ", defects)}");

            foreach (var expectation in scenario.GetProperty("paths").EnumerateArray())
            {
                var path = expectation.GetProperty("path").GetString()!;

                Assert.True(
                    expectation.GetProperty("excluded").GetBoolean() == rules!.IsExcluded(path),
                    $"scenario '{name}', path '{path}': excluded disagrees");
                Assert.True(
                    expectation.GetProperty("captured").GetBoolean() == rules.IsCaptured(path),
                    $"scenario '{name}', path '{path}': captured disagrees");
            }
        }
    }
}
