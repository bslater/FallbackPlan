using FallbackPlan.Filesystem.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Filesystem.Tests;

/// <summary>
/// The owner and group names a scan records (FR-MAN-003: the metadata in a
/// file version's immutable manifest) come from a cache the whole process
/// shares, and more than one scan reaches it at a time: the service's writer
/// lane is a pool (ADR-0047), so two sets can be captured in one process at
/// once. A name recorded wrongly is recorded for good.
/// </summary>
[TestClass]
public sealed class PosixNameCacheTests
{
    [TestMethod]
    [PlatformCondition(TestPlatforms.Posix, "the names come from getpwuid and getgrgid, which Windows does not have")]
    [PlatformTrait(TestPlatforms.Posix)]
    public void Lookups_FromConcurrentScans_NeitherCorruptTheCacheNorConfuseNames()
    {
        // Ids no account uses and this process has not looked up, so every
        // lookup in the storm is a first lookup, and every first lookup
        // writes to the cache while the other threads read and write it.
        const uint FirstUnusedId = 3_000_000_000;
        const int Threads = 8;
        const int IdsPerThread = 2_000;

        var root = PosixNames.UserName(0);
        Assert.IsNotNull(root, "uid 0 is named on every POSIX system this runs on");

        Parallel.For(0, Threads, thread =>
        {
            for (var i = 0; i < IdsPerThread; i++)
            {
                var id = FirstUnusedId + (uint)((thread * IdsPerThread) + i);
                Assert.IsNull(PosixNames.UserName(id), $"uid {id} names no account");
                Assert.IsNull(PosixNames.GroupName(id), $"gid {id} names no group");
                Assert.AreEqual(root, PosixNames.UserName(0), "a known id keeps its own name mid-storm");
            }
        });
    }
}
