using Bodu;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Cbor;

namespace FallbackPlan.Repository.Format.Manifests;

/// <summary>
/// The advisory source-identity hint (specification 06 §11): one small object
/// saying which file version a snapshot captured from one source file,
/// published at
/// <c>/hints/identity/&lt;shard&gt;/&lt;source-key&gt;/&lt;captured-at&gt;/&lt;snapshot-id&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// It answers one question the catalogue also answers, and answers it
/// durably: which prior file version came from this same inode. The
/// catalogue is a disposable cache, so after a rebuild it has no identities
/// — and a rename captured in that window would record no
/// <c>parent_version</c> at all, severing the file's history permanently in
/// an immutable manifest because a local database happened to be cold.
/// </para>
/// <para>
/// One object per file version, written only when a version is created, is
/// what keeps that durability from costing growth proportional to the
/// repository. The earlier design was one object per snapshot naming every
/// file it contained, which bought the same answer at ~52 bytes per file
/// every run — the growth NFR-PERF-005 exists to prevent.
/// </para>
/// <para>
/// The store key repeats <see cref="SourceKey"/>, <see cref="SnapshotId"/>
/// and <see cref="CapturedAt"/>, and the body repeats them deliberately: a
/// store key is not covered by the AEAD, so a reader must be able to check
/// that the object it fetched is the object that key promised.
/// </para>
/// </remarks>
public sealed record SourceIdentityHint
{
    /// <summary>The schema version; only 1 exists.</summary>
    public const ushort CurrentSchemaVersion = 1;

    /// <summary>The length of a <c>source_key</c>, in bytes.</summary>
    public const int SourceKeyLength = 16;

    /// <summary>The keyed identity of the source file, 16 bytes.</summary>
    public required ReadOnlyMemory<byte> SourceKey { get; init; }

    /// <summary>The snapshot that captured this version, 16 bytes.</summary>
    public required ReadOnlyMemory<byte> SnapshotId { get; init; }

    /// <summary>The file version captured from that source.</summary>
    public required ObjectId ObjectId { get; init; }

    /// <summary>The snapshot's capture time, epoch milliseconds.</summary>
    public required ulong CapturedAt { get; init; }

    /// <summary>Compares the byte fields by value, not by buffer reference.</summary>
    public bool Equals(SourceIdentityHint? other) =>
        other is not null
        && ObjectId == other.ObjectId
        && CapturedAt == other.CapturedAt
        && SourceKey.Span.SequenceEqual(other.SourceKey.Span)
        && SnapshotId.Span.SequenceEqual(other.SnapshotId.Span);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ObjectId);
        hash.Add(CapturedAt);
        hash.AddBytes(SourceKey.Span);
        hash.AddBytes(SnapshotId.Span);
        return hash.ToHashCode();
    }
}

/// <summary>Encodes and decodes source-identity hints (specification 06 §11).</summary>
public static class SourceIdentityHintCodec
{
    /// <summary>Encodes a hint canonically.</summary>
    /// <exception cref="ArgumentException">A fixed-width field is the wrong length.</exception>
    public static byte[] Encode(SourceIdentityHint hint)
    {
        ThrowHelper.ThrowIfNull(hint);

        if (hint.SourceKey.Length != SourceIdentityHint.SourceKeyLength)
        {
            throw new ArgumentException(
                $"A source key is exactly {SourceIdentityHint.SourceKeyLength} bytes.", nameof(hint));
        }

        if (hint.SnapshotId.Length != 16)
        {
            throw new ArgumentException("A snapshot identifier is exactly 16 bytes.", nameof(hint));
        }

        var writer = new CanonicalCborWriter();
        writer.WriteStartMap(5);
        writer.WriteKey(1);
        writer.WriteUnsignedInteger(SourceIdentityHint.CurrentSchemaVersion);
        writer.WriteKey(2);
        writer.WriteByteString(hint.SourceKey.Span);
        writer.WriteKey(3);
        writer.WriteByteString(hint.SnapshotId.Span);
        writer.WriteKey(4);
        writer.WriteByteString(hint.ObjectId.ToArray());
        writer.WriteKey(5);
        writer.WriteUnsignedInteger(hint.CapturedAt);
        writer.WriteEndMap();

        return writer.Encode();
    }

    /// <summary>Decodes and validates a source-identity hint.</summary>
    /// <exception cref="ManifestValidationException">The bytes violate specification 06 §11.</exception>
    public static SourceIdentityHint Decode(ReadOnlyMemory<byte> data)
    {
        try
        {
            return DecodeCore(data);
        }
        catch (CborFormatException exception)
        {
            throw new ManifestValidationException(
                $"The source-identity hint is not canonical CBOR: {exception.Message}", exception);
        }
    }

    private static SourceIdentityHint DecodeCore(ReadOnlyMemory<byte> data)
    {
        var reader = new CanonicalCborReader(data);

        if (reader.ReadStartMap() != 5)
        {
            throw new ManifestValidationException(
                "A source-identity hint carries exactly keys 1-5 (specification 06 §11).");
        }

        if (reader.ReadKey() != 1)
        {
            throw new ManifestValidationException("A source-identity hint opens with key 1, the schema version.");
        }

        var schemaVersion = reader.ReadUInt16();
        if (schemaVersion != SourceIdentityHint.CurrentSchemaVersion)
        {
            throw new ManifestValidationException(
                $"Source-identity schema {schemaVersion} is unknown; this reader implements " +
                $"{SourceIdentityHint.CurrentSchemaVersion}.");
        }

        if (reader.ReadKey() != 2)
        {
            throw new ManifestValidationException("A source-identity hint carries the source key at key 2.");
        }

        var sourceKey = reader.ReadFixedByteString(SourceIdentityHint.SourceKeyLength);

        if (reader.ReadKey() != 3)
        {
            throw new ManifestValidationException("A source-identity hint carries the snapshot identifier at key 3.");
        }

        var snapshotId = reader.ReadFixedByteString(16);

        if (reader.ReadKey() != 4)
        {
            throw new ManifestValidationException("A source-identity hint carries the object identifier at key 4.");
        }

        var objectId = ObjectId.FromBytes(reader.ReadFixedByteString(ObjectId.Size));

        if (reader.ReadKey() != 5)
        {
            throw new ManifestValidationException("A source-identity hint carries the capture time at key 5.");
        }

        var capturedAt = reader.ReadUnsignedInteger();

        reader.ReadEndMap();
        reader.AssertEndOfDocument();

        return new SourceIdentityHint
        {
            SourceKey = sourceKey,
            SnapshotId = snapshotId,
            ObjectId = objectId,
            CapturedAt = capturedAt,
        };
    }
}
