using FallbackPlan.Api;
using FallbackPlan.Cli;
using FallbackPlan.Domain.Status;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The changes-waiting token on a set's line of <c>status</c> (FR-SNP-013,
/// contract 1.83): how many changed files the set's next backup will take,
/// or that it will walk in full; and how many of them are online-only files it
/// must fetch (ADR-0192 Amendment 2, contract 1.86).
/// </summary>
/// <remarks>
/// A set whose backups ask no change journal prints no token, as a service
/// before 1.83 does: every one of its backups walks in full, and nothing is
/// counted.
/// </remarks>
[TestClass]
public sealed class StatusPendingTokenTests
{
    [TestMethod]
    public void ChangedFilesWaiting_PrintTheirCount() =>
        Assert.AreEqual("waiting:12", CliApplication.DescribePending(Set(new PendingChangesDescriptor(12, null, 5_000))));

    [TestMethod]
    public void NothingWaiting_PrintsNought() =>
        Assert.AreEqual("waiting:0", CliApplication.DescribePending(Set(new PendingChangesDescriptor(0, null, 5_000))));

    [TestMethod]
    public void ANextBackupThatWalksInFull_SaysSo() =>
        Assert.AreEqual(
            "waiting:full-walk",
            CliApplication.DescribePending(Set(new PendingChangesDescriptor(null, "its full walk is due", 5_000))));

    [TestMethod]
    public void OnlineOnlyFilesToFetch_PrintTheirCount_AndNoneWhereThereAreNone()
    {
        Assert.AreEqual(
            "to-fetch:3",
            CliApplication.DescribeToFetch(Set(new PendingChangesDescriptor(12, null, 5_000, FilesToFetch: 3, BytesToFetch: 2_048))));
        Assert.AreEqual(
            string.Empty,
            CliApplication.DescribeToFetch(Set(new PendingChangesDescriptor(12, null, 5_000, FilesToFetch: 0, BytesToFetch: 0))));
        Assert.AreEqual(string.Empty, CliApplication.DescribeToFetch(Set(new PendingChangesDescriptor(12, null, 5_000))));
        Assert.AreEqual(string.Empty, CliApplication.DescribeToFetch(Set(pending: null)));
    }

    [TestMethod]
    public void ASetWhoseBackupsAskNoJournal_PrintsNoToken() =>
        Assert.AreEqual(string.Empty, CliApplication.DescribePending(Set(pending: null)));

    private static BackupSetStatusDescriptor Set(PendingChangesDescriptor? pending) =>
        new("docs", new BackupSetStatus(ProtectionState.Protected, null, []),
            NextRun: null, Destinations: [], PendingChanges: pending);
}
