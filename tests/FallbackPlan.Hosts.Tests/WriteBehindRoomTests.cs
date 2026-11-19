using System.Text;
using FallbackPlan.Agent;
using FallbackPlan.Storage.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace FallbackPlan.Hosts.Tests;

/// <summary>
/// The room a direct-ship run beside a peer alone keeps its blobs in until
/// the set's stores have them (FR-DEST-021, ADR-0196 Amendment 8): a blob is
/// kept while the room holds less than its capacity, its copy goes once
/// every store it was kept for has it, and the room it held is freed for the
/// blobs after it.
/// </summary>
[TestClass]
public sealed class WriteBehindRoomTests : IDisposable
{
    private static readonly ObjectKey First = ObjectKey.Parse("blobs/data/aaaa/aaaaaaaaaaaaaaaaaaaaaaaaaa");
    private static readonly ObjectKey Second = ObjectKey.Parse("blobs/data/bbbb/bbbbbbbbbbbbbbbbbbbbbbbbbb");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fbp-room", Guid.NewGuid().ToString("n"));

    [TestMethod]
    public async Task ABlobIsKept_WhileTheRoomHoldsLessThanItsCapacity()
    {
        using var room = Open(capacity: 1);

        Assert.IsTrue(await KeepAsync(room, First, stores: 1), "the room was empty");
        Assert.IsFalse(await KeepAsync(room, Second, stores: 1), "the room held its capacity");
        Assert.IsTrue(await HeldAsync(room, First));
        Assert.IsFalse(await HeldAsync(room, Second));
    }

    [TestMethod]
    public async Task ACopyGoes_OnceEveryStoreHasIt_AndFreesItsRoom()
    {
        using var room = Open(capacity: 1);
        Assert.IsTrue(await KeepAsync(room, First, stores: 2));

        await room.SentAsync(First);
        Assert.IsTrue(await HeldAsync(room, First), "one store of two still reads it");
        Assert.IsFalse(await KeepAsync(room, Second, stores: 1), "its room is still held");

        await room.SentAsync(First);
        Assert.IsFalse(await HeldAsync(room, First), "every store has it");
        Assert.IsTrue(await KeepAsync(room, Second, stores: 1), "its room was freed");
    }

    [TestMethod]
    public async Task AVolumeAtItsFloor_KeepsNothing()
    {
        using var room = WriteBehindRoom.Open(_directory, capacity: 1L << 30, _ => 0, NullLogger.Instance)
            ?? throw new AssertFailedException("the room did not open");

        Assert.IsFalse(await KeepAsync(room, First, stores: 1));
        Assert.IsFalse(await HeldAsync(room, First));
    }

    [TestMethod]
    public void ARoomARunLeftBehind_IsEmptiedWhenTheNextOpensIt()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "blobs"));
        File.WriteAllText(Path.Combine(_directory, "blobs", "left"), "a crashed run's copy");

        using var room = Open(capacity: 1);

        Assert.IsEmpty(Directory.GetFiles(_directory, "*", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task ARoomDisposed_HoldsNothing()
    {
        var room = Open(capacity: 1L << 20);
        Assert.IsTrue(await KeepAsync(room, First, stores: 1));

        room.Dispose();

        Assert.IsFalse(Directory.Exists(_directory));
    }

    private WriteBehindRoom Open(long capacity) =>
        WriteBehindRoom.Open(_directory, capacity, _ => long.MaxValue, NullLogger.Instance)
            ?? throw new AssertFailedException("the room did not open");

    private static ValueTask<bool> KeepAsync(WriteBehindRoom room, ObjectKey key, int stores) =>
        room.TryKeepAsync(
            key,
            _ => ValueTask.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(key.Value))),
            stores,
            CancellationToken.None);

    private static async Task<bool> HeldAsync(WriteBehindRoom room, ObjectKey key) =>
        (await room.Copies.GetMetadataAsync(key, CancellationToken.None)).Found;

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A test directory that will not delete is not a test failure.
        }
    }
}
