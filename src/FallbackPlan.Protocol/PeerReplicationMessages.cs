using Bodu;
using System.Formats.Cbor;
using System.Buffers.Binary;
using System.Text;

namespace FallbackPlan.Protocol;

/// <summary>
/// A source's offer to replicate a repository to a destination
/// (specification peer-protocol 03 §3.1).
/// </summary>
/// <param name="RepositoryId">The repository the offered objects belong to (16 bytes).</param>
/// <param name="FormatCapability">The repository format the source's objects are in.</param>
/// <param name="Scope">What the source offers — <c>all</c> in this revision (03 §4).</param>
/// <param name="ReclaimPublicKey">
/// The repository's reclaim public key
/// ([ADR-0055](../../docs/adr/0055-reclaim-authority.md) §5), 32 bytes, or
/// empty when the source has none to publish. The destination records it at
/// first attribution and checks deletion instructions against it — it holds no
/// repository keys of its own, so this is the only thing it can check.
/// </param>
/// <param name="ClaimPublicKey">
/// The installation's claim public key
/// ([ADR-0053](../../docs/adr/0053-peer-claim-and-configuration-recovery.md) §1),
/// 32 bytes, or empty when the source has none to publish. Recorded at the
/// same first attribution and checked when a machine rebuilt after total loss
/// asks for the replica back. It has to travel on the ordinary offer, while
/// the owner still exists, because afterwards there is nobody left to publish
/// it.
/// </param>
public sealed record ReplicationOffer(
    ReadOnlyMemory<byte> RepositoryId,
    uint FormatCapability,
    string Scope,
    ReadOnlyMemory<byte> ReclaimPublicKey = default,
    ReadOnlyMemory<byte> ClaimPublicKey = default) : IPeerMessage
{
    /// <summary>The repository identity's length in bytes.</summary>
    public const int RepositoryIdLength = 16;

    /// <summary>The most bytes a scope token may occupy (00 §2.3).</summary>
    public const int MaximumScopeBytes = 64;

    /// <summary>An Ed25519 public key is 32 bytes.</summary>
    public const int ReclaimPublicKeyLength = 32;

    /// <summary>An Ed25519 public key is 32 bytes.</summary>
    public const int ClaimPublicKeyLength = 32;

    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.ReplicationOffer;

    /// <inheritdoc/>
    public int BodyEntryCount =>
        3 + (ReclaimPublicKey.IsEmpty ? 0 : 1) + (ClaimPublicKey.IsEmpty ? 0 : 1);

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        writer.WriteInt32(1);
        writer.WriteByteString(RepositoryId.Span);
        writer.WriteInt32(2);
        writer.WriteUInt32(FormatCapability);
        writer.WriteInt32(3);
        writer.WriteTextString(Scope);

        // Key 4, the reclaim public key
        // ([ADR-0055](../../docs/adr/0055-reclaim-authority.md) §5). Written
        // on every offer and recorded by the destination only at first
        // attribution, because a destination holds no repository keys and
        // this is the only thing it can check a deletion instruction against.
        // Omitted by a source that has none — an older build, or a write-only
        // set provisioned before the decision — and a reader that predates it
        // skips the key like any other it does not know.
        if (!ReclaimPublicKey.IsEmpty)
        {
            writer.WriteInt32(4);
            writer.WriteByteString(ReclaimPublicKey.Span);
        }

        // Key 5, the claim public key (ADR-0053 §1). Written on every offer
        // and, like key 4, recorded by the destination only at first
        // attribution — a key a later offer could replace would let whoever
        // can reach the peer nominate themselves the owner.
        if (!ClaimPublicKey.IsEmpty)
        {
            writer.WriteInt32(5);
            writer.WriteByteString(ClaimPublicKey.Span);
        }
    }

    /// <summary>Reads an offer from a body positioned after the message type.</summary>
    /// <param name="reader">The frame's reader.</param>
    /// <returns>The offer.</returns>
    /// <exception cref="PeerProtocolException">The body violates 03 §3.1 or a 00 §2.3 limit.</exception>
    public static ReplicationOffer Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        byte[]? repositoryId = null;
        uint capability = 0;
        string? scope = null;
        byte[]? reclaimPublicKey = null;
        byte[]? claimPublicKey = null;

        PeerCbor.ReadEntries(reader, key =>
        {
            switch (key)
            {
                case 1:
                    repositoryId = reader.ReadByteString();
                    break;
                case 2:
                    capability = reader.ReadUInt32();
                    break;
                case 3:
                    scope = reader.ReadTextString();
                    break;
                case 4:
                    reclaimPublicKey = reader.ReadByteString();
                    break;
                case 5:
                    claimPublicKey = reader.ReadByteString();
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        });

        if (repositoryId is null || repositoryId.Length != RepositoryIdLength
            || scope is null || Encoding.UTF8.GetByteCount(scope) > MaximumScopeBytes)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "An offer is not the shape 03 §3.1 defines.");
        }

        // A key of the wrong width is malformed rather than ignored: a
        // destination that quietly dropped it would go on accepting unsigned
        // deletion instructions while believing it had a key to check them
        // against.
        if (reclaimPublicKey is { } published && published.Length != ReclaimPublicKeyLength)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                "An offer's reclaim public key is not 32 bytes (03 §3.1).");
        }

        // And the same for the claim key: a destination that dropped a
        // malformed one would believe it had a key to check claims against,
        // and would then refuse the owner's own claim.
        if (claimPublicKey is { } claimed && claimed.Length != ClaimPublicKeyLength)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                "An offer's claim public key is not 32 bytes (03 §3.1).");
        }

        return new ReplicationOffer(
            repositoryId,
            capability,
            scope,
            reclaimPublicKey ?? ReadOnlyMemory<byte>.Empty,
            claimPublicKey ?? ReadOnlyMemory<byte>.Empty);
    }

    /// <inheritdoc/>
    public bool Equals(ReplicationOffer? other) =>
        other is not null
        && FormatCapability == other.FormatCapability
        && string.Equals(Scope, other.Scope, StringComparison.Ordinal)
        && RepositoryId.Span.SequenceEqual(other.RepositoryId.Span);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(FormatCapability, Scope, RepositoryId.Length);
}

