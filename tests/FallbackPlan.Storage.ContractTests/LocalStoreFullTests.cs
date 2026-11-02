using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Storage.ContractTests;

/// <summary>
/// A local destination whose drive fills says so, as an object store with
/// no room does (FR-QUOTA-001, FR-QUOTA-002; ADR-0012 Amendment 7): a put the
/// volume has no room for is <see cref="StoreFullException"/>, naming the
/// object, and leaves nothing behind, so the room it took is there again.
/// </summary>
/// <remarks>
/// Each case fills a real volume of its own: a small virtual disk on
/// Windows, a memory-backed one on Linux, which only an elevated process or
/// root may mount.
/// </remarks>
[TestClass]
[PrivilegedPlatformCondition(TestPlatforms.Windows | TestPlatforms.Linux, "only an elevated process, or root, may make and mount a volume of its own")]
public sealed class LocalStoreFullTests
{
    private const int VolumeMegabytes = 16;

    private static readonly ObjectKey Large = ObjectKey.Parse("blobs/data/abcd/large");

    [TestMethod]
    public async Task Put_LargerThanTheRoomLeft_IsStoreFull_NamingTheObject_AndLeavesNothing()
    {
        using var volume = SmallVolume();
        var root = Path.Combine(volume.MountedAt, "repo");
        var store = new LocalFileSystemObjectStore(root);

        var full = await Assert.ThrowsExactlyAsync<StoreFullException>(async () =>
            await store.PutAsync(Large, ThreeTimesTheVolume, PutConditions.None, CancellationToken.None));

        Assert.Contains("blobs/data/abcd/large", full.Message, StringComparison.Ordinal);
        Assert.IsInstanceOfType<IOException>(full.InnerException);
        Assert.IsFalse((await store.GetMetadataAsync(Large, CancellationToken.None)).Found, "no object, partial or whole");
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(Path.Combine(root, ".fbp-tmp")), "nothing spooled is left");
    }

    [TestMethod]
    public async Task Put_AfterOneThereWasNoRoomFor_LandsInTheRoomItGaveBack()
    {
        using var volume = SmallVolume();
        var store = new LocalFileSystemObjectStore(Path.Combine(volume.MountedAt, "repo"));
        await Assert.ThrowsExactlyAsync<StoreFullException>(async () =>
            await store.PutAsync(Large, ThreeTimesTheVolume, PutConditions.None, CancellationToken.None));

        // Half the volume: room only if the refused put gave its room back.
        var half = await store.PutAsync(
            ObjectKey.Parse("blobs/data/abcd/half"),
            _ => ValueTask.FromResult<Stream>(new SyntheticStream(VolumeMegabytes / 2 * 1024L * 1024, seed: 2)),
            PutConditions.None,
            CancellationToken.None);

        Assert.AreEqual(PutOutcome.Created, half.Outcome);
    }

    private static ValueTask<Stream> ThreeTimesTheVolume(CancellationToken _) =>
        ValueTask.FromResult<Stream>(new SyntheticStream(3L * VolumeMegabytes * 1024 * 1024, seed: 1));

    private static FormattedVolume SmallVolume()
    {
        if (OperatingSystem.IsWindows())
        {
            return FormattedVolume.Mount("ntfs", VolumeMegabytes);
        }

        try
        {
            return FormattedVolume.Tmpfs(VolumeMegabytes);
        }
        catch (InvalidOperationException refused)
        {
            Assert.Inconclusive($"This root may not mount a volume: {refused.Message}");
            throw;
        }
    }
}
