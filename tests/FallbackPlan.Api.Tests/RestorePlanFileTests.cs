using System.Globalization;
using System.Text.Json;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Api.Tests;

/// <summary>
/// A restore's plan kept in a file (FR-RST-003, ADR-0108 Amendment 1,
/// ADR-0113): one shape whichever client keeps it, the command line or the
/// console. It holds the run the plan was made for, in the contract's own
/// words, and the plan itself, field for field as <c>restore_plan</c> crosses
/// the wire. A plan made now is held to a kept one by a comparison both
/// clients share (ADR-0118).
/// </summary>
/// <remarks>
/// Version 2 keeps every field of version 1 with the meaning it had, and
/// adds what a run asks beside one path: several paths, its target, its
/// existing-file policy and whether it lands in place. A restore source is an
/// open handle that means nothing once it closes, so it is not kept. A kept
/// plan reads back as the run and the plan it holds, of either version, so a
/// run can be held to it (ADR-0114); anything else is refused by name.
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

    [TestMethod]
    public void AKeptPlan_ReadsBack_AsTheRunAndThePlanItHolds()
    {
        var kept = RestorePlanFile.Read(RestorePlanFile.Serialize(
            new PlanRestoreCommand(
                "0123456789abcdef", null, Source: "src-1", Paths: ["docs/a.txt"],
                OutputDirectory: "/srv/restored", Target: "folder", Existing: "rename", InPlace: true),
            new RestorePlanResult(2, 9_000, ["docs/b.txt"], Conflicts: 1, ArchivedObjects: ["docs/a.txt"]),
            At));

        Assert.AreEqual(RestorePlanFile.Version, kept.Version);
        Assert.AreEqual("0123456789abcdef", kept.Request.SnapshotId);
        SequenceAssert.AreEqual(["docs/a.txt"], kept.Request.Paths!);
        Assert.AreEqual("/srv/restored", kept.Request.OutputDirectory);
        Assert.AreEqual("folder", kept.Request.Target);
        Assert.AreEqual("rename", kept.Request.Existing);
        Assert.IsTrue(kept.Request.InPlace);
        Assert.IsNull(kept.Request.Source, "a source was never kept");
        Assert.AreEqual(2, kept.Plan.Files);
        SequenceAssert.AreEqual(["docs/b.txt"], kept.Plan.MissingObjects);
        SequenceAssert.AreEqual(["docs/a.txt"], kept.Plan.ArchivedObjects!);
        Assert.AreEqual(1, kept.Plan.Conflicts);
        Assert.AreEqual(At, kept.PlannedAt);
    }

    [TestMethod]
    public void AVersionOneFile_ReadsBack_AsARunOfOnePath()
    {
        var kept = RestorePlanFile.Read("""
            {"kind":"fallbackplan-restore-plan","version":1,"contract":"1.66","snapshot_id":"0123456789abcdef",
             "path":"docs","output_directory":"/srv/restored","planned_at":"2026-10-20T08:00:00.0000000+00:00",
             "plan":{"files":1,"bytes":42,"missing_objects":[]}}
            """);

        Assert.AreEqual(1, kept.Version);
        Assert.AreEqual("docs", kept.Request.Path);
        Assert.IsNull(kept.Request.Paths);
        Assert.IsNull(kept.Request.Target);
        Assert.IsFalse(kept.Request.InPlace);
        Assert.AreEqual(1, kept.Plan.Files);
    }

    [TestMethod]
    [DataRow("""{"kind":"fallbackplan-diagnostic-bundle","version":1}""", "not a kept restore plan")]
    [DataRow("""{"kind":"fallbackplan-restore-plan","version":3,"snapshot_id":"ab","plan":{"files":1,"bytes":1,"missing_objects":[]}}""", "version 3")]
    [DataRow("""{"kind":"fallbackplan-restore-plan","version":2,"snapshot_id":"ab"}""", "no plan")]
    [DataRow("""{ not json""", "not JSON")]
    public void AFileThatIsNotAKeptPlanThisBuildReads_IsRefused_SayingWhy(string text, string why)
    {
        var refused = Assert.ThrowsExactly<InvalidDataException>(() => RestorePlanFile.Read(text));

        Assert.Contains(why, refused.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void APlanMadeNow_HeldToAKeptOne_NamesEachChange_EachWay()
    {
        // ADR-0118: one comparison for both clients, so the console says what
        // changed in the words the command line says it (ADR-0114 §3).
        var then = new RestorePlanResult(3, 0, ["docs/a.txt"], Conflicts: 1, ArchivedObjects: ["docs/x.txt"]);
        var now = new RestorePlanResult(2, 0, ["docs/b.txt"], Conflicts: 0, ArchivedObjects: ["docs/y.txt"]);

        var changes = RestorePlanFile.Changes(then, now);

        CollectionAssert.AreEquivalent(
            new[]
            {
                "files: 3 kept, 2 now",
                "missing now: docs/b.txt",
                "missing no longer: docs/a.txt",
                "archived now: docs/y.txt",
                "archived no longer: docs/x.txt",
                "conflicts: 1 kept, 0 now",
            },
            changes.ToArray());
    }

    [TestMethod]
    public void APlanMadeNow_AsKept_ChangesNothing_EvenWhereItWouldFailFiles()
    {
        // What the kept plan already said would fail was approved, and is not news.
        var kept = new RestorePlanResult(2, 0, ["docs/a.txt"], Conflicts: 1, ArchivedObjects: ["docs/x.txt"]);

        Assert.IsEmpty(RestorePlanFile.Changes(kept, kept with { Bytes = 99, ReadBytes = 7 }));
    }
}
