using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Storage.ContractTests;

/// <summary>
/// Runs the full provider contract suite against
/// <see cref="LocalFileSystemObjectStore"/> rooted on a FAT32 volume, as a
/// removable drive often is (FR-DEST-001; ADR-0012 Amendment 6).
/// FAT32 holds no links, and refuses the call that asks a folder for one; the
/// store's guard against a linked folder asked every folder on a key's path,
/// so every write into a folder an earlier write had made failed.
/// </summary>
[TestClass]
[PrivilegedPlatformCondition(TestPlatforms.Windows, "only an elevated process may make and mount a volume of its own")]
public sealed class Fat32LocalFileSystemObjectStoreContractTests : ObjectStoreContractTests
{
    private static Fat32Volume? s_volume;

    private string? _root;

    private static Func<CancellationToken, ValueTask<Stream>> Content(byte[] bytes) =>
        _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));

    /// <summary>Mounts the one volume every test in the class writes to, each under a folder of its own.</summary>
    [ClassInitialize]
    public static void MountVolume(TestContext _)
    {
        if (OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess)
        {
            s_volume = Fat32Volume.Mount();
        }
    }

    /// <summary>Takes the volume away again.</summary>
    [ClassCleanup]
    public static void DismountVolume()
    {
        s_volume?.Dispose();
        s_volume = null;
    }

    /// <inheritdoc />
    protected override IObjectStore CreateStore() =>
        new LocalFileSystemObjectStore(_root ??= Path.Combine(
            s_volume?.MountedAt ?? throw new InvalidOperationException("the FAT32 volume is not mounted"),
            Guid.NewGuid().ToString("n")));

    [TestMethod]
    public async Task Put_IntoAFolderAnEarlierPutMade_IsStored()
    {
        // The shape of every backup's second blob: its folder is there already.
        var store = CreateStore();
        var first = ObjectKey.Parse("blobs/data/abcd/first");
        var second = ObjectKey.Parse("blobs/data/abcd/second");

        await store.PutAsync(first, Content([1]), PutConditions.None, CancellationToken.None);
        var put = await store.PutAsync(second, Content([2, 2]), PutConditions.None, CancellationToken.None);

        Assert.AreEqual(PutOutcome.Created, put.Outcome);
        var metadata = await store.GetMetadataAsync(second, CancellationToken.None);
        Assert.IsTrue(metadata.Found);
        Assert.AreEqual(2, metadata.Metadata!.Length);
    }
}
