using System.Text.Json;
using FallbackPlan.Restore;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// A run's receipt is kept (FR-RST-004, ADR-0122): as its run's own file under
/// the folder a state directory keeps receipts in, written whole or not at
/// all, so the service and the command line's direct mode keep it alike and
/// "the restore succeeded" stays a claim a person can check. At schema 9 it
/// names the set whose snapshot its run restored, where the run knew it
/// (ADR-0126), and says nothing of a set where it did not.
/// </summary>
[TestClass]
public sealed class RestoreReceiptKeptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fbp-receipts", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void AReceiptKept_IsItsRunsOwnFile_AndReadsBackAsWritten()
    {
        var receipts = Path.Combine(_root, "receipts");

        var path = ReceiptOf("notes.txt").Keep(receipts, "0123456789abcdef");

        Assert.AreEqual(Path.Combine(receipts, "0123456789abcdef.json"), path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.AreEqual("notes.txt", document.RootElement.GetProperty("items")[0].GetProperty("path").GetString());
        Assert.AreEqual("/srv/restored", document.RootElement.GetProperty("written_to").GetString());
        CollectionAssert.AreEqual(new[] { path }, Directory.GetFiles(receipts), "nothing is left beside it");
    }

    [TestMethod]
    public void AReceiptKeptAgainUnderItsRun_IsReplacedWhole()
    {
        var receipts = Path.Combine(_root, "receipts");
        ReceiptOf("first.txt").Keep(receipts, "0123456789abcdef");

        var path = ReceiptOf("second.txt").Keep(receipts, "0123456789abcdef");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.AreEqual("second.txt", document.RootElement.GetProperty("items")[0].GetProperty("path").GetString());
        Assert.HasCount(1, Directory.GetFiles(receipts));
    }

    [TestMethod]
    public void AReceiptNamesTheSetItsRunRestored_AtSchemaNine_AndOneThatKnewNoneSaysNothingOfOne()
    {
        var receipts = Path.Combine(_root, "receipts");

        var named = (ReceiptOf("notes.txt") with { Set = "docs" }).Keep(receipts, "0123456789abcdef");
        var unnamed = ReceiptOf("notes.txt").Keep(receipts, "fedcba9876543210");

        Assert.AreEqual(9, RestoreReceipt.CurrentSchemaVersion);
        using (var document = JsonDocument.Parse(File.ReadAllText(named)))
        {
            Assert.AreEqual("docs", document.RootElement.GetProperty("set").GetString());
        }

        using (var document = JsonDocument.Parse(File.ReadAllText(unnamed)))
        {
            Assert.IsFalse(document.RootElement.TryGetProperty("set", out _), "a run that knew no set names none");
        }

        var kept = RestoreReceipt.ReadKept(receipts);
        Assert.AreEqual("docs", kept.Single(entry => entry.Run == "0123456789abcdef").Receipt!.Set);
        Assert.IsNull(kept.Single(entry => entry.Run == "fedcba9876543210").Receipt!.Set);
    }

    private static RestoreReceipt ReceiptOf(string path) => new()
    {
        SchemaVersion = RestoreReceipt.CurrentSchemaVersion,
        SnapshotId = new string('e', 32),
        StartedAt = 10,
        CompletedAt = 20,
        Items = [new ReceiptItem { Path = path, Outcome = "restored", Bytes = 5 }],
        Displaced = [],
        WrittenTo = "/srv/restored",
        Outcome = RestoreOutcome.Complete,
    };
}
