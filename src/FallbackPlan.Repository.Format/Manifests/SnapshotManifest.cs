using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Cbor;

namespace FallbackPlan.Repository.Format.Manifests;

/// <summary>The source filesystem's observed capabilities (specification 06 §6 key 12; shape per ADR-0022 §Decision 6).</summary>
public sealed record SourceFilesystem(bool CaseSensitive, bool SupportsSparse, string Name);

/// <summary>
/// A snapshot (specification 06 §6, object type <c>0x04</c>): the signed
/// root of the metadata graph, stored both as a metadata record and as a
/// standalone object under <c>/snapshots/…</c>.
/// </summary>
public sealed record SnapshotManifest
{
    /// <summary>The snapshot identity (key 1), 16 bytes.</summary>
    public required ReadOnlyMemory<byte> SnapshotId { get; init; }

    /// <summary>The claiming device (key 2), 16 bytes — attribution by claim (ADR-0020).</summary>
    public required ReadOnlyMemory<byte> DeviceId { get; init; }

    /// <summary>The backup set (key 3), 16 bytes.</summary>
    public required ReadOnlyMemory<byte> BackupSetId { get; init; }

    /// <summary>Capture start, epoch milliseconds (key 4).</summary>
    public required ulong CaptureStartedAt { get; init; }

    /// <summary>Capture completion (key 5).</summary>
    public required ulong CaptureCompletedAt { get; init; }

    /// <summary>The root tree manifest's object identifier (key 6).</summary>
    public required ObjectId RootTree { get; init; }

    /// <summary>Parent snapshot identities (key 7), 16 bytes each.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> ParentSnapshots { get; init; } = [];

    /// <summary>The policy manifest's object identifier (key 8).</summary>
    public required ObjectId PolicyManifest { get; init; }

    /// <summary>The error manifest's object identifier (key 9); null when nothing failed.</summary>
    public ObjectId? ErrorManifest { get; init; }

    /// <summary>The consistency method (key 10): 1 live, 2 VSS, 3 filesystem snapshot, 4 application-quiesced.</summary>
    public required byte ConsistencyMethod { get; init; }

    /// <summary>The capture status (key 11): 1 complete, 2 partial, 3 aborted.</summary>
    public required byte CaptureStatus { get; init; }

    /// <summary>The observed source filesystem (key 12).</summary>
    public required SourceFilesystem SourceFilesystem { get; init; }

    /// <summary>The publication generation (key 13) — selects the signing key.</summary>
    public required ulong PublicationGeneration { get; init; }

    /// <summary>Observed clock skew in milliseconds (key 14) — the format's only signed integer; null when no reference was available.</summary>
    public long? ObservedClockSkewMs { get; init; }

    /// <summary>The writing client's version string (key 15).</summary>
    public required string ClientVersion { get; init; }

    /// <summary>User tags (key 16).</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>A decoded snapshot: the manifest, the exact bytes its signature covers, and the signature.</summary>
/// <remarks>
/// Verification is the caller's step — Format produces the bytes-to-verify,
/// the crypto layer verifies against the derived signing key for
/// <see cref="SnapshotManifest.PublicationGeneration"/>, and a failure is a
/// <b>security finding</b>, never corruption (specification 06 §6.1).
/// </remarks>
public sealed record DecodedSnapshot(
    SnapshotManifest Manifest,
    ReadOnlyMemory<byte> SignedBytes,
    ReadOnlyMemory<byte> Signature);

/// <summary>
/// Encodes and decodes snapshot manifests with the 06 §6.1 two-pass
/// signature construction: the signature covers the deterministic encoding
/// of the map containing keys 1–16, and the stored object re-encodes the
/// map <b>with key 17 added</b> — the map header's count changes, so a
/// verifier can never strip a suffix; it must re-encode the 1–16 prefix,
/// which <see cref="Decode"/> does and returns as
/// <see cref="DecodedSnapshot.SignedBytes"/>.
/// </summary>
public static class SnapshotManifestCodec
{
    /// <summary>Encodes the map of keys 1–16 — the exact bytes the signature covers (06 §6.1).</summary>
    public static byte[] EncodeForSigning(SnapshotManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Validate(manifest);

        var writer = new CanonicalCborWriter();
        WriteBody(writer, manifest, signature: null);
        return writer.Encode();
    }

    /// <summary>Encodes the stored form: keys 1–16 plus the 64-byte signature at key 17.</summary>
    public static byte[] Encode(SnapshotManifest manifest, ReadOnlySpan<byte> signature)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Validate(manifest);

        if (signature.Length != 64)
        {
            throw new ManifestValidationException("A snapshot signature is exactly 64 bytes (specification 06 §6).");
        }

        var writer = new CanonicalCborWriter();
        WriteBody(writer, manifest, signature.ToArray());
        return writer.Encode();
    }

