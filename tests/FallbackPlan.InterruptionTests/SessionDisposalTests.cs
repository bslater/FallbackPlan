using FallbackPlan.Repository;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.InterruptionTests;

/// <summary>
/// A publication that dies in its producer — the source stream fails, the
/// scanner throws — reaches the archive session's disposal with upload
/// workers still alive: parked on the channel, or mid-put against the store.
/// Disposal must be the end of the session's work, not a fence the work
/// outruns: a put that completes after the publication has reported failure
/// is work escaping the lifetime that owned it, against key material the
/// disposal below it is about to zero.
/// </summary>
[TestClass]
public sealed class SessionDisposalTests : InterruptionHarness
{
    [TestMethod]
    public async Task Publication_TheSourceFailsMidRun_LeavesNoUploadInFlightWhenItReturns()
    {
        var parking = new ParkingBlobStore(CreateStore());
        using var keys = CreateKeys();
        using var hierarchy = CreateHierarchy();

        // Fails after ~700 KiB: enough for the first blob (target 256 KiB)
        // to seal and its upload to be in flight when the source dies.
        var source = new FaultingSource(BuildFile(seed: 1), failAfterBytes: 700 * 1024);

        var publish = Task.Run(async () =>
        {
            using (source)
            {
                await CreateOrchestrator(parking, keys, hierarchy)
                    .PublishAsync(Job(source, snapshotSeed: 0xA1), CancellationToken.None);
            }
        });

        Assert.IsTrue(await parking.WaitUntilParkedAsync(TimeSpan.FromSeconds(10)), "no upload reached the store");

        // The upload stays parked while the source failure unwinds the
        // producer. Released on a delay so a session that correctly waits
        // for its workers can finish; a session that does not will have
        // returned long before this fires.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1500));
            parking.Release();
        });

        await Assert.ThrowsExactlyAsync<IOException>(async () => await publish);

        Assert.AreEqual(0, parking.ParkedCount,
            "an upload was still in flight when the publication returned — disposal must wait for its workers");
    }

    /// <summary>Parks every blob put before it reaches the store, until released once.</summary>
    private sealed class ParkingBlobStore(IObjectStore inner) : IObjectStore
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _parked;

        public int ParkedCount => Volatile.Read(ref _parked);

        public StoreCapabilities Capabilities => inner.Capabilities;

        public async Task<bool> WaitUntilParkedAsync(TimeSpan timeout) =>
            await Task.WhenAny(_reached.Task, Task.Delay(timeout)).ConfigureAwait(false) == _reached.Task;

        public void Release() => _release.TrySetResult();

        public ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.GetMetadataAsync(key, cancellationToken);

        public ValueTask<OpenReadResult> OpenReadAsync(ObjectKey key, ObjectRange? range, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(key, range, cancellationToken);

        public async ValueTask<PutResult> PutAsync(
            ObjectKey key,
            Func<CancellationToken, ValueTask<Stream>> openContent,
            PutConditions conditions,
            CancellationToken cancellationToken)
        {
            if (key.ToString().StartsWith("blobs/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _parked);
                _reached.TrySetResult();
                await _release.Task.ConfigureAwait(false);
                Interlocked.Decrement(ref _parked);
            }

            return await inner.PutAsync(key, openContent, conditions, cancellationToken).ConfigureAwait(false);
        }

        public IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, ListOptions options, CancellationToken cancellationToken) =>
            inner.ListAsync(prefix, options, cancellationToken);

        public ValueTask<DeleteResult> DeleteAsync(ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
            inner.DeleteAsync(key, conditions, cancellationToken);
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
