using System.Runtime.CompilerServices;
using Bodu;
using FallbackPlan.Protocol;
using FallbackPlan.Storage.Abstractions;

namespace FallbackPlan.Agent;

/// <summary>
/// A direct-ship set's archive with a whole peer behind it for blobs
/// (ADR-0198): every key is asked of the archive first, and a blob it does not
/// hold is read from the peer. Only blobs: the archive's metadata is this
/// machine's planning copy, and a snapshot or index the peer still holds and
/// the set has let go is not the copy's to carry back.
/// </summary>
/// <remarks>
/// A failure of the peer is said as the source's, not the destination's: the
/// copy's callers record an <see cref="IOException"/> against the destination
/// being written, so the peer is named in it.
/// </remarks>
internal sealed class PeerFilledObjectStore(IObjectStore archive, IObjectStore peer, string peerName) : IObjectStore
{
    /// <inheritdoc />
    public StoreCapabilities Capabilities => archive.Capabilities;

    /// <summary>Whether <paramref name="exception"/> is a peer that could not be read, as against a fault here.</summary>
    internal static bool IsPeerFailure(Exception exception) =>
        exception is IOException or PeerProtocolException or System.Net.Sockets.SocketException
            or System.Security.Authentication.AuthenticationException;

    /// <inheritdoc />
    public async ValueTask<GetMetadataResult> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        var held = await archive.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
        if (held.Found || !IsBlob(key.Value))
        {
            return held;
        }

        try
        {
            return await peer.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsPeerFailure(exception))
        {
            throw Unanswered(exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask<OpenReadResult> OpenReadAsync(
        ObjectKey key, ObjectRange? range, CancellationToken cancellationToken)
    {
        var held = await archive.OpenReadAsync(key, range, cancellationToken).ConfigureAwait(false);
        if (held.Outcome != OpenReadOutcome.NotFound || !IsBlob(key.Value))
        {
            return held;
        }

        held.Dispose();
        OpenReadResult read;
        try
        {
            read = await peer.OpenReadAsync(key, range, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsPeerFailure(exception))
        {
            throw Unanswered(exception);
        }

        return read is { Outcome: OpenReadOutcome.Found, Content: { } content }
            ? new OpenReadResult(new PeerReadStream(content, this))
            : read;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ObjectEntry> ListAsync(
        ObjectPrefix prefix,
        ListOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var value = prefix.Value;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var entry in archive.ListAsync(prefix, options, cancellationToken).ConfigureAwait(false))
        {
            if (IsBlob(entry.Key.Value))
            {
                seen.Add(entry.Key.Value);
            }

            yield return entry;
        }

        // A prefix shorter than "blobs/" that is one of its beginnings can
        // match blobs too; one that is not cannot.
        ObjectPrefix blobs;
        if (IsBlob(value))
        {
            blobs = prefix;
        }
        else if ("blobs/".StartsWith(value, StringComparison.Ordinal))
        {
            blobs = ObjectPrefix.Parse("blobs/");
        }
        else
        {
            yield break;
        }

        await using var listing = peer.ListAsync(blobs, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            ObjectEntry entry;
            try
            {
                if (!await listing.MoveNextAsync().ConfigureAwait(false))
                {
                    yield break;
                }

                entry = listing.Current;
            }
            catch (Exception exception) when (IsPeerFailure(exception))
            {
                throw Unanswered(exception);
            }

            if (seen.Add(entry.Key.Value))
            {
                yield return entry;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<PutResult> PutAsync(
        ObjectKey key,
        Func<CancellationToken, ValueTask<Stream>> openContent,
        PutConditions conditions,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("A sync's source is read, never written.");

    /// <inheritdoc />
    public ValueTask<DeleteResult> DeleteAsync(
        ObjectKey key, DeleteConditions conditions, CancellationToken cancellationToken) =>
        throw new NotSupportedException("A sync's source is read, never written.");

    private static bool IsBlob(string key) => key.StartsWith("blobs/", StringComparison.Ordinal);

    private IOException Unanswered(Exception exception) =>
        new($"peer '{peerName}', which the set's newest backup is read from, stopped answering: {exception.Message}", exception);

    /// <summary>A peer's read, whose failure part-way is said as the peer's.</summary>
    private sealed class PeerReadStream(Stream inner, PeerFilledObjectStore owner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                return inner.Read(buffer, offset, count);
            }
            catch (Exception exception) when (IsPeerFailure(exception))
            {
                throw owner.Unanswered(exception);
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsPeerFailure(exception))
            {
                throw owner.Unanswered(exception);
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
