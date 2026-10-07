using Bodu;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Cbor;
using FallbackPlan.Repository.Format.Resources;

namespace FallbackPlan.Repository.Format.Manifests;

/// <summary>
/// One entry of a source-identity pack: which file version a publication
/// created from one source file (specification 06 §11.5).
/// </summary>
public sealed record SourceIdentityPackEntry
{
    /// <summary>The keyed identity of the source file, 16 bytes (06 §11).</summary>
    public required ReadOnlyMemory<byte> SourceKey { get; init; }

    /// <summary>The file version the publication captured from that source.</summary>
    public required ObjectId ObjectId { get; init; }

    /// <summary>Compares the source key by value, not by buffer reference.</summary>
    public bool Equals(SourceIdentityPackEntry? other) =>
        other is not null
        && ObjectId == other.ObjectId
        && SourceKey.Span.SequenceEqual(other.SourceKey.Span);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ObjectId);
        hash.AddBytes(SourceKey.Span);
        return hash.ToHashCode();
    }
}

/// <summary>
/// The advisory source-identity pack (specification 06 §11.5): the
/// source-identity hints of every file version one publication created, in
/// one object at
/// <c>/hints/identity-pack/&lt;device&gt;/&lt;captured-at&gt;/&lt;snapshot-id&gt;/&lt;part&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// It answers the question the per-file hint answers — which prior version
/// came from this same source file — for a whole publication at once. A hint
/// per version was a store request per version, and on a store that charges by
/// the request that was most of what a first backup cost (NFR-PERF-008,
/// ADR-0090). A pack still names only the versions its publication
/// <em>created</em>, so what a backup writes still follows what changed
/// (NFR-PERF-005).
/// </para>
/// <para>
/// The store key repeats the device, the capture time, the snapshot and the
/// part, and the body repeats them deliberately: a store key is not covered
/// by the AEAD, so a reader must be able to check that the object it fetched
/// is the object that key promised.
/// </para>
/// </remarks>
public sealed record SourceIdentityPack
{
    /// <summary>The schema version; only 1 exists.</summary>
    public const ushort CurrentSchemaVersion = 1;

    /// <summary>
    /// The most entries one pack holds. A publication that created more writes
    /// further parts. At 52 bytes an entry a full pack is under 7 MB, well
    /// inside the 16 MiB a standalone metadata object may be
    /// (<see cref="FallbackPlan.Domain.FormatLimits.MaxMetadataObjectSize"/>).
    /// </summary>
    public const int MaxEntries = 131_072;

    /// <summary>The device whose source keys the entries are, 16 bytes.</summary>
    public required ReadOnlyMemory<byte> DeviceId { get; init; }

    /// <summary>The snapshot that created the versions, 16 bytes.</summary>
    public required ReadOnlyMemory<byte> SnapshotId { get; init; }

    /// <summary>The snapshot's capture time, epoch milliseconds.</summary>
    public required ulong CapturedAt { get; init; }

    /// <summary>Which part of the publication's hints this is, from 0.</summary>
    public required uint Part { get; init; }

    /// <summary>The entries; the codec writes them ascending by source key.</summary>
    public required IReadOnlyList<SourceIdentityPackEntry> Entries { get; init; }
}

/// <summary>Encodes and decodes source-identity packs (specification 06 §11.5).</summary>
public static class SourceIdentityPackCodec
{
    private const int IdentifierLength = 16;

