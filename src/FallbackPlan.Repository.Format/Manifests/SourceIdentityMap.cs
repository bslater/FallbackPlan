using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Cbor;

namespace FallbackPlan.Repository.Format.Manifests;

/// <summary>
/// One entry of the source-identity map: a published file version, and the
/// keyed identity of the file it was captured from (specification 06 §11).
/// </summary>
public sealed record SourceIdentity(ObjectId ObjectId, ReadOnlyMemory<byte> SourceKey)
{
    /// <summary>Compares the source key by value, not by buffer reference.</summary>
    public bool Equals(SourceIdentity? other) =>
        other is not null && ObjectId == other.ObjectId && SourceKey.Span.SequenceEqual(other.SourceKey.Span);

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
/// The advisory per-snapshot source-identity map (specification 06 §11),
/// published at <c>/hints/identity/&lt;snapshot-id&gt;</c>.
/// </summary>
/// <remarks>
/// It answers one question the catalogue also answers, and answers it
/// durably: which prior file version came from this same inode. The
/// catalogue is a disposable cache, so after a rebuild it has no identities
/// — and a rename captured in that window would record no
/// <c>parent_version</c> at all, severing the file's history permanently in
/// an immutable manifest because a local cache happened to be cold.
/// </remarks>
public sealed record SourceIdentityMap
{
    /// <summary>The schema version; only 1 exists.</summary>
    public const ushort CurrentSchemaVersion = 1;

    /// <summary>The upper bound on entries a reader will accept in one map.</summary>
    public const int MaximumIdentities = 20_000_000;

    /// <summary>The snapshot these identities were captured in, 16 bytes.</summary>
    public required ReadOnlyMemory<byte> SnapshotId { get; init; }

    /// <summary>The identities, sorted by object identifier ascending.</summary>
    public required IReadOnlyList<SourceIdentity> Identities { get; init; }

    /// <summary>
    /// Builds a map from a capture's raw observations, enforcing the §11
    /// uniqueness rule the encoder then relies on.
    /// </summary>
    /// <remarks>
    /// One object identifier can legitimately be observed twice in a single
    /// snapshot: a file-version manifest is identified by its own bytes, so
    /// two files with the same name, content, and metadata in different
    /// directories are one object. When those two files are different inodes
    /// the object cannot say which one it came from, and this drops it —
    /// asserting either would attach an ancestry that is as likely wrong as
    /// right, to a manifest that keeps it forever. Exact repeats, from the
    /// same inode, collapse to one entry.
    /// </remarks>
    public static SourceIdentityMap Create(
        ReadOnlyMemory<byte> snapshotId, IEnumerable<SourceIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);

        var claimed = new Dictionary<ObjectId, SourceIdentity?>();
        foreach (var identity in identities)
        {
            if (!claimed.TryGetValue(identity.ObjectId, out var existing))
            {
                claimed.Add(identity.ObjectId, identity);
            }
            else if (existing is not null && !existing.Equals(identity))
            {
                claimed[identity.ObjectId] = null;
            }
        }

        return new SourceIdentityMap
        {
            SnapshotId = snapshotId,
            Identities = [.. claimed.Values.Where(static identity => identity is not null).Select(static identity => identity!)],
        };
    }
}

/// <summary>Encodes and decodes source-identity maps (specification 06 §11).</summary>
public static class SourceIdentityMapCodec
{
    /// <summary>
    /// Encodes a map canonically, sorting the identities by object identifier
    /// so the same capture always produces the same bytes.
    /// </summary>
    /// <exception cref="ArgumentException">The snapshot identifier is not 16 bytes, or a source key is not 16 bytes.</exception>
    public static byte[] Encode(SourceIdentityMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        if (map.SnapshotId.Length != 16)
        {
            throw new ArgumentException("A snapshot identifier is exactly 16 bytes.", nameof(map));
        }

        var ordered = new List<SourceIdentity>(map.Identities);
        ordered.Sort(static (left, right) => CompareObjectIds(left.ObjectId, right.ObjectId));

        var writer = new CanonicalCborWriter();
        writer.WriteStartMap(3);
        writer.WriteKey(1);
        writer.WriteUnsignedInteger(SourceIdentityMap.CurrentSchemaVersion);
        writer.WriteKey(2);
        writer.WriteByteString(map.SnapshotId.Span);
        writer.WriteKey(3);
        writer.WriteStartArray(ordered.Count);

        Span<byte> objectId = stackalloc byte[ObjectId.Size];
        for (var i = 0; i < ordered.Count; i++)
        {
            var identity = ordered[i];

            if (identity.SourceKey.Length != SourceKeyLength)
            {
                throw new ArgumentException($"A source key is exactly {SourceKeyLength} bytes.", nameof(map));
            }

            if (i > 0 && ordered[i - 1].ObjectId == identity.ObjectId)
            {
                throw new ArgumentException(
                    "A source-identity map names each object identifier once (specification 06 §11); " +
                    $"use {nameof(SourceIdentityMap)}.{nameof(SourceIdentityMap.Create)} to resolve repeats.",
                    nameof(map));
            }

            identity.ObjectId.CopyTo(objectId);
            writer.WriteStartArray(2);
            writer.WriteByteString(objectId);
            writer.WriteByteString(identity.SourceKey.Span);
            writer.WriteEndArray();
        }

        writer.WriteEndArray();
        writer.WriteEndMap();

        return writer.Encode();
    }

