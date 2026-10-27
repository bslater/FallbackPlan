using System.Globalization;
using FallbackPlan.Api;
using FallbackPlan.Cli;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// A cloud destination whose store holds the replica's metadata only in an
/// archival tier, on the line under its row of <c>status</c> (FR-DEST-020,
/// contract 1.70): nothing restores from there until the object it names is
/// rehydrated, and a lifecycle rule must leave all but the data blobs served.
/// </summary>
/// <remarks>
/// Said beside an in-sync row, because the copy did land: what the line
/// tells is that no restore can open it. A destination that names no such
/// object, and a service before 1.70, print no line.
/// </remarks>
[TestClass]
public sealed class StatusArchivedMetadataLineTests
{
    [TestMethod]
    public void AStoreThatArchivedTheReplicasMetadata_SaysNothingRestoresFromHere_AndWhatToKeepServed()
    {
        var since = (ulong)DateTimeOffset.Parse("2026-10-20T09:30:00Z", CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

        Assert.AreEqual(
            "nothing restores from here: since 2026-10-20 the store has held repository-format only in an archival tier; "
            + "rehydrate it there, and keep everything but blobs/data/ out of the store's lifecycle rule",
            CliApplication.DescribeArchivedMetadata(Row("repository-format", since)));
    }

    [TestMethod]
    public void ADestinationThatNamesNoArchivedMetadata_PrintsNoLine() =>
        Assert.IsNull(CliApplication.DescribeArchivedMetadata(Row(null, null)));

    private static DestinationStatusDescriptor Row(string? key, ulong? since) =>
        new("cloud", "s3", "in-sync", LastSuccessAt: 1_000, Detail: null, "offsite", "proven",
            ArchivedMetadata: key, ArchivedMetadataSince: since);
}
