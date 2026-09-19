using Bodu;
using System.Formats.Cbor;
using System.Text;

namespace FallbackPlan.Protocol;

/// <summary>
/// An owner's request to read back its own replica from a destination
/// (specification peer-protocol 07 §3.1). Feature-gated as
/// <c>retrieval</c>; the destination serves only replicas attributed to the
/// dialing peer's pinned identity, and serves ciphertext — nothing decrypts
/// on its side.
/// </summary>
/// <param name="RepositoryId">The replica's repository identity (16 bytes).</param>
/// <param name="FormatCapability">The repository format the owner expects to read.</param>
public sealed record RetrieveOpen(ReadOnlyMemory<byte> RepositoryId, uint FormatCapability) : IPeerMessage
{
    /// <summary>The repository identity's length in bytes.</summary>
    public const int RepositoryIdLength = 16;

    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.RetrieveOpen;

    /// <inheritdoc/>
    public int BodyEntryCount => 2;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        writer.WriteInt32(1);
        writer.WriteByteString(RepositoryId.Span);
        writer.WriteInt32(2);
        writer.WriteUInt32(FormatCapability);
    }

    /// <summary>Reads an open from a body positioned after the message type.</summary>
    /// <exception cref="PeerProtocolException">The body violates 07 §3.1.</exception>
    public static RetrieveOpen Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        byte[]? repositoryId = null;
        uint capability = 0;

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
                default:
                    reader.SkipValue();
                    break;
            }
        });

        if (repositoryId is null || repositoryId.Length != RepositoryIdLength)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A retrieve-open is not the shape 07 §3.1 defines.");
        }

        return new RetrieveOpen(repositoryId, capability);
    }

    /// <inheritdoc/>
    public bool Equals(RetrieveOpen? other) =>
        other is not null
        && FormatCapability == other.FormatCapability
        && RepositoryId.Span.SequenceEqual(other.RepositoryId.Span);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(FormatCapability, RepositoryId.Length);
}

/// <summary>
/// The destination's grant of a retrieval session (07 §3.2): the replica is
/// this peer's to read, and the session is retrieval-only from here.
/// </summary>
public sealed record RetrieveReady : IPeerMessage
{
    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.RetrieveReady;

    /// <inheritdoc/>
    public int BodyEntryCount => 0;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer) => ThrowHelper.ThrowIfNull(writer);

    /// <summary>Reads a ready from a body positioned after the message type.</summary>
    public static RetrieveReady Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);
        PeerCbor.ReadEntries(reader, _ => reader.SkipValue());
        return new RetrieveReady();
    }
}

/// <summary>
/// A request for one page of the replica's object listing (07 §3.3),
/// resuming strictly after a named key.
/// </summary>
/// <param name="Prefix">The key prefix to list under.</param>
/// <param name="After">Resume strictly after this key; empty starts at the beginning.</param>
public sealed record RetrieveList(string Prefix, string After) : IPeerMessage
{
    /// <summary>The most bytes a prefix or resume key may occupy (00 §2.3).</summary>
    public const int MaximumKeyBytes = 1024;

    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.RetrieveList;

    /// <inheritdoc/>
    public int BodyEntryCount => 2;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        writer.WriteInt32(1);
        writer.WriteTextString(Prefix);
        writer.WriteInt32(2);
        writer.WriteTextString(After);
    }

    /// <summary>Reads a list request from a body positioned after the message type.</summary>
    /// <exception cref="PeerProtocolException">The body violates 07 §3.3 or a 00 §2.3 limit.</exception>
    public static RetrieveList Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        string? prefix = null;
        string? after = null;

        PeerCbor.ReadEntries(reader, key =>
        {
            switch (key)
            {
                case 1:
                    prefix = reader.ReadTextString();
                    break;
                case 2:
                    after = reader.ReadTextString();
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        });

        if (prefix is null || after is null
            || Encoding.UTF8.GetByteCount(prefix) > MaximumKeyBytes
            || Encoding.UTF8.GetByteCount(after) > MaximumKeyBytes)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A retrieve-list is not the shape 07 §3.3 defines.");
        }

        return new RetrieveList(prefix, after);
    }
}

