using Bodu;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Format.Cbor;

namespace FallbackPlan.Repository.Format.Manifests;

/// <summary>
/// The effective configuration a snapshot was captured under
/// (specification 06 §7, object type <c>0x05</c>): what lets a snapshot
/// answer "what settings produced this?" years after the configuration file
/// is gone. Inner map shapes per ADR-0022 §Decision 6.
/// </summary>
public sealed record PolicyManifest
{
    /// <summary>The segmentation profile (key 1).</summary>
    public required ushort SegmentationProfile { get; init; }

    /// <summary>Segmentation parameters (key 2): segment/target size, and min/max/window for cdc-v1.</summary>
    public required ulong SegmentSizeOrTarget { get; init; }

    /// <summary>cdc-v1 minimum size; null under fixed-v1.</summary>
    public ulong? CdcMinSize { get; init; }

    /// <summary>cdc-v1 maximum size; null under fixed-v1.</summary>
    public ulong? CdcMaxSize { get; init; }

    /// <summary>cdc-v1 window size; null under fixed-v1.</summary>
    public byte? CdcWindowSize { get; init; }

    /// <summary>The compression profile (key 3).</summary>
    public required ushort CompressionProfile { get; init; }

    /// <summary>The storage threshold in permille (key 4).</summary>
    public required ushort CompressionThresholdPermille { get; init; }

    /// <summary>The encryption profile (key 5).</summary>
    public required ushort EncryptionProfile { get; init; }

    /// <summary>Blob write profile (key 6): target size.</summary>
    public required ulong BlobTargetSize { get; init; }

    /// <summary>Blob write profile: maximum size.</summary>
    public required ulong BlobMaxSize { get; init; }

    /// <summary>Blob write profile: maximum record count.</summary>
    public required uint BlobMaxRecordCount { get; init; }

    /// <summary>The dedup trust domain (key 7): 1 device, 2 repository, 3 repository-unverified.</summary>
    public required byte DedupTrustDomain { get; init; }

    /// <summary>Include rules (key 8).</summary>
    public IReadOnlyList<string> IncludeRules { get; init; } = [];

    /// <summary>Exclude rules (key 9) — an excluded path is never a capture failure (06 §8).</summary>
    public IReadOnlyList<string> ExcludeRules { get; init; } = [];
}

/// <summary>Encodes and decodes policy manifests (specification 06 §7; ADR-0022 §Decision 6).</summary>
public static class PolicyManifestCodec
{
    /// <summary>Encodes a policy manifest canonically.</summary>
    public static byte[] Encode(PolicyManifest manifest)
    {
        ThrowHelper.ThrowIfNull(manifest);

        var writer = new CanonicalCborWriter();
        writer.WriteStartMap(9);
        writer.WriteKey(1);
        writer.WriteUnsignedInteger(manifest.SegmentationProfile);
        writer.WriteKey(2);
        var parameterCount = 1
            + (manifest.CdcMinSize is not null ? 1 : 0)
            + (manifest.CdcMaxSize is not null ? 1 : 0)
            + (manifest.CdcWindowSize is not null ? 1 : 0);
        writer.WriteStartMap(parameterCount);
        writer.WriteKey(1);
        writer.WriteUnsignedInteger(manifest.SegmentSizeOrTarget);
        if (manifest.CdcMinSize is { } minSize)
        {
            writer.WriteKey(2);
            writer.WriteUnsignedInteger(minSize);
        }

        if (manifest.CdcMaxSize is { } maxSize)
        {
            writer.WriteKey(3);
            writer.WriteUnsignedInteger(maxSize);
        }

        if (manifest.CdcWindowSize is { } window)
        {
            writer.WriteKey(4);
            writer.WriteUnsignedInteger(window);
        }

        writer.WriteEndMap();
        writer.WriteKey(3);
        writer.WriteUnsignedInteger(manifest.CompressionProfile);
        writer.WriteKey(4);
        writer.WriteUnsignedInteger(manifest.CompressionThresholdPermille);
        writer.WriteKey(5);
        writer.WriteUnsignedInteger(manifest.EncryptionProfile);
        writer.WriteKey(6);
        writer.WriteStartMap(3);
        writer.WriteKey(1);
        writer.WriteUnsignedInteger(manifest.BlobTargetSize);
        writer.WriteKey(2);
        writer.WriteUnsignedInteger(manifest.BlobMaxSize);
        writer.WriteKey(3);
        writer.WriteUnsignedInteger(manifest.BlobMaxRecordCount);
        writer.WriteEndMap();
        writer.WriteKey(7);
        writer.WriteUnsignedInteger(manifest.DedupTrustDomain);
        writer.WriteKey(8);
        writer.WriteStartArray(manifest.IncludeRules.Count);
        foreach (var rule in manifest.IncludeRules)
        {
            writer.WriteTextString(rule);
        }

        writer.WriteEndArray();
        writer.WriteKey(9);
        writer.WriteStartArray(manifest.ExcludeRules.Count);
        foreach (var rule in manifest.ExcludeRules)
        {
            writer.WriteTextString(rule);
        }

        writer.WriteEndArray();
        writer.WriteEndMap();

        return writer.Encode();
    }

