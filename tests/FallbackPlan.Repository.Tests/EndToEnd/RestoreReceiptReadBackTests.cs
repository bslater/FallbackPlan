using FallbackPlan.Restore;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The receipts a state directory keeps are read back (FR-RST-004, ADR-0124):
/// each run's own file, newest first, as it was kept. A file under a run's
/// name that does not read as a receipt is listed as unreadable rather than
/// hiding the rest, and what is not a run's receipt, the peers' receipts in
/// the folders beside included, is passed over. A run's receipt is let go by
/// the run's name alone, and nothing that is not a run's name reaches a path
/// (ADR-0139).
/// </summary>
[TestClass]
public sealed class RestoreReceiptReadBackTests : IDisposable
{
    private readonly string _receipts =
        Path.Combine(Path.GetTempPath(), "fbp-receipts-read", Guid.NewGuid().ToString("n"), "receipts");

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_receipts)!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void TheReceiptsKept_ReadBackNewestFirst_EachUnderItsRun()
    {
        var older = ReceiptOf("older.txt", completedAt: 20).Keep(_receipts, "0123456789abcdef");
        var newer = ReceiptOf("newer.txt", completedAt: 40).Keep(_receipts, "fedcba9876543210");

        var kept = RestoreReceipt.ReadKept(_receipts);

        Assert.HasCount(2, kept);
        Assert.AreEqual("fedcba9876543210", kept[0].Run);
        Assert.AreEqual(newer, kept[0].Path);
        Assert.AreEqual("newer.txt", Assert.ContainsSingle(kept[0].Receipt!.Items).Path);
        Assert.IsNull(kept[0].Problem);
        Assert.AreEqual("0123456789abcdef", kept[1].Run);
        Assert.AreEqual(older, kept[1].Path);
        Assert.AreEqual(20UL, kept[1].Receipt!.CompletedAt);
        Assert.AreEqual(RestoreOutcome.Complete, kept[1].Receipt!.Outcome);
    }

    [TestMethod]
    public void AFileUnderARunsNameThatIsNoReceipt_IsUnreadable_AndTheRestStillRead()
    {
        ReceiptOf("kept.txt", completedAt: 20).Keep(_receipts, "0123456789abcdef");
        File.WriteAllText(Path.Combine(_receipts, "fedcba9876543210.json"), "{ not a receipt");

        var kept = RestoreReceipt.ReadKept(_receipts);

        Assert.HasCount(2, kept);
        var unreadable = kept.Single(entry => entry.Run == "fedcba9876543210");
        Assert.IsNull(unreadable.Receipt);
        Assert.IsFalse(string.IsNullOrWhiteSpace(unreadable.Problem));
        Assert.AreEqual("kept.txt", kept.Single(entry => entry.Run == "0123456789abcdef").Receipt!.Items[0].Path);
    }

    [TestMethod]
    public void WhatIsNotARunsReceipt_IsPassedOver()
    {
        ReceiptOf("kept.txt", completedAt: 20).Keep(_receipts, "0123456789abcdef");
        File.WriteAllText(Path.Combine(_receipts, "fedcba9876543210.json.tmp"), "{ half");
        File.WriteAllText(Path.Combine(_receipts, "fedcba9876543210.txt"), "{}");
        File.WriteAllText(Path.Combine(_receipts, "notes.json"), "{}");
        File.WriteAllText(Path.Combine(_receipts, "FEDCBA9876543210.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_receipts, "deletions"));
        File.WriteAllText(Path.Combine(_receipts, "deletions", "fedcba9876543210.json"), "{}");

        var kept = RestoreReceipt.ReadKept(_receipts);

        Assert.AreEqual("0123456789abcdef", Assert.ContainsSingle(kept).Run);
    }

    [TestMethod]
    public void AReceiptLetGo_IsGone_AndOnlyItsRunsIs()
    {
        ReceiptOf("gone.txt", completedAt: 20).Keep(_receipts, "0123456789abcdef");
        ReceiptOf("kept.txt", completedAt: 40).Keep(_receipts, "fedcba9876543210");

        Assert.IsTrue(RestoreReceipt.LetGo(_receipts, "0123456789abcdef"));

        Assert.AreEqual("fedcba9876543210", Assert.ContainsSingle(RestoreReceipt.ReadKept(_receipts)).Run);
        Assert.IsFalse(RestoreReceipt.LetGo(_receipts, "0123456789abcdef"), "a run whose receipt is gone keeps none to let go");
    }

    [TestMethod]
    public void WhatIsNotARunsName_LetsNothingGo()
    {
        Directory.CreateDirectory(_receipts);
        var beside = Path.Combine(Path.GetDirectoryName(_receipts)!, "config.json");
        File.WriteAllText(beside, "{}");

        Assert.IsFalse(RestoreReceipt.LetGo(_receipts, Path.Combine("..", "config")));
        Assert.IsTrue(File.Exists(beside), "what is not a run's name is never joined to a path");
    }

    [TestMethod]
    public void AStateDirectoryThatKeptNone_ReadsBackNone()
    {
        Assert.IsEmpty(RestoreReceipt.ReadKept(_receipts));
    }

    [TestMethod]
    public void ARunsName_IsSixteenLowercaseHexDigits()
    {
        Assert.IsTrue(RestoreReceipt.IsRunName("0123456789abcdef"));
        Assert.IsFalse(RestoreReceipt.IsRunName("0123456789ABCDEF"));
        Assert.IsFalse(RestoreReceipt.IsRunName("0123456789abcde"));
        Assert.IsFalse(RestoreReceipt.IsRunName("0123456789abcdef0"));
        Assert.IsFalse(RestoreReceipt.IsRunName("../config.json..."));
    }

    private static RestoreReceipt ReceiptOf(string path, ulong completedAt) => new()
    {
        SchemaVersion = RestoreReceipt.CurrentSchemaVersion,
        SnapshotId = new string('e', 32),
        StartedAt = 10,
        CompletedAt = completedAt,
        Items = [new ReceiptItem { Path = path, Outcome = "restored", Bytes = 5 }],
        Displaced = [],
        WrittenTo = "/srv/restored",
        Outcome = RestoreOutcome.Complete,
    };
}