/// <summary>One page of the replica's listing (07 §3.3).</summary>
/// <param name="Keys">The object keys, in ordinal order.</param>
/// <param name="Lengths">Each key's stored length, parallel to <paramref name="Keys"/>.</param>
/// <param name="More">Whether another page follows the last key here.</param>
public sealed record RetrieveListPage(
    IReadOnlyList<string> Keys, IReadOnlyList<ulong> Lengths, bool More) : IPeerMessage
{
    /// <summary>The most keys one page may carry (00 §2.3).</summary>
    public const int MaximumKeys = 4096;

    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.RetrieveListPage;

    /// <inheritdoc/>
    public int BodyEntryCount => 3;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        if (Keys.Count > MaximumKeys || Lengths.Count != Keys.Count)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                $"A listing page of {Keys.Count} keys and {Lengths.Count} lengths violates 07 §3.3.");
        }

        writer.WriteInt32(1);
        writer.WriteStartArray(Keys.Count);
        foreach (var key in Keys)
        {
            writer.WriteTextString(key);
        }

        writer.WriteEndArray();
        writer.WriteInt32(2);
        writer.WriteStartArray(Lengths.Count);
        foreach (var length in Lengths)
        {
            writer.WriteUInt64(length);
        }

        writer.WriteEndArray();
        writer.WriteInt32(3);
        writer.WriteBoolean(More);
    }

    /// <summary>Reads a page from a body positioned after the message type.</summary>
    /// <exception cref="PeerProtocolException">The body violates 07 §3.3 or a 00 §2.3 limit.</exception>
    public static RetrieveListPage Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        List<string>? keys = null;
        List<ulong>? lengths = null;
        var more = false;

        PeerCbor.ReadEntries(reader, key =>
        {
            switch (key)
            {
                case 1:
                    keys = [];
                    reader.ReadStartArray();
                    while (reader.PeekState() != CborReaderState.EndArray)
                    {
                        keys.Add(reader.ReadTextString());
                    }

                    reader.ReadEndArray();
                    break;
                case 2:
                    lengths = [];
                    reader.ReadStartArray();
                    while (reader.PeekState() != CborReaderState.EndArray)
                    {
                        lengths.Add(reader.ReadUInt64());
                    }

                    reader.ReadEndArray();
                    break;
                case 3:
                    more = reader.ReadBoolean();
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        });

        if (keys is null || lengths is null || keys.Count > MaximumKeys || lengths.Count != keys.Count)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A listing page is not the shape 07 §3.3 defines.");
        }

        return new RetrieveListPage(keys, lengths, more);
    }
}

/// <summary>
/// A request for bytes of one object (07 §3.4). Strictly request/response:
/// each read is answered by exactly one <see cref="RetrieveData"/>, bounded
/// by the chunk limit — a longer range is fetched by further reads.
/// </summary>
/// <param name="Key">The object's store key.</param>
/// <param name="Offset">The zero-based byte offset.</param>
/// <param name="Length">How many bytes; 0 asks for existence and total length only.</param>
public sealed record RetrieveRead(string Key, ulong Offset, ulong Length) : IPeerMessage
{
    /// <summary>The most bytes one read may request — one chunk's worth (00 §2.3).</summary>
    public const int MaximumLength = ReplicationChunk.MaximumBytes;

    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.RetrieveRead;

    /// <inheritdoc/>
    public int BodyEntryCount => 3;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        if (Length > MaximumLength)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                $"A read of {Length} bytes exceeds the chunk limit of {MaximumLength}.");
        }

        writer.WriteInt32(1);
        writer.WriteTextString(Key);
        writer.WriteInt32(2);
        writer.WriteUInt64(Offset);
        writer.WriteInt32(3);
        writer.WriteUInt64(Length);
    }

    /// <summary>Reads a read request from a body positioned after the message type.</summary>
    /// <exception cref="PeerProtocolException">The body violates 07 §3.4 or a 00 §2.3 limit.</exception>
    public static RetrieveRead Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        string? key = null;
        ulong offset = 0;
        ulong length = 0;

        PeerCbor.ReadEntries(reader, entry =>
        {
            switch (entry)
            {
                case 1:
                    key = reader.ReadTextString();
                    break;
                case 2:
                    offset = reader.ReadUInt64();
                    break;
                case 3:
                    length = reader.ReadUInt64();
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        });

        if (key is null
            || Encoding.UTF8.GetByteCount(key) > RetrieveList.MaximumKeyBytes
            || length > MaximumLength)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A retrieve-read is not the shape 07 §3.4 defines.");
        }

        return new RetrieveRead(key, offset, length);
    }
}

/// <summary>The answer to one read (07 §3.4).</summary>
/// <param name="Found">Whether the object exists.</param>
/// <param name="TotalLength">The object's whole stored length when found.</param>
/// <param name="Bytes">The requested range's bytes; empty for a stat-only read or a miss.</param>
public sealed record RetrieveData(bool Found, ulong TotalLength, ReadOnlyMemory<byte> Bytes) : IPeerMessage
{
    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.RetrieveData;

