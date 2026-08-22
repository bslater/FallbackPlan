using Bodu;
using System.Buffers.Binary;
using System.Security.Cryptography;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Cbor;
using FallbackPlan.Repository.Format.Resources;

namespace FallbackPlan.Repository.Format.RecoveryKit;

/// <summary>A kit violates the format — checksum, framing, or field rules.</summary>
public sealed class RecoveryKitFormatException : FormatException
{
    public RecoveryKitFormatException(string message)
        : base(message)
    {
    }

    public RecoveryKitFormatException()
    {
    }

    public RecoveryKitFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The machine form (specifications/recovery-kit §2–§3): FBPKRKIT framing
/// around a deterministic-CBOR body, SHA-256 checksum verified before the
/// body is parsed, 64 KiB body bound checked before allocation.
/// </summary>
public static class RecoveryKitCodec
{
    /// <summary>The framing magic, <c>"FBPKRKIT"</c>.</summary>
    public static ReadOnlySpan<byte> Magic => "FBPKRKIT"u8;

    /// <summary>Fixed header length: magic, version, reserved, body length.</summary>
    public const int HeaderLength = 16;

    /// <summary>The checksum length.</summary>
    public const int ChecksumLength = 32;

    /// <summary>The §3 body bound.</summary>
    public const int MaxBodyLength = 64 * 1024;

    /// <summary>Serializes a kit into its framed machine form.</summary>
    public static byte[] Serialize(RecoveryKit kit)
    {
        ThrowHelper.ThrowIfNull(kit);
        Validate(kit);

        var body = EncodeBody(kit);
        var framed = new byte[HeaderLength + body.Length + ChecksumLength];
        var span = framed.AsSpan();

        Magic.CopyTo(span);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], kit.KitFormatVersion);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(span[12..], (uint)body.Length);
        body.CopyTo(span[HeaderLength..]);
        SHA256.HashData(span[..(HeaderLength + body.Length)], span[(HeaderLength + body.Length)..]);

