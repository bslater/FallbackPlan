using System.Text.Json;
using FallbackPlan.Domain;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// Drives every <c>path-rules.json</c> case through the C# rules-v1
/// implementation (specification 06 §7.1; ADR-0024). The vectors are
/// produced by an independent pure-Python matcher inside the generator, so
/// green here means two implementations in two languages agree on every
/// committed case — the property the dialect exists to guarantee.
/// </summary>
[TestClass]
public sealed class PathRulesConformanceTests
{
    private static JsonDocument Vectors { get; } =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "path-rules.json")));

    [TestMethod]
    public void PathRuleMatching_EveryCommittedCase_Agrees()
    {
        foreach (var vectorCase in Vectors.RootElement.GetProperty("match_cases").EnumerateArray())
        {
            var rule = vectorCase.GetProperty("rule").GetString()!;
            var path = vectorCase.GetProperty("path").GetString()!;
            var caseSensitive = vectorCase.GetProperty("case_sensitive").GetBoolean();

            Assert.IsTrue(
                PathRule.TryCreate(rule, caseSensitive, out var compiled, out var defect),
                $"rule '{rule}' failed to compile: {defect}");

            Assert.IsTrue(
                vectorCase.GetProperty("matches").GetBoolean() == compiled!.Matches(path),
                $"rule '{rule}' vs '{path}': expected {vectorCase.GetProperty("matches").GetBoolean()}");
        }
    }

    [TestMethod]
    public void PathRuleValidation_EveryCommittedInvalidRule_IsRefusedWithANamedDefect()
    {
        foreach (var vectorCase in Vectors.RootElement.GetProperty("invalid_cases").EnumerateArray())
        {
            var rule = vectorCase.GetProperty("rule").GetString()!;

            Assert.IsFalse(
                PathRule.TryCreate(rule, caseSensitive: true, out _, out var defect),
                $"invalid rule '{rule}' ({vectorCase.GetProperty("reason").GetString()}) unexpectedly compiled");
            Assert.IsFalse(string.IsNullOrEmpty(defect), $"invalid rule '{rule}' produced no named defect");
        }
    }

    [TestMethod]
    public void PathRuleEvaluation_EveryCommittedScenario_AgreesOnExcludedAndCaptured()
    {
        foreach (var scenario in Vectors.RootElement.GetProperty("evaluation_scenarios").EnumerateArray())
        {
            var name = scenario.GetProperty("name").GetString();
            var includes = scenario.GetProperty("includes").EnumerateArray().Select(rule => rule.GetString()!).ToList();
            var excludes = scenario.GetProperty("excludes").EnumerateArray().Select(rule => rule.GetString()!).ToList();
            var caseSensitive = scenario.GetProperty("case_sensitive").GetBoolean();

            Assert.IsTrue(
                PathRuleSet.TryCreate(includes, excludes, caseSensitive, out var rules, out var defects),
                $"scenario '{name}' rules failed: {string.Join("; ", defects)}");

            foreach (var expectation in scenario.GetProperty("paths").EnumerateArray())
            {
                var path = expectation.GetProperty("path").GetString()!;

                Assert.IsTrue(
                    expectation.GetProperty("excluded").GetBoolean() == rules!.IsExcluded(path),
                    $"scenario '{name}', path '{path}': excluded disagrees");
                Assert.IsTrue(
                    expectation.GetProperty("captured").GetBoolean() == rules.IsCaptured(path),
                    $"scenario '{name}', path '{path}': captured disagrees");
            }
        }
    }
}
