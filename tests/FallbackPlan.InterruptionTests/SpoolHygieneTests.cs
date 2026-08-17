using FallbackPlan.Repository.Packing;
using FallbackPlan.TestSupport;

namespace FallbackPlan.InterruptionTests;

/// <summary>
/// Spool files a crash strands. A spool with its checkpoint sidecar is
/// resumable state and sacrosanct (specification 05 §6.3). A spool
/// <em>without</em> one is not: sealing deletes the sidecar before the
/// upload collects the spool, and a metadata blob never writes one — so a
/// kill in either window leaves a file the resume walk cannot see and
/// nothing else reclaims. The next publication owns the directory (one
/// writer per state directory, ADR-0028 §2) and must sweep what can never
/// be resumed.
/// </summary>
[TestClass]
public sealed class SpoolHygieneTests : InterruptionHarness
{
    [TestMethod]
    public async Task Publication_AnOrphanSpoolFromAKilledUpload_IsSweptByTheNextRun()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        // The shape a kill between seal and upload leaves: sealed bytes on
        // disk, sidecar already deleted by the seal, nothing in the store.
        Directory.CreateDirectory(SpoolDirectory);
        var orphan = Path.Combine(SpoolDirectory, "blob-00000000000000000000000000000001.spool");
        await File.WriteAllBytesAsync(orphan, new byte[128]);

        using (var source = new MemoryStream(BuildFile(seed: 1)))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xA1), CancellationToken.None);
        }

        Assert.IsFalse(File.Exists(orphan), "a spool no resume can reach must be reclaimed by the next run");
        SequenceAssert.AreEqual(BuildFile(seed: 1), await RestoreSnapshotAsync(store, keys, 0xA1));
    }

    [TestMethod]
    public async Task Publication_AResumableSpool_IsNotSweptAwayFromTheResumeWalk()
    {
        var store = CreateStore();
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        // A resumable pair, made by the production path: a job killed with a
        // blob open leaves spool + sidecar (04 §5.1 row 3). The source dies
        // at 300 KiB — past the first blob's 256 KiB seal, mid-second-blob.
        using (var interrupted = new FaultingSource(BuildFile(seed: 1), failAfterBytes: 300 * 1024))
        {
            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(interrupted, snapshotSeed: 0xA1), CancellationToken.None));
        }

        var spools = Directory.GetFiles(SpoolDirectory, "blob-*.spool");
        var sidecars = Directory.GetFiles(SpoolDirectory, "blob-*.spool.checkpoint");
        Assert.AreEqual(1, spools.Length);
        Assert.AreEqual(1, sidecars.Length);

        // The next run must resume it, not sweep it.
        var second = BuildFile(seed: 2);
        using (var source = new MemoryStream(second))
        {
            await CreateOrchestrator(store, keys, hierarchy).PublishAsync(Job(source, snapshotSeed: 0xB2), CancellationToken.None);
        }

        SequenceAssert.AreEqual(second, await RestoreSnapshotAsync(store, keys, 0xB2));
    }

    /// <summary>A source that fails once the reader passes the given offset.</summary>
    private sealed class FaultingSource(byte[] content, int failAfterBytes) : Stream
    {
        private readonly MemoryStream _inner = new(content);

        public override bool CanRead => true;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_inner.Position >= failAfterBytes)
            {
                throw new IOException("Injected fault: the source failed mid-file.");
            }

            return _inner.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void Flush() => _inner.Flush();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