/// <summary>
/// A destination's inventory of the objects it already holds in scope, one page
/// (specification peer-protocol 03 §3.2).
/// </summary>
/// <param name="Keys">The object keys the destination holds, this page.</param>
/// <param name="More">Whether another page follows.</param>
/// <param name="Headroom">
/// Bytes this destination can still accept under the peer's quota, as of the
/// moment the inventory was taken; null when no quota bounds it (05 §1).
/// </param>
/// <remarks>
/// The headroom rides here rather than in the hello or the terms, and the
/// choice matters. Terms are persisted in the grant and compared for
/// narrowing, so a per-session number there would raise "your friend reduced
/// your space" on every single sync. The hello is too early: the destination
/// does not yet know which repository is coming, and computing usage means
/// walking every object it holds — a cost the periodic verification sessions
/// would pay for a number nobody reads. By the inventory the scope is known
/// and <c>quota − usage</c> is already sitting in a local.
/// </remarks>
public sealed record ReplicationInventory(
    IReadOnlyList<string> Keys, bool More, ulong? Headroom = null) : IPeerMessage
{
    /// <summary>The most object keys one inventory page may carry (00 §2.3).</summary>
    public const int MaximumKeys = 4096;

    /// <summary>The most bytes an object key may occupy.</summary>
    public const int MaximumKeyBytes = 1024;

    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.ReplicationInventory;

    /// <inheritdoc/>
    public int BodyEntryCount => Headroom is null ? 2 : 3;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        if (Keys.Count > MaximumKeys)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                $"An inventory page of {Keys.Count} keys exceeds the limit of {MaximumKeys}.");
        }

        writer.WriteInt32(1);
        writer.WriteStartArray(Keys.Count);
        foreach (var objectKey in Keys)
        {
            writer.WriteTextString(objectKey);
        }

        writer.WriteEndArray();
        writer.WriteInt32(2);
        writer.WriteBoolean(More);

        // Written only when there is a ceiling: an absent key means "no quota
        // bounds this", which is not the same statement as "no room left" and
        // must not encode as zero.
        if (Headroom is { } headroom)
        {
            writer.WriteInt32(3);
            writer.WriteUInt64(headroom);
        }
    }

    /// <summary>Reads an inventory page.</summary>
    /// <param name="reader">The frame's reader.</param>
    /// <returns>The page.</returns>
    /// <exception cref="PeerProtocolException">The body violates 03 §3.2 or a 00 §2.3 limit.</exception>
    public static ReplicationInventory Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        IReadOnlyList<string>? keys = null;
        var more = false;
        ulong? headroom = null;

        PeerCbor.ReadEntries(reader, key =>
        {
            switch (key)
            {
                case 1:
                    keys = ReadKeys(reader);
                    break;
                case 2:
                    more = reader.ReadBoolean();
                    break;
                case 3:
                    headroom = reader.ReadUInt64();
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        });

        if (keys is null)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "An inventory page carried no key array.");
        }

        return new ReplicationInventory(keys, more, headroom);
    }

    private static List<string> ReadKeys(CborReader reader)
    {
        var length = reader.ReadStartArray();
        if (length is null || length > MaximumKeys)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                $"An inventory page declared more than the {MaximumKeys} keys this protocol permits.");
        }

        var keys = new List<string>(length.Value);
        for (var i = 0; i < length; i++)
        {
            var objectKey = reader.ReadTextString();
            if (objectKey.Length == 0 || Encoding.UTF8.GetByteCount(objectKey) > MaximumKeyBytes)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed, "An inventory key is empty or over the length limit.");
            }

            keys.Add(objectKey);
        }

        reader.ReadEndArray();
        return keys;
    }

    /// <inheritdoc/>
    public bool Equals(ReplicationInventory? other) =>
        other is not null && More == other.More && Headroom == other.Headroom
        && Keys.SequenceEqual(other.Keys, StringComparer.Ordinal);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(More);
        hash.Add(Headroom);
        foreach (var objectKey in Keys)
        {
            hash.Add(objectKey, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// The start of one object's bytes (specification peer-protocol 03 §3.3).
/// </summary>
/// <param name="Key">The object's store key.</param>
/// <param name="Length">The object's total length in bytes.</param>
/// <param name="ResumeOffset">
/// Where this transfer begins: zero for the whole object, and the bytes the
/// destination already staged when a cut one is being finished
/// ([ADR-0057](../../docs/adr/0057-resumable-object-transfer.md)). The source
/// decides it, never the destination — the destination declares what it holds
/// and the source verifies that claim against its own copy before agreeing to
/// skip anything.
/// </param>
public sealed record ReplicationObject(string Key, ulong Length, ulong ResumeOffset = 0) : IPeerMessage
{
    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.ReplicationObject;

    /// <inheritdoc/>
    public int BodyEntryCount => ResumeOffset > 0 ? 3 : 2;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        writer.WriteInt32(1);
        writer.WriteTextString(Key);
        writer.WriteInt32(2);
        writer.WriteUInt64(Length);

        // Omitted rather than written as zero when the object starts at the
        // beginning, so an older destination — which skips keys it does not
        // know — sees exactly the message it has always seen. A source only
        // ever sets it when "partial-object-resume" is in the intersection
        // ([ADR-0057](../../docs/adr/0057-resumable-object-transfer.md)).
        if (ResumeOffset > 0)
        {
            writer.WriteInt32(3);
            writer.WriteUInt64(ResumeOffset);
        }
    }

    /// <summary>Reads an object header.</summary>
    /// <param name="reader">The frame's reader.</param>
    /// <returns>The header.</returns>
    /// <exception cref="PeerProtocolException">The body violates 03 §3.3.</exception>
    public static ReplicationObject Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        string? objectKey = null;
        ulong length = 0;
        ulong resumeOffset = 0;

        PeerCbor.ReadEntries(reader, key =>
        {
            switch (key)
            {
                case 1:
                    objectKey = reader.ReadTextString();
                    break;
                case 2:
                    length = reader.ReadUInt64();
                    break;
                case 3:
                    resumeOffset = reader.ReadUInt64();
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        });

        if (objectKey is null || objectKey.Length == 0
            || Encoding.UTF8.GetByteCount(objectKey) > ReplicationInventory.MaximumKeyBytes)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "An object header names no key, or one over the length limit.");
        }

        // A resume point outside the object is not a transfer this destination
        // could complete: it would leave a gap no later chunk can fill, and
        // the commit would publish bytes nobody sent.
        if (resumeOffset > length)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                $"An object header resumes at {resumeOffset}, past the {length} bytes it declares.");
        }

        return new ReplicationObject(objectKey, length, resumeOffset);
    }
}

/// <summary>
/// A slice of the current object's bytes (specification peer-protocol 03 §3.3).
/// </summary>
/// <param name="Offset">The offset of these bytes within the current object.</param>
/// <param name="Bytes">The bytes.</param>
public sealed record ReplicationChunk(ulong Offset, ReadOnlyMemory<byte> Bytes) : IPeerMessage
{
    /// <summary>The most bytes of an object one chunk may carry (00 §2.3).</summary>
    public const int MaximumBytes = 1024 * 1024;

    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.ReplicationChunk;

    /// <inheritdoc/>
    public int BodyEntryCount => 2;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        if (Bytes.Length > MaximumBytes)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                $"A chunk of {Bytes.Length} bytes exceeds the limit of {MaximumBytes}.");
        }

        writer.WriteInt32(1);
        writer.WriteUInt64(Offset);
        writer.WriteInt32(2);
        writer.WriteByteString(Bytes.Span);
    }

    /// <summary>Reads a chunk.</summary>
    /// <param name="reader">The frame's reader.</param>
    /// <returns>The chunk.</returns>
    /// <exception cref="PeerProtocolException">The body violates 03 §3.3 or the chunk limit.</exception>
    public static ReplicationChunk Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        ulong offset = 0;
        byte[]? bytes = null;

        PeerCbor.ReadEntries(reader, key =>
        {
            switch (key)
            {
                case 1:
                    offset = reader.ReadUInt64();
                    break;
                case 2:
                    bytes = reader.ReadByteString();
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        });

        if (bytes is null || bytes.Length > MaximumBytes)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A chunk carried no bytes, or more than the limit.");
        }

        return new ReplicationChunk(offset, bytes);
    }

    /// <inheritdoc/>
    public bool Equals(ReplicationChunk? other) =>
        other is not null && Offset == other.Offset && Bytes.Span.SequenceEqual(other.Bytes.Span);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Offset, Bytes.Length);
}

