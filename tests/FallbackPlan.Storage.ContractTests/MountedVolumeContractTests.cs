using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Storage.ContractTests;

/// <summary>
/// The provider contract suite, and the shape of every backup's second blob,
/// run against <see cref="LocalFileSystemObjectStore"/> rooted on a volume a
/// subclass mounts, each test under a folder of its own.
/// </summary>
public abstract class MountedVolumeContractTests : ObjectStoreContractTests
{
    private string? _root;

    /// <summary>The folder the subclass's volume is mounted on.</summary>
    protected abstract string MountedAt { get; }

    private static Func<CancellationToken, ValueTask<Stream>> Content(byte[] bytes) =>
        _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));

    /// <inheritdoc />
    protected override IObjectStore CreateStore() =>
        new LocalFileSystemObjectStore(_root ??= Path.Combine(MountedAt, Guid.NewGuid().ToString("n")));

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
