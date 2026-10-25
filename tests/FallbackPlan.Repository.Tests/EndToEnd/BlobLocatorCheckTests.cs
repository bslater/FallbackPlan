using System.Buffers.Binary;
using System.Security.Cryptography;
using FallbackPlan.Repository.Packing;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Repository.Tests.EndToEnd;

/// <summary>
/// The locator check: what a holder with no key can establish of a whole
/// blob (specification 05 §4) — that its locator's footer offset falls inside
/// it, and that the first eight bytes of the SHA-256 of everything before the
/// locator are the digest prefix the locator states. A peer judges a repair
/// on it, of its own copy and of the replacement, and on nothing it is told
/// (FR-VER-008, ADR-0100).
/// </summary>
/// <remarks>
/// It is a corruption check and never an integrity guarantee: unkeyed and
/// truncated, it cannot tell the blob sealed under a key from another
/// well-formed blob. What it can tell is a blob whose bytes have changed
/// since it was sealed, which is what rot is.
/// </remarks>
[TestClass]
public sealed class BlobLocatorCheckTests : ArchiveTestHarness
{
    [TestMethod]
    public async Task ASealedBlob_PassesIt_AndItsLocatorIsItsLastSixteenBytes()
    {
        var blob = await AnyBlobAsync();

        var check = await CheckAsync(blob);

        Assert.IsTrue(check.Passes);
        CollectionAssert.AreEqual(blob[^FooterLocator.Length..], check.Locator.ToArray());
    }

    [TestMethod]
    public async Task AByteChangedAnywhereBeforeTheLocator_FailsIt_AndLeavesTheLocatorAsItWas()
    {
        var blob = await AnyBlobAsync();
        foreach (var at in new[] { 0, blob.Length / 2, blob.Length - FooterLocator.Length - 1 })
        {
            var rotted = (byte[])blob.Clone();
            rotted[at] ^= 0x01;

            var check = await CheckAsync(rotted);

            Assert.IsFalse(check.Passes, $"a byte changed at {at} of {blob.Length}");
            CollectionAssert.AreEqual(
                blob[^FooterLocator.Length..], check.Locator.ToArray(),
                "rot in the body leaves the locator naming the blob that was sealed");
        }
    }

    [TestMethod]
    public async Task AChangedDigestPrefix_FailsIt()
    {
        var blob = await AnyBlobAsync();
        blob[^1] ^= 0x01;

        Assert.IsFalse((await CheckAsync(blob)).Passes);
    }

    [TestMethod]
    public async Task AFooterOffsetOutsideTheBlob_FailsIt_ThoughTheDigestPrefixStillAgrees()
    {
        var blob = await AnyBlobAsync();
        BinaryPrimitives.WriteUInt64BigEndian(blob.AsSpan(blob.Length - FooterLocator.Length), (ulong)blob.Length);

        Assert.IsFalse((await CheckAsync(blob)).Passes);
    }

    [TestMethod]
    public async Task ATruncatedBlob_FailsIt()
    {
        var blob = await AnyBlobAsync();

        Assert.IsFalse((await CheckAsync(blob[..(blob.Length / 2)])).Passes);
    }

    [TestMethod]
    public async Task AnObjectTooShortToHoldALocator_FailsIt_AndStatesNone()
    {
        var check = await CheckAsync(new byte[FooterLocator.Length - 1]);

        Assert.IsFalse(check.Passes);
        Assert.AreEqual(0, check.Locator.Length);
    }

    [TestMethod]
    public async Task ABlobRewrittenToBeConsistentWithItself_PassesIt_UnderAnotherLocator()
    {
        // What the check cannot see, and why a peer never takes a replacement
        // on it alone: bytes changed and the prefix recomputed to match pass,
        // and only the locator — which states a different digest — says they
        // are not the blob that was sealed.
        var blob = await AnyBlobAsync();
        var forged = Reseal(blob, at: blob.Length / 2);

        var check = await CheckAsync(forged);

        Assert.IsTrue(check.Passes);
        CollectionAssert.AreNotEqual(blob[^FooterLocator.Length..], check.Locator.ToArray());
    }

    /// <summary>
    /// A blob with one byte changed and its digest prefix recomputed: a
    /// well-formed blob that is not the one sealed.
    /// </summary>
    internal static byte[] Reseal(byte[] blob, int at)
    {
        var forged = (byte[])blob.Clone();
        forged[at] ^= 0x01;
        var digest = SHA256.HashData(forged.AsSpan(0, forged.Length - FooterLocator.Length));
        digest.AsSpan(0, 8).CopyTo(forged.AsSpan(forged.Length - 8));
        return forged;
    }

    private static async Task<BlobLocatorCheck.Result> CheckAsync(byte[] bytes)
    {
        using var content = new MemoryStream(bytes, writable: false);
        return await BlobLocatorCheck.CheckAsync(content, bytes.Length, CancellationToken.None);
    }

    private async Task<byte[]> AnyBlobAsync()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        var archiver = CreateArchiver(store, keys);
        using var file = new MemoryStream(BuildTestFile());
        await archiver.ArchiveAsync(file, CancellationToken.None);

        await foreach (var entry in store.ListAsync(
            ObjectPrefix.Parse("blobs/data/"), ListOptions.Default, CancellationToken.None))
        {
            using var read = await store.OpenReadAsync(entry.Key, range: null, CancellationToken.None);
            var bytes = new byte[entry.Length];
            await read.Content!.ReadExactlyAsync(bytes, CancellationToken.None);
            return bytes;
        }

        throw new AssertFailedException("archiving the test file sealed no data blob");
    }
}