/// <summary>
/// What the destination already holds part of, so a transfer cut inside an
/// object can begin where it stopped (specification peer-protocol 03 §3.4;
/// [ADR-0057](../../docs/adr/0057-resumable-object-transfer.md)).
/// </summary>
/// <remarks>
/// <para>
/// Sent by the destination once, after its last inventory page, and only when
/// "partial-object-resume" is in the session's intersection. A partial is a
/// different kind of fact from the inventory's: the inventory says what the
/// replica holds, and this says what a scratch file holds — bytes that are in
/// no store, answer no read, and may yet be thrown away.
/// </para>
/// <para>
/// The digest is over exactly the staged bytes, computed from the file at the
/// moment of declaring rather than remembered from when they arrived, so a
/// staged prefix that rotted on disk fails the comparison instead of being
/// resumed on top of.
/// </para>
/// </remarks>
/// <param name="Keys">The objects part held.</param>
/// <param name="StagedLengths">How many bytes of each, parallel to <paramref name="Keys"/>.</param>
/// <param name="Digests">SHA-256 of each staged prefix, parallel to <paramref name="Keys"/>.</param>
public sealed record ReplicationPartial(
    IReadOnlyList<string> Keys,
    IReadOnlyList<ulong> StagedLengths,
    IReadOnlyList<ReadOnlyMemory<byte>> Digests) : IPeerMessage
{
    /// <summary>How many partials one message may declare.</summary>
    /// <remarks>
    /// A destination has at most one transfer in flight per session, so a
    /// healthy peer declares nought or one. The cap is for the unhealthy one:
    /// it bounds the work a peer can ask a source to do by claiming, and it is
    /// generous enough that an installation which lost power mid-transfer on
    /// several repositories still declares them all.
    /// </remarks>
    public const int MaximumEntries = 64;

    /// <summary>The length of a staged-prefix digest.</summary>
    public const int DigestLength = 32;

    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.ReplicationPartial;

    /// <inheritdoc/>
    public int BodyEntryCount => 3;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        if (Keys.Count != StagedLengths.Count || Keys.Count != Digests.Count)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A partial declaration's three arrays are not the same length.");
        }

        writer.WriteInt32(1);
        writer.WriteStartArray(Keys.Count);
        foreach (var key in Keys)
        {
            writer.WriteTextString(key);
        }

        writer.WriteEndArray();

        writer.WriteInt32(2);
        writer.WriteStartArray(StagedLengths.Count);
        foreach (var staged in StagedLengths)
        {
            writer.WriteUInt64(staged);
        }

        writer.WriteEndArray();

        writer.WriteInt32(3);
        writer.WriteStartArray(Digests.Count);
        foreach (var digest in Digests)
        {
            writer.WriteByteString(digest.Span);
        }

        writer.WriteEndArray();
    }

    /// <summary>Reads a partial declaration.</summary>
    /// <param name="reader">The frame's reader.</param>
    /// <returns>The declaration.</returns>
    /// <exception cref="PeerProtocolException">The body violates 03 §3.4.</exception>
    public static ReplicationPartial Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        List<string>? keys = null;
        List<ulong>? staged = null;
        List<ReadOnlyMemory<byte>>? digests = null;

        PeerCbor.ReadEntries(reader, key =>
        {
            switch (key)
            {
                case 1:
                    keys = ReadPartialKeys(reader);
                    break;
                case 2:
                    staged = ReadStagedLengths(reader);
                    break;
                case 3:
                    digests = ReadDigests(reader);
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        });

        keys ??= [];
        staged ??= [];
        digests ??= [];

        // Refused, not trimmed to the shortest: three arrays that disagree are
        // a peer that does not mean what this message means, and pairing a key
        // with somebody else's digest is how a resume lands bytes in the wrong
        // object.
        if (keys.Count != staged.Count || keys.Count != digests.Count)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                $"A partial declaration carried {keys.Count} key(s), {staged.Count} length(s) "
                + $"and {digests.Count} digest(s).");
        }

        return new ReplicationPartial(keys, staged, digests);
    }

    private static int ArrayLength(CborReader reader)
    {
        var length = reader.ReadStartArray();
        if (length is null || length > MaximumEntries)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                $"A partial declaration carried more than the {MaximumEntries} entries this protocol permits.");
        }

        return length.Value;
    }

    private static List<string> ReadPartialKeys(CborReader reader)
    {
        var count = ArrayLength(reader);
        var keys = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            var key = reader.ReadTextString();
            if (key.Length == 0 || Encoding.UTF8.GetByteCount(key) > ReplicationInventory.MaximumKeyBytes)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed, "A partial declaration names an empty or over-long key.");
            }

            keys.Add(key);
        }

        reader.ReadEndArray();
        return keys;
    }

    private static List<ulong> ReadStagedLengths(CborReader reader)
    {
        var count = ArrayLength(reader);
        var lengths = new List<ulong>(count);
        for (var index = 0; index < count; index++)
        {
            lengths.Add(reader.ReadUInt64());
        }

        reader.ReadEndArray();
        return lengths;
    }

    private static List<ReadOnlyMemory<byte>> ReadDigests(CborReader reader)
    {
        var count = ArrayLength(reader);
        var digests = new List<ReadOnlyMemory<byte>>(count);
        for (var index = 0; index < count; index++)
        {
            var digest = reader.ReadByteString();

            // Malformed rather than skipped, for the reason the reclaim key is
            // (ADR-0055 §5): a digest of the wrong width is the check this
            // message exists for, arriving broken. Dropping it quietly would
            // turn a verification into a shrug.
            if (digest.Length != DigestLength)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed,
                    $"A partial declaration's digest is {digest.Length} bytes; {DigestLength} were expected.");
            }

            digests.Add(digest);
        }

        reader.ReadEndArray();
        return digests;
    }
}

/// <summary>
/// The source has sent every object in scope (specification peer-protocol 03 §3.4).
/// </summary>
/// <param name="Count">The number of objects the source sent this scope.</param>
public sealed record ReplicationComplete(ulong Count) : IPeerMessage
{
    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.ReplicationComplete;

    /// <inheritdoc/>
    public int BodyEntryCount => 1;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        writer.WriteInt32(1);
        writer.WriteUInt64(Count);
    }

    /// <summary>Reads a completion.</summary>
    /// <param name="reader">The frame's reader.</param>
    /// <returns>The completion.</returns>
    public static ReplicationComplete Read(CborReader reader) => new(ReadCount(reader));

    internal static ulong ReadCount(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        ulong count = 0;
        PeerCbor.ReadEntries(reader, key =>
        {
            if (key == 1)
            {
                count = reader.ReadUInt64();
            }
            else
            {
                reader.SkipValue();
            }
        });

        return count;
    }
}

/// <summary>
/// The destination confirms what it received and committed
/// (specification peer-protocol 03 §3.4).
/// </summary>
/// <param name="Count">The number of objects the destination received and committed.</param>
public sealed record ReplicationAck(ulong Count) : IPeerMessage
{
    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.ReplicationAck;

