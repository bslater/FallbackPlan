using System.Buffers.Binary;
using System.Security.Cryptography;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Cbor;

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
        ArgumentNullException.ThrowIfNull(kit);
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
            throw new RecoveryKitFormatException(
                $"A kit is at least {HeaderLength + ChecksumLength} bytes; got {span.Length}.");
        }

        if (!span[..8].SequenceEqual(Magic))
        {
            throw new RecoveryKitFormatException("Not a FallbackPlan recovery kit: the FBPKRKIT magic is absent.");
        }

        var framedVersion = BinaryPrimitives.ReadUInt16BigEndian(span[8..]);
        if (framedVersion != 1)
        {
            throw new RecoveryKitFormatException(
                $"Kit format version {framedVersion} is not supported by this implementation (v1 only).");
        }

        var bodyLength = BinaryPrimitives.ReadUInt32BigEndian(span[12..]);
        if (bodyLength > MaxBodyLength)
        {
            throw new RecoveryKitFormatException(
                $"The kit declares a {bodyLength}-byte body; the bound is {MaxBodyLength} (specifications/recovery-kit §3).");
        }

        if (span.Length != HeaderLength + (int)bodyLength + ChecksumLength)
        {
            throw new RecoveryKitFormatException(
                $"The kit is {span.Length} bytes; the framing declares {HeaderLength + bodyLength + ChecksumLength}.");
        }

        // Checksum before the body is interpreted (§3) — a transcription or
        // storage error is reported as damage, never as a parse error.
        Span<byte> digest = stackalloc byte[ChecksumLength];
        SHA256.HashData(span[..(HeaderLength + (int)bodyLength)], digest);
        if (!digest.SequenceEqual(span[(HeaderLength + (int)bodyLength)..]))
        {
            throw new RecoveryKitFormatException(
                "The kit checksum does not verify — transcription or storage damage (specifications/recovery-kit §3).");
        }

        var kit = DecodeBody(data.Slice(HeaderLength, (int)bodyLength));

        if (kit.KitFormatVersion != framedVersion)
        {
            throw new RecoveryKitFormatException(
                $"The framed version ({framedVersion}) and body key 1 ({kit.KitFormatVersion}) disagree.");
        }

        return kit;
    }

    private static void Validate(RecoveryKit kit)
    {
        if (kit.KdfSalt.Length != 16 || kit.IssuingDeviceId.Length != 16)
        {
            throw new RecoveryKitFormatException("kdf salt and issuing device id are 16 bytes each (specifications/recovery-kit §2).");
        }

        if (!kit.KeyObject.Span.StartsWith(Keys.KeyObjectFraming.Magic))
        {
            throw new RecoveryKitFormatException(
                "Key 5 must be a verbatim FBPKKEYS key object (specifications/recovery-kit §2).");
        }
    }

    private static byte[] EncodeBody(RecoveryKit kit)
    {
        var writer = new CanonicalCborWriter();
        writer.WriteStartMap(10);
        writer.WriteKey(1);
        writer.WriteUnsignedInteger(kit.KitFormatVersion);
        writer.WriteKey(2);
        writer.WriteTextString(kit.MinimumToolVersion);
        writer.WriteKey(3);
        writer.WriteByteString(kit.RepositoryId.ToArray());
        writer.WriteKey(4);
        writer.WriteUnsignedInteger(kit.RepositoryFormatVersion);
        writer.WriteKey(5);
        writer.WriteByteString(kit.KeyObject.Span);
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
        writer.WriteKey(7);
        writer.WriteStartArray(kit.Destinations.Count);
        foreach (var destination in kit.Destinations)
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
        writer.WriteKey(8);
        writer.WriteByteString(kit.IssuingDeviceId.Span);
        writer.WriteKey(9);
        writer.WriteUnsignedInteger(kit.IssuedAt);
        writer.WriteKey(10);
        writer.WriteTextString(kit.Instructions);
        writer.WriteEndMap();
        return writer.Encode();
    }

    private static RecoveryKit DecodeBody(ReadOnlyMemory<byte> body)
    {
        var reader = new CanonicalCborReader(body);
        var count = reader.ReadStartMap();
        if (count != 10)
        {
            throw new RecoveryKitFormatException(
                $"A v1 kit body carries exactly 10 keys; got {count} (specifications/recovery-kit §2).");
        }

        ushort? version = null, formatVersion = null;
        string? minimumTool = null, instructions = null;
        byte[]? repositoryId = null, keyObject = null, salt = null, deviceId = null;
        uint? memory = null, iterations = null;
        byte? parallelism = null;
        ulong? issuedAt = null;
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
                        throw new RecoveryKitFormatException("kdf_parameters carries exactly 4 keys (specifications/recovery-kit §2).");
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
                                throw new RecoveryKitFormatException("kdf_parameters carries an unknown key (specifications/recovery-kit §2).");
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
                            throw new RecoveryKitFormatException("A destination carries exactly 4 keys (specifications/recovery-kit §2).");
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
                                    throw new RecoveryKitFormatException("A destination carries an unknown key (specifications/recovery-kit §2).");
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
                default:
                    throw new RecoveryKitFormatException(
                        "The kit body carries an unknown key; a v1 kit assigns keys 1-10 only (specifications/recovery-kit §2).");
            }
        }

        reader.ReadEndMap();
        reader.AssertEndOfDocument();

        if (version is null || minimumTool is null || repositoryId is null || formatVersion is null
            || keyObject is null || memory is null || iterations is null || parallelism is null || salt is null
            || deviceId is null || issuedAt is null || instructions is null)
        {
            throw new RecoveryKitFormatException("The kit body omits a mandatory key (specifications/recovery-kit §2).");
        }

        if (repositoryId.Length != 16 || salt.Length != 16 || deviceId.Length != 16)
        {
            throw new RecoveryKitFormatException("repository_id, kdf salt, and issuing device id are 16 bytes each.");
        }

        if (!keyObject.AsSpan().StartsWith(Keys.KeyObjectFraming.Magic))
        {
            throw new RecoveryKitFormatException(
                "Key 5 must be a verbatim FBPKKEYS key object (specifications/recovery-kit §2).");
        }

        return new RecoveryKit
        {
            KitFormatVersion = version.Value,
            MinimumToolVersion = minimumTool,
            RepositoryId = RepositoryId.FromBytes(repositoryId),
            RepositoryFormatVersion = formatVersion.Value,
            KeyObject = keyObject,
            KdfMemoryKiB = memory.Value,
            KdfIterations = iterations.Value,
            KdfParallelism = parallelism.Value,
            KdfSalt = salt,
            Destinations = destinations,
            IssuingDeviceId = deviceId,
            IssuedAt = issuedAt.Value,
            Instructions = instructions,
        };
    }
}