    /// <summary>
    /// Encodes a pack canonically: its entries ascending by source key,
    /// whatever order they are given in.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A fixed-width field is the wrong length, the pack names no version or
    /// more than <see cref="SourceIdentityPack.MaxEntries"/>, or it names one
    /// source key twice.
    /// </exception>
    public static byte[] Encode(SourceIdentityPack pack)
    {
        ThrowHelper.ThrowIfNull(pack);
        ThrowHelper.ThrowIfNull(pack.Entries);

        if (pack.DeviceId.Length != IdentifierLength)
        {
            throw new ArgumentException(Strings.SourceIdentityPackCodec_DeviceIdentifierExactlyBytes, nameof(pack));
        }

        if (pack.SnapshotId.Length != IdentifierLength)
        {
            throw new ArgumentException(Strings.SourceIdentityPackCodec_SnapshotIdentifierExactlyBytes, nameof(pack));
        }

        if (pack.Entries.Count is 0 or > SourceIdentityPack.MaxEntries)
        {
            throw new ArgumentException(
                Strings.FormatSourceIdentityPackCodec_EntryCountOutOfRange(SourceIdentityPack.MaxEntries, pack.Entries.Count),
                nameof(pack));
        }

        foreach (var entry in pack.Entries)
        {
            if (entry.SourceKey.Length != SourceIdentityHint.SourceKeyLength)
            {
                throw new ArgumentException(
                    Strings.FormatSourceIdentityPackCodec_SourceKeyExactlyBytes(SourceIdentityHint.SourceKeyLength),
                    nameof(pack));
            }
        }

        var ordered = pack.Entries.ToArray();
        Array.Sort(ordered, static (left, right) => left.SourceKey.Span.SequenceCompareTo(right.SourceKey.Span));
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1].SourceKey.Span.SequenceEqual(ordered[index].SourceKey.Span))
            {
                throw new ArgumentException(Strings.SourceIdentityPackCodec_SourceKeyRepeated, nameof(pack));
            }
        }

        var writer = new CanonicalCborWriter();
        writer.WriteStartMap(6);
        writer.WriteKey(1);
        writer.WriteUnsignedInteger(SourceIdentityPack.CurrentSchemaVersion);
        writer.WriteKey(2);
        writer.WriteByteString(pack.DeviceId.Span);
        writer.WriteKey(3);
        writer.WriteByteString(pack.SnapshotId.Span);
        writer.WriteKey(4);
        writer.WriteUnsignedInteger(pack.CapturedAt);
        writer.WriteKey(5);
        writer.WriteUnsignedInteger(pack.Part);
        writer.WriteKey(6);
        writer.WriteStartArray(ordered.Length);
        foreach (var entry in ordered)
        {
            writer.WriteStartArray(2);
            writer.WriteByteString(entry.SourceKey.Span);
            writer.WriteByteString(entry.ObjectId.ToArray());
            writer.WriteEndArray();
        }

        writer.WriteEndArray();
        writer.WriteEndMap();

        return writer.Encode();
    }

    /// <summary>Decodes and validates a source-identity pack.</summary>
    /// <exception cref="ManifestValidationException">The bytes violate specification 06 §11.5.</exception>
    public static SourceIdentityPack Decode(ReadOnlyMemory<byte> data)
    {
        try
        {
            return DecodeCore(data);
        }
        catch (CborFormatException exception)
        {
            throw new ManifestValidationException(
                Strings.FormatSourceIdentityPackCodec_SourceIdentityPackNotCanonical(exception.Message), exception);
        }
    }

    private static SourceIdentityPack DecodeCore(ReadOnlyMemory<byte> data)
    {
        var reader = new CanonicalCborReader(data);

        if (reader.ReadStartMap() != 6)
        {
            throw new ManifestValidationException(Strings.SourceIdentityPackCodec_SourceIdentityPackCarriesExactly);
        }

        ExpectKey(reader, 1);
        var schemaVersion = reader.ReadUInt16();
        if (schemaVersion != SourceIdentityPack.CurrentSchemaVersion)
        {
            throw new ManifestValidationException(
                Strings.FormatSourceIdentityPackCodec_SourceIdentityPackSchemaUnknownReader(
                    schemaVersion, SourceIdentityPack.CurrentSchemaVersion));
        }

        ExpectKey(reader, 2);
        var deviceId = reader.ReadFixedByteString(IdentifierLength);

        ExpectKey(reader, 3);
        var snapshotId = reader.ReadFixedByteString(IdentifierLength);

        ExpectKey(reader, 4);
        var capturedAt = reader.ReadUnsignedInteger();

        ExpectKey(reader, 5);
        var part = reader.ReadUInt32();

        ExpectKey(reader, 6);
        var count = reader.ReadStartArray(SourceIdentityPack.MaxEntries);
        if (count == 0)
        {
            throw new ManifestValidationException(
                Strings.FormatSourceIdentityPackCodec_EntryCountOutOfRange(SourceIdentityPack.MaxEntries, count));
        }

        var entries = new SourceIdentityPackEntry[count];
        for (var index = 0; index < count; index++)
        {
            if (reader.ReadStartArray(2) != 2)
            {
                throw new ManifestValidationException(Strings.SourceIdentityPackCodec_EntryIsSourceKeyAndObject);
            }

            var sourceKey = reader.ReadFixedByteString(SourceIdentityHint.SourceKeyLength);
            var objectId = ObjectId.FromBytes(reader.ReadFixedByteString(ObjectId.Size));
            reader.ReadEndArray();

            // Strictly ascending: a later entry for the same key, or an
            // earlier key after a later one, is not what a conforming writer
            // writes, and "the last entry wins" must not depend on who wrote
            // the body.
            if (index > 0 && entries[index - 1].SourceKey.Span.SequenceCompareTo(sourceKey) >= 0)
            {
                throw new ManifestValidationException(Strings.SourceIdentityPackCodec_EntriesAscendBySourceKey);
            }

            entries[index] = new SourceIdentityPackEntry { SourceKey = sourceKey, ObjectId = objectId };
        }

        reader.ReadEndArray();
        reader.ReadEndMap();
        reader.AssertEndOfDocument();

        return new SourceIdentityPack
        {
            DeviceId = deviceId,
            SnapshotId = snapshotId,
            CapturedAt = capturedAt,
            Part = part,
            Entries = entries,
        };
    }

    private static void ExpectKey(CanonicalCborReader reader, uint expected)
    {
        var key = reader.ReadKey();
        if (key != expected)
        {
            throw new ManifestValidationException(
                Strings.FormatSourceIdentityPackCodec_SourceIdentityPackKeyOutOfPlace(key, expected));
        }
    }
}