    /// <summary>Decodes and validates a source-identity map.</summary>
    /// <exception cref="ManifestValidationException">The bytes violate specification 06 §11.</exception>
    public static SourceIdentityMap Decode(ReadOnlyMemory<byte> data)
    {
        try
        {
            return DecodeCore(data);
        }
        catch (CborFormatException exception)
        {
            throw new ManifestValidationException(
                $"The source-identity map is not canonical CBOR: {exception.Message}", exception);
        }
    }

    /// <summary>The length of a <c>source_key</c>, in bytes.</summary>
    public const int SourceKeyLength = 16;

    private static SourceIdentityMap DecodeCore(ReadOnlyMemory<byte> data)
    {
        var reader = new CanonicalCborReader(data);
        var entryCount = reader.ReadStartMap();

        if (entryCount != 3)
        {
            throw new ManifestValidationException(
                "A source-identity map carries exactly keys 1-3 (specification 06 §11).");
        }

        if (reader.ReadKey() != 1)
        {
            throw new ManifestValidationException("A source-identity map opens with key 1, the schema version.");
        }

        var schemaVersion = reader.ReadUInt16();
        if (schemaVersion != SourceIdentityMap.CurrentSchemaVersion)
        {
            throw new ManifestValidationException(
                $"Source-identity schema {schemaVersion} is unknown; this reader implements {SourceIdentityMap.CurrentSchemaVersion}.");
        }

        if (reader.ReadKey() != 2)
        {
            throw new ManifestValidationException("A source-identity map carries the snapshot identifier at key 2.");
        }

        var snapshotId = reader.ReadFixedByteString(16);

        if (reader.ReadKey() != 3)
        {
            throw new ManifestValidationException("A source-identity map carries the identities at key 3.");
        }

        var identityCount = reader.ReadStartArray(SourceIdentityMap.MaximumIdentities);
        var identities = new List<SourceIdentity>(identityCount);
        ObjectId? previous = null;

        for (var i = 0; i < identityCount; i++)
        {
            if (reader.ReadStartArray(maxCount: 2) != 2)
            {
                throw new ManifestValidationException(
                    "An identity is exactly [object_id, source_key] (specification 06 §11).");
            }

            var objectId = ObjectId.FromBytes(reader.ReadFixedByteString(ObjectId.Size));
            var sourceKey = reader.ReadFixedByteString(SourceKeyLength);
            reader.ReadEndArray();

            // Sorted and unique, both checked. A duplicate is not a
            // tie-break for the reader to resolve: the two entries disagree
            // about which file this version came from, and guessing would
            // attach the wrong ancestry to a manifest that keeps it forever.
            if (previous is { } earlier)
            {
                var order = CompareObjectIds(earlier, objectId);
                if (order > 0)
                {
                    throw new ManifestValidationException(
                        "Source identities are sorted by object identifier ascending (specification 06 §11).");
                }

                if (order == 0)
                {
                    throw new ManifestValidationException(
                        "A source-identity map names the same object identifier twice (specification 06 §11).");
                }
            }

            previous = objectId;
            identities.Add(new SourceIdentity(objectId, sourceKey));
        }

        reader.ReadEndArray();
        reader.ReadEndMap();
        reader.AssertEndOfDocument();

        return new SourceIdentityMap { SnapshotId = snapshotId, Identities = identities };
    }

    private static int CompareObjectIds(ObjectId left, ObjectId right)
    {
        Span<byte> a = stackalloc byte[ObjectId.Size];
        Span<byte> b = stackalloc byte[ObjectId.Size];
        left.CopyTo(a);
        right.CopyTo(b);
        return a.SequenceCompareTo(b);
    }
}
