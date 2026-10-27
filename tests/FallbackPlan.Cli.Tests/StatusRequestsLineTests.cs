using System.Globalization;
using FallbackPlan.Api;
using FallbackPlan.Cli;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// What a cloud destination's store was asked, on the line under its row of
/// <c>status</c> (FR-DEST-019, contract 1.69): the requests sent over the
/// window, by operation, and the bytes they carried both ways.
/// </summary>
/// <remarks>
/// A count that began inside the window says when, because a week counted is
/// not a month of nothing asked. A destination the service counts nothing for
/// — a local path, a peer, a service before 1.69 — prints no line, never a
/// line of zeros.
/// </remarks>
[TestClass]
public sealed class StatusRequestsLineTests
{
    [TestMethod]
    public void ACloudDestination_SaysWhatItsStoreWasAsked_ByOperation_WithTheBytes() =>
        Assert.AreEqual(
            "requests (30 days): 12 put, 40 get, 3 head, 5 list, 1 delete; 4096 bytes sent, 300 bytes received",
            CliApplication.DescribeRequests(Row(new StoreRequestsDescriptor(
                Days: 30, Puts: 12, Gets: 40, Heads: 3, Lists: 5, Deletes: 1, BytesSent: 4_096, BytesReceived: 300))));

    [TestMethod]
    public void ACountBegunInsideTheWindow_SaysWhenItBegan()
    {
        var began = (ulong)DateTimeOffset.Parse("2026-10-20T09:30:00Z", CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

        Assert.AreEqual(
            "requests (30 days, counted since 2026-10-20): 1 put, 0 get, 0 head, 0 list, 0 delete; 10 bytes sent, 0 bytes received",
            CliApplication.DescribeRequests(Row(new StoreRequestsDescriptor(
                Days: 30, Puts: 1, Gets: 0, Heads: 0, Lists: 0, Deletes: 0, BytesSent: 10, BytesReceived: 0, Since: began))));
    }

    [TestMethod]
    public void ADestinationNothingIsCountedFor_PrintsNoLine() =>
        Assert.IsNull(CliApplication.DescribeRequests(Row(requests: null)));

    private static DestinationStatusDescriptor Row(StoreRequestsDescriptor? requests) =>
        new("cloud", "s3", "in-sync", LastSuccessAt: 1_000, Detail: null, "offsite", "proven", Requests: requests);
}
