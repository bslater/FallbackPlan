using FallbackPlan.Application;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// The floor a destination volume keeps for the machine that owns it
/// (FR-DEST-003). The decision lives apart from the measurement so it can be
/// exercised without arranging a nearly-full disk — which is the only reason
/// this rule would otherwise ship untested.
/// Establishes FR-DEST-010's floor arithmetic.
/// </summary>
[TestClass]
public sealed class DestinationCapacityTests
{
    private const string ReplicaRoot = "/mnt/vault/0123456789abcdef";

    [TestMethod]
    public void FloorShortfall_RoomToSpare_LetsTheCopyStart()
    {
        Assert.IsNull(
            DestinationCapacity.FloorShortfall(ReplicaRoot, DestinationCapacity.FreeSpaceFloorBytes * 100));
    }

    [TestMethod]
    public void FloorShortfall_ExactlyAtTheFloor_IsStillRoom()
    {
        // The floor is what must remain, so having exactly it is compliance,
        // not a breach.
        Assert.IsNull(DestinationCapacity.FloorShortfall(ReplicaRoot, DestinationCapacity.FreeSpaceFloorBytes));
        Assert.IsNotNull(
            DestinationCapacity.FloorShortfall(ReplicaRoot, DestinationCapacity.FreeSpaceFloorBytes - 1));
    }

    [TestMethod]
    public void FloorShortfall_BelowTheFloor_NamesThePathAndBothNumbers()
    {
        // Filling a destination volume to zero breaks the machine, not just
        // the backup — and on the source's own volume it stops the next
        // capture from staging at all. The refusal has to be legible enough
        // that somebody can act on it without guessing which disk.
        var shortfall = DestinationCapacity.FloorShortfall(ReplicaRoot, 1_024);

        Assert.IsNotNull(shortfall);
        Assert.Contains(ReplicaRoot, shortfall, StringComparison.Ordinal);
        Assert.Contains("1024 byte(s) free", shortfall, StringComparison.Ordinal);
        Assert.Contains(
            DestinationCapacity.FreeSpaceFloorBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            shortfall,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void FloorShortfall_APlatformThatWillNotSay_AnswersThereIsRoom()
    {
        // This guard exists to stop a disk being filled. It must never be the
        // reason a healthy destination stops receiving backups, so an unknown
        // reads as room rather than as a refusal.
        Assert.IsNull(DestinationCapacity.FloorShortfall(ReplicaRoot, availableBytes: null));
    }

    [TestMethod]
    public void ProbeRootFor_APath_NamesTheDestinationsOwnVolume()
    {
        // The 2026-08 defect this pins against: Path.GetPathRoot answers "/"
        // for every absolute path on Unix, so the floor was measured on the
        // OS volume — precisely wrong for a destination on a different
        // volume, which is a destination's whole job. On Unix the probe asks
        // about the replica root itself (statvfs answers for whatever volume
        // holds it); only Windows wants the drive root.
        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual("C:\\", DestinationCapacity.ProbeRootFor("C:\\vault\\abc"));
        }
        else
        {
            Assert.AreEqual("/mnt/vault/abc", DestinationCapacity.ProbeRootFor("/mnt/vault/abc"));
        }
    }
}
