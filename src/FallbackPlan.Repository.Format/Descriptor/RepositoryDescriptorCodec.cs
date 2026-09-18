using Bodu;
using System.Buffers.Binary;
using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Cbor;
using FallbackPlan.Repository.Format.Resources;

namespace FallbackPlan.Repository.Format.Descriptor;

/// <summary>
/// Serialises and parses the repository descriptor (specification 01 §3;
/// FR-REP-002, NFR-COMP-003): <c>FBPKREPO</c> magic, u16 version, u16
/// reserved, u32 body length (max 65 536), deterministic CBOR body keys 1–8,
/// SHA-256 over everything before the digest. The magic is checked first —
/// an object without it is "not a FallbackPlan repository", never a parse
/// error — and the digest is verified before the body is interpreted.
/// </summary>
public static class RepositoryDescriptorCodec
{
    /// <summary>The descriptor magic, <c>"FBPKREPO"</c>.</summary>
    public static ReadOnlySpan<byte> Magic => "FBPKREPO"u8;

    /// <summary>The fixed framing before the body: magic, version, reserved, length.</summary>
    public const int HeaderLength = 16;

    /// <summary>The trailing SHA-256 length.</summary>
    public const int DigestLength = 32;

    /// <summary>The Argon2id KDF profile value (specification 01 §3.3).</summary>
    public const ushort KdfProfileArgon2id = 0x0001;

    /// <summary>
    /// The sealed-data-plane feature (ADR-0042): a format-v2 write-only
    /// repository names it in <c>required_features</c>, so a reader that
    /// predates it refuses through the 01 §3.2 path with the identifier
    /// named, never by half-reading sealed blobs.
    /// </summary>
    public const ushort FeatureSealedDataPlane = 0x0001;

    /// <summary>
    /// The reclaim-authority feature
    /// ([ADR-0055](../../../docs/adr/0055-reclaim-authority.md) §4): a
    /// repository naming it in <c>required_features</c> signs its tombstones
    /// under the reclaim key rather than the signing key, and a collector
    /// verifies them that way.
    /// </summary>
    /// <remarks>
    /// A repository-level statement and deliberately not the tombstone's own
    /// <c>schema_version</c>. A per-object version is a per-object choice and
    /// the attacker makes it — write a schema-1 tombstone and the weaker key
    /// is back. Required rather than optional for the same reason: an
    /// optional feature lets an older collector proceed and accept
    /// signing-key tombstones, which is the downgrade this exists to stop.
    /// </remarks>
    public const ushort FeatureReclaimAuthority = 0x0002;

    /// <summary>
    /// The relocatable-records feature
    /// ([ADR-0052](../../../docs/adr/0052-relocatable-records-format-v3.md)
    /// Amendment 1): a format-3 repository names it in
    /// <c>required_features</c> — and a format-2 repository never does — so a
    /// reader that predates format 3 refuses through the 01 §3.2 path with
    /// the identifier named, rather than half-reading records whose nonces
    /// it would misconstruct.
    /// </summary>
    public const ushort FeatureRelocatableRecords = 0x0003;

    /// <summary>The feature identifiers this implementation understands.</summary>
    private static readonly HashSet<ushort> Implemented =
        [FeatureSealedDataPlane, FeatureReclaimAuthority, FeatureRelocatableRecords];

