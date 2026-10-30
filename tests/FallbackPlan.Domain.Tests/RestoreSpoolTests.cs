using System.Text;

namespace FallbackPlan.Domain.Tests;

/// <summary>
/// Where a file is written whole before it is moved to its name (FR-RST-005,
/// ADR-0173): beside it, so the move is one rename, and under a name no
/// longer than any target holds, so a file whose own name is as long as the
/// target allows is not failed for the name of its spool. The name follows
/// from the file's own alone, so the planner and the recovery tool can tell
/// an entry a snapshot names there (ADR-0152 Amendment 4).
/// </summary>
[TestClass]
public sealed class RestoreSpoolTests
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "fbp-restore-spool");

    [TestMethod]
    [DataRow(5, "n", DisplayName = "a short name")]
    [DataRow(255, "n", DisplayName = "a name of 255 ASCII characters")]
    [DataRow(85, "語", DisplayName = "a name of 255 UTF-8 bytes in three-byte characters")]
    public void ASpool_IsBesideItsFile_UnderOneShortName_WhateverTheFilesOwnName(int count, string unit)
    {
        var file = string.Concat(Enumerable.Repeat(unit, count));
        var destination = Path.Combine(Folder, file);

        var spool = RestorePaths.SpoolBeside(destination, "restore");

        Assert.AreEqual(Folder, Path.GetDirectoryName(spool));
        Assert.AreNotEqual(destination, spool);
        Assert.IsLessThanOrEqualTo(40, Encoding.UTF8.GetByteCount(Path.GetFileName(spool)));
        Assert.AreEqual(
            Path.GetFileName(RestorePaths.SpoolBeside(Path.Combine(Folder, "a.bin"), "restore")).Length,
            Path.GetFileName(spool).Length,
            "every spool name is as long as every other");
    }

    [TestMethod]
    public void ASpool_IsTheSameForTheSameFile_AndApartForAnother()
    {
        var one = Path.Combine(Folder, "one.bin");

        Assert.AreEqual(RestorePaths.SpoolBeside(one, "restore"), RestorePaths.SpoolBeside(one, "restore"));
        Assert.AreNotEqual(
            RestorePaths.SpoolBeside(one, "restore"),
            RestorePaths.SpoolBeside(Path.Combine(Folder, "two.bin"), "restore"));
        Assert.AreNotEqual(
            RestorePaths.SpoolBeside(one, "restore"),
            RestorePaths.SpoolBeside(Path.Combine(Folder, "One.bin"), "restore"),
            "names that differ only in case keep apart on a target that folds case");
    }

    [TestMethod]
    [DataRow("one.bin", "restore")]
    [DataRow("語.bi", "recover")]
    public void ASpoolsName_IsTheNameSpoolBesideGivesBesideTheFile(string file, string owner) =>
        Assert.AreEqual(
            Path.GetFileName(RestorePaths.SpoolBeside(Path.Combine(Folder, file), owner)),
            RestorePaths.SpoolName(file, owner));

    [TestMethod]
    public void TheExecutorsSpool_AndTheRecoveryToolsSpool_AreApart()
    {
        var file = Path.Combine(Folder, "one.bin");

        var executor = Path.GetFileName(RestorePaths.SpoolBeside(file, "restore"));
        var recovery = Path.GetFileName(RestorePaths.SpoolBeside(file, "recover"));

        Assert.AreNotEqual(executor, recovery);
        Assert.Contains("restore", executor, StringComparison.Ordinal);
        Assert.Contains("recover", recovery, StringComparison.Ordinal);
    }
}
