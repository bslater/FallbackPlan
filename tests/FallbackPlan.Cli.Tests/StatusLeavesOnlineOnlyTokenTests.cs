using FallbackPlan.Api;
using FallbackPlan.Cli;
using FallbackPlan.Domain.Status;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// The token on a set's line of <c>status</c> that says the set leaves its
/// online-only files to their cloud provider (FR-SNP-015, contract 1.89).
/// </summary>
/// <remarks>
/// A set that fetches them prints no token, as a service before 1.89 does.
/// </remarks>
[TestClass]
public sealed class StatusLeavesOnlineOnlyTokenTests
{
    [TestMethod]
    public void ASetThatLeavesThem_PrintsTheToken() =>
        Assert.AreEqual("online-only:left-to-provider", CliApplication.DescribeLeavesOnlineOnly(Set(leaves: true)));

    [TestMethod]
    public void ASetThatFetchesThem_PrintsNoToken()
    {
        Assert.AreEqual(string.Empty, CliApplication.DescribeLeavesOnlineOnly(Set(leaves: null)));
        Assert.AreEqual(string.Empty, CliApplication.DescribeLeavesOnlineOnly(Set(leaves: false)));
    }

    private static BackupSetStatusDescriptor Set(bool? leaves) =>
        new("docs", new BackupSetStatus(ProtectionState.Protected, null, []),
            NextRun: null, Destinations: [], LeavesOnlineOnly: leaves);
}
