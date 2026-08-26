using Bodu;
using System.Formats.Cbor;
using System.Text;

namespace FallbackPlan.Protocol;

/// <summary>
/// A source's offer to replicate a repository to a destination
/// (specification peer-protocol 03 §3.1).
/// </summary>
/// <param name="RepositoryId">The repository the offered objects belong to (16 bytes).</param>
/// <param name="FormatCapability">The repository format the source's objects are in.</param>
/// <param name="Scope">What the source offers — <c>all</c> in this revision (03 §4).</param>
public sealed record ReplicationOffer(
    ReadOnlyMemory<byte> RepositoryId, uint FormatCapability, string Scope) : IPeerMessage
{
    /// <summary>The repository identity's length in bytes.</summary>
    public const int RepositoryIdLength = 16;

    /// <summary>The most bytes a scope token may occupy (00 §2.3).</summary>
    public const int MaximumScopeBytes = 64;

    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.ReplicationOffer;

    /// <inheritdoc/>
    public int BodyEntryCount => 3;

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

        return new ReplicationOffer(repositoryId, capability, scope);
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
/// <param name="ClaimToken">
/// This destination's own token for the offered repository, carried on the
/// final page and only while no claim credential is registered for it
/// (03 §3.2.1). Null the rest of the time, and a source reads that as "not
/// asked" rather than as an empty token it should answer.
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
    IReadOnlyList<string> Keys,
    bool More,
    ulong? Headroom = null,
    ReadOnlyMemory<byte>? ClaimToken = null) : IPeerMessage
{
    /// <summary>A claim token is 16 bytes (07 §5.3).</summary>
    public const int ClaimTokenLength = 16;

    /// <summary>The most object keys one inventory page may carry (00 §2.3).</summary>
    public const int MaximumKeys = 4096;

    /// <summary>The most bytes an object key may occupy.</summary>
    public const int MaximumKeyBytes = 1024;

    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.ReplicationInventory;

    /// <inheritdoc/>
    public int BodyEntryCount => 2 + (Headroom is null ? 0 : 1) + (ClaimToken is null ? 0 : 1);

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

        // Sent once, and only while this destination holds no claim credential
        // for the repository: it is how disaster recovery is armed a session
        // ahead of the disaster (03 §3.2.1). A destination that already holds
        // one never offers again — a second offer would invite a source to
        // derive a keypair and register nothing.
        if (ClaimToken is { } token)
        {
            if (token.Length != ClaimTokenLength)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed,
                    $"A claim token of {token.Length} bytes is not the {ClaimTokenLength} bytes 07 §5.3 defines.");
            }

            writer.WriteInt32(4);
            writer.WriteByteString(token.Span);
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

        // Held as the nullable memory it is returned as, not as a byte[] that
        // is converted at the end. A null array assigned to
        // ReadOnlyMemory<byte>? lifts to HasValue TRUE wrapping an empty
        // memory, so every page that carried no key 4 would read back as an
        // offer of nothing — and a source would answer a token no destination
        // ever minted.
        ReadOnlyMemory<byte>? claimToken = null;

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
                case 4:
                    claimToken = reader.ReadByteString();
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

        if (claimToken is { } token && token.Length != ClaimTokenLength)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "An inventory's claim token is not the shape 07 §5.3 defines.");
        }

        return new ReplicationInventory(keys, more, headroom, claimToken);
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
    /// <remarks>
    /// The claim token is compared by its bytes, like every other memory this
    /// record carries: leaving it out would make an inventory that offers a
    /// credential equal to one that offers none, which is the single most
    /// consequential difference between two pages.
    /// </remarks>
    public bool Equals(ReplicationInventory? other) =>
        other is not null && More == other.More && Headroom == other.Headroom
        && ClaimToken.HasValue == other.ClaimToken.HasValue
        && (!ClaimToken.HasValue || ClaimToken.Value.Span.SequenceEqual(other.ClaimToken!.Value.Span))
        && Keys.SequenceEqual(other.Keys, StringComparer.Ordinal);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(More);
        hash.Add(Headroom);
        hash.Add(ClaimToken?.Length ?? -1);
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
public sealed record ReplicationObject(string Key, ulong Length) : IPeerMessage
{
    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.ReplicationObject;

    /// <inheritdoc/>
    public int BodyEntryCount => 2;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        writer.WriteInt32(1);
        writer.WriteTextString(Key);
        writer.WriteInt32(2);
        writer.WriteUInt64(Length);
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

        return new ReplicationObject(objectKey, length);
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
public sealed record RetentionOffer(
    ReadOnlyMemory<byte> RepositoryId, IReadOnlyList<string> Keys, bool More) : IPeerMessage
{
    /// <summary>The most keys one page may carry (06 §4.1).</summary>
    public const int MaximumKeys = 4096;

    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.RetentionOffer;

    /// <inheritdoc/>
    public int BodyEntryCount => 3;

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

        return new RetentionOffer(repositoryId, keys, more);
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
public sealed record RetentionAck(ulong Deleted) : IPeerMessage
{
    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.RetentionAck;

    /// <inheritdoc/>
    public int BodyEntryCount => 1;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        writer.WriteInt32(1);
        writer.WriteUInt64(Deleted);
    }

    /// <summary>Reads an acknowledgement.</summary>
    /// <param name="reader">The frame's reader.</param>
    /// <returns>The acknowledgement.</returns>
    public static RetentionAck Read(CborReader reader) => new(ReplicationComplete.ReadCount(reader));
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
