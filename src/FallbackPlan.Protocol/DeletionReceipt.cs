using System.Buffers.Binary;
using System.Text;
using Bodu;

namespace FallbackPlan.Protocol;

/// <summary>
/// A destination's signed record of what it deleted on a retention
/// instruction (specification peer-protocol 06 §4.2;
/// [ADR-0063](../../docs/adr/0063-deletion-receipts.md)) — the peer plane's
/// signed audit record (FR-GC-008), signed under the destination's own
/// device key because it holds no repository keys.
/// </summary>
/// <remarks>
/// <para>
/// The receipt commits to the instruction it acted on (a digest of each
/// signed page, in order), the session it was sent in, the commander that
/// sent it, the key the destination checked it against, the floor in force,
/// and the keys it actually removed. It carries no reasons, object ids or
/// manifests: the store keys are the whole vocabulary (06 §5).
/// </para>
/// <para>
/// <see cref="EncodeForSigning"/> is the artefact. Everything a reader
/// displays is parsed back out of those bytes with <see cref="Parse"/>, so
/// nothing can be shown as attested that was not under the signature.
/// </para>
/// </remarks>
/// <param name="SessionId">The session identifier the instruction was sent in (02 §3.5).</param>
/// <param name="RepositoryId">The repository the instruction named.</param>
/// <param name="CommanderPublicKey">The device key of the peer that sent the instruction.</param>
/// <param name="IssuedAtUnixMilliseconds">When the destination signed this, by its own clock.</param>
/// <param name="FloorGenerations">The retention floor the destination held the instruction to.</param>
/// <param name="ReclaimPublicKey">The reclaim public key the pages were verified against, or empty when the destination held none.</param>
/// <param name="PageDigests">SHA-256 of each page's signed bytes, in the order received.</param>
/// <param name="DeletedCount">How many objects were actually removed.</param>
/// <param name="Deleted">The keys removed, at most <see cref="MaximumListedKeys"/> of them; the count and the page digests carry the rest.</param>
/// <param name="NotHeld">How many instructed keys the destination did not hold.</param>
public sealed record DeletionReceipt(
    ReadOnlyMemory<byte> SessionId,
    ReadOnlyMemory<byte> RepositoryId,
    ReadOnlyMemory<byte> CommanderPublicKey,
    ulong IssuedAtUnixMilliseconds,
    uint FloorGenerations,
    ReadOnlyMemory<byte> ReclaimPublicKey,
    IReadOnlyList<ReadOnlyMemory<byte>> PageDigests,
    ulong DeletedCount,
    IReadOnlyList<string> Deleted,
    uint NotHeld)
{
    /// <summary>The session identifier is 32 bytes (02 §3.5).</summary>
    public const int SessionIdLength = SessionBinding.SessionIdLength;

    /// <summary>A device public key is 32 bytes.</summary>
    public const int PublicKeyLength = PeerIdentity.KeyLength;

    /// <summary>A reclaim public key is 32 bytes.</summary>
    public const int ReclaimPublicKeyLength = ReplicationOffer.ReclaimPublicKeyLength;

    /// <summary>A page digest is SHA-256.</summary>
    public const int DigestLength = 32;

    /// <summary>An Ed25519 signature is 64 bytes.</summary>
    public const int SignatureLength = PeerKeypair.SignatureLength;

    /// <summary>
    /// The most keys one receipt lists. An instruction may run to many pages
    /// of <see cref="RetentionOffer.MaximumKeys"/> keys each; the page digests
    /// commit to all of them, and the listing is for a reader, not a proof.
    /// </summary>
    public const int MaximumListedKeys = RetentionOffer.MaximumKeys;

    /// <summary>The most pages one receipt may cite.</summary>
    public const int MaximumPages = 4096;

    private static ReadOnlySpan<byte> SigningLabel => "fbp-peer-v1:deletion-receipt"u8;

    /// <summary>
    /// The bytes the destination signs — and the artefact itself:
    /// the label, then every field fixed-length or length-prefixed
    /// (00 §4), so no two receipts share an encoding.
    /// </summary>
    /// <exception cref="PeerProtocolException">A field is out of bounds.</exception>
    public byte[] EncodeForSigning()
    {
        if (SessionId.Length != SessionIdLength
            || RepositoryId.Length != ReplicationOffer.RepositoryIdLength
            || CommanderPublicKey.Length != PublicKeyLength
            || (!ReclaimPublicKey.IsEmpty && ReclaimPublicKey.Length != ReclaimPublicKeyLength)
            || PageDigests.Count > MaximumPages
            || PageDigests.Any(digest => digest.Length != DigestLength)
            || Deleted.Count > MaximumListedKeys
            || (ulong)Deleted.Count > DeletedCount
            || Deleted.Any(key => key.Length == 0 || Encoding.UTF8.GetByteCount(key) > ReplicationInventory.MaximumKeyBytes))
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A deletion receipt violates a 06 §4.2 width or bound.");
        }

        var length = SigningLabel.Length + SessionIdLength + ReplicationOffer.RepositoryIdLength + PublicKeyLength
            + sizeof(ulong) + sizeof(uint) + 1 + ReclaimPublicKey.Length
            + sizeof(uint) + (PageDigests.Count * DigestLength)
            + sizeof(ulong) + sizeof(uint) + sizeof(uint);
        foreach (var key in Deleted)
        {
            length += sizeof(uint) + Encoding.UTF8.GetByteCount(key);
        }

        var bytes = new byte[length];
        var offset = 0;
        SigningLabel.CopyTo(bytes);
        offset += SigningLabel.Length;
        SessionId.Span.CopyTo(bytes.AsSpan(offset));
        offset += SessionIdLength;
        RepositoryId.Span.CopyTo(bytes.AsSpan(offset));
        offset += ReplicationOffer.RepositoryIdLength;
        CommanderPublicKey.Span.CopyTo(bytes.AsSpan(offset));
        offset += PublicKeyLength;
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(offset), IssuedAtUnixMilliseconds);
        offset += sizeof(ulong);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), FloorGenerations);
        offset += sizeof(uint);
        bytes[offset++] = ReclaimPublicKey.IsEmpty ? (byte)0 : (byte)1;
        ReclaimPublicKey.Span.CopyTo(bytes.AsSpan(offset));
        offset += ReclaimPublicKey.Length;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), (uint)PageDigests.Count);
        offset += sizeof(uint);
        foreach (var digest in PageDigests)
        {
            digest.Span.CopyTo(bytes.AsSpan(offset));
            offset += DigestLength;
        }

        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(offset), DeletedCount);
        offset += sizeof(ulong);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), (uint)Deleted.Count);
        offset += sizeof(uint);
        foreach (var key in Deleted)
        {
            var written = Encoding.UTF8.GetBytes(key, bytes.AsSpan(offset + sizeof(uint)));
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), (uint)written);
            offset += sizeof(uint) + written;
        }

        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), NotHeld);
        return bytes;
    }

    /// <summary>
    /// The inverse of <see cref="EncodeForSigning"/>: exactly those bytes,
    /// with nothing before, after or out of bounds.
    /// </summary>
    /// <param name="bytes">The signed bytes.</param>
    /// <returns>The receipt.</returns>
    /// <exception cref="PeerProtocolException">The bytes are not a receipt.</exception>
    public static DeletionReceipt Parse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var cursor = new Cursor(bytes);
            if (!cursor.Take(SigningLabel.Length).SequenceEqual(SigningLabel))
            {
                throw Malformed();
            }

            var sessionId = cursor.Take(SessionIdLength).ToArray();
            var repositoryId = cursor.Take(ReplicationOffer.RepositoryIdLength).ToArray();
            var commander = cursor.Take(PublicKeyLength).ToArray();
            var issuedAt = BinaryPrimitives.ReadUInt64BigEndian(cursor.Take(sizeof(ulong)));
            var floor = BinaryPrimitives.ReadUInt32BigEndian(cursor.Take(sizeof(uint)));
            var hasReclaim = cursor.Take(1)[0];
            if (hasReclaim > 1)
            {
                throw Malformed();
            }

            var reclaim = hasReclaim == 1 ? cursor.Take(ReclaimPublicKeyLength).ToArray() : [];
            var pages = BinaryPrimitives.ReadUInt32BigEndian(cursor.Take(sizeof(uint)));
            if (pages > MaximumPages)
            {
                throw Malformed();
            }

            var digests = new List<ReadOnlyMemory<byte>>((int)pages);
            for (var page = 0; page < pages; page++)
            {
                digests.Add(cursor.Take(DigestLength).ToArray());
            }

            var deletedCount = BinaryPrimitives.ReadUInt64BigEndian(cursor.Take(sizeof(ulong)));
            var listed = BinaryPrimitives.ReadUInt32BigEndian(cursor.Take(sizeof(uint)));
            if (listed > MaximumListedKeys || listed > deletedCount)
            {
                throw Malformed();
            }

            var deleted = new List<string>((int)listed);
            for (var index = 0; index < listed; index++)
            {
                var keyLength = BinaryPrimitives.ReadUInt32BigEndian(cursor.Take(sizeof(uint)));
                if (keyLength is 0 or > ReplicationInventory.MaximumKeyBytes)
                {
                    throw Malformed();
                }

                deleted.Add(Encoding.UTF8.GetString(cursor.Take((int)keyLength)));
            }

            var notHeld = BinaryPrimitives.ReadUInt32BigEndian(cursor.Take(sizeof(uint)));
            if (!cursor.AtEnd)
            {
                // A signed statement is exactly its bytes: trailing bytes
                // would be unsigned baggage a reader might show as attested.
                throw Malformed();
            }

            return new DeletionReceipt(
                sessionId, repositoryId, commander, issuedAt, floor, reclaim, digests, deletedCount, deleted, notHeld);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A deletion receipt ends before its fields do.", exception);
        }
    }

    private static PeerProtocolException Malformed() =>
        new(PeerRefusalReason.Malformed, "These bytes are not a deletion receipt (06 §4.2).");

    /// <inheritdoc/>
    public bool Equals(DeletionReceipt? other) =>
        other is not null
        && SessionId.Span.SequenceEqual(other.SessionId.Span)
        && RepositoryId.Span.SequenceEqual(other.RepositoryId.Span)
        && CommanderPublicKey.Span.SequenceEqual(other.CommanderPublicKey.Span)
        && IssuedAtUnixMilliseconds == other.IssuedAtUnixMilliseconds
        && FloorGenerations == other.FloorGenerations
        && ReclaimPublicKey.Span.SequenceEqual(other.ReclaimPublicKey.Span)
        && PageDigests.Count == other.PageDigests.Count
        && PageDigests.Zip(other.PageDigests).All(pair => pair.First.Span.SequenceEqual(pair.Second.Span))
        && DeletedCount == other.DeletedCount
        && Deleted.SequenceEqual(other.Deleted, StringComparer.Ordinal)
        && NotHeld == other.NotHeld;

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(IssuedAtUnixMilliseconds, DeletedCount, Deleted.Count, PageDigests.Count, NotHeld);

    /// <summary>A forward-only reader over the signed bytes that refuses to run past the end.</summary>
    private ref struct Cursor(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private int _offset;

        public readonly bool AtEnd => _offset == _bytes.Length;

        public ReadOnlySpan<byte> Take(int length)
        {
            ThrowHelper.ThrowIfGreaterThan(length, _bytes.Length - _offset);
            var slice = _bytes.Slice(_offset, length);
            _offset += length;
            return slice;
        }
    }
}
