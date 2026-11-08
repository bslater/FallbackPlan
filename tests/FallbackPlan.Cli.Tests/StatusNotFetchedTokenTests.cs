using FallbackPlan.Api;
using FallbackPlan.Cli;
using FallbackPlan.Domain.Status;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The not-fetched token on a set's line of <c>status</c> (FR-SNP-014,
/// contract 1.84): how many online-only files the set's last backup could
/// not fetch from their provider. The not-listed token beside it says how
/// many folders it could not list, and a last backup that missed only
/// folders prints no not-fetched token (contract 1.90, ADR-0193
/// Amendment 3).
/// </summary>
/// <remarks>
/// A set whose last backup fetched every one prints no token, as a service
/// before 1.84 does.
/// </remarks>
[TestClass]
public sealed class StatusNotFetchedTokenTests
{
    [TestMethod]
    public void FilesNotFetched_PrintTheirCount() =>
        Assert.AreEqual(
            "not-fetched:3",
            CliApplication.DescribeNotFetched(Set(new NotFetchedDescriptor(3, 2, 5_000, "docs/a.docx", "timed out"))));

    [TestMethod]
    public void FoldersNotListed_PrintTheirCount_AndNoFilesCountWhereNoFileWasMissed()
    {
        var set = Set(new NotFetchedDescriptor(0, 0, 5_000, "docs/drive", "timed out", Folders: 2, FoldersKeptEarlier: 1, Listing: true));
        Assert.AreEqual("not-listed:2", CliApplication.DescribeNotListed(set));
        Assert.AreEqual(string.Empty, CliApplication.DescribeNotFetched(set));
    }

    [TestMethod]
    public void FilesAndFoldersMissed_PrintBoth()
    {
        var set = Set(new NotFetchedDescriptor(3, 2, 5_000, "docs/a.docx", "timed out", Folders: 1, FoldersKeptEarlier: 1));
        Assert.AreEqual("not-fetched:3", CliApplication.DescribeNotFetched(set));
        Assert.AreEqual("not-listed:1", CliApplication.DescribeNotListed(set));
    }

    [TestMethod]
    public void FilesMissedAlone_PrintNoNotListedToken() =>
        Assert.AreEqual(
            string.Empty,
            CliApplication.DescribeNotListed(Set(new NotFetchedDescriptor(3, 2, 5_000, "docs/a.docx", "timed out"))));

    [TestMethod]
    public void ALastBackupThatFetchedEveryOne_PrintsNoToken() =>
        Assert.AreEqual(string.Empty, CliApplication.DescribeNotFetched(Set(notFetched: null)));

    private static BackupSetStatusDescriptor Set(NotFetchedDescriptor? notFetched) =>
        new("docs", new BackupSetStatus(ProtectionState.Protected, null, []),
            NextRun: null, Destinations: [], NotFetched: notFetched);
}