    /// <inheritdoc/>
    public int BodyEntryCount => 1;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        writer.WriteInt32(1);
        writer.WriteUInt64(Count);
    }

    /// <summary>Reads an acknowledgement.</summary>
    /// <param name="reader">The frame's reader.</param>
    /// <returns>The acknowledgement.</returns>
    public static ReplicationAck Read(CborReader reader) => new(ReplicationComplete.ReadCount(reader));
}

/// <summary>
/// A commander's instruction naming the store keys a replica drops
/// (specification peer-protocol 06 §4.1), one page. Feature-gated as
/// <c>retention-instruction</c>: the hub computes — only it can read
/// manifests — and the spoke deletes exactly what it is told, bounded below
/// by its own granted floor.
/// </summary>
/// <param name="RepositoryId">The repository the instruction applies to (16 bytes).</param>
/// <param name="Keys">The store keys to delete, this page.</param>
/// <param name="More">Whether another page follows.</param>
/// <param name="Signature">
/// An Ed25519 signature over <see cref="EncodeForSigning"/> under the
/// repository's reclaim key
/// ([ADR-0055](../../docs/adr/0055-reclaim-authority.md) §5), 64 bytes, or
/// empty when the commander has none to make. A spoke that negotiated
/// <c>signed-retention</c> announces that it will refuse a page without one
/// — an announcement rather than a gate, since a spoke enforces on the
/// reclaim key it recorded and not on what the sender chose to offer.
/// </param>
public sealed record RetentionOffer(
    ReadOnlyMemory<byte> RepositoryId,
    IReadOnlyList<string> Keys,
    bool More,
    ReadOnlyMemory<byte> Signature = default) : IPeerMessage
{
    /// <summary>The most keys one page may carry (06 §4.1).</summary>
    public const int MaximumKeys = 4096;

    /// <summary>An Ed25519 signature is 64 bytes.</summary>
    public const int SignatureLength = 64;

    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.RetentionOffer;

    /// <inheritdoc/>
    public int BodyEntryCount => Signature.IsEmpty ? 3 : 4;

    /// <summary>
    /// The bytes a page's signature covers: the repository identity, then each
    /// key length-prefixed in the order sent, then the continuation flag.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Length-prefixed rather than delimited, so no key can be split or joined
    /// by its own contents — a separator-delimited encoding would let two
    /// different drop-lists produce identical signed bytes.
    /// </para>
    /// <para>
    /// <b>Per page, and bound to one session.</b> Each page stands alone, so a
    /// page cannot be forged and cannot be edited. Passing the session's
    /// identifier ([02 §3.5](../../specifications/peer-protocol/02-session.md))
    /// also stops it being <em>replayed</em>: the signature then covers a value
    /// neither end chose and no other connection shares, so a recording of the
    /// exchange verifies against nothing later. Without it the signature says
    /// who authorised the instruction and not when, which is what a captured
    /// page exploits.
    /// </para>
    /// <para>
    /// A page can still be <em>dropped</em> by the sender, which deletes less
    /// than was instructed and is the safe direction. It cannot be dropped or
    /// reordered by anyone else: the pages ride one TLS stream, so there is no
    /// position between the endpoints from which to do either.
    /// </para>
    /// </remarks>
    /// <param name="sessionBinding">
    /// The session's identifier, appended to the signed bytes — empty for the
    /// unbound encoding a peering that predates the reclaim key still uses.
    /// </param>
    /// <returns>The canonical signed bytes.</returns>
    public byte[] EncodeForSigning(ReadOnlySpan<byte> sessionBinding = default)
    {
        var length = RepositoryId.Length + sizeof(uint) + sessionBinding.Length;
        foreach (var objectKey in Keys)
        {
            length += sizeof(uint) + Encoding.UTF8.GetByteCount(objectKey);
        }

        var bytes = new byte[length + 1];
        var offset = 0;
        RepositoryId.Span.CopyTo(bytes);
        offset += RepositoryId.Length;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), (uint)Keys.Count);
        offset += sizeof(uint);

        foreach (var objectKey in Keys)
        {
            var written = Encoding.UTF8.GetBytes(objectKey, bytes.AsSpan(offset + sizeof(uint)));
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), (uint)written);
            offset += sizeof(uint) + written;
        }

        bytes[offset] = More ? (byte)1 : (byte)0;
        offset++;

        // Last, and fixed-length, so the bound and unbound encodings share a
        // prefix and neither can be mistaken for the other: an unbound page is
        // exactly this without the tail, and a tail is always 32 bytes or none.
        sessionBinding.CopyTo(bytes.AsSpan(offset));
        return bytes;
    }

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        if (RepositoryId.Length != ReplicationOffer.RepositoryIdLength)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A retention offer's repository identifier is 16 bytes.");
        }

        if (Keys.Count > MaximumKeys)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                $"A retention page of {Keys.Count} keys exceeds the limit of {MaximumKeys}.");
        }

        writer.WriteInt32(1);
        writer.WriteByteString(RepositoryId.Span);
        writer.WriteInt32(2);
        writer.WriteStartArray(Keys.Count);
        foreach (var objectKey in Keys)
        {
            writer.WriteTextString(objectKey);
        }

        writer.WriteEndArray();
        writer.WriteInt32(3);
        writer.WriteBoolean(More);

        if (!Signature.IsEmpty)
        {
            writer.WriteInt32(4);
            writer.WriteByteString(Signature.Span);
        }
    }

    /// <summary>Reads one instruction page.</summary>
    /// <param name="reader">The frame's reader.</param>
    /// <returns>The page.</returns>
    /// <exception cref="PeerProtocolException">The body violates 06 §4.1 or a 00 §2.3 limit.</exception>
    public static RetentionOffer Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        byte[]? repositoryId = null;
        IReadOnlyList<string>? keys = null;
        var more = false;
        byte[]? signature = null;

        PeerCbor.ReadEntries(reader, key =>
        {
            switch (key)
            {
                case 1:
                    repositoryId = reader.ReadByteString();
                    break;
                case 2:
                    keys = ReadDropKeys(reader);
                    break;
                case 3:
                    more = reader.ReadBoolean();
                    break;
                case 4:
                    signature = reader.ReadByteString();
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        });

        if (repositoryId is null || repositoryId.Length != ReplicationOffer.RepositoryIdLength || keys is null)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A retention page omits its repository identifier or key array.");
        }

        // A signature of the wrong width is malformed rather than ignored: a
        // spoke that dropped it would fall back to accepting the instruction
        // unsigned, which is the check the whole feature exists to make.
        if (signature is { } carried && carried.Length != SignatureLength)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A retention page's signature is not 64 bytes (06 §4.1).");
        }

        return new RetentionOffer(repositoryId, keys, more, signature ?? ReadOnlyMemory<byte>.Empty);
    }

    private static List<string> ReadDropKeys(CborReader reader)
    {
        var length = reader.ReadStartArray();
        if (length is null || length > MaximumKeys)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                $"A retention page declared more than the {MaximumKeys} keys this protocol permits.");
        }

        var keys = new List<string>(length.Value);
        for (var i = 0; i < length; i++)
        {
            var objectKey = reader.ReadTextString();
            if (objectKey.Length == 0 || Encoding.UTF8.GetByteCount(objectKey) > ReplicationInventory.MaximumKeyBytes)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed, "A retention key is empty or over the length limit.");
            }

            keys.Add(objectKey);
        }

        reader.ReadEndArray();
        return keys;
    }

    /// <inheritdoc/>
    public bool Equals(RetentionOffer? other) =>
        other is not null && More == other.More
        && RepositoryId.Span.SequenceEqual(other.RepositoryId.Span)
        && Keys.SequenceEqual(other.Keys, StringComparer.Ordinal);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Keys.Count, More);
}