    /// <summary>
    /// Decodes a stored snapshot and rebuilds the signed 1–16 prefix for the
    /// caller to verify.
    /// </summary>
    /// <exception cref="ManifestValidationException">The bytes violate specification 06 §6 — a damage finding.</exception>
    public static DecodedSnapshot Decode(ReadOnlyMemory<byte> data)
    {
        try
        {
            return DecodeCore(data);
        }
        catch (CborFormatException exception)
        {
            throw new ManifestValidationException($"The snapshot manifest is not canonical CBOR: {exception.Message}", exception);
        }
    }

    private static DecodedSnapshot DecodeCore(ReadOnlyMemory<byte> data)
    {
        var reader = new CanonicalCborReader(data);
        var count = reader.ReadStartMap();

        ReadOnlyMemory<byte>? snapshotId = null, deviceId = null, backupSetId = null;
        ulong? started = null, completed = null, generation = null;
        ObjectId? rootTree = null, policy = null, error = null;
        List<ReadOnlyMemory<byte>> parents = [];
        byte? consistency = null, status = null;
        SourceFilesystem? filesystem = null;
        long? clockSkew = null;
        string? clientVersion = null;
        List<string> tags = [];
        byte[]? signature = null;

        for (var i = 0; i < count; i++)
        {
            switch (reader.ReadKey())
            {
                case 1:
                    snapshotId = reader.ReadFixedByteString(16);
                    break;
                case 2:
                    deviceId = reader.ReadFixedByteString(16);
                    break;
                case 3:
                    backupSetId = reader.ReadFixedByteString(16);
                    break;
                case 4:
                    started = reader.ReadUnsignedInteger();
                    break;
                case 5:
                    completed = reader.ReadUnsignedInteger();
                    break;
                case 6:
                    rootTree = ObjectId.FromBytes(reader.ReadFixedByteString(32));
                    break;
                case 7:
                    var parentCount = reader.ReadStartArray(maxCount: 4096);
                    for (var j = 0; j < parentCount; j++)
                    {
                        parents.Add(reader.ReadFixedByteString(16));
                    }

                    reader.ReadEndArray();
                    break;
                case 8:
                    policy = ObjectId.FromBytes(reader.ReadFixedByteString(32));
                    break;
                case 9:
                    error = ObjectId.FromBytes(reader.ReadFixedByteString(32));
                    break;
                case 10:
                    consistency = reader.ReadByte();
                    break;
                case 11:
                    status = reader.ReadByte();
                    break;
                case 12:
                    filesystem = ReadSourceFilesystem(reader);
                    break;
                case 13:
                    generation = reader.ReadUnsignedInteger();
                    break;
                case 14:
                    clockSkew = reader.ReadSignedInteger();
                    break;
                case 15:
                    clientVersion = reader.ReadTextString(maxUtf8Length: 256);
                    break;
                case 16:
                    var tagCount = reader.ReadStartArray(maxCount: 4096);
                    for (var j = 0; j < tagCount; j++)
                    {
                        tags.Add(reader.ReadTextString(maxUtf8Length: 1024));
                    }

                    reader.ReadEndArray();
                    break;
                case 17:
                    signature = reader.ReadFixedByteString(64);
                    break;
                default:
                    throw new ManifestValidationException(
                        "The snapshot manifest carries an unknown key; specification 06 §6 assigns keys 1-17 only.");
            }
        }

        reader.ReadEndMap();
        reader.AssertEndOfDocument();

        if (snapshotId is null || deviceId is null || backupSetId is null || started is null ||
            completed is null || rootTree is null || policy is null || consistency is null ||
            status is null || filesystem is null || generation is null || clientVersion is null ||
            signature is null)
        {
            throw new ManifestValidationException("The snapshot manifest omits a mandatory key (specification 06 §6).");
        }

        var manifest = new SnapshotManifest
        {
            SnapshotId = snapshotId.Value,
            DeviceId = deviceId.Value,
            BackupSetId = backupSetId.Value,
            CaptureStartedAt = started.Value,
            CaptureCompletedAt = completed.Value,
            RootTree = rootTree.Value,
            ParentSnapshots = parents,
            PolicyManifest = policy.Value,
            ErrorManifest = error,
            ConsistencyMethod = consistency.Value,
            CaptureStatus = status.Value,
            SourceFilesystem = filesystem,
            PublicationGeneration = generation.Value,
            ObservedClockSkewMs = clockSkew,
            ClientVersion = clientVersion,
            Tags = tags,
        };

        Validate(manifest);

        // The verifier cannot strip a suffix — the map header's count differs
        // between the signed and stored forms — so the signed prefix is
        // re-encoded from the decoded values (06 §6.1).
        return new DecodedSnapshot(manifest, EncodeForSigning(manifest), signature);
    }

