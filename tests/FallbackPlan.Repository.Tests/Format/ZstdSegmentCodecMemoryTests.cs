using System.Security.Cryptography;
using FallbackPlan.Repository.Format.Compression;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Format;

/// <summary>
/// The zstd codec takes no memory of its own to compress a segment: the
/// frame is written into the destination its caller hands it, which already
/// holds at least the plaintext's length, and a stored frame is never longer
/// than that (specification 10 §3). Memory is bounded by the pipeline's own
/// buffers, not by a process-wide pool that keeps what it was lent
/// (NFR-PERF-001).
/// </summary>
[TestClass]
public sealed class ZstdSegmentCodecMemoryTests
{
    private static byte[] Compressible(int length) =>
        [.. Enumerable.Repeat("compressible segment content "u8.ToArray(), length / 29 + 1).SelectMany(text => text).Take(length)];

    [TestMethod]
    public void Compressing_OnAThreadThatNeverHas_AllocatesNothingBeyondTheDestination()
    {
        // Larger than any segment another suite compresses, so nothing the
        // shared array pool already holds could stand in for a buffer this
        // codec rented, and a fresh thread has nothing pooled of its own.
        const int SegmentBytes = 40 * 1024 * 1024;
        using var codec = new ZstdSegmentCodec(level: 3, maximumSegmentBytes: 64 * 1024 * 1024);
        var plaintext = Compressible(SegmentBytes);
        var destination = new byte[SegmentBytes];

        // The library's one-time setup is not the codec's to answer for.
        using (var warm = new ZstdSegmentCodec(level: 3, maximumSegmentBytes: 64 * 1024))
        {
            Assert.IsTrue(warm.TryCompressForStorage(Compressible(1000), 50, new byte[1000], out _));
        }

        var stored = false;
        var allocated = -1L;
        var thread = new Thread(() =>
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            stored = codec.TryCompressForStorage(plaintext, 50, destination, out _);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        });
        thread.Start();
        thread.Join();

        Assert.IsTrue(stored, "the compressible segment was not stored compressed");
        Assert.IsLessThan(64 * 1024, allocated, $"compressing a {SegmentBytes}-byte segment allocated {allocated} bytes");
    }

    [TestMethod]
    public void Compressing_IntoADestinationExactlyThePlaintextsLength_StoresTheFrameALargerOneGets()
    {
        using var codec = new ZstdSegmentCodec(level: 3, maximumSegmentBytes: 64 * 1024);
        var plaintext = Compressible(50_000);
        var exact = new byte[plaintext.Length];
        var roomy = new byte[plaintext.Length + 4096];

        Assert.IsTrue(codec.TryCompressForStorage(plaintext, 50, exact, out var exactWritten));
        Assert.IsTrue(codec.TryCompressForStorage(plaintext, 50, roomy, out var roomyWritten));

        Assert.AreEqual(roomyWritten, exactWritten);
        SequenceAssert.AreEqual(roomy[..roomyWritten], exact[..exactWritten]);
    }

    [TestMethod]
    public void Compressing_WhenTheFrameWouldOutgrowTheDestination_DeclinesRatherThanThrowing()
    {
        // At threshold 0 a frame no longer than the plaintext is stored, so
        // the only thing declining this one is that it does not fit.
        using var codec = new ZstdSegmentCodec(level: 3, maximumSegmentBytes: 64 * 1024);
        var plaintext = new byte[10_000];
        RandomNumberGenerator.Fill(plaintext);

        Assert.IsFalse(codec.TryCompressForStorage(plaintext, 0, new byte[plaintext.Length], out var written));
        Assert.AreEqual(0, written);
    }
}
