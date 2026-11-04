using FallbackPlan.Api;
using FallbackPlan.Cli;
using FallbackPlan.Domain.Status;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The not-fetched token on a set's line of <c>status</c> (FR-SNP-014,
/// contract 1.84): how many online-only files the set's last backup could
/// not fetch from their provider.
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
    public void ALastBackupThatFetchedEveryOne_PrintsNoToken() =>
        Assert.AreEqual(string.Empty, CliApplication.DescribeNotFetched(Set(notFetched: null)));

    private static BackupSetStatusDescriptor Set(NotFetchedDescriptor? notFetched) =>
        new("docs", new BackupSetStatus(ProtectionState.Protected, null, []),
            NextRun: null, Destinations: [], NotFetched: notFetched);
}