    private static void WriteBody(CanonicalCborWriter writer, SnapshotManifest manifest, byte[]? signature)
    {
        var keyCount = 14
            + (manifest.ErrorManifest is not null ? 1 : 0)
            + (manifest.ObservedClockSkewMs is not null ? 1 : 0)
            + (signature is not null ? 1 : 0);

        writer.WriteStartMap(keyCount);
        writer.WriteKey(1);
        writer.WriteByteString(manifest.SnapshotId.Span);
        writer.WriteKey(2);
        writer.WriteByteString(manifest.DeviceId.Span);
        writer.WriteKey(3);
        writer.WriteByteString(manifest.BackupSetId.Span);
        writer.WriteKey(4);
        writer.WriteUnsignedInteger(manifest.CaptureStartedAt);
        writer.WriteKey(5);
        writer.WriteUnsignedInteger(manifest.CaptureCompletedAt);
        writer.WriteKey(6);
        writer.WriteByteString(manifest.RootTree.ToArray());
        writer.WriteKey(7);
        writer.WriteStartArray(manifest.ParentSnapshots.Count);
        foreach (var parent in manifest.ParentSnapshots)
        {
            writer.WriteByteString(parent.Span);
        }

        writer.WriteEndArray();
        writer.WriteKey(8);
        writer.WriteByteString(manifest.PolicyManifest.ToArray());

        if (manifest.ErrorManifest is { } error)
        {
            writer.WriteKey(9);
            writer.WriteByteString(error.ToArray());
        }

        writer.WriteKey(10);
        writer.WriteUnsignedInteger(manifest.ConsistencyMethod);
        writer.WriteKey(11);
        writer.WriteUnsignedInteger(manifest.CaptureStatus);
        writer.WriteKey(12);
        writer.WriteStartMap(3);
        writer.WriteKey(1);
        writer.WriteBoolean(manifest.SourceFilesystem.CaseSensitive);
        writer.WriteKey(2);
        writer.WriteBoolean(manifest.SourceFilesystem.SupportsSparse);
        writer.WriteKey(3);
        writer.WriteTextString(manifest.SourceFilesystem.Name);
        writer.WriteEndMap();
        writer.WriteKey(13);
        writer.WriteUnsignedInteger(manifest.PublicationGeneration);

        if (manifest.ObservedClockSkewMs is { } skew)
        {
            writer.WriteKey(14);
            writer.WriteSignedInteger(skew);
        }

        writer.WriteKey(15);
        writer.WriteTextString(manifest.ClientVersion);
        writer.WriteKey(16);
        writer.WriteStartArray(manifest.Tags.Count);
        foreach (var tag in manifest.Tags)
        {
            writer.WriteTextString(tag);
        }

        writer.WriteEndArray();

        if (signature is not null)
        {
            writer.WriteKey(17);
            writer.WriteByteString(signature);
        }

        writer.WriteEndMap();
    }

    private static SourceFilesystem ReadSourceFilesystem(CanonicalCborReader reader)
    {
        var count = reader.ReadStartMap();

        bool? caseSensitive = null, supportsSparse = null;
        string? name = null;

        for (var i = 0; i < count; i++)
        {
            switch (reader.ReadKey())
            {
                case 1:
                    caseSensitive = reader.ReadBoolean();
                    break;
                case 2:
                    supportsSparse = reader.ReadBoolean();
                    break;
                case 3:
                    name = reader.ReadTextString(maxUtf8Length: 128);
                    break;
                default:
                    throw new ManifestValidationException(
                        "source_filesystem carries an unknown key (ADR-0022 §Decision 6).");
            }
        }

        reader.ReadEndMap();

        if (caseSensitive is null || supportsSparse is null || name is null)
        {
            throw new ManifestValidationException("source_filesystem omits a mandatory key (ADR-0022 §Decision 6).");
        }

        return new SourceFilesystem(caseSensitive.Value, supportsSparse.Value, name);
    }

    private static void Validate(SnapshotManifest manifest)
    {
        if (manifest.SnapshotId.Length != 16 || manifest.DeviceId.Length != 16 || manifest.BackupSetId.Length != 16)
        {
            throw new ManifestValidationException("snapshot_id, device_id, and backup_set_id are 16 bytes each (specification 06 §6).");
        }

        if (manifest.ConsistencyMethod is < 1 or > 4)
        {
            throw new ManifestValidationException($"consistency_method {manifest.ConsistencyMethod} is unassigned (specification 06 §6).");
        }

        if (manifest.CaptureStatus is < 1 or > 3)
        {
            throw new ManifestValidationException($"capture_status {manifest.CaptureStatus} is unassigned (specification 06 §6).");
        }
    }
}
