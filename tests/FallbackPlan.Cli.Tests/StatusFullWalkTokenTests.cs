using System.Globalization;
using FallbackPlan.Api;
using FallbackPlan.Cli;
using FallbackPlan.Domain.Status;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The full walk's token on a set's line of <c>status</c> (FR-SNP-012,
/// contract 1.82): when the set was last walked in full, every folder looked
/// at whatever the change journal said, to the minute, in UTC as every other
/// time on the line is.
/// </summary>
/// <remarks>
/// A set whose backups ask no journal prints no token, rather than "never":
/// every one of its backups walks in full, and the set's last completed backup
/// already says when that was. A service before 1.82 prints none either.
/// </remarks>
[TestClass]
public sealed class StatusFullWalkTokenTests
{
    [TestMethod]
    public void ASetLastWalkedInFull_PrintsWhenToTheMinute()
    {
        var walked = (ulong)DateTimeOffset.Parse("2026-11-04T02:13:59Z", CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

        Assert.AreEqual("full-walk@2026-11-04T02:13Z", CliApplication.DescribeFullWalk(Set(walked)));
    }

    [TestMethod]
    public void ASetWhoseBackupsAskNoJournal_PrintsNoToken() =>
        Assert.AreEqual(string.Empty, CliApplication.DescribeFullWalk(Set(lastFullWalkAt: null)));

    private static BackupSetStatusDescriptor Set(ulong? lastFullWalkAt) =>
        new("docs", new BackupSetStatus(ProtectionState.Protected, null, []),
            NextRun: null, Destinations: [], LastFullWalkAt: lastFullWalkAt);
}