    /// <summary>Decodes and validates a policy manifest.</summary>
    /// <exception cref="ManifestValidationException">The bytes violate specification 06 §7.</exception>
    public static PolicyManifest Decode(ReadOnlyMemory<byte> data)
    {
        try
        {
            return DecodeCore(data);
        }
        catch (CborFormatException exception)
        {
            throw new ManifestValidationException($"The policy manifest is not canonical CBOR: {exception.Message}", exception);
        }
    }

    private static PolicyManifest DecodeCore(ReadOnlyMemory<byte> data)
    {
        var reader = new CanonicalCborReader(data);
        var count = reader.ReadStartMap();

        ushort? segmentationProfile = null, compressionProfile = null, thresholdPermille = null, encryptionProfile = null;
        ulong? segmentSize = null, blobTarget = null, blobMax = null;
        ulong? cdcMin = null, cdcMax = null;
        byte? cdcWindow = null, trustDomain = null;
        uint? blobRecords = null;
        List<string> include = [], exclude = [];

        for (var i = 0; i < count; i++)
        {
            switch (reader.ReadKey())
            {
                case 1:
                    segmentationProfile = reader.ReadUInt16();
                    break;
                case 2:
                    var parameterCount = reader.ReadStartMap();
                    for (var j = 0; j < parameterCount; j++)
                    {
                        switch (reader.ReadKey())
                        {
                            case 1:
                                segmentSize = reader.ReadUnsignedInteger();
                                break;
                            case 2:
                                cdcMin = reader.ReadUnsignedInteger();
                                break;
                            case 3:
                                cdcMax = reader.ReadUnsignedInteger();
                                break;
                            case 4:
                                cdcWindow = reader.ReadByte();
                                break;
                            default:
                                throw new ManifestValidationException(
                                    "segmentation_parameters carries an unknown key (ADR-0022 §Decision 6).");
                        }
                    }

                    reader.ReadEndMap();
                    break;
                case 3:
                    compressionProfile = reader.ReadUInt16();
                    break;
                case 4:
                    thresholdPermille = reader.ReadUInt16();
                    break;
                case 5:
                    encryptionProfile = reader.ReadUInt16();
                    break;
                case 6:
                    var profileCount = reader.ReadStartMap();
                    for (var j = 0; j < profileCount; j++)
                    {
                        switch (reader.ReadKey())
                        {
                            case 1:
                                blobTarget = reader.ReadUnsignedInteger();
                                break;
                            case 2:
                                blobMax = reader.ReadUnsignedInteger();
                                break;
                            case 3:
                                blobRecords = reader.ReadUInt32();
                                break;
                            default:
                                throw new ManifestValidationException(
                                    "blob_write_profile carries an unknown key (ADR-0022 §Decision 6).");
                        }
                    }

                    reader.ReadEndMap();
                    break;
                case 7:
                    trustDomain = reader.ReadByte();
                    break;
                case 8:
                    include = ReadRules(reader);
                    break;
                case 9:
                    exclude = ReadRules(reader);
                    break;
                default:
                    throw new ManifestValidationException(
                        "The policy manifest carries an unknown key; specification 06 §7 assigns keys 1-9 only.");
            }
        }

        reader.ReadEndMap();
        reader.AssertEndOfDocument();

        if (segmentationProfile is null || segmentSize is null || compressionProfile is null ||
            thresholdPermille is null || encryptionProfile is null || blobTarget is null ||
            blobMax is null || blobRecords is null || trustDomain is null)
        {
            throw new ManifestValidationException("The policy manifest omits a mandatory key (specification 06 §7).");
        }

        return new PolicyManifest
        {
            SegmentationProfile = segmentationProfile.Value,
            SegmentSizeOrTarget = segmentSize.Value,
            CdcMinSize = cdcMin,
            CdcMaxSize = cdcMax,
            CdcWindowSize = cdcWindow,
            CompressionProfile = compressionProfile.Value,
            CompressionThresholdPermille = thresholdPermille.Value,
            EncryptionProfile = encryptionProfile.Value,
            BlobTargetSize = blobTarget.Value,
            BlobMaxSize = blobMax.Value,
            BlobMaxRecordCount = blobRecords.Value,
            DedupTrustDomain = trustDomain.Value,
            IncludeRules = include,
            ExcludeRules = exclude,
        };
    }

