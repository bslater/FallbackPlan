using FallbackPlan.Agent;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// A drill's words go where anyone signed in reads them, so the path of the
/// file it was at is taken out of the engine's message before they do
/// (FR-WOR-007, ADR-0089 Amendment 1): as the snapshot names it, as the
/// platform writes it, and the file's own name where it stands as a part of a
/// path or in quotes. A name in running prose is left, because a short one
/// would be found inside other words and the diagnosis with it.
/// </summary>
[TestClass]
public sealed class DrillWordsTests
{
    private const string Sampled = "the sampled file";

    [TestMethod]
    public void Unnamed_ThePathAsTheSnapshotNamesIt_IsTakenOut()
    {
        var said = RecoveryDrillJob.Unnamed("could not open 'docs/2026/report.txt' for reading", "docs/2026/report.txt");

        Assert.AreEqual($"could not open '{Sampled}' for reading", said);
    }

    [TestMethod]
    public void Unnamed_ThePathAsThisPlatformWritesIt_IsTakenOut_InsideALongerPath()
    {
        var native = Path.Combine("scratch", "drill-1", "0", "docs", "2026", "report.txt");

        var said = RecoveryDrillJob.Unnamed($"access to the path {native} is denied.", "docs/2026/report.txt");

        Assert.DoesNotContain("report.txt", said, StringComparison.Ordinal);
        Assert.DoesNotContain("2026", said, StringComparison.Ordinal);
        Assert.StartsWith("access to the path ", said, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Unnamed_TheFilesOwnName_IsTakenOut_InQuotesAndAsAPartOfAPath()
    {
        const string FilePath = "docs/2026/report.txt";

        Assert.AreEqual(
            $"the file '{Sampled}' failed its hash", RecoveryDrillJob.Unnamed("the file 'report.txt' failed its hash", FilePath));
        Assert.AreEqual(
            $"\"{Sampled}\": hash mismatch", RecoveryDrillJob.Unnamed("\"report.txt\": hash mismatch", FilePath));
        Assert.AreEqual(
            $"/restore/0/{Sampled}: hash mismatch", RecoveryDrillJob.Unnamed("/restore/0/report.txt: hash mismatch", FilePath));
    }

    [TestMethod]
    public void Unnamed_AShortName_IsNotTakenOutOfOtherWords()
    {
        // A file called "a" must cost the diagnosis nothing: only the name
        // where it stands as a name is a name.
        var said = RecoveryDrillJob.Unnamed("a data blob was rejected: 'a' is missing an object", "docs/a");

        Assert.AreEqual($"a data blob was rejected: '{Sampled}' is missing an object", said);
    }

    [TestMethod]
    public void Unnamed_NoPath_LeavesTheWordsAsTheyAre()
    {
        Assert.AreEqual("the replica could not be opened", RecoveryDrillJob.Unnamed("the replica could not be opened", null));
    }
}