    /// <summary>Serialises a descriptor to its store bytes.</summary>
    public static byte[] Serialize(RepositoryDescriptor descriptor)
    {
        ThrowHelper.ThrowIfNull(descriptor);

        if (descriptor.KdfSalt.Length != 16)
        {
            throw new ArgumentException(Strings.RepositoryDescriptorCodec_KDFSaltExactlyBytes, nameof(descriptor));
        }

        // Only formats 2 and 3 are written (ADR-0042 §1, ADR-0052; format 1
        // withdrawn before freeze), and a descriptor of either is nothing
        // without its sealing public key — the verifier every open compares
        // against. A caller handing over anything else is a bug refused here
        // rather than a stored contradiction.
        if (!FormatVersions.IsReadable(descriptor.FormatVersion))
        {
            throw new ArgumentException(
                $"Only format {FormatLimits.FormatVersion} to {FormatLimits.LatestFormatVersion} descriptors are "
                + "written; format 1 is withdrawn.",
                nameof(descriptor));
        }

        if (descriptor.SealingPublicKey.Length != 32)
        {
            throw new ArgumentException(
                "A format-2 or format-3 descriptor carries exactly a 32-byte sealing public key (ADR-0042).",
                nameof(descriptor));
        }

        if (FeatureVersionDisagreement(descriptor.FormatVersion, descriptor.RequiredFeatures) is { } disagreement)
        {
            throw new ArgumentException(disagreement, nameof(descriptor));
        }

        var writer = new CanonicalCborWriter();
        writer.WriteStartMap(9);
        writer.WriteKey(1);
        writer.WriteByteString(descriptor.RepositoryId.ToArray());
        writer.WriteKey(2);
        writer.WriteUnsignedInteger(descriptor.FormatVersion);
        writer.WriteKey(3);
        writer.WriteStartArray(descriptor.RequiredFeatures.Count);
        foreach (var feature in descriptor.RequiredFeatures)
        {
            writer.WriteUnsignedInteger(feature);
        }

        writer.WriteEndArray();
        writer.WriteKey(4);
        writer.WriteStartArray(descriptor.OptionalFeatures.Count);
        foreach (var feature in descriptor.OptionalFeatures)
        {
            writer.WriteUnsignedInteger(feature);
        }

        writer.WriteEndArray();
        writer.WriteKey(5);
        writer.WriteStartMap(5);
        writer.WriteKey(1);
        writer.WriteUnsignedInteger(KdfProfileArgon2id);
        writer.WriteKey(2);
        writer.WriteByteString(descriptor.KdfSalt.Span);
        writer.WriteKey(3);
        writer.WriteUnsignedInteger(descriptor.KdfParameters.MemoryKiB);
        writer.WriteKey(4);
        writer.WriteUnsignedInteger(descriptor.KdfParameters.Iterations);
        writer.WriteKey(5);
        writer.WriteUnsignedInteger(descriptor.KdfParameters.Parallelism);
        writer.WriteEndMap();
        writer.WriteKey(6);
        writer.WriteUnsignedInteger(descriptor.CreatedAt);
        writer.WriteKey(7);
        writer.WriteTextString(descriptor.CreatedBy);
        writer.WriteKey(8);
        writer.WriteBoolean(descriptor.UnstableFormat);
        writer.WriteKey(9);
        writer.WriteByteString(descriptor.SealingPublicKey.Span);
        writer.WriteEndMap();

        var body = writer.Encode();

        if (body.Length > FormatLimits.MaxDescriptorCborLength)
        {
            throw new ArgumentException(Strings.FormatRepositoryDescriptorCodec_DescriptorBodyBytesLimit(body.Length, FormatLimits.MaxDescriptorCborLength),
                nameof(descriptor));
        }

        var buffer = new byte[HeaderLength + body.Length + DigestLength];
        var span = buffer.AsSpan();

        Magic.CopyTo(span);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], descriptor.FormatVersion);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], 0); // reserved, MUST be zero on write (00 §9)
        BinaryPrimitives.WriteUInt32BigEndian(span[12..], (uint)body.Length);
        body.CopyTo(span[HeaderLength..]);
        SHA256.HashData(span[..(HeaderLength + body.Length)], span[(HeaderLength + body.Length)..]);

        return buffer;
    }

    /// <summary>
    /// Parses candidate descriptor bytes through the 01 §3 sequence: magic
    /// first, then framing, then digest, then body — each refusal a distinct
    /// finding.
    /// </summary>
    public static DescriptorParseResult Parse(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;

        if (span.Length < 8 || !span[..8].SequenceEqual(Magic))
        {
            return new DescriptorParseResult.NotARepository();
        }

        if (span.Length < HeaderLength + DigestLength)
        {
            return new DescriptorParseResult.FormatViolation(
                $"A descriptor is at least {HeaderLength + DigestLength} bytes; got {span.Length} (specification 01 §3.1).");
        }

        // 00 §9: the reserved field is ignored on read — a reader MUST NOT
        // refuse an object because it is non-zero.
        var cborLength = BinaryPrimitives.ReadUInt32BigEndian(span[12..]);

        if (cborLength > FormatLimits.MaxDescriptorCborLength)
        {
            return new DescriptorParseResult.FormatViolation(
                $"The declared body length {cborLength} exceeds the 65 536-byte limit (specification 01 §3.1) — refused before allocation.");
        }

        if (span.Length != HeaderLength + (int)cborLength + DigestLength)
        {
            return new DescriptorParseResult.FormatViolation(
                $"The object is {span.Length} bytes; the framing declares {HeaderLength + cborLength + DigestLength} (specification 01 §3.1).");
        }

        Span<byte> digest = stackalloc byte[DigestLength];
        SHA256.HashData(span[..(HeaderLength + (int)cborLength)], digest);

        if (!digest.SequenceEqual(span[(HeaderLength + (int)cborLength)..]))
        {
            return new DescriptorParseResult.IntegrityFailure();
        }

        var framingVersion = BinaryPrimitives.ReadUInt16BigEndian(span[8..]);

        try
        {
            return ParseBody(data.Slice(HeaderLength, (int)cborLength), framingVersion);
        }
        catch (CborFormatException exception)
        {
            return new DescriptorParseResult.FormatViolation(exception.Message);
        }
    }

    private static DescriptorParseResult ParseBody(ReadOnlyMemory<byte> body, ushort framingVersion)
    {
        var reader = new CanonicalCborReader(body);
        var count = reader.ReadStartMap();

        if (count != 9)
        {
            return new DescriptorParseResult.FormatViolation(
                $"The descriptor body carries {count} keys; specification 01 §3.2 defines 9.");
        }

        RepositoryId repositoryId = default;
        ushort formatVersion = 0;
        List<ushort>? required = null;
        List<ushort>? optional = null;
        Argon2Parameters? kdf = null;
        byte[]? salt = null;
        ulong createdAt = 0;
        string? createdBy = null;
        var unstable = false;
        byte[]? sealingPublicKey = null;

        for (var i = 0; i < count; i++)
        {
            var key = reader.ReadKey();

            switch (key)
            {
                case 1:
                    repositoryId = RepositoryId.FromBytes(reader.ReadFixedByteString(RepositoryId.Size));
                    break;
                case 2:
                    formatVersion = reader.ReadUInt16();
                    break;
                case 3:
                    required = ReadFeatureArray(reader);
                    break;
                case 4:
                    optional = ReadFeatureArray(reader);
                    break;
                case 5:
                    (kdf, salt) = ReadKdfParameters(reader);
                    break;
                case 6:
                    createdAt = reader.ReadUnsignedInteger();
                    break;
                case 7:
                    createdBy = reader.ReadTextString(maxUtf8Length: 256);
                    break;
                case 8:
                    unstable = reader.ReadBoolean();
                    break;
                case 9:
                    sealingPublicKey = reader.ReadFixedByteString(32);
                    break;
                default:
                    return new DescriptorParseResult.FormatViolation(
                        $"The descriptor body carries unknown key {key}; specification 01 §3.2 assigns keys 1-9 only.");
            }
        }

        reader.ReadEndMap();
        reader.AssertEndOfDocument();

        if (required is null || optional is null || kdf is null || salt is null || createdBy is null)
        {
            return new DescriptorParseResult.FormatViolation(
                "The descriptor body omits a mandatory key (specification 01 §3.2).");
        }

        if (formatVersion != framingVersion)
        {
            return new DescriptorParseResult.FormatViolation(
                $"The body's format_version {formatVersion} disagrees with the framing's {framingVersion} — the corruption check specification 01 §3.2 defines this field for.");
        }

        // Refused by name, never misread (ADR-0014): format 1 — a master key
        // wrapped under the passphrase at /keys/ — was withdrawn before any
        // freeze, and no reader of it remains. A repository stamped with it
        // is re-seeded from a live installation, not opened. A version above
        // the newest this build reads is refused the same way, naming the
        // range, before the feature check can name the feature: either
        // refusal is by name, and the version is the earlier fact.
        if (!FormatVersions.IsReadable(formatVersion))
        {
            return new DescriptorParseResult.FormatViolation(
                $"The repository is format {formatVersion}; formats {FormatLimits.FormatVersion} to "
                + $"{FormatLimits.LatestFormatVersion} are read. "
                + (formatVersion < FormatLimits.FormatVersion
                    ? "Format 1 is withdrawn — re-seed this location from a live installation."
                    : "Update this installation to read it."));
        }

        // A descriptor without its sealing public key has lost its verifier;
        // the map count admits the key, and this makes it mandatory.
        if (sealingPublicKey is null)
        {
            return new DescriptorParseResult.FormatViolation(
                "The sealing public key (key 9) is mandatory for a format-2 or format-3 descriptor (ADR-0042).");
        }

        var unsupported = required.Where(feature => !Implemented.Contains(feature)).ToArray();
        if (unsupported.Length > 0)
        {
            return new DescriptorParseResult.UnsupportedRequiredFeatures(unsupported);
        }

        // The version and the relocatable-records feature name one fact
        // (01 §3.2): a descriptor in which they disagree was not written by
        // a conforming writer, and reading it either way would be a guess.
        if (FeatureVersionDisagreement(formatVersion, required) is { } disagreement)
        {
            return new DescriptorParseResult.FormatViolation(disagreement);
        }

        return new DescriptorParseResult.Ok(new RepositoryDescriptor(
            repositoryId,
            formatVersion,
            required,
            optional,
            kdf,
            salt,
            createdAt,
            createdBy,
            unstable,
            sealingPublicKey));
    }

    /// <summary>
    /// Why the version and the relocatable-records feature disagree, or null
    /// when they agree: a format-3 descriptor lists the feature and a
    /// format-2 one does not (01 §3.2).
    /// </summary>
    private static string? FeatureVersionDisagreement(ushort formatVersion, IReadOnlyList<ushort> required)
    {
        var listed = required.Contains(FeatureRelocatableRecords);
        var relocatable = FormatVersions.HasRelocatableRecords(formatVersion);
        if (listed == relocatable)
        {
            return null;
        }

        return relocatable
            ? $"A format-{formatVersion} descriptor must list feature 0x{FeatureRelocatableRecords:x4} "
              + "(relocatable-records) and this one does not (specification 01 §3.2)."
            : $"A format-{formatVersion} descriptor must not list feature 0x{FeatureRelocatableRecords:x4} "
              + "(relocatable-records) and this one does (specification 01 §3.2).";
    }

    private static List<ushort> ReadFeatureArray(CanonicalCborReader reader)
    {
        var count = reader.ReadStartArray(maxCount: 256);
        var features = new List<ushort>(count);

        for (var i = 0; i < count; i++)
        {
            features.Add(reader.ReadUInt16());
        }

        reader.ReadEndArray();
        return features;
    }

    private static (Argon2Parameters Kdf, byte[] Salt) ReadKdfParameters(CanonicalCborReader reader)
    {
        var count = reader.ReadStartMap();

        if (count != 5)
        {
            throw new CborFormatException(Strings.FormatRepositoryDescriptorCodec_KdfParametersCarriesKeysSpecification(count));
        }

        uint memory = 0;
        uint iterations = 0;
        byte parallelism = 0;
        byte[]? salt = null;

        for (var i = 0; i < count; i++)
        {
            switch (reader.ReadKey())
            {
                case 1:
                    var profile = reader.ReadUInt16();
                    if (profile != KdfProfileArgon2id)
                    {
                        throw new CborFormatException(Strings.FormatRepositoryDescriptorCodec_KdfProfileXNotArgon(profile));
                    }

                    break;
                case 2:
                    salt = reader.ReadFixedByteString(16);
                    break;
                case 3:
                    memory = reader.ReadUInt32();
                    break;
                case 4:
                    iterations = reader.ReadUInt32();
                    break;
                case 5:
                    parallelism = reader.ReadByte();
                    break;
                default:
                    throw new CborFormatException(Strings.RepositoryDescriptorCodec_KdfParametersCarriesUnknownKey);
            }
        }

        reader.ReadEndMap();

        if (salt is null)
        {
            throw new CborFormatException(Strings.RepositoryDescriptorCodec_KdfParametersOmitsSalt);
        }

        return (new Argon2Parameters { MemoryKiB = memory, Iterations = iterations, Parallelism = parallelism }, salt);
    }
}