/// <summary>The spoke confirms what it deleted (specification peer-protocol 06 §4.2).</summary>
/// <param name="Deleted">Objects actually removed — a key not held counts nothing.</param>
/// <param name="Receipt">
/// The spoke's signed deletion receipt — the bytes of
/// <see cref="DeletionReceipt.EncodeForSigning"/> — or empty from a spoke
/// that predates receipts ([ADR-0063](../../docs/adr/0063-deletion-receipts.md)).
/// </param>
/// <param name="Signature">The receipt's signature under the spoke's device key; present exactly when the receipt is.</param>
public sealed record RetentionAck(
    ulong Deleted,
    ReadOnlyMemory<byte> Receipt = default,
    ReadOnlyMemory<byte> Signature = default) : IPeerMessage
{
    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.RetentionAck;

    /// <inheritdoc/>
    public int BodyEntryCount => Receipt.IsEmpty ? 1 : 3;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);
        RequireReceiptShape(Receipt, Signature);

        writer.WriteInt32(1);
        writer.WriteUInt64(Deleted);
        if (!Receipt.IsEmpty)
        {
            writer.WriteInt32(2);
            writer.WriteByteString(Receipt.Span);
            writer.WriteInt32(3);
            writer.WriteByteString(Signature.Span);
        }
    }

    /// <summary>Reads an acknowledgement.</summary>
    /// <param name="reader">The frame's reader.</param>
    /// <returns>The acknowledgement.</returns>
    /// <exception cref="PeerProtocolException">A receipt without its signature, or one that does not parse.</exception>
    public static RetentionAck Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        ulong deleted = 0;
        byte[]? receipt = null;
        byte[]? signature = null;
        PeerCbor.ReadEntries(reader, key =>
        {
            switch (key)
            {
                case 1:
                    deleted = reader.ReadUInt64();
                    break;
                case 2:
                    receipt = reader.ReadByteString();
                    break;
                case 3:
                    signature = reader.ReadByteString();
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        });

        var ack = new RetentionAck(deleted, receipt ?? [], signature ?? []);
        RequireReceiptShape(ack.Receipt, ack.Signature);
        return ack;
    }

    /// <summary>
    /// A receipt and its signature travel together or not at all, and the
    /// receipt must be one. Malformed rather than dropped, for the reclaim
    /// key's reason: an audit record silently discarded is an audit that
    /// did not happen.
    /// </summary>
    private static void RequireReceiptShape(ReadOnlyMemory<byte> receipt, ReadOnlyMemory<byte> signature)
    {
        if (receipt.IsEmpty && signature.IsEmpty)
        {
            return;
        }

        if (receipt.IsEmpty || signature.Length != DeletionReceipt.SignatureLength)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                "A retention acknowledgement's receipt and its 64-byte signature travel together (06 §4.2).");
        }

        _ = DeletionReceipt.Parse(receipt.Span);
    }

    /// <inheritdoc/>
    public bool Equals(RetentionAck? other) =>
        other is not null
        && Deleted == other.Deleted
        && Receipt.Span.SequenceEqual(other.Receipt.Span)
        && Signature.Span.SequenceEqual(other.Signature.Span);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Deleted, Receipt.Length);
}

/// <summary>
/// A keyed random-range challenge (specification peer-protocol 04 §4.1): the
/// verifier names a store key, a byte range inside it, and a fresh nonce and
/// challenge key. A proof over exactly those bytes cannot be precomputed,
/// cached, or replayed — producing it requires holding them at this moment
/// (FR-VER-001).
/// </summary>
/// <param name="RepositoryId">The repository the key belongs to (16 bytes).</param>
/// <param name="Key">The store key to prove.</param>
/// <param name="Offset">The range's byte offset.</param>
/// <param name="Length">The range's length in bytes (1–65536).</param>
/// <param name="Nonce">Fresh per challenge (16 bytes).</param>
/// <param name="ChallengeKey">Fresh per challenge (32 bytes) — freshness, not secrecy.</param>
public sealed record VerificationChallenge(
    ReadOnlyMemory<byte> RepositoryId,
    string Key,
    ulong Offset,
    uint Length,
    ReadOnlyMemory<byte> Nonce,
    ReadOnlyMemory<byte> ChallengeKey) : IPeerMessage
{
    /// <summary>The longest range one challenge may name (04 §4.1).</summary>
    public const uint MaximumLength = 65_536;

    /// <summary>The nonce's width.</summary>
    public const int NonceLength = 16;

    /// <summary>The challenge key's width.</summary>
    public const int ChallengeKeyLength = 32;

    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.VerificationChallenge;

    /// <inheritdoc/>
    public int BodyEntryCount => 6;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        if (RepositoryId.Length != ReplicationOffer.RepositoryIdLength
            || Nonce.Length != NonceLength
            || ChallengeKey.Length != ChallengeKeyLength
            || Length is 0 or > MaximumLength
            || string.IsNullOrEmpty(Key))
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A verification challenge violates a 04 §4.1 width or bound.");
        }

        writer.WriteInt32(1);
        writer.WriteByteString(RepositoryId.Span);
        writer.WriteInt32(2);
        writer.WriteTextString(Key);
        writer.WriteInt32(3);
        writer.WriteUInt64(Offset);
        writer.WriteInt32(4);
        writer.WriteUInt32(Length);
        writer.WriteInt32(5);
        writer.WriteByteString(Nonce.Span);
        writer.WriteInt32(6);
        writer.WriteByteString(ChallengeKey.Span);
    }

    /// <summary>Reads one challenge.</summary>
    /// <param name="reader">The frame's reader.</param>
    /// <returns>The challenge.</returns>
    /// <exception cref="PeerProtocolException">The body violates 04 §4.1.</exception>
    public static VerificationChallenge Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        byte[]? repositoryId = null;
        string? key = null;
        var offset = 0UL;
        var length = 0U;
        byte[]? nonce = null;
        byte[]? challengeKey = null;

        PeerCbor.ReadEntries(reader, entry =>
        {
            switch (entry)
            {
                case 1:
                    repositoryId = reader.ReadByteString();
                    break;
                case 2:
                    key = reader.ReadTextString();
                    break;
                case 3:
                    offset = reader.ReadUInt64();
                    break;
                case 4:
                    length = reader.ReadUInt32();
                    break;
                case 5:
                    nonce = reader.ReadByteString();
                    break;
                case 6:
                    challengeKey = reader.ReadByteString();
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        });

        if (repositoryId is null || repositoryId.Length != ReplicationOffer.RepositoryIdLength
            || key is null or "" || Encoding.UTF8.GetByteCount(key) > ReplicationInventory.MaximumKeyBytes
            || length is 0 or > MaximumLength
            || nonce is null || nonce.Length != NonceLength
            || challengeKey is null || challengeKey.Length != ChallengeKeyLength)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A verification challenge violates a 04 §4.1 width or bound.");
        }

        return new VerificationChallenge(repositoryId, key, offset, length, nonce, challengeKey);
    }

    /// <inheritdoc/>
    public bool Equals(VerificationChallenge? other) =>
        other is not null
        && string.Equals(Key, other.Key, StringComparison.Ordinal)
        && Offset == other.Offset && Length == other.Length
        && RepositoryId.Span.SequenceEqual(other.RepositoryId.Span)
        && Nonce.Span.SequenceEqual(other.Nonce.Span)
        && ChallengeKey.Span.SequenceEqual(other.ChallengeKey.Span);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Key, Offset, Length);
}

