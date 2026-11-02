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

    /// <summary>What the folders the refused put made may keep of the room.</summary>
    private const long Slack = 1024 * 1024;

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

        // Measured, not taken from the volume's size: a file system keeps some
        // of a volume this small for itself, and need not give a deleted
        // file's room back the instant it is deleted.
        var room = FreeSpace.On(volume.MountedAt);
        await Assert.ThrowsExactlyAsync<StoreFullException>(async () =>
            await store.PutAsync(Large, ThreeTimesTheVolume, PutConditions.None, CancellationToken.None));
        var givenBack = await RoomComesBackAsync(volume.MountedAt, room);
        Assert.IsGreaterThanOrEqualTo(room - Slack, givenBack, $"{room} byte(s) free before the refused put");

        // Half the room: there only if the refused put gave its room back.
        var half = await store.PutAsync(
            ObjectKey.Parse("blobs/data/abcd/half"),
            _ => ValueTask.FromResult<Stream>(new SyntheticStream(room / 2, seed: 2)),
            PutConditions.None,
            CancellationToken.None);

        Assert.AreEqual(PutOutcome.Created, half.Outcome);
    }

    /// <summary>
    /// The room free on the volume once it is back within <see cref="Slack"/>
    /// of <paramref name="room"/>, or as it stands after a while if never.
    /// </summary>
    private static async Task<long> RoomComesBackAsync(string mountedAt, long room)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        var free = FreeSpace.On(mountedAt);
        while (free < room - Slack && waited.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            free = FreeSpace.On(mountedAt);
        }

        return free;
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
