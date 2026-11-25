using System.Globalization;
using FallbackPlan.Api;
using FallbackPlan.Cli;

namespace FallbackPlan.Cli.Tests;

/// <summary>
/// Where a destination's proof of new backups stands, on the line under its
/// row of <c>status</c> (FR-VER-003, contract 1.91, ADR-0199 Amendment 2):
/// the backup it was last proven through, and the backup whose proof is
/// under way with how many of its blobs are proven of how many it needs. Since
/// contract 1.95 (ADR-0199 Amendment 14) a proof under way also says at least
/// how many more passes it needs; a failed proof says none, its next pass
/// asking the failed blob again.
/// </summary>
/// <remarks>
/// A backup is named by when it was taken, as a person knows it, and by its
/// identity only where the service no longer holds it to say when. A
/// destination with neither, and a service before 1.91, print no line.
/// </remarks>
[TestClass]
public sealed class StatusNewBackupProofLineTests
{
    private const string Earlier = "0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a";
    private const string Newest = "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b";

    private static readonly ulong EarlierAt = At("2026-11-20T14:03:00Z");
    private static readonly ulong NewestAt = At("2026-11-21T09:00:00Z");

    [TestMethod]
    public void ADestinationProvenThroughABackup_SaysWhichByWhenItWasTaken() =>
        Assert.AreEqual(
            "proven through the backup of 2026-11-20 14:03",
            CliApplication.DescribeNewBackupProof(Row(new NewBackupProofDescriptor(Earlier, EarlierAt))));

    [TestMethod]
    public void AProofUnderWay_SaysHowFarItHasGot_AndWhatWasProvenBeforeIt() =>
        Assert.AreEqual(
            "proving the backup of 2026-11-21 09:00: 3 of 10 blobs; proven through the backup of 2026-11-20 14:03",
            CliApplication.DescribeNewBackupProof(Row(new NewBackupProofDescriptor(
                Earlier, EarlierAt, ProvingSnapshotId: Newest, ProvingCapturedAt: NewestAt, BlobsProven: 3, BlobsLeft: 7))));

    [TestMethod]
    public void AProofUnderWay_SaysAtLeastHowManyMorePassesItNeeds() =>
        Assert.AreEqual(
            "proving the backup of 2026-11-21 09:00: 3 of 10 blobs, at least 4 more passes; "
            + "proven through the backup of 2026-11-20 14:03",
            CliApplication.DescribeNewBackupProof(Row(new NewBackupProofDescriptor(
                Earlier, EarlierAt, ProvingSnapshotId: Newest, ProvingCapturedAt: NewestAt, BlobsProven: 3, BlobsLeft: 7,
                PassesLeft: 4))));

    [TestMethod]
    public void AProofOnePassFromItsEnd_SaysOneMorePass() =>
        Assert.AreEqual(
            "proving the backup of 2026-11-21 09:00: 8 of 10 blobs, at least 1 more pass; "
            + "proven through the backup of 2026-11-20 14:03",
            CliApplication.DescribeNewBackupProof(Row(new NewBackupProofDescriptor(
                Earlier, EarlierAt, ProvingSnapshotId: Newest, ProvingCapturedAt: NewestAt, BlobsProven: 8, BlobsLeft: 2,
                PassesLeft: 1))));

    [TestMethod]
    public void AProofWithNoPassesLeftToCount_SaysNoneRatherThanZero() =>
        Assert.AreEqual(
            "proving the backup of 2026-11-21 09:00: 10 of 10 blobs; proven through the backup of 2026-11-20 14:03",
            CliApplication.DescribeNewBackupProof(Row(new NewBackupProofDescriptor(
                Earlier, EarlierAt, ProvingSnapshotId: Newest, ProvingCapturedAt: NewestAt, BlobsProven: 10, BlobsLeft: 0,
                PassesLeft: 0))));

    [TestMethod]
    public void AFailedProof_SaysNoPassesLeft() =>
        Assert.AreEqual(
            "the backup of 2026-11-21 09:00 failed its proof at blobs/data/ab/cd: 3 of 10 blobs proven; "
            + "proven through the backup of 2026-11-20 14:03",
            CliApplication.DescribeNewBackupProof(Row(new NewBackupProofDescriptor(
                Earlier, EarlierAt, ProvingSnapshotId: Newest, ProvingCapturedAt: NewestAt, BlobsProven: 3, BlobsLeft: 7,
                FailedBlob: "blobs/data/ab/cd", PassesLeft: 4))));

    [TestMethod]
    public void AFirstProofUnderWay_SaysNoBackupIsProvenThereYet() =>
        Assert.AreEqual(
            "proving the backup of 2026-11-21 09:00: 0 of 12 blobs; no backup proven here yet",
            CliApplication.DescribeNewBackupProof(Row(new NewBackupProofDescriptor(
                null, null, ProvingSnapshotId: Newest, ProvingCapturedAt: NewestAt, BlobsProven: 0, BlobsLeft: 12))));

    [TestMethod]
    public void AFailedProof_SaysWhichBackupFailedAndAtWhichBlob_AndWhatStillStands() =>
        Assert.AreEqual(
            "the backup of 2026-11-21 09:00 failed its proof at blobs/data/ab/cd: 3 of 10 blobs proven; "
            + "proven through the backup of 2026-11-20 14:03",
            CliApplication.DescribeNewBackupProof(Row(new NewBackupProofDescriptor(
                Earlier, EarlierAt, ProvingSnapshotId: Newest, ProvingCapturedAt: NewestAt, BlobsProven: 3, BlobsLeft: 7,
                FailedBlob: "blobs/data/ab/cd"))));

    [TestMethod]
    public void AProofUnderWayNotYetCounted_SaysItIsUnderWay_NeverNoneProven() =>
        Assert.AreEqual(
            "proving the backup of 2026-11-21 09:00: under way; proven through the backup of 2026-11-20 14:03",
            CliApplication.DescribeNewBackupProof(Row(new NewBackupProofDescriptor(
                Earlier, EarlierAt, ProvingSnapshotId: Newest, ProvingCapturedAt: NewestAt))));

    [TestMethod]
    public void ABackupTheServiceNoLongerHolds_IsNamedByItsIdentity() =>
        Assert.AreEqual(
            $"proven through backup {Earlier}",
            CliApplication.DescribeNewBackupProof(Row(new NewBackupProofDescriptor(Earlier, ProvenCapturedAt: null))));

    [TestMethod]
    public void ADestinationWithNoProofToReport_PrintsNoLine() =>
        Assert.IsNull(CliApplication.DescribeNewBackupProof(Row(null)));

    private static ulong At(string text) =>
        (ulong)DateTimeOffset.Parse(text, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

    private static DestinationStatusDescriptor Row(NewBackupProofDescriptor? proof) =>
        new("vault", "local-path", "in-sync", LastSuccessAt: 1_000, Detail: null, "independent", "proven",
            NewBackupProof: proof);
}