/// <summary>
/// The destination's answer to a challenge (specification peer-protocol
/// 04 §4.2): a proof over the exact stored bytes, or the honest statement
/// that it cannot produce one — which is the answer the challenge exists to
/// force into the open.
/// </summary>
/// <param name="Held">Whether a proof follows.</param>
/// <param name="Proof">The MAC of 04 §2; present exactly when <paramref name="Held"/>.</param>
public sealed record VerificationProof(bool Held, ReadOnlyMemory<byte> Proof) : IPeerMessage
{
    /// <summary>The proof's width.</summary>
    public const int ProofLength = 32;

    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.VerificationProof;

    /// <inheritdoc/>
    public int BodyEntryCount => Held ? 2 : 1;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        if (Held && Proof.Length != ProofLength)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A verification proof is 32 bytes.");
        }

        writer.WriteInt32(1);
        writer.WriteUInt32(Held ? 0U : 1U);
        if (Held)
        {
            writer.WriteInt32(2);
            writer.WriteByteString(Proof.Span);
        }
    }

    /// <summary>Reads one proof.</summary>
    /// <param name="reader">The frame's reader.</param>
    /// <returns>The proof.</returns>
    /// <exception cref="PeerProtocolException">Status and proof disagree (04 §4.2).</exception>
    public static VerificationProof Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        uint? status = null;
        byte[]? proof = null;

        PeerCbor.ReadEntries(reader, entry =>
        {
            switch (entry)
            {
                case 1:
                    status = reader.ReadUInt32();
                    break;
                case 2:
                    proof = reader.ReadByteString();
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        });

        return status switch
        {
            0 when proof is { Length: ProofLength } => new VerificationProof(true, proof),
            1 when proof is null => new VerificationProof(false, ReadOnlyMemory<byte>.Empty),
            _ => throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                "A verification proof's status and payload disagree (04 §4.2)."),
        };
    }

    /// <inheritdoc/>
    public bool Equals(VerificationProof? other) =>
        other is not null && Held == other.Held && Proof.Span.SequenceEqual(other.Proof.Span);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Held, Proof.Length);
}

/// <summary>
/// A claimant opens the claim ceremony (specification peer-protocol 03 §6;
/// [ADR-0053](../../docs/adr/0053-peer-claim-and-configuration-recovery.md)
/// Amendment 2): it holds the passphrase and nothing else — no kit, no
/// repository id, no salt — and asks the destination which derivations to
/// run. Empty by design: there is nothing a claimant could say yet.
/// </summary>
public sealed record ReplicationClaimOpen : IPeerMessage
{
    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.ReplicationClaimOpen;

    /// <inheritdoc/>
    public int BodyEntryCount => 0;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer) => ThrowHelper.ThrowIfNull(writer);

    /// <summary>Reads an open — which carries nothing, and skips anything a later version adds.</summary>
    /// <param name="reader">The body reader.</param>
    public static ReplicationClaimOpen Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);
        PeerCbor.ReadEntries(reader, _ => reader.SkipValue());
        return new ReplicationClaimOpen();
    }
}

