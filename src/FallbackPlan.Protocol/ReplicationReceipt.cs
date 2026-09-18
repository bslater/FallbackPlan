using System.Buffers.Binary;
using System.Text;
using Bodu;

namespace FallbackPlan.Protocol;

/// <summary>
/// A destination's signed record of what it committed in a push and what it
/// holds afterwards (specification peer-protocol 03 §3.5;
/// [ADR-0064](../../docs/adr/0064-replication-receipts.md)) — the statement
/// that turns a peer's completeness from a count the source read off an
/// unsigned inventory into one the peer signed for, under its own device key
/// because it holds no repository keys.
/// </summary>
/// <remarks>
/// <para>
/// The receipt commits to the session the push arrived in, the commander that
/// pushed, the objects created this session (listed up to a cap, counted
/// beyond it), and the replica's size for the repository once the session's
/// commits are in. It is a record of what the destination <em>says</em> it
/// holds; possession is verification's to prove (04), and a receipt licenses
/// nothing (FR-GC-009).
/// </para>
/// <para>
/// <see cref="EncodeForSigning"/> is the artefact. Everything a reader
/// displays is parsed back out of those bytes with <see cref="Parse"/>, so
/// nothing can be shown as attested that was not under the signature.
/// </para>
/// </remarks>
/// <param name="SessionId">The session identifier the push arrived in (02 §3.5).</param>
/// <param name="RepositoryId">The repository pushed.</param>
/// <param name="CommanderPublicKey">The device key of the peer that pushed.</param>
/// <param name="IssuedAtUnixMilliseconds">When the destination signed this, by its own clock.</param>
/// <param name="CommittedCount">How many objects this session created at the destination.</param>
/// <param name="Committed">Those keys, in commit order, at most <see cref="MaximumListedKeys"/> of them; the count carries the rest.</param>
/// <param name="HeldObjects">How many objects the destination holds for the repository after this session.</param>
/// <param name="HeldBytes">Their total length.</param>
public sealed record ReplicationReceipt(
    ReadOnlyMemory<byte> SessionId,
    ReadOnlyMemory<byte> RepositoryId,
    ReadOnlyMemory<byte> CommanderPublicKey,
    ulong IssuedAtUnixMilliseconds,
    ulong CommittedCount,
    IReadOnlyList<string> Committed,
    ulong HeldObjects,
    ulong HeldBytes)
{
    /// <summary>The session identifier is 32 bytes (02 §3.5).</summary>
    public const int SessionIdLength = SessionBinding.SessionIdLength;

    /// <summary>A device public key is 32 bytes.</summary>
    public const int PublicKeyLength = PeerIdentity.KeyLength;

    /// <summary>An Ed25519 signature is 64 bytes.</summary>
    public const int SignatureLength = PeerKeypair.SignatureLength;

    /// <summary>
    /// The most keys one receipt lists — one inventory page's worth. A push
    /// may create more; the count commits to all of them, and the listing is
    /// for a reader, not a proof.
    /// </summary>
    public const int MaximumListedKeys = ReplicationInventory.MaximumKeys;

    private static ReadOnlySpan<byte> SigningLabel => "fbp-peer-v1:replication-receipt"u8;

    /// <summary>
    /// The bytes the destination signs — and the artefact itself: the label,
    /// then every field fixed-length or length-prefixed (00 §4), so no two
    /// receipts share an encoding and no deletion receipt reads as one.
    /// </summary>
    /// <exception cref="PeerProtocolException">A field is out of bounds.</exception>
    public byte[] EncodeForSigning()
    {
        if (SessionId.Length != SessionIdLength
            || RepositoryId.Length != ReplicationOffer.RepositoryIdLength
            || CommanderPublicKey.Length != PublicKeyLength
            || Committed.Count > MaximumListedKeys
            || (ulong)Committed.Count > CommittedCount
            || HeldObjects < CommittedCount
            || Committed.Any(key => key.Length == 0 || Encoding.UTF8.GetByteCount(key) > ReplicationInventory.MaximumKeyBytes))
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A replication receipt violates a 03 §3.5 width or bound.");
        }

        var length = SigningLabel.Length + SessionIdLength + ReplicationOffer.RepositoryIdLength + PublicKeyLength
            + sizeof(ulong) + sizeof(ulong) + sizeof(uint) + sizeof(ulong) + sizeof(ulong);
        foreach (var key in Committed)
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
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(offset), CommittedCount);
        offset += sizeof(ulong);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), (uint)Committed.Count);
        offset += sizeof(uint);
        foreach (var key in Committed)
        {
            var written = Encoding.UTF8.GetBytes(key, bytes.AsSpan(offset + sizeof(uint)));
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), (uint)written);
            offset += sizeof(uint) + written;
        }

        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(offset), HeldObjects);
        offset += sizeof(ulong);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(offset), HeldBytes);
        return bytes;
    }

    /// <summary>
    /// The inverse of <see cref="EncodeForSigning"/>: exactly those bytes,
    /// with nothing before, after or out of bounds.
    /// </summary>
    /// <param name="bytes">The signed bytes.</param>
    /// <returns>The receipt.</returns>
    /// <exception cref="PeerProtocolException">The bytes are not a receipt.</exception>
    public static ReplicationReceipt Parse(ReadOnlySpan<byte> bytes)
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
            var committedCount = BinaryPrimitives.ReadUInt64BigEndian(cursor.Take(sizeof(ulong)));
            var listed = BinaryPrimitives.ReadUInt32BigEndian(cursor.Take(sizeof(uint)));
            if (listed > MaximumListedKeys || listed > committedCount)
            {
                throw Malformed();
            }

            var committed = new List<string>((int)listed);
            for (var index = 0; index < listed; index++)
            {
                var keyLength = BinaryPrimitives.ReadUInt32BigEndian(cursor.Take(sizeof(uint)));
                if (keyLength is 0 or > ReplicationInventory.MaximumKeyBytes)
                {
                    throw Malformed();
                }

                committed.Add(Encoding.UTF8.GetString(cursor.Take((int)keyLength)));
            }

            var heldObjects = BinaryPrimitives.ReadUInt64BigEndian(cursor.Take(sizeof(ulong)));
            var heldBytes = BinaryPrimitives.ReadUInt64BigEndian(cursor.Take(sizeof(ulong)));
            if (heldObjects < committedCount || !cursor.AtEnd)
            {
                // A signed statement is exactly its bytes: trailing bytes
                // would be unsigned baggage a reader might show as attested.
                throw Malformed();
            }

            return new ReplicationReceipt(
                sessionId, repositoryId, commander, issuedAt, committedCount, committed, heldObjects, heldBytes);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A replication receipt ends before its fields do.", exception);
        }
    }

    private static PeerProtocolException Malformed() =>
        new(PeerRefusalReason.Malformed, "These bytes are not a replication receipt (03 §3.5).");

    /// <inheritdoc/>
    public bool Equals(ReplicationReceipt? other) =>
        other is not null
        && SessionId.Span.SequenceEqual(other.SessionId.Span)
        && RepositoryId.Span.SequenceEqual(other.RepositoryId.Span)
        && CommanderPublicKey.Span.SequenceEqual(other.CommanderPublicKey.Span)
        && IssuedAtUnixMilliseconds == other.IssuedAtUnixMilliseconds
        && CommittedCount == other.CommittedCount
        && Committed.SequenceEqual(other.Committed, StringComparer.Ordinal)
        && HeldObjects == other.HeldObjects
        && HeldBytes == other.HeldBytes;

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(IssuedAtUnixMilliseconds, CommittedCount, Committed.Count, HeldObjects, HeldBytes);

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
