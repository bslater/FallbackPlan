using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Cbor;

namespace FallbackPlan.Repository.Format.Manifests;

/// <summary>One directory entry: raw name bytes, the entry's manifest object identifier, and its kind (specification 06 §5 key 1).</summary>
public sealed record TreeEntry(ReadOnlyMemory<byte> Name, ObjectId ObjectId, EntryKind EntryKind);

/// <summary>
/// A directory (specification 06 §5, object type <c>0x03</c>). Entries are
/// sorted by the <b>raw bytes</b> of their names — byte order, never a
/// collation, because collation is locale-dependent and would break object
/// identifiers. A sharded directory chains manifests via
/// <see cref="Continuation"/> (06 §9); metadata, name, and normalisation
/// ride on the first manifest of a chain only.
/// </summary>
public sealed record TreeManifest
{
    /// <summary>The sorted entries (key 1).</summary>
    public required IReadOnlyList<TreeEntry> Entries { get; init; }

    /// <summary>The directory's own metadata (key 2); null on chain continuations.</summary>
    public EntryMetadata? Metadata { get; init; }

    /// <summary>The directory's name, raw bytes (key 3); null on continuations.</summary>
    public ReadOnlyMemory<byte>? Name { get; init; }

    /// <summary>The name's normalisation (key 4); null on continuations.</summary>
    public NameNormalisation? NameNormalisation { get; init; }

    /// <summary>The next manifest in a sharded chain (key 5); null on the last and on unsharded trees.</summary>
    public ObjectId? Continuation { get; init; }
}

/// <summary>
/// Encodes and decodes tree manifests, enforcing 06 §5's sorting and
/// duplicate rules per manifest; whole-chain rules live in
/// <see cref="TreeChain"/>.
/// </summary>
public static class TreeManifestCodec
{
    /// <summary>Encodes a tree manifest canonically.</summary>
    /// <exception cref="ManifestValidationException">The manifest violates specification 06 §5.</exception>
    public static byte[] Encode(TreeManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ValidateEntryOrder(manifest.Entries);

        var isFirst = manifest.Name is not null;
        if (isFirst != (manifest.NameNormalisation is not null) || (manifest.Metadata is not null && !isFirst))
        {
            throw new ManifestValidationException(
                "metadata, name, and name_normalisation ride together on the first manifest of a chain and are absent from continuations (specification 06 §9).");
        }

        var writer = new CanonicalCborWriter();
        var keyCount = 1
            + (manifest.Metadata is not null ? 1 : 0)
            + (manifest.Name is not null ? 1 : 0)
            + (manifest.NameNormalisation is not null ? 1 : 0)
            + (manifest.Continuation is not null ? 1 : 0);

        writer.WriteStartMap(keyCount);
        writer.WriteKey(1);
        writer.WriteStartArray(manifest.Entries.Count);
        foreach (var entry in manifest.Entries)
        {
            writer.WriteStartArray(3);
            writer.WriteByteString(entry.Name.Span);
            writer.WriteByteString(entry.ObjectId.ToArray());
            writer.WriteUnsignedInteger((ushort)entry.EntryKind);
            writer.WriteEndArray();
        }

        writer.WriteEndArray();

        if (manifest.Metadata is { } metadata)
        {
            writer.WriteKey(2);
            FileVersionManifestCodec.WriteMetadata(writer, metadata);
        }

        if (manifest.Name is { } name)
        {
            writer.WriteKey(3);
            writer.WriteByteString(name.Span);
        }

        if (manifest.NameNormalisation is { } normalisation)
        {
            writer.WriteKey(4);
            writer.WriteUnsignedInteger((byte)normalisation);
        }

        if (manifest.Continuation is { } continuation)
        {
            writer.WriteKey(5);
            writer.WriteByteString(continuation.ToArray());
        }

        writer.WriteEndMap();

        var encoded = writer.Encode();
        if (encoded.Length > FormatLimits.MaxMetadataObjectSize)
        {
            throw new ManifestValidationException(
                $"The encoded tree manifest is {encoded.Length} bytes; a directory this large MUST shard (specification 06 §9).");
        }

        return encoded;
    }

    /// <summary>Decodes and validates one tree manifest.</summary>
    /// <exception cref="ManifestValidationException">The bytes violate specification 06 §5 — a damage finding.</exception>
    public static TreeManifest Decode(ReadOnlyMemory<byte> data)
    {
        try
        {
            return DecodeCore(data);
        }
        catch (CborFormatException exception)
        {
            throw new ManifestValidationException($"The tree manifest is not canonical CBOR: {exception.Message}", exception);
        }
    }