    /// <inheritdoc/>
    public int BodyEntryCount => 3;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        if (Bytes.Length > ReplicationChunk.MaximumBytes)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                $"A data frame of {Bytes.Length} bytes exceeds the chunk limit of {ReplicationChunk.MaximumBytes}.");
        }

        writer.WriteInt32(1);
        writer.WriteBoolean(Found);
        writer.WriteInt32(2);
        writer.WriteUInt64(TotalLength);
        writer.WriteInt32(3);
        writer.WriteByteString(Bytes.Span);
    }

    /// <summary>Reads a data frame from a body positioned after the message type.</summary>
    /// <exception cref="PeerProtocolException">The body violates 07 §3.4 or a 00 §2.3 limit.</exception>
    public static RetrieveData Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        var found = false;
        ulong totalLength = 0;
        byte[]? bytes = null;

        PeerCbor.ReadEntries(reader, key =>
        {
            switch (key)
            {
                case 1:
                    found = reader.ReadBoolean();
                    break;
                case 2:
                    totalLength = reader.ReadUInt64();
                    break;
                case 3:
                    bytes = reader.ReadByteString();
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        });

        if (bytes is null || bytes.Length > ReplicationChunk.MaximumBytes)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A retrieve-data is not the shape 07 §3.4 defines.");
        }

        return new RetrieveData(found, totalLength, bytes);
    }

    /// <inheritdoc/>
    public bool Equals(RetrieveData? other) =>
        other is not null
        && Found == other.Found
        && TotalLength == other.TotalLength
        && Bytes.Span.SequenceEqual(other.Bytes.Span);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Found, TotalLength, Bytes.Length);
}

/// <summary>
/// A request for one leaf of a blob's Merkle commitment (07 §3.6). Strictly
/// request/response: each challenge is answered by exactly one
/// <see cref="MerkleProof"/>, and the verifier sends no second challenge
/// before the previous proof arrives.
/// </summary>
/// <param name="RepositoryId">The repository whose replica is challenged.</param>
/// <param name="Key">The object key of the blob, as the replica holds it.</param>
/// <param name="LeafIndex">Which one-mebibyte leaf of the blob's preimage to produce.</param>
public sealed record MerkleChallenge(ReadOnlyMemory<byte> RepositoryId, string Key, uint LeafIndex) : IPeerMessage
{
    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.MerkleChallenge;

    /// <inheritdoc/>
    public int BodyEntryCount => 3;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        if (Encoding.UTF8.GetByteCount(Key) > RetrieveList.MaximumKeyBytes)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                $"A challenged key exceeds the {RetrieveList.MaximumKeyBytes}-byte limit.");
        }

        if (RepositoryId.Length != 16)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A repository identifier is 16 bytes.");
        }

        writer.WriteInt32(1);
        writer.WriteByteString(RepositoryId.Span);
        writer.WriteInt32(2);
        writer.WriteTextString(Key);
        writer.WriteInt32(3);
        writer.WriteUInt32(LeafIndex);
    }

    /// <summary>Reads a challenge from a body positioned after the message type.</summary>
    /// <exception cref="PeerProtocolException">The body violates 07 §3.6 or a 00 §2.3 limit.</exception>
    public static MerkleChallenge Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        byte[]? repositoryId = null;
        string? key = null;
        uint leafIndex = 0;

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
                    leafIndex = reader.ReadUInt32();
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        });

        if (repositoryId is not { Length: 16 }
            || key is null
            || Encoding.UTF8.GetByteCount(key) > RetrieveList.MaximumKeyBytes)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A Merkle challenge is not the shape 07 §3.6 defines.");
        }

        return new MerkleChallenge(repositoryId, key, leafIndex);
    }

    /// <inheritdoc/>
    public bool Equals(MerkleChallenge? other) =>
        other is not null
        && string.Equals(Key, other.Key, StringComparison.Ordinal)
        && LeafIndex == other.LeafIndex
        && RepositoryId.Span.SequenceEqual(other.RepositoryId.Span);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Key, LeafIndex, RepositoryId.Length);
}

