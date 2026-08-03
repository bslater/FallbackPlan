using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Cbor;

namespace FallbackPlan.Repository.Format.Manifests;

/// <summary>
/// Encodes and decodes file-version manifests (specification 06 §4;
/// FR-MAN-003, FR-ARCH-010). Decode rejects unknown keys outright — a blob
/// identifier or physical offset cannot hide anywhere (06 §3.1: "a reader
/// MUST reject a manifest that contains them") — and enforces the 06 §3.2
/// ordering-and-coverage obligation: references ascending, non-overlapping,
/// and together with sparse extents covering <c>[0, logical_length)</c>
/// exactly.
/// </summary>
public static class FileVersionManifestCodec
{
    /// <summary>The maximum references per manifest (specification 06 §3.2).</summary>
    public const int MaxSegmentReferences = 1_048_576;

    /// <summary>Encodes a manifest canonically, validating before any byte is emitted.</summary>
    /// <exception cref="ManifestValidationException">The manifest violates specification 06.</exception>
    public static byte[] Encode(FileVersionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Validate(manifest);

        var writer = new CanonicalCborWriter();
        var keyCount = 8
            + (manifest.SparseExtents.Count > 0 ? 1 : 0)
            + (manifest.ParentVersion is not null ? 1 : 0)
            + (manifest.LinkTarget is not null ? 1 : 0)
            + (manifest.HardlinkGroup is not null ? 1 : 0)
            + (manifest.CaptureDiagnostics.Count > 0 ? 1 : 0);

        writer.WriteStartMap(keyCount);
        writer.WriteKey(1);
        writer.WriteUnsignedInteger((ushort)manifest.EntryKind);
        writer.WriteKey(2);
        writer.WriteByteString(manifest.Name.Span);
        writer.WriteKey(3);
        writer.WriteUnsignedInteger((byte)manifest.NameNormalisation);
        writer.WriteKey(4);
        writer.WriteUnsignedInteger(manifest.LogicalLength);
        writer.WriteKey(5);
        writer.WriteStartArray(manifest.SegmentReferences.Count);
        foreach (var reference in manifest.SegmentReferences)
        {
            WriteSegmentReference(writer, reference);
        }

        writer.WriteEndArray();

        if (manifest.SparseExtents.Count > 0)
        {
            writer.WriteKey(6);
            writer.WriteStartArray(manifest.SparseExtents.Count);
            foreach (var extent in manifest.SparseExtents)
            {
                writer.WriteStartArray(2);
                writer.WriteUnsignedInteger(extent.Offset);
                writer.WriteUnsignedInteger(extent.Length);
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
        }

        writer.WriteKey(7);
        writer.WriteByteString(manifest.WholeFileHash.Span);
        writer.WriteKey(8);
        writer.WriteUnsignedInteger(manifest.SegmentationProfile);

        if (manifest.ParentVersion is { } parent)
        {
            writer.WriteKey(9);
            writer.WriteByteString(parent.ToArray());
        }

        writer.WriteKey(10);
        WriteMetadata(writer, manifest.Metadata);

        if (manifest.LinkTarget is { } linkTarget)
        {
            writer.WriteKey(11);
            writer.WriteByteString(linkTarget.Span);
        }

        if (manifest.HardlinkGroup is { } hardlinkGroup)
        {
            writer.WriteKey(12);
            writer.WriteByteString(hardlinkGroup.Span);
        }

        if (manifest.CaptureDiagnostics.Count > 0)
        {
            writer.WriteKey(13);
            writer.WriteStartArray(manifest.CaptureDiagnostics.Count);
            foreach (var diagnostic in manifest.CaptureDiagnostics)
            {
                writer.WriteTextString(diagnostic);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndMap();

        var encoded = writer.Encode();

        if (encoded.Length > FormatLimits.MaxMetadataObjectSize)
        {
            throw new ManifestValidationException(
                $"The encoded manifest is {encoded.Length} bytes; the metadata limit is 16 MiB (specification 00 §8).");
        }

        return encoded;
    }

    /// <summary>Decodes and fully validates a manifest.</summary>
    /// <exception cref="ManifestValidationException">The bytes violate specification 06 — a damage finding.</exception>
    public static FileVersionManifest Decode(ReadOnlyMemory<byte> data)
    {
        try
        {
            return DecodeCore(data);
        }
        catch (CborFormatException exception)
        {
            throw new ManifestValidationException($"The manifest is not canonical CBOR: {exception.Message}", exception);
        }
    }

    private static FileVersionManifest DecodeCore(ReadOnlyMemory<byte> data)
    {
        var reader = new CanonicalCborReader(data);
        var count = reader.ReadStartMap();

        EntryKind? entryKind = null;
        byte[]? name = null;
        NameNormalisation? normalisation = null;
        ulong? logicalLength = null;
        List<SegmentReference>? references = null;
        List<SparseExtent> sparse = [];
        byte[]? wholeFileHash = null;
        ushort? segmentationProfile = null;
        ObjectId? parentVersion = null;
        EntryMetadata? metadata = null;
        byte[]? linkTarget = null;
        byte[]? hardlinkGroup = null;
        List<string> diagnostics = [];

        for (var i = 0; i < count; i++)
        {
            switch (reader.ReadKey())
            {
                case 1:
                    var kind = reader.ReadUInt16();
                    if (kind is < 1 or > 4)
                    {
                        throw new ManifestValidationException($"entry_kind {kind} is unassigned (specification 06 §4).");
                    }

                    entryKind = (EntryKind)kind;
                    break;
                case 2:
                    name = reader.ReadByteString(maxLength: 1024);
                    break;
                case 3:
                    var norm = reader.ReadByte();
                    if (norm > 2)
                    {
                        throw new ManifestValidationException($"name_normalisation {norm} is unassigned (specification 06 §4).");
                    }

                    normalisation = (NameNormalisation)norm;
                    break;
                case 4:
                    logicalLength = reader.ReadUnsignedInteger();
                    break;
                case 5:
                    references = ReadSegmentReferences(reader);
                    break;
                case 6:
                    sparse = ReadSparseExtents(reader);
                    break;
                case 7:
                    wholeFileHash = reader.ReadFixedByteString(32);
                    break;
                case 8:
                    segmentationProfile = reader.ReadUInt16();
                    break;
                case 9:
                    parentVersion = ObjectId.FromBytes(reader.ReadFixedByteString(32));
                    break;
                case 10:
                    metadata = ReadMetadata(reader);
                    break;
                case 11:
                    linkTarget = reader.ReadByteString(maxLength: 4096);
                    break;
                case 12:
                    hardlinkGroup = reader.ReadFixedByteString(16);
                    break;
                case 13:
                    diagnostics = ReadDiagnostics(reader);
                    break;
                default:
                    // 06 §3.1's enforcement point: an unknown key is exactly
                    // where a physical-location field would hide.
                    throw new ManifestValidationException(
                        "The manifest carries an unknown key; specification 06 §4 assigns keys 1-13 only, and a reader MUST reject rather than skip (06 §3.1).");
            }
        }

        reader.ReadEndMap();
        reader.AssertEndOfDocument();

        if (entryKind is null || name is null || normalisation is null || logicalLength is null ||
            references is null || wholeFileHash is null || segmentationProfile is null || metadata is null)
        {
            throw new ManifestValidationException("The manifest omits a mandatory key (specification 06 §4).");
        }

        var manifest = new FileVersionManifest
        {
            EntryKind = entryKind.Value,
            Name = name,
            NameNormalisation = normalisation.Value,
            LogicalLength = logicalLength.Value,
            SegmentReferences = references,
            SparseExtents = sparse,
            WholeFileHash = wholeFileHash,
            SegmentationProfile = segmentationProfile.Value,
            ParentVersion = parentVersion,
            Metadata = metadata,
            // A null byte[] would silently convert to a PRESENT empty
            // memory through the implicit conversion — keep absence absent.
            LinkTarget = linkTarget is null ? null : (ReadOnlyMemory<byte>?)linkTarget,
            HardlinkGroup = hardlinkGroup is null ? null : (ReadOnlyMemory<byte>?)hardlinkGroup,
            CaptureDiagnostics = diagnostics,
        };

        Validate(manifest);
        return manifest;
    }

    /// <summary>
    /// The 06 §3.2 obligation, applied on both encode and decode: ascending,
    /// non-overlapping, exact coverage of <c>[0, logical_length)</c> by
    /// references and sparse extents together.
    /// </summary>
    private static void Validate(FileVersionManifest manifest)
    {
        if (manifest.SegmentReferences.Count > MaxSegmentReferences)
        {
            throw new ManifestValidationException(
                $"{manifest.SegmentReferences.Count} segment references exceed the 1 048 576 maximum — a file this large requires a larger segment size (specification 06 §3.2).");
        }

        var pieces = new List<(ulong Offset, ulong Length)>(manifest.SegmentReferences.Count + manifest.SparseExtents.Count);
        foreach (var reference in manifest.SegmentReferences)
        {
            if (reference.LogicalOffset < 0 || reference.LogicalLength <= 0)
            {
                throw new ManifestValidationException(
                    "A segment reference's offset must be non-negative and its length positive (specification 06 §3).");
            }

            pieces.Add(((ulong)reference.LogicalOffset, (ulong)reference.LogicalLength));
        }

        foreach (var extent in manifest.SparseExtents)
        {
            if (extent.Length == 0)
            {
                throw new ManifestValidationException("A sparse extent's length must be positive (specification 06 §4).");
            }

            pieces.Add((extent.Offset, extent.Length));
        }

        pieces.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));

        var expected = 0UL;
        foreach (var (offset, length) in pieces)
        {
            if (offset != expected)
            {
                throw new ManifestValidationException(
                    $"Coverage breaks at offset {expected}: the next piece starts at {offset}. References and sparse extents MUST cover [0, logical_length) exactly, without gaps or overlap (specification 06 §3.2).");
            }

            expected = checked(offset + length);
        }

        if (expected != manifest.LogicalLength)
        {
            throw new ManifestValidationException(
                $"References and sparse extents cover {expected} bytes; logical_length declares {manifest.LogicalLength} (specification 06 §3.2).");
        }

        // Ascending order of the references themselves (not just the merged
        // tiling) is normative for the encoded array.
        for (var i = 1; i < manifest.SegmentReferences.Count; i++)
        {
            if (manifest.SegmentReferences[i].LogicalOffset <= manifest.SegmentReferences[i - 1].LogicalOffset)
            {
                throw new ManifestValidationException(
                    "Segment references MUST be ordered by ascending logical_offset (specification 06 §3.2).");
            }
        }

        if (manifest.WholeFileHash.Length != 32)
        {
            throw new ManifestValidationException("whole_file_hash is exactly 32 bytes (specification 06 §4).");
        }
    }

    private static void WriteSegmentReference(CanonicalCborWriter writer, SegmentReference reference)
    {
        // An array of exactly three elements, never a map — and never a blob
        // identifier, physical offset, or stored length (06 §3, §3.1).
        writer.WriteStartArray(3);
        writer.WriteUnsignedInteger((ulong)reference.LogicalOffset);
        writer.WriteUnsignedInteger((ulong)reference.LogicalLength);
        writer.WriteByteString(reference.ObjectId.ToArray());
        writer.WriteEndArray();
    }

    private static List<SegmentReference> ReadSegmentReferences(CanonicalCborReader reader)
    {
        var count = reader.ReadStartArray(MaxSegmentReferences);
        var references = new List<SegmentReference>(count);

        for (var i = 0; i < count; i++)
        {
            var elements = reader.ReadStartArray(maxCount: 8);
            if (elements != 3)
            {
                throw new ManifestValidationException(
                    $"A segment reference is exactly three elements; got {elements} — a fourth element is where physical location would smuggle in (specification 06 §3, §3.1).");
            }

            var offset = reader.ReadUnsignedInteger();
            var length = reader.ReadUnsignedInteger();
            var objectId = ObjectId.FromBytes(reader.ReadFixedByteString(32));
            reader.ReadEndArray();

            references.Add(new SegmentReference((long)offset, (long)length, objectId));
        }

        reader.ReadEndArray();
        return references;
    }

    private static List<SparseExtent> ReadSparseExtents(CanonicalCborReader reader)
    {
        var count = reader.ReadStartArray(MaxSegmentReferences);
        var extents = new List<SparseExtent>(count);

        for (var i = 0; i < count; i++)
        {
            var elements = reader.ReadStartArray(maxCount: 2);
            if (elements != 2)
            {
                throw new ManifestValidationException(
                    $"A sparse extent is exactly two elements; got {elements} (specification 06 §4).");
            }

            var offset = reader.ReadUnsignedInteger();
            var length = reader.ReadUnsignedInteger();
            reader.ReadEndArray();
            extents.Add(new SparseExtent(offset, length));
        }

        reader.ReadEndArray();
        return extents;
    }

    private static List<string> ReadDiagnostics(CanonicalCborReader reader)
    {
        var count = reader.ReadStartArray(maxCount: 4096);
        if (count == 0)
        {
            throw new ManifestValidationException(
                "capture_diagnostics is present only when non-empty (specification 06 §4).");
        }

        var diagnostics = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            diagnostics.Add(reader.ReadTextString(maxUtf8Length: 4096));
        }

        reader.ReadEndArray();
        return diagnostics;
    }