/// <summary>
/// The derivations a claimant may prove itself under (specification
/// peer-protocol 03 §6): the distinct KDF salt-and-parameter pairs behind
/// every replica this destination holds whose attribution carries a claim
/// key, read from each replica's own descriptor.
/// </summary>
/// <remarks>
/// <para>
/// A claim key derives from the passphrase and the installation's salt, and
/// a claimant that has lost everything holds only the passphrase. The salt is
/// inside the replica, behind the attribution gate, so the destination hands
/// it over: to a <em>paired</em> peer, and only what a paired peer could
/// learn anyway by holding a replica — a salt and public parameters are not
/// secrets, and without a verifier beside them they are not an oracle either.
/// What is deliberately <b>not</b> here: repository ids, sealing public keys
/// (a real offline wrong-passphrase verifier), and which fingerprint each
/// pair belongs to.
/// </para>
/// <para>
/// Sorted by salt, so the answer says nothing about the order attributions
/// were recorded in; distinct, so one installation storing many sets here
/// costs the claimant one derivation. The caps are for the claimant's sake:
/// it runs Argon2id on parameters the destination chose, so a destination
/// that named a terabyte of memory would be naming a denial of service, and
/// the reader refuses such an entry as malformed rather than attempting it.
/// </para>
/// </remarks>
/// <param name="Salts">Each 16-byte KDF salt.</param>
/// <param name="MemoryKiB">Argon2id memory per entry, parallel to <paramref name="Salts"/>.</param>
/// <param name="Iterations">Argon2id iterations per entry, parallel to <paramref name="Salts"/>.</param>
/// <param name="Parallelism">Argon2id parallelism per entry, parallel to <paramref name="Salts"/>.</param>
public sealed record ReplicationClaimParameters(
    IReadOnlyList<ReadOnlyMemory<byte>> Salts,
    IReadOnlyList<uint> MemoryKiB,
    IReadOnlyList<uint> Iterations,
    IReadOnlyList<byte> Parallelism) : IPeerMessage
{
    /// <summary>A KDF salt is 16 bytes (repository-format 01 §3.3).</summary>
    public const int SaltLength = 16;

    /// <summary>
    /// The most derivations one answer may name. One installation is one
    /// pair; the bound is for a destination that stores for many and a
    /// claimant that must run each.
    /// </summary>
    public const int MaximumEntries = 16;

    /// <summary>The most memory an entry may ask the claimant to spend: 1 GiB.</summary>
    public const uint MaximumMemoryKiB = 1024 * 1024;

    /// <summary>The most iterations an entry may ask for.</summary>
    public const uint MaximumIterations = 64;

    /// <summary>The most parallelism an entry may ask for.</summary>
    public const byte MaximumParallelism = 64;

    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.ReplicationClaimParameters;

    /// <inheritdoc/>
    public int BodyEntryCount => 4;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        if (Salts.Count != MemoryKiB.Count || Salts.Count != Iterations.Count || Salts.Count != Parallelism.Count)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A claim-parameters answer's four arrays are not the same length.");
        }

        writer.WriteInt32(1);
        writer.WriteStartArray(Salts.Count);
        foreach (var salt in Salts)
        {
            writer.WriteByteString(salt.Span);
        }

        writer.WriteEndArray();

        writer.WriteInt32(2);
        writer.WriteStartArray(MemoryKiB.Count);
        foreach (var memory in MemoryKiB)
        {
            writer.WriteUInt32(memory);
        }

        writer.WriteEndArray();

        writer.WriteInt32(3);
        writer.WriteStartArray(Iterations.Count);
        foreach (var iterations in Iterations)
        {
            writer.WriteUInt32(iterations);
        }

        writer.WriteEndArray();

        writer.WriteInt32(4);
        writer.WriteStartArray(Parallelism.Count);
        foreach (var parallelism in Parallelism)
        {
            writer.WriteUInt32(parallelism);
        }

        writer.WriteEndArray();
    }

    /// <summary>Reads a claim-parameters answer.</summary>
    /// <param name="reader">The body reader.</param>
    /// <exception cref="PeerProtocolException">The body is not the shape 03 §6 defines, or an entry exceeds the caps.</exception>
    public static ReplicationClaimParameters Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        List<ReadOnlyMemory<byte>>? salts = null;
        List<uint>? memory = null;
        List<uint>? iterations = null;
        List<byte>? parallelism = null;

        PeerCbor.ReadEntries(reader, key =>
        {
            switch (key)
            {
                case 1:
                    salts = ReadSalts(reader);
                    break;
                case 2:
                    memory = ReadBounded(reader, MaximumMemoryKiB, "memory");
                    break;
                case 3:
                    iterations = ReadBounded(reader, MaximumIterations, "iterations");
                    break;
                case 4:
                    parallelism = [.. ReadBounded(reader, MaximumParallelism, "parallelism").Select(value => (byte)value)];
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        });

        salts ??= [];
        memory ??= [];
        iterations ??= [];
        parallelism ??= [];

        if (salts.Count != memory.Count || salts.Count != iterations.Count || salts.Count != parallelism.Count)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                $"A claim-parameters answer carried {salts.Count} salt(s), {memory.Count} memory value(s), "
                + $"{iterations.Count} iteration count(s) and {parallelism.Count} parallelism value(s).");
        }

        // Sorted by salt is part of the shape, not a courtesy: an unsorted
        // answer would let a destination encode the order attributions were
        // recorded in, and a reader that quietly sorted would hide that.
        for (var index = 1; index < salts.Count; index++)
        {
            if (salts[index - 1].Span.SequenceCompareTo(salts[index].Span) >= 0)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed, "A claim-parameters answer is not sorted by salt, or repeats one.");
            }
        }

        return new ReplicationClaimParameters(salts, memory, iterations, parallelism);
    }

    private static int ArrayLength(CborReader reader)
    {
        var length = reader.ReadStartArray();
        if (length is null || length > MaximumEntries)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                $"A claim-parameters answer carried more than the {MaximumEntries} entries this protocol permits.");
        }

        return length.Value;
    }

    private static List<ReadOnlyMemory<byte>> ReadSalts(CborReader reader)
    {
        var count = ArrayLength(reader);
        var salts = new List<ReadOnlyMemory<byte>>(count);
        for (var index = 0; index < count; index++)
        {
            var salt = reader.ReadByteString();
            if (salt.Length != SaltLength)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed, "A claim-parameters answer named a salt that is not 16 bytes.");
            }

            salts.Add(salt);
        }

        reader.ReadEndArray();
        return salts;
    }

    private static List<uint> ReadBounded(CborReader reader, uint maximum, string what)
    {
        var count = ArrayLength(reader);
        var values = new List<uint>(count);
        for (var index = 0; index < count; index++)
        {
            var value = reader.ReadUInt32();

            // Refused rather than clamped: the claimant is about to run
            // Argon2id on this, and a value it did not choose is a cost it did
            // not agree to. Zero is refused for the other reason — the
            // primitive will not accept it, and a reader that passed it on
            // would fail later with a worse message.
            if (value == 0 || value > maximum)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed,
                    $"A claim-parameters answer names Argon2id {what} of {value}, outside 1–{maximum}.");
            }

            values.Add(value);
        }

        reader.ReadEndArray();
        return values;
    }

    /// <inheritdoc/>
    public bool Equals(ReplicationClaimParameters? other) =>
        other is not null
        && Salts.Count == other.Salts.Count
        && Salts.Select((salt, index) => salt.Span.SequenceEqual(other.Salts[index].Span)).All(same => same)
        && MemoryKiB.SequenceEqual(other.MemoryKiB)
        && Iterations.SequenceEqual(other.Iterations)
        && Parallelism.SequenceEqual(other.Parallelism);

    /// <inheritdoc/>
    public override int GetHashCode() => Salts.Count;
}