/// <summary>
/// The answer to one Merkle challenge (07 §3.6): the leaf's bytes and the
/// sibling hashes that carry them to the root, or an honest inability to
/// produce either.
/// </summary>
/// <remarks>
/// The <b>bytes</b> are the proof and the path is not. A path is public
/// arithmetic over hashes the destination may freely cache, so producing one
/// establishes nothing; producing the chunk it commits to establishes that
/// the chunk is held. That is the whole difference between this message and
/// the digest answer [ADR-0058] refuses as a self-report.
/// </remarks>
/// <param name="Held">Whether the destination could produce the leaf.</param>
/// <param name="Leaf">The leaf's bytes; empty when not held.</param>
/// <param name="Path">The authentication path, leaf-upward; empty when not held, and legitimately empty for a one-leaf blob.</param>
public sealed record MerkleProof(bool Held, ReadOnlyMemory<byte> Leaf, IReadOnlyList<ReadOnlyMemory<byte>> Path)
    : IPeerMessage
{
    /// <summary>
    /// The most bytes one leaf may carry. The repository format fixes the
    /// leaf at one mebibyte (repository-format 05 §5.2); this protocol's
    /// chunk limit is the same number, arrived at independently, and a test
    /// in the agent holds the two together.
    /// </summary>
    public const int MaximumLeafBytes = ReplicationChunk.MaximumBytes;

    /// <summary>Each path step's width.</summary>
    public const int StepLength = 32;

    /// <summary>
    /// The most steps a path may carry. A blob is bounded at 512 MiB, so
    /// nine would do; the bound exists to stop an answer allocating.
    /// </summary>
    public const int MaximumSteps = 32;

    /// <inheritdoc/>
    public PeerMessageType Type => PeerMessageType.MerkleProof;

    /// <inheritdoc/>
    public int BodyEntryCount => Held ? 3 : 1;

    /// <inheritdoc/>
    public void WriteBody(CborWriter writer)
    {
        ThrowHelper.ThrowIfNull(writer);

        if (Held && (Leaf.Length is 0 or > MaximumLeafBytes || Path.Count > MaximumSteps))
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                $"A Merkle proof carries {Leaf.Length} leaf byte(s) and {Path.Count} step(s), "
                + $"outside the 1..{MaximumLeafBytes} and 0..{MaximumSteps} this protocol permits.");
        }

        writer.WriteInt32(1);
        writer.WriteUInt32(Held ? 0u : 1u);

        if (!Held)
        {
            return;
        }

        writer.WriteInt32(2);
        writer.WriteByteString(Leaf.Span);
        writer.WriteInt32(3);
        writer.WriteStartArray(Path.Count);
        foreach (var step in Path)
        {
            if (step.Length != StepLength)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed,
                    $"A Merkle path step is {step.Length} bytes; 07 §3.6 defines {StepLength}.");
            }

            writer.WriteByteString(step.Span);
        }

        writer.WriteEndArray();
    }

    /// <summary>Reads a proof from a body positioned after the message type.</summary>
    /// <exception cref="PeerProtocolException">The body violates 07 §3.6 or a 00 §2.3 limit.</exception>
    public static MerkleProof Read(CborReader reader)
    {
        ThrowHelper.ThrowIfNull(reader);

        uint status = 1;
        byte[]? leaf = null;
        List<ReadOnlyMemory<byte>>? path = null;

        PeerCbor.ReadEntries(reader, entry =>
        {
            switch (entry)
            {
                case 1:
                    status = reader.ReadUInt32();
                    break;
                case 2:
                    leaf = reader.ReadByteString();
                    break;
                case 3:
                    var count = reader.ReadStartArray();
                    if (count is null || count > MaximumSteps)
                    {
                        throw new PeerProtocolException(
                            PeerRefusalReason.Malformed,
                            $"A Merkle path carried more than the {MaximumSteps} steps this protocol permits.");
                    }

                    path = new List<ReadOnlyMemory<byte>>(count.Value);
                    for (var index = 0; index < count.Value; index++)
                    {
                        var step = reader.ReadByteString();

                        // Fatal rather than dropped, on ReplicationPartial's
                        // rule: a step of the wrong width is the check this
                        // message exists for, arriving broken.
                        if (step.Length != StepLength)
                        {
                            throw new PeerProtocolException(
                                PeerRefusalReason.Malformed,
                                $"A Merkle path step is {step.Length} bytes; 07 §3.6 defines {StepLength}.");
                        }

                        path.Add(step);
                    }

                    reader.ReadEndArray();
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        });

        // Status and payload must agree, on VerificationProof's rule: a
        // "cannot prove" carrying bytes, or a proof carrying none, is a peer
        // that does not mean what this message means.
        return (status, leaf, path) switch
        {
            (0, { Length: > 0 and <= MaximumLeafBytes }, not null) =>
                new MerkleProof(true, leaf, path),
            (1, null, null) => new MerkleProof(false, ReadOnlyMemory<byte>.Empty, []),
            _ => throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                "A Merkle proof's status and payload disagree (07 §3.6)."),
        };
    }

    /// <inheritdoc/>
    public bool Equals(MerkleProof? other) =>
        other is not null
        && Held == other.Held
        && Leaf.Span.SequenceEqual(other.Leaf.Span)
        && Path.Count == other.Path.Count
        && Path.Zip(other.Path).All(pair => pair.First.Span.SequenceEqual(pair.Second.Span));

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Held, Leaf.Length, Path.Count);
}