    private static TreeManifest DecodeCore(ReadOnlyMemory<byte> data)
    {
        var reader = new CanonicalCborReader(data);
        var count = reader.ReadStartMap();

        List<TreeEntry>? entries = null;
        EntryMetadata? metadata = null;
        byte[]? name = null;
        NameNormalisation? normalisation = null;
        ObjectId? continuation = null;

        for (var i = 0; i < count; i++)
        {
            switch (reader.ReadKey())
            {
                case 1:
                    var entryCount = reader.ReadStartArray(maxCount: 10_000_000);
                    entries = new List<TreeEntry>(entryCount);
                    for (var j = 0; j < entryCount; j++)
                    {
                        var elements = reader.ReadStartArray(maxCount: 3);
                        if (elements != 3)
                        {
                            throw new ManifestValidationException(
                                $"A tree entry is exactly three elements; got {elements} (specification 06 §5).");
                        }

                        var entryName = reader.ReadByteString(maxLength: 1024);
                        var objectId = ObjectId.FromBytes(reader.ReadFixedByteString(32));
                        var kind = reader.ReadUInt16();
                        if (kind is < 1 or > 4)
                        {
                            throw new ManifestValidationException($"entry_kind {kind} is unassigned (specification 06 §4).");
                        }

                        reader.ReadEndArray();
                        entries.Add(new TreeEntry(entryName, objectId, (EntryKind)kind));
                    }

                    reader.ReadEndArray();
                    break;
                case 2:
                    metadata = FileVersionManifestCodec.ReadMetadata(reader);
                    break;
                case 3:
                    name = reader.ReadByteString(maxLength: 1024);
                    break;
                case 4:
                    var norm = reader.ReadByte();
                    if (norm > 2)
                    {
                        throw new ManifestValidationException($"name_normalisation {norm} is unassigned (specification 06 §4).");
                    }

                    normalisation = (NameNormalisation)norm;
                    break;
                case 5:
                    continuation = ObjectId.FromBytes(reader.ReadFixedByteString(32));
                    break;
                default:
                    throw new ManifestValidationException(
                        "The tree manifest carries an unknown key; specification 06 §5 assigns keys 1-5 only.");
            }
        }

        reader.ReadEndMap();
        reader.AssertEndOfDocument();

        if (entries is null)
        {
            throw new ManifestValidationException("The tree manifest omits its entries (specification 06 §5).");
        }

        ValidateEntryOrder(entries);

        return new TreeManifest
        {
            Entries = entries,
            Metadata = metadata,
            Name = name,
            NameNormalisation = normalisation,
            Continuation = continuation,
        };
    }

    /// <summary>
    /// 06 §5: ascending raw-byte order, no duplicate names. Entries differing
    /// only by case or normalisation are legitimate — that becomes a
    /// restore-plan conflict, never a capture error.
    /// </summary>
    internal static void ValidateEntryOrder(IReadOnlyList<TreeEntry> entries)
    {
        for (var i = 1; i < entries.Count; i++)
        {
            var comparison = entries[i - 1].Name.Span.SequenceCompareTo(entries[i].Name.Span);

            if (comparison > 0)
            {
                throw new ManifestValidationException(
                    "Tree entries MUST be sorted by the raw bytes of name, ascending (specification 06 §5).");
            }

            if (comparison == 0)
            {
                throw new ManifestValidationException(
                    "A tree MUST NOT contain two entries with the same name bytes (specification 06 §5).");
            }
        }
    }
}

/// <summary>
/// Whole-chain validation for sharded directories (specification 06 §9):
/// every manifest except the last carries a continuation, chain-order
/// sorting applies to the <b>logical</b> concatenated list, head-only fields
/// stay on the head, and a cycle or missing continuation target is a damage
/// finding — a truncated directory that reads as complete is silent data
/// loss.
/// </summary>
public static class TreeChain
{
    /// <summary>
    /// Validates an ordered chain of decoded manifests against 06 §9 and
    /// returns the logical entry list.
    /// </summary>
    /// <exception cref="ManifestValidationException">The chain violates 06 §9 — a damage finding.</exception>
    public static IReadOnlyList<TreeEntry> ValidateAndFlatten(IReadOnlyList<TreeManifest> chain)
    {
        ArgumentNullException.ThrowIfNull(chain);

        if (chain.Count == 0)
        {
            throw new ManifestValidationException("A tree chain has at least one manifest (specification 06 §9).");
        }

        if (chain[0].Name is null)
        {
            throw new ManifestValidationException(
                "The first manifest of a chain carries name, name_normalisation, and metadata (specification 06 §9).");
        }

        var entries = new List<TreeEntry>();

        for (var i = 0; i < chain.Count; i++)
        {
            var manifest = chain[i];
            var isLast = i == chain.Count - 1;

            if (isLast != (manifest.Continuation is null))
            {
                throw new ManifestValidationException(
                    "Each manifest except the last carries continuation; the last omits it (specification 06 §9).");
            }

            if (i > 0 && (manifest.Name is not null || manifest.NameNormalisation is not null || manifest.Metadata is not null))
            {
                throw new ManifestValidationException(
                    "Continuations MUST NOT carry metadata, name, or name_normalisation (specification 06 §9).");
            }

            if (i > 0 && entries.Count > 0 && manifest.Entries.Count > 0 &&
                entries[^1].Name.Span.SequenceCompareTo(manifest.Entries[0].Name.Span) >= 0)
            {
                throw new ManifestValidationException(
                    "Every entry in a manifest MUST sort strictly after every entry in its predecessor (specification 06 §9).");
            }

            entries.AddRange(manifest.Entries);
        }

        // The whole logical list obeys §5's rules, not just each shard.
        TreeManifestCodec.ValidateEntryOrder(entries);

        return entries;
    }
}