        return framed;
    }

    /// <summary>
    /// Parses a framed kit: magic, version cross-check, body bound before
    /// allocation, checksum before body interpretation, then every field
    /// rule of §2.
    /// </summary>
    /// <exception cref="RecoveryKitFormatException">The bytes violate the kit format.</exception>
    public static RecoveryKit Parse(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;
        if (span.Length < HeaderLength + ChecksumLength)
        {
            throw new RecoveryKitFormatException(Strings.FormatRecoveryKitCodec_KitLeastBytesGot(HeaderLength + ChecksumLength, span.Length));
        }

        if (!span[..8].SequenceEqual(Magic))
        {
            throw new RecoveryKitFormatException(Strings.RecoveryKitCodec_NotFallbackPlanRecoveryKitFBPKRKIT);
        }

        var framedVersion = BinaryPrimitives.ReadUInt16BigEndian(span[8..]);
        if (framedVersion is not (1 or RecoveryKit.InstallationKitVersion))
        {
            throw new RecoveryKitFormatException(Strings.FormatRecoveryKitCodec_KitFormatVersionNotSupported(framedVersion));
        }

        var bodyLength = BinaryPrimitives.ReadUInt32BigEndian(span[12..]);
        if (bodyLength > MaxBodyLength)
        {
            throw new RecoveryKitFormatException(Strings.FormatRecoveryKitCodec_KitDeclaresByteBodyBound(bodyLength, MaxBodyLength));
        }

        if (span.Length != HeaderLength + (int)bodyLength + ChecksumLength)
        {
            throw new RecoveryKitFormatException(Strings.FormatRecoveryKitCodec_KitBytesFramingDeclares(span.Length, HeaderLength + bodyLength + ChecksumLength));
        }

        // Checksum before the body is interpreted (§3) — a transcription or
        // storage error is reported as damage, never as a parse error.
        Span<byte> digest = stackalloc byte[ChecksumLength];
        SHA256.HashData(span[..(HeaderLength + (int)bodyLength)], digest);
        if (!digest.SequenceEqual(span[(HeaderLength + (int)bodyLength)..]))
        {
            throw new RecoveryKitFormatException(Strings.RecoveryKitCodec_KitChecksumDoesNotVerify);
        }

        var kit = DecodeBody(data.Slice(HeaderLength, (int)bodyLength));

        if (kit.KitFormatVersion != framedVersion)
        {
            throw new RecoveryKitFormatException(Strings.FormatRecoveryKitCodec_FramedVersionBodyKeyDisagree(framedVersion, kit.KitFormatVersion));
        }

        return kit;
    }

    private static void Validate(RecoveryKit kit)
    {
        if (kit.KdfSalt.Length != 16 || kit.IssuingDeviceId.Length != 16)
        {
            throw new RecoveryKitFormatException(Strings.RecoveryKitCodec_KdfSaltIssuingDeviceId);
        }

        // An installation kit (§2.2) names no repository, carries no key
        // object and lists no destination. Each absence is checked rather
        // than assumed: a v2 kit that claims a repository id is either a
        // forgery or a v1 kit with its version stamped over, and both should
        // fail here rather than open one archive and silently fail the rest.
        if (kit.IsInstallationKit)
        {
            if (kit.RepositoryId is not null || !kit.KeyObject.IsEmpty || kit.Destinations.Count > 0)
            {
                throw new RecoveryKitFormatException(Strings.RecoveryKitCodec_InstallationKitShape);
            }

            if (kit.SealingPublicKey.Length != 32 || kit.RepositoryFormatVersion < 2)
            {
                throw new RecoveryKitFormatException(Strings.RecoveryKitCodec_InstallationKitShape);
            }

            return;
        }

        if (kit.RepositoryId is null)
        {
            throw new RecoveryKitFormatException(Strings.RecoveryKitCodec_RepositoryKitOmitsRepositoryId);
        }

        // A write-only v1 kit carries the sealing public key and NO key
        // object — every real key re-derives from the passphrase
        // (ADR-0042 §8); an ordinary v1 kit carries the verbatim key object
        // and no public key. Anything else is a contradiction refused before
        // it is stored or trusted.
        if (kit.RepositoryFormatVersion >= 2)
        {
            if (!kit.KeyObject.IsEmpty || kit.SealingPublicKey.Length != 32)
            {
                throw new RecoveryKitFormatException(Strings.RecoveryKitCodec_WriteOnlyKitShape);
            }
        }
        else if (!kit.SealingPublicKey.IsEmpty || !kit.KeyObject.Span.StartsWith(Keys.KeyObjectFraming.Magic))
        {
            throw new RecoveryKitFormatException(Strings.RecoveryKitCodec_KeyMustVerbatimFBPKKEYSKey);
        }
    }

    private static byte[] EncodeBody(RecoveryKit kit)
    {
        var writer = new CanonicalCborWriter();

        // The key numbering keeps its holes on an installation kit — 3, 5
        // and 7 are skipped rather than the rest renumbered — so a reader
        // that confuses the two versions fails on a missing mandatory key
        // instead of reading one field as another (§2.2).
        writer.WriteStartMap(kit.IsInstallationKit ? 8 : kit.SealingPublicKey.IsEmpty ? 10 : 11);
        writer.WriteKey(1);
        writer.WriteUnsignedInteger(kit.KitFormatVersion);
        writer.WriteKey(2);
        writer.WriteTextString(kit.MinimumToolVersion);
        if (!kit.IsInstallationKit)
        {
            writer.WriteKey(3);
            writer.WriteByteString(kit.RepositoryId!.Value.ToArray());
        }

        writer.WriteKey(4);
        writer.WriteUnsignedInteger(kit.RepositoryFormatVersion);
        if (!kit.IsInstallationKit)
        {
            writer.WriteKey(5);
            writer.WriteByteString(kit.KeyObject.Span);
        }

        writer.WriteKey(6);
        writer.WriteStartMap(4);
        writer.WriteKey(1);
        writer.WriteUnsignedInteger(kit.KdfMemoryKiB);
        writer.WriteKey(2);
        writer.WriteUnsignedInteger(kit.KdfIterations);
        writer.WriteKey(3);
        writer.WriteUnsignedInteger(kit.KdfParallelism);
        writer.WriteKey(4);
        writer.WriteByteString(kit.KdfSalt.Span);
        writer.WriteEndMap();
        if (!kit.IsInstallationKit)
        {
            WriteDestinations(writer, kit.Destinations);
        }

        writer.WriteKey(8);
        writer.WriteByteString(kit.IssuingDeviceId.Span);
        writer.WriteKey(9);
        writer.WriteUnsignedInteger(kit.IssuedAt);
        writer.WriteKey(10);
        writer.WriteTextString(kit.Instructions);
        if (!kit.SealingPublicKey.IsEmpty)
        {
            writer.WriteKey(11);
            writer.WriteByteString(kit.SealingPublicKey.Span);
        }

        writer.WriteEndMap();
        return writer.Encode();
    }

    private static void WriteDestinations(CanonicalCborWriter writer, IReadOnlyList<KitDestination> destinations)
    {
        writer.WriteKey(7);
        writer.WriteStartArray(destinations.Count);
        foreach (var destination in destinations)
        {
            writer.WriteStartMap(4);
            writer.WriteKey(1);
            writer.WriteTextString(destination.Kind);
            writer.WriteKey(2);
            writer.WriteTextString(destination.Endpoint);
            writer.WriteKey(3);
            writer.WriteTextString(destination.Container);
            writer.WriteKey(4);
            writer.WriteTextString(destination.Prefix);
            writer.WriteEndMap();
        }

        writer.WriteEndArray();
    }

    private static RecoveryKit DecodeBody(ReadOnlyMemory<byte> body)
    {
        var reader = new CanonicalCborReader(body);
        var count = reader.ReadStartMap();

        // Eight keys is an installation kit, ten or eleven a repository kit
        // (§2.2). The count decides which mandatory set applies, and the
        // version stamped in key 1 is cross-checked against it below.
        if (count is not (8 or 10 or 11))
        {
            throw new RecoveryKitFormatException(Strings.FormatRecoveryKitCodec_VKitBodyCarriesExactly(count));
        }

        ushort? version = null, formatVersion = null;
        string? minimumTool = null, instructions = null;
        byte[]? repositoryId = null, keyObject = null, salt = null, deviceId = null;
        uint? memory = null, iterations = null;
        byte? parallelism = null;
        ulong? issuedAt = null;
        byte[]? sealingPublicKey = null;
        var destinations = new List<KitDestination>();

        for (var i = 0; i < count; i++)
        {
            switch (reader.ReadKey())
            {
                case 1:
                    version = (ushort)reader.ReadUnsignedInteger();
                    break;
                case 2:
                    minimumTool = reader.ReadTextString(maxUtf8Length: 64);
                    break;
                case 3:
                    repositoryId = reader.ReadByteString(maxLength: 16);
                    break;
                case 4:
                    formatVersion = (ushort)reader.ReadUnsignedInteger();
                    break;
                case 5:
                    keyObject = reader.ReadByteString(maxLength: MaxBodyLength);
                    break;
                case 6:
                    var kdfCount = reader.ReadStartMap();
                    if (kdfCount != 4)
                    {
                        throw new RecoveryKitFormatException(Strings.RecoveryKitCodec_KdfParametersCarriesExactlyKeys);
                    }

                    for (var j = 0; j < kdfCount; j++)
                    {
                        switch (reader.ReadKey())
                        {
                            case 1:
                                memory = (uint)reader.ReadUnsignedInteger();
                                break;
                            case 2:
                                iterations = (uint)reader.ReadUnsignedInteger();
                                break;
                            case 3:
                                parallelism = (byte)reader.ReadUnsignedInteger();
                                break;
                            case 4:
                                salt = reader.ReadByteString(maxLength: 16);
                                break;
                            default:
                                throw new RecoveryKitFormatException(Strings.RecoveryKitCodec_KdfParametersCarriesUnknownKey);
                        }
                    }

                    reader.ReadEndMap();
                    break;
                case 7:
                    var destinationCount = reader.ReadStartArray(maxCount: 64);
                    for (var j = 0; j < destinationCount; j++)
                    {
                        var fieldCount = reader.ReadStartMap();
                        if (fieldCount != 4)
                        {
                            throw new RecoveryKitFormatException(Strings.RecoveryKitCodec_DestinationCarriesExactlyKeys);
                        }

                        string? kind = null, endpoint = null, container = null, prefix = null;
                        for (var k = 0; k < fieldCount; k++)
                        {
                            switch (reader.ReadKey())
                            {
                                case 1:
                                    kind = reader.ReadTextString(maxUtf8Length: 64);
                                    break;
                                case 2:
                                    endpoint = reader.ReadTextString(maxUtf8Length: 1024);
                                    break;
                                case 3:
                                    container = reader.ReadTextString(maxUtf8Length: 256);
                                    break;
                                case 4:
                                    prefix = reader.ReadTextString(maxUtf8Length: 1024);
                                    break;
                                default:
                                    throw new RecoveryKitFormatException(Strings.RecoveryKitCodec_DestinationCarriesUnknownKey);
                            }
                        }

                        reader.ReadEndMap();
                        destinations.Add(new KitDestination(kind ?? "", endpoint ?? "", container ?? "", prefix ?? ""));
                    }

                    reader.ReadEndArray();
                    break;
                case 8:
                    deviceId = reader.ReadByteString(maxLength: 16);
                    break;
                case 9:
                    issuedAt = reader.ReadUnsignedInteger();
                    break;
                case 10:
                    instructions = reader.ReadTextString(maxUtf8Length: 16 * 1024);
                    break;
                case 11:
                    sealingPublicKey = reader.ReadFixedByteString(32);
                    break;
                default:
                    throw new RecoveryKitFormatException(Strings.RecoveryKitCodec_KitBodyCarriesUnknownKey);
            }
        }

        reader.ReadEndMap();
        reader.AssertEndOfDocument();

        var installation = count == 8;

        if (version is null || minimumTool is null || formatVersion is null || memory is null
            || iterations is null || parallelism is null || salt is null || deviceId is null
            || issuedAt is null || instructions is null)
        {
            throw new RecoveryKitFormatException(Strings.RecoveryKitCodec_KitBodyOmitsMandatoryKey);
        }

        if (installation)
        {
            // Checked here as well as in Validate, because a parser must
            // refuse a hostile body before anything downstream trusts its
            // shape — and the absences are the whole of what makes an
            // installation kit one (§2.2).
            if (version.Value != RecoveryKit.InstallationKitVersion
                || repositoryId is not null || keyObject is not null
                || destinations.Count > 0 || sealingPublicKey is null)
            {
                throw new RecoveryKitFormatException(Strings.RecoveryKitCodec_InstallationKitShape);
            }
        }
        else if (repositoryId is null || keyObject is null)
        {
            throw new RecoveryKitFormatException(Strings.RecoveryKitCodec_KitBodyOmitsMandatoryKey);
        }

        if (salt.Length != 16 || deviceId.Length != 16 || (repositoryId is not null && repositoryId.Length != 16))
        {
            throw new RecoveryKitFormatException(Strings.RecoveryKitCodec_RepositoryIdKdfSaltIssuing);
        }

        var kit = new RecoveryKit
        {
            KitFormatVersion = version.Value,
            MinimumToolVersion = minimumTool,
            RepositoryId = repositoryId is null ? null : RepositoryId.FromBytes(repositoryId),
            RepositoryFormatVersion = formatVersion.Value,
            KeyObject = keyObject ?? ReadOnlyMemory<byte>.Empty,
            KdfMemoryKiB = memory.Value,
            KdfIterations = iterations.Value,
            KdfParallelism = parallelism.Value,
            KdfSalt = salt,
            Destinations = destinations,
            IssuingDeviceId = deviceId,
            IssuedAt = issuedAt.Value,
            Instructions = instructions,
            SealingPublicKey = sealingPublicKey ?? ReadOnlyMemory<byte>.Empty,
        };

        // The same shape rules the serialiser enforces (a v1 kit's verbatim
        // FBPKKEYS object, a v2 kit's public key and nothing wrapped) apply
        // to parsed bytes — one rule set, both directions.
        Validate(kit);
        return kit;
    }
}