/// <summary>
/// A machine rebuilt after total loss proves a replica is its own
/// (specification peer-protocol 03 §6;
/// [ADR-0053](../../docs/adr/0053-peer-claim-and-configuration-recovery.md)):
/// one entry per derivation the destination served in
/// <see cref="ReplicationClaimParameters"/>, each a claim public key and a
/// signature under its private half over the session-bound material.
/// </summary>
/// <remarks>
/// <para>
/// <b>It names no repository, and that is the decision.</b> ADR-0053 §2 had
/// the claimant sign over a repository id, which a claimant cannot supply: a
/// machine that lost everything holds a passphrase and nothing else. So the
/// claim public key is the selector. The destination re-attributes whatever it
/// recorded that key against, which for one installation may be several
/// repositories at once, and says which in its answer.
/// </para>
/// <para>
/// One entry per served derivation, because the claimant cannot tell which
/// salt is its own — every pair it was given yields a well-formed key, and
/// only the destination knows which one it recorded. The entries the
/// destination recognises re-point their attributions; the rest are simply
/// keys nobody here has heard of. A claim that recognises nothing refuses
/// exactly as one whose signature is wrong.
/// </para>
/// <para>
/// §2 also had the destination answer with a fresh random nonce for the
/// claimant to sign, a round trip whose only purpose was freshness. The
/// session identifier ([02 §3.5](../../specifications/peer-protocol/02-session.md))
/// is already fresh per connection, already derived from both sides'
/// contributions including both TLS keys, and already known to both ends
/// before the claim — so the signed material is the session's.
/// </para>
/// </remarks>
/// <param name="ClaimPublicKeys">The claimant's Ed25519 claim public keys, one per served derivation.</param>
/// <param name="Signatures">
/// Ed25519 over <see cref="EncodeForSigning"/>, parallel to
/// <paramref name="ClaimPublicKeys"/>, each under the private half the
/// claimant derived from the passphrase and that entry's salt.
/// </param>
public sealed record ReplicationClaim(
    IReadOnlyList<ReadOnlyMemory<byte>> ClaimPublicKeys,
    IReadOnlyList<ReadOnlyMemory<byte>> Signatures) : IPeerMessage
{
    /// <summary>An Ed25519 public key is 32 bytes.</summary>
    public const int ClaimPublicKeyLength = 32;

    /// <summary>An Ed25519 signature is 64 bytes.</summary>
    public const int SignatureLength = 64;

    /// <summary>The most entries a claim may carry — one per served derivation, so the same bound.</summary>
    public const int MaximumEntries = ReplicationClaimParameters.MaximumEntries;

    private static ReadOnlySpan<byte> SigningLabel => "fbp-peer-v1:replica-claim"u8;

    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.ReplicationClaim;

    /// <inheritdoc/>
    public int BodyEntryCount => 2;

    /// <summary>
    /// The bytes a claim is signed over:
    /// <c>"fbp-peer-v1:replica-claim" ‖ session_id ‖ claimant_fingerprint</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The session identifier is what makes a captured claim useless in a
    /// later connection, exactly as it does for a retention instruction
    /// ([ADR-0059](../../docs/adr/0059-session-bound-deletion-authority.md)) —
    /// and it matters more here, because a replayed claim re-points ownership
    /// rather than deleting one page.
    /// </para>
    /// <para>
    /// The fingerprint is redundant with that identifier today: the session
    /// binding is a hash over both peer identities, so it already covers
    /// which device is asking. It is written out anyway so that the signed
    /// statement names its own subject — a reader can see <em>whom</em> the
    /// attribution is being moved to without having to trust that a hash
    /// covered it.
    /// </para>
    /// <para>
    /// Both fields are fixed-length, so the concatenation is unambiguous
    /// without a separator ([00 §4](../../specifications/peer-protocol/00-conventions.md)).
    /// The same bytes are signed under every entry's key: the entries differ
    /// by key, not by statement.
    /// </para>
    /// </remarks>
    /// <param name="sessionId">This session's 32-byte identifier (02 §3.5).</param>
    /// <param name="claimantFingerprint">The claimant's peer fingerprint, lower-hex.</param>
    public static byte[] EncodeForSigning(ReadOnlySpan<byte> sessionId, string claimantFingerprint)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(claimantFingerprint);

        var fingerprint = Encoding.UTF8.GetBytes(claimantFingerprint);
        var bytes = new byte[SigningLabel.Length + sessionId.Length + fingerprint.Length];
        SigningLabel.CopyTo(bytes);
        sessionId.CopyTo(bytes.AsSpan(SigningLabel.Length));
        fingerprint.CopyTo(bytes, SigningLabel.Length + sessionId.Length);
        return bytes;
    }

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        if (ClaimPublicKeys.Count != Signatures.Count)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A claim's two arrays are not the same length.");
        }

        writer.WriteInt32(1);
        writer.WriteStartArray(ClaimPublicKeys.Count);
        foreach (var key in ClaimPublicKeys)
        {
            writer.WriteByteString(key.Span);
        }

        writer.WriteEndArray();

        writer.WriteInt32(2);
        writer.WriteStartArray(Signatures.Count);
        foreach (var signature in Signatures)
        {
            writer.WriteByteString(signature.Span);
        }

        writer.WriteEndArray();
    }

    /// <summary>Reads a claim.</summary>
    /// <param name="reader">The body reader.</param>
    /// <exception cref="PeerProtocolException">The body is not the shape 03 §6 defines.</exception>
    public static ReplicationClaim Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        List<ReadOnlyMemory<byte>>? keys = null;
        List<ReadOnlyMemory<byte>>? signatures = null;

        PeerCbor.ReadEntries(reader, entry =>
        {
            switch (entry)
            {
                case 1:
                    keys = ReadFixed(reader, ClaimPublicKeyLength, "claim public key");
                    break;
                case 2:
                    signatures = ReadFixed(reader, SignatureLength, "signature");
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        });

        keys ??= [];
        signatures ??= [];

        // Refused rather than trimmed, for the partial declaration's reason:
        // pairing a key with somebody else's signature is not a claim.
        if (keys.Count == 0 || keys.Count != signatures.Count)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                $"A claim carried {keys.Count} key(s) and {signatures.Count} signature(s); it needs at least one of each, matched.");
        }

        return new ReplicationClaim(keys, signatures);
    }

    private static List<ReadOnlyMemory<byte>> ReadFixed(CborReader reader, int length, string what)
    {
        var count = reader.ReadStartArray();
        if (count is null || count > MaximumEntries)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                $"A claim carried more than the {MaximumEntries} entries this protocol permits.");
        }

        var values = new List<ReadOnlyMemory<byte>>(count.Value);
        for (var index = 0; index < count.Value; index++)
        {
            var value = reader.ReadByteString();
            if (value.Length != length)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed, $"A claim carried a {what} that is not {length} bytes.");
            }

            values.Add(value);
        }

        reader.ReadEndArray();
        return values;
    }

    /// <inheritdoc/>
    public bool Equals(ReplicationClaim? other) =>
        other is not null
        && ClaimPublicKeys.Count == other.ClaimPublicKeys.Count
        && Signatures.Count == other.Signatures.Count
        && ClaimPublicKeys.Select((key, index) => key.Span.SequenceEqual(other.ClaimPublicKeys[index].Span)).All(same => same)
        && Signatures.Select((signature, index) => signature.Span.SequenceEqual(other.Signatures[index].Span)).All(same => same);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(ClaimPublicKeys.Count, Signatures.Count);
}

/// <summary>
/// What a claim re-attributed (specification peer-protocol 03 §6).
/// </summary>
/// <remarks>
/// The claimant could not have known to ask for these: it named no repository
/// because it holds no repository id. Having proved the key, it learns which
/// replicas are now pointed at it, and that is the list it opens for
/// retrieval.
/// </remarks>
/// <param name="RepositoryIds">The repositories now attributed to the claimant, 16 bytes each.</param>
public sealed record ReplicationClaimAccepted(
    IReadOnlyList<ReadOnlyMemory<byte>> RepositoryIds) : IPeerMessage
{
    /// <summary>The most repositories one claim may re-attribute.</summary>
    /// <remarks>
    /// One installation's sets, not one household's objects, so the bound is
    /// generous rather than tight — but it is a bound, because a length a
    /// peer chooses is a length a peer could choose badly.
    /// </remarks>
    public const int MaximumEntries = 1024;

    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.ReplicationClaimAccepted;

    /// <inheritdoc/>
    public int BodyEntryCount => 1;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        writer.WriteInt32(1);
        writer.WriteStartArray(RepositoryIds.Count);
        for (var index = 0; index < RepositoryIds.Count; index++)
        {
            writer.WriteByteString(RepositoryIds[index].Span);
        }

        writer.WriteEndArray();
    }

    /// <summary>Reads an acceptance.</summary>
    /// <param name="reader">The body reader.</param>
    /// <exception cref="PeerProtocolException">The body is not the shape 03 §6 defines.</exception>
    public static ReplicationClaimAccepted Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        List<ReadOnlyMemory<byte>>? ids = null;

        PeerCbor.ReadEntries(reader, entry =>
        {
            if (entry != 1)
            {
                reader.SkipValue();
                return;
            }

            var length = reader.ReadStartArray();
            if (length is null || length > MaximumEntries)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed,
                    $"An acceptance carried more than the {MaximumEntries} entries this protocol permits.");
            }

            ids = new List<ReadOnlyMemory<byte>>(length.Value);
            for (var index = 0; index < length.Value; index++)
            {
                var id = reader.ReadByteString();
                if (id.Length != ReplicationOffer.RepositoryIdLength)
                {
                    throw new PeerProtocolException(
                        PeerRefusalReason.Malformed, "An acceptance named a repository that is not 16 bytes.");
                }

                ids.Add(id);
            }

            reader.ReadEndArray();
        });

        return new ReplicationClaimAccepted(ids ?? []);
    }

    /// <inheritdoc/>
    public bool Equals(ReplicationClaimAccepted? other) =>
        other is not null
        && RepositoryIds.Count == other.RepositoryIds.Count
        && RepositoryIds.Select((id, index) => id.Span.SequenceEqual(other.RepositoryIds[index].Span)).All(same => same);

    /// <inheritdoc/>
    public override int GetHashCode() => RepositoryIds.Count;
}
