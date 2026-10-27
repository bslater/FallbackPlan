using System.Globalization;
using System.Text.Json;

namespace FallbackPlan.Api.Tests;

/// <summary>
/// A restore's plan kept in a file (FR-RST-003, ADR-0108 Amendment 1,
/// ADR-0113): one shape whichever client keeps it, the command line or the
/// console. It holds the run the plan was made for, in the contract's own
/// words, and the plan itself, field for field as <c>restore_plan</c> crosses
/// the wire.
/// </summary>
/// <remarks>
/// Version 2 keeps every field of version 1 with the meaning it had, and
/// adds what a run asks beside one path: several paths, its target, its
/// existing-file policy and whether it lands in place. A restore source is an
/// open handle that means nothing once it closes, so it is not kept.
/// </remarks>
[TestClass]
public sealed class RestorePlanFileTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-27T09:30:00Z", CultureInfo.InvariantCulture);

    [TestMethod]
    public void AKeptPlan_HoldsTheRunItWasMadeFor_AndThePlan_InTheContractsOwnWords()
    {
        var text = RestorePlanFile.Serialize(
            new PlanRestoreCommand(
                "0123456789abcdef", null, Source: "src-1", Paths: ["docs/a.txt", "photos"],
                OutputDirectory: "/srv/restored", Target: "folder", Existing: "rename", InPlace: true),
            new RestorePlanResult(2, 9_000, [], ArchivedObjects: ["docs/a.txt"]),
            At);

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        Assert.AreEqual("fallbackplan-restore-plan", root.GetProperty("kind").GetString());
        Assert.AreEqual(2, root.GetProperty("version").GetInt32());
        Assert.AreEqual(ContractVersion.Current.ToString(), root.GetProperty("contract").GetString());
        Assert.AreEqual("0123456789abcdef", root.GetProperty("snapshot_id").GetString());
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("path").ValueKind);
        CollectionAssert.AreEqual(
            new[] { "docs/a.txt", "photos" },
            root.GetProperty("paths").EnumerateArray().Select(path => path.GetString()).ToArray());
        Assert.AreEqual("/srv/restored", root.GetProperty("output_directory").GetString());
        Assert.AreEqual("folder", root.GetProperty("target").GetString());
        Assert.AreEqual("rename", root.GetProperty("existing").GetString());
        Assert.IsTrue(root.GetProperty("in_place").GetBoolean());
        Assert.AreEqual("2026-10-27T09:30:00.0000000+00:00", root.GetProperty("planned_at").GetString());
        Assert.AreEqual(2, root.GetProperty("plan").GetProperty("files").GetInt64());
        Assert.AreEqual("docs/a.txt", root.GetProperty("plan").GetProperty("archived_objects")[0].GetString());
        Assert.DoesNotContain("src-1", text, StringComparison.Ordinal);
        Assert.IsFalse(root.TryGetProperty("source", out _), "a source is an open handle, meaningless once it closes");
    }

    [TestMethod]
    public void APlanOfOnePath_KeepsTheFieldsVersionOneHad_WithTheirMeaning()
    {
        // A script written against version 1 reads the same fields the same
        // way: one path, where the run writes, and the plan.
        using var document = JsonDocument.Parse(RestorePlanFile.Serialize(
            new PlanRestoreCommand("0123456789abcdef", "docs", OutputDirectory: "/srv/restored"),
            new RestorePlanResult(1, 42, []),
            At));
        var root = document.RootElement;

        Assert.AreEqual("docs", root.GetProperty("path").GetString());
        Assert.AreEqual("/srv/restored", root.GetProperty("output_directory").GetString());
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("paths").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("target").ValueKind);
        Assert.IsFalse(root.GetProperty("in_place").GetBoolean());
        Assert.AreEqual(1, root.GetProperty("plan").GetProperty("files").GetInt64());
    }
}