    internal static void WriteMetadata(CanonicalCborWriter writer, EntryMetadata metadata)
    {
        var keyCount =
            (metadata.ModifiedAt.HasValue ? 1 : 0)
            + (metadata.CreatedAt.HasValue ? 1 : 0)
            + (metadata.AccessedAt.HasValue ? 1 : 0)
            + (metadata.PosixMode.HasValue ? 1 : 0)
            + (metadata.OwnerName is not null ? 1 : 0)
            + (metadata.GroupName is not null ? 1 : 0)
            + (metadata.WindowsSecurityDescriptor is not null ? 1 : 0)
            + (metadata.ExtendedAttributes.Count > 0 ? 1 : 0)
            + (metadata.AlternateStreams.Count > 0 ? 1 : 0)
            + (metadata.FileAttributes.HasValue ? 1 : 0);

        writer.WriteStartMap(keyCount);

        if (metadata.ModifiedAt is { } modified)
        {
            writer.WriteKey(1);
            writer.WriteUnsignedInteger(modified);
        }

        if (metadata.CreatedAt is { } created)
        {
            writer.WriteKey(2);
            writer.WriteUnsignedInteger(created);
        }

        if (metadata.AccessedAt is { } accessed)
        {
            writer.WriteKey(3);
            writer.WriteUnsignedInteger(accessed);
        }

        if (metadata.PosixMode is { } mode)
        {
            writer.WriteKey(4);
            writer.WriteUnsignedInteger(mode);
        }

        if (metadata.OwnerName is { } owner)
        {
            writer.WriteKey(5);
            writer.WriteTextString(owner);
        }

        if (metadata.GroupName is { } group)
        {
            writer.WriteKey(6);
            writer.WriteTextString(group);
        }

        if (metadata.WindowsSecurityDescriptor is { } descriptor)
        {
            writer.WriteKey(7);
            writer.WriteByteString(descriptor.Span);
        }

        if (metadata.ExtendedAttributes.Count > 0)
        {
            writer.WriteKey(8);
            writer.WriteStartArray(metadata.ExtendedAttributes.Count);
            foreach (var attribute in metadata.ExtendedAttributes)
            {
                writer.WriteStartArray(2);
                writer.WriteByteString(attribute.Name.Span);
                writer.WriteByteString(attribute.Value.Span);
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
        }

        if (metadata.AlternateStreams.Count > 0)
        {
            writer.WriteKey(9);
            writer.WriteStartArray(metadata.AlternateStreams.Count);
            foreach (var stream in metadata.AlternateStreams)
            {
                writer.WriteStartArray(3);
                writer.WriteByteString(stream.Name.Span);
                writer.WriteByteString(stream.ObjectId.ToArray());
                writer.WriteUnsignedInteger(stream.Length);
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
        }

        if (metadata.FileAttributes is { } attributes)
        {
            writer.WriteKey(10);
            writer.WriteUnsignedInteger(attributes);
        }

        writer.WriteEndMap();
    }

    internal static EntryMetadata ReadMetadata(CanonicalCborReader reader)
    {
        var count = reader.ReadStartMap();

        ulong? modified = null, created = null, accessed = null;
        uint? posixMode = null, fileAttributes = null;
        string? owner = null, group = null;
        byte[]? descriptor = null;
        List<ExtendedAttributeEntry> attributes = [];
        List<AlternateStreamEntry> streams = [];

        for (var i = 0; i < count; i++)
        {
            switch (reader.ReadKey())
            {
                case 1:
                    modified = reader.ReadUnsignedInteger();
                    break;
                case 2:
                    created = reader.ReadUnsignedInteger();
                    break;
                case 3:
                    accessed = reader.ReadUnsignedInteger();
                    break;
                case 4:
                    posixMode = reader.ReadUInt32();
                    break;
                case 5:
                    owner = reader.ReadTextString(maxUtf8Length: 256);
                    break;
                case 6:
                    group = reader.ReadTextString(maxUtf8Length: 256);
                    break;
                case 7:
                    descriptor = reader.ReadByteString(maxLength: 64 * 1024);
                    break;
                case 8:
                    var attributeCount = reader.ReadStartArray(maxCount: 4096);
                    for (var j = 0; j < attributeCount; j++)
                    {
                        var pair = reader.ReadStartArray(maxCount: 2);
                        if (pair != 2)
                        {
                            throw new ManifestValidationException(
                                "An extended attribute is exactly two byte strings (specification 06 §4.1).");
                        }

                        attributes.Add(new ExtendedAttributeEntry(
                            reader.ReadByteString(maxLength: 1024),
                            reader.ReadByteString(maxLength: 64 * 1024)));
                        reader.ReadEndArray();
                    }

                    reader.ReadEndArray();
                    break;
                case 9:
                    var streamCount = reader.ReadStartArray(maxCount: 4096);
                    for (var j = 0; j < streamCount; j++)
                    {
                        var triple = reader.ReadStartArray(maxCount: 3);
                        if (triple != 3)
                        {
                            throw new ManifestValidationException(
                                "An alternate stream is exactly three elements (specification 06 §4.1).");
                        }

                        streams.Add(new AlternateStreamEntry(
                            reader.ReadByteString(maxLength: 1024),
                            ObjectId.FromBytes(reader.ReadFixedByteString(32)),
                            reader.ReadUnsignedInteger()));
                        reader.ReadEndArray();
                    }

                    reader.ReadEndArray();
                    break;
                case 10:
                    fileAttributes = reader.ReadUInt32();
                    break;
                default:
                    throw new ManifestValidationException(
                        "The metadata map carries an unknown key; specification 06 §4.1 assigns keys 1-10 only.");
            }
        }

        reader.ReadEndMap();

        return new EntryMetadata
        {
            ModifiedAt = modified,
            CreatedAt = created,
            AccessedAt = accessed,
            PosixMode = posixMode,
            OwnerName = owner,
            GroupName = group,
            WindowsSecurityDescriptor = descriptor is null ? null : (ReadOnlyMemory<byte>?)descriptor,
            ExtendedAttributes = attributes,
            AlternateStreams = streams,
            FileAttributes = fileAttributes,
        };
    }
}
