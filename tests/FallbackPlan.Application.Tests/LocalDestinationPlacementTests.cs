using FallbackPlan.Application;

namespace FallbackPlan.Application.Tests;

/// <summary>
/// The condition of choosing a local destination (ADR-0051, FR-DEST-017):
/// it must sit on a different volume than every one of the set's roots, and
/// — where the platform can say — on a different physical drive. Judged
/// purely, with the platform probes injected, so every answer is testable
/// on a machine with one volume.
/// </summary>
[TestClass]
public sealed class LocalDestinationPlacementTests
{
    private static ulong? Volume(string path) => path.StartsWith("/mnt/b", StringComparison.Ordinal) ? 2UL
        : path.StartsWith("/mnt/c", StringComparison.Ordinal) ? 3UL
        : 1UL;

    [TestMethod]
    public void ADestinationOnARootsVolume_IsRefusedNamingTheRoot()
    {
        var conflict = LocalDestinationPlacement.Judge(
            ["/home/user/docs", "/mnt/b/pics"], "/home/user/vault", Volume, _ => null);

        Assert.IsNotNull(conflict);
        Assert.AreEqual("/home/user/docs", conflict.Root);
        Assert.IsFalse(conflict.SamePhysicalDisk, "same volume is the sharper finding — name it as such");
    }

    [TestMethod]
    public void ADestinationOnItsOwnVolume_IsAccepted()
    {
        Assert.IsNull(LocalDestinationPlacement.Judge(
            ["/home/user/docs"], "/mnt/b/vault", Volume, _ => null));
    }

    [TestMethod]
    public void DifferentVolumesOnOneDisk_AreRefusedWhereThePlatformCanSay()
    {
        // Two partitions of one drive: the volumes differ, the failure does
        // not — "a different physical hdd where possible" is the condition.
        static string? Disk(string path) => "disk-a";

        var conflict = LocalDestinationPlacement.Judge(
            ["/home/user/docs"], "/mnt/b/vault", Volume, Disk);

        Assert.IsNotNull(conflict);
        Assert.AreEqual("/home/user/docs", conflict.Root);
        Assert.IsTrue(conflict.SamePhysicalDisk);
    }

    [TestMethod]
    public void SeparateDisks_AreAccepted()
    {
        static string? Disk(string path) => path.StartsWith("/mnt/b", StringComparison.Ordinal) ? "disk-b" : "disk-a";

        Assert.IsNull(LocalDestinationPlacement.Judge(
            ["/home/user/docs"], "/mnt/b/vault", Volume, Disk));
    }

    [TestMethod]
    public void AnUnknowableTopology_FallsBackToTheVolumeAnswer()
    {
        // "Where possible": distinct volumes whose disks the platform will
        // not name are accepted — the volume separation is the hard check,
        // the disk refinement applies only where it can be judged.
        Assert.IsNull(LocalDestinationPlacement.Judge(
            ["/home/user/docs"], "/mnt/b/vault", Volume, _ => null));

        // And a volume the platform cannot identify at all is not refused
        // on a guess — the status derivation stays conservative for it.
        Assert.IsNull(LocalDestinationPlacement.Judge(
            ["/home/user/docs"], "/mnt/b/vault", _ => null, _ => null));
    }
}