    private static List<string> ReadRules(CanonicalCborReader reader)
    {
        var count = reader.ReadStartArray(maxCount: 65_536);
        var rules = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            rules.Add(reader.ReadTextString(maxUtf8Length: 4096));
        }

        reader.ReadEndArray();
        return rules;
    }
}

/// <summary>One capture failure (specification 06 §8.1).</summary>
public sealed record CaptureFailure(
    IReadOnlyList<ReadOnlyMemory<byte>> PathComponents,
    CaptureFailureReason Reason,
    string Detail);

/// <summary>
/// The error manifest (specification 06 §8, object type <c>0x06</c>): what
/// could not be captured and why. A path excluded by <em>policy</em> is not
/// a failure and MUST NOT appear here.
/// </summary>
public sealed record ErrorManifest(IReadOnlyList<CaptureFailure> Failures);

/// <summary>Encodes and decodes error manifests (specification 06 §8).</summary>
public static class ErrorManifestCodec
{
    /// <summary>Encodes an error manifest canonically.</summary>
    public static byte[] Encode(ErrorManifest manifest)
    {
        ThrowHelper.ThrowIfNull(manifest);

        var writer = new CanonicalCborWriter();
        writer.WriteStartMap(1);
        writer.WriteKey(1);
        writer.WriteStartArray(manifest.Failures.Count);

        foreach (var failure in manifest.Failures)
        {
            writer.WriteStartMap(3);
            writer.WriteKey(1);
            writer.WriteStartArray(failure.PathComponents.Count);
            foreach (var component in failure.PathComponents)
            {
                writer.WriteByteString(component.Span);
            }

            writer.WriteEndArray();
            writer.WriteKey(2);
            writer.WriteUnsignedInteger((ushort)failure.Reason);
            writer.WriteKey(3);
            writer.WriteTextString(failure.Detail);
            writer.WriteEndMap();
        }

        writer.WriteEndArray();
        writer.WriteEndMap();

        return writer.Encode();
    }

    /// <summary>Decodes and validates an error manifest.</summary>
    /// <exception cref="ManifestValidationException">The bytes violate specification 06 §8.</exception>
    public static ErrorManifest Decode(ReadOnlyMemory<byte> data)
    {
        try
        {
            return DecodeCore(data);
        }
        catch (CborFormatException exception)
        {
            throw new ManifestValidationException($"The error manifest is not canonical CBOR: {exception.Message}", exception);
        }
    }

    private static ErrorManifest DecodeCore(ReadOnlyMemory<byte> data)
    {
        var reader = new CanonicalCborReader(data);
        var mapCount = reader.ReadStartMap();

        if (mapCount != 1 || reader.ReadKey() != 1)
        {
            throw new ManifestValidationException("The error manifest carries exactly key 1 (specification 06 §8).");
        }

        var failureCount = reader.ReadStartArray(maxCount: 1_000_000);
        var failures = new List<CaptureFailure>(failureCount);

        for (var i = 0; i < failureCount; i++)
        {
            var entryCount = reader.ReadStartMap();
            List<ReadOnlyMemory<byte>>? components = null;
            CaptureFailureReason? reason = null;
            string? detail = null;

            for (var j = 0; j < entryCount; j++)
            {
                switch (reader.ReadKey())
                {
                    case 1:
                        var componentCount = reader.ReadStartArray(maxCount: 512);
                        components = new List<ReadOnlyMemory<byte>>(componentCount);
                        for (var k = 0; k < componentCount; k++)
                        {
                            components.Add(reader.ReadByteString(maxLength: 1024));
                        }

                        reader.ReadEndArray();
                        break;
                    case 2:
                        var value = reader.ReadUInt16();
                        if (value is < 1 or > 7)
                        {
                            throw new ManifestValidationException($"Failure reason {value} is unassigned (specification 06 §8.1).");
                        }

                        reason = (CaptureFailureReason)value;
                        break;
                    case 3:
                        detail = reader.ReadTextString(maxUtf8Length: 4096);
                        break;
                    default:
                        throw new ManifestValidationException(
                            "A failure entry carries an unknown key; specification 06 §8.1 assigns keys 1-3 only.");
                }
            }

            reader.ReadEndMap();

            if (components is null || reason is null || detail is null)
            {
                throw new ManifestValidationException("A failure entry omits a mandatory key (specification 06 §8.1).");
            }

            failures.Add(new CaptureFailure(components, reason.Value, detail));
        }

        reader.ReadEndArray();
        reader.ReadEndMap();
        reader.AssertEndOfDocument();

        return new ErrorManifest(failures);
    }
}
