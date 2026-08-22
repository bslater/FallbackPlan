using Bodu;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;

namespace FallbackPlan.Repository.Packing;

/// <summary>
/// Everything that could vary between the interrupted process and the
/// resuming one, pinned (specification 05 §6.2): segmentation profile and
/// parameters, compression profile <b>and codec version</b>, and encryption
/// profile. On resume every field is compared; any mismatch forces a
/// restart under a fresh salt, never a resume.
/// </summary>
public sealed record SpoolPinnedConfiguration(
    ushort SegmentationProfile,
    ulong SegmentationParameter1,
    ulong SegmentationParameter2,
    ulong SegmentationParameter3,
    ushort CompressionProfile,
    string CodecVersion,
    ushort EncryptionProfile)
{
    /// <summary>
    /// Pins a capture policy's variable surface. <paramref name="codecVersion"/>
    /// is the loaded compression library identity (e.g.
    /// <c>ZstdSegmentCodec.CodecVersion</c>), or a fixed marker when the
    /// policy stores plaintext only.
    /// </summary>
    public static SpoolPinnedConfiguration FromPolicy(Domain.Configuration.CapturePolicy policy, string codecVersion)
    {
        ThrowHelper.ThrowIfNull(policy);
        ThrowHelper.ThrowIfNullOrWhiteSpace(codecVersion);

        return policy.SegmentationProfile == Domain.Profiles.SegmentationProfile.CdcV1
            ? new SpoolPinnedConfiguration(
                policy.SegmentationProfile.Value,
                (ulong)policy.CdcParameters!.Value.TargetSize,
                (ulong)policy.CdcParameters.Value.MinSize,
                (ulong)policy.CdcParameters.Value.MaxSize,
                policy.Compression.Profile.Value,
                codecVersion,
                policy.EncryptionProfile.Value)
            : new SpoolPinnedConfiguration(
                policy.SegmentationProfile.Value,
                (ulong)policy.SegmentSize.Bytes,
                0,
                0,
                policy.Compression.Profile.Value,
                codecVersion,
                policy.EncryptionProfile.Value);
    }
}

/// <summary>The outcome of <see cref="BlobWriter.TryResume"/> (specification 05 §6.3).</summary>
public abstract record ResumeResult
{
    private ResumeResult()
    {
    }

    /// <summary>No checkpointed spool exists in the directory.</summary>
    public sealed record NoSpool : ResumeResult;

    /// <summary>
    /// The spool cannot be safely resumed — the reason names the pinned
    /// field that mismatched or the damage found. The spool and checkpoint
    /// have been discarded; the caller starts a new blob, which draws a
    /// fresh salt and therefore a different key (05 §6.3).
    /// </summary>
    public sealed record MustRestart(string Reason) : ResumeResult;

    /// <summary>
    /// The spool resumed: the writer holds the checkpointed sealed bytes
    /// verbatim and continues at the next ordinal.
    /// </summary>
    public sealed record Resumed(BlobWriter Writer) : ResumeResult;
}

/// <summary>
/// The durable sidecar beside a spool file (specification 05 §6; C1;
/// FR-ARCH-011, NFR-REL-005): the spool itself carries the sealed record
/// bytes — header, ciphertext, tag, exactly as they will appear in the blob
/// (05 §6.1's load-bearing rule) — and this sidecar carries the pinned
/// configuration and the envelope's key-derivation selectors. A trailing
/// SHA-256 makes a torn sidecar detectable; anything detectably wrong
/// resolves to restart, the always-safe failure.
/// </summary>
/// <remarks>
/// Every field here is fixed for the blob's life, which is what lets the
/// sidecar be written once at create rather than rewritten per record. It
/// carries no watermark: <see cref="BlobWriter.TryResume"/> finds the resume
/// point by authenticating the spool's records, so the bytes bound
/// themselves and nothing has to be kept in step with them. 05 §6.2 requires
/// exactly seven pinned fields and mandates no watermark, no write frequency,
/// and no per-record durability.
/// <para>
/// The layout carries no version of its own and needs none. A sidecar from a
/// build that still wrote a watermark is eight bytes longer and fails
/// <see cref="TryParse"/>'s total-length check, which the caller resolves to
/// a restart — the safe failure, and the same outcome as any other
/// unreadable sidecar.
/// </para>
/// </remarks>
public sealed class SpoolCheckpoint
{
    /// <summary>The sidecar magic, <c>"FBPKSPCK"</c>.</summary>
    public static ReadOnlySpan<byte> Magic => "FBPKSPCK"u8;

    internal SpoolCheckpoint(
        ushort formatVersion,
        BlobClass blobClass,
        KeyGeneration keyGeneration,
        ReadOnlyMemory<byte> blobSalt,
        WriterId writerId,
        ulong blobCounter,
        BlobId blobId,
        SpoolPinnedConfiguration pinned,
        ReadOnlyMemory<byte>? contentKey = null)
    {
        // Empty is "none" — the shape a v1 writer's null byte[] arrives in
        // through the implicit ReadOnlyMemory conversion — so only a PRESENT
        // key of the wrong length is a caller defect.
        if (contentKey is { Length: not (0 or 32) })
        {
            throw new ArgumentException(
                Resources.Strings.FormatSpoolCheckpoint_ContentKeyExactlyBytes(32), nameof(contentKey));
        }

        FormatVersion = formatVersion;
        BlobClass = blobClass;
        KeyGeneration = keyGeneration;
        BlobSalt = blobSalt;
        WriterId = writerId;
        BlobCounter = blobCounter;
        BlobId = blobId;
        Pinned = pinned;
        ContentKey = contentKey;
    }

    /// <summary>The format version the spool was written under.</summary>
    public ushort FormatVersion { get; }

    /// <summary>The blob's class — selects the key family on resume.</summary>
    public BlobClass BlobClass { get; }

    /// <summary>The key generation (05 §6.2).</summary>
    public KeyGeneration KeyGeneration { get; }

    /// <summary>The blob salt (05 §6.2) — 32 bytes.</summary>
    public ReadOnlyMemory<byte> BlobSalt { get; }

    /// <summary>The writer identity.</summary>
    public WriterId WriterId { get; }

    /// <summary>The blob counter (05 §6.2).</summary>
    public ulong BlobCounter { get; }

    /// <summary>The blob identifier (05 §6.2).</summary>
    public BlobId BlobId { get; }

    /// <summary>The pinned configuration (05 §6.2).</summary>
    public SpoolPinnedConfiguration Pinned { get; }

    /// <summary>
    /// A v2 data blob's in-flight content key (ADR-0042 §3) — resume must
    /// authenticate the spooled records, and for a sealed blob only this key
    /// can. Held for the blob under construction only, on a machine that
    /// holds the same bytes as plaintext files; destroyed with the sidecar
    /// at seal. Null for every v1 blob and every metadata blob.
    /// </summary>
    public ReadOnlyMemory<byte>? ContentKey { get; }

    /// <summary>The sidecar path for a spool path.</summary>
    public static string PathFor(string spoolPath) => spoolPath + ".checkpoint";

    /// <summary>Serialises the sidecar with its trailing integrity hash.</summary>
    public byte[] Serialize()
    {
        var codecVersion = Encoding.UTF8.GetBytes(Pinned.CodecVersion);
        var length = 8 + 2 + 2 + 4 + 32 + 16 + 8 + 16
            + 2 + 8 + 8 + 8 + 2 + 2
            + 2 + codecVersion.Length
            + (ContentKey?.Length ?? 0)
            + 32;
        var buffer = new byte[length];
        var span = buffer.AsSpan();

        Magic.CopyTo(span);
        var offset = 8;
        BinaryPrimitives.WriteUInt16BigEndian(span[offset..], FormatVersion);
        offset += 2;
        BinaryPrimitives.WriteUInt16BigEndian(span[offset..], (ushort)BlobClass);
        offset += 2;
        BinaryPrimitives.WriteUInt32BigEndian(span[offset..], KeyGeneration.Value);
        offset += 4;
        BlobSalt.Span.CopyTo(span[offset..]);
        offset += 32;
        WriterId.CopyTo(span.Slice(offset, 16));
        offset += 16;
        BinaryPrimitives.WriteUInt64BigEndian(span[offset..], BlobCounter);
        offset += 8;
        BlobId.CopyTo(span.Slice(offset, 16));
        offset += 16;
        BinaryPrimitives.WriteUInt16BigEndian(span[offset..], Pinned.SegmentationProfile);
        offset += 2;
        BinaryPrimitives.WriteUInt64BigEndian(span[offset..], Pinned.SegmentationParameter1);
        offset += 8;
        BinaryPrimitives.WriteUInt64BigEndian(span[offset..], Pinned.SegmentationParameter2);
        offset += 8;
        BinaryPrimitives.WriteUInt64BigEndian(span[offset..], Pinned.SegmentationParameter3);
        offset += 8;
        BinaryPrimitives.WriteUInt16BigEndian(span[offset..], Pinned.CompressionProfile);
        offset += 2;
        BinaryPrimitives.WriteUInt16BigEndian(span[offset..], Pinned.EncryptionProfile);
        offset += 2;
        BinaryPrimitives.WriteUInt16BigEndian(span[offset..], (ushort)codecVersion.Length);
        offset += 2;
        codecVersion.CopyTo(span[offset..]);
        offset += codecVersion.Length;

        if (ContentKey is { } contentKey)
        {
            contentKey.Span.CopyTo(span[offset..]);
            offset += contentKey.Length;
        }

        SHA256.HashData(span[..offset], span.Slice(offset, 32));

        return buffer;
    }

    /// <summary>
    /// Parses a sidecar, refusing anything torn or altered — the integrity
    /// hash is unkeyed and defends against interruption, not attackers; a
    /// hostile spool directory could only force a restart, which is safe.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out SpoolCheckpoint? checkpoint)
    {
        checkpoint = null;

        // Fixed prefix through the codec-version length field.
        const int FixedPrefix = 8 + 2 + 2 + 4 + 32 + 16 + 8 + 16 + 2 + 8 + 8 + 8 + 2 + 2 + 2;

        if (data.Length < FixedPrefix + 32 || !data[..8].SequenceEqual(Magic))
        {
            return false;
        }

        // A v2 data blob's sidecar carries its 32-byte content key between
        // the codec version and the hash; the version and class fields sit
        // at fixed offsets, so the expected shape is known before the
        // length check — and a sidecar of the other shape fails it, which
        // resolves to restart exactly like any other unreadable sidecar.
        var sidecarVersion = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
        var sidecarClass = (BlobClass)BinaryPrimitives.ReadUInt16BigEndian(data[10..]);
        var contentKeyLength =
            sidecarVersion >= FormatLimits.SealedFormatVersion && sidecarClass == BlobClass.Data ? 32 : 0;

        var codecLength = BinaryPrimitives.ReadUInt16BigEndian(data[(FixedPrefix - 2)..]);
        var total = FixedPrefix + codecLength + contentKeyLength + 32;

        if (data.Length != total)
        {
            return false;
        }

        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(data[..(total - 32)], digest);

        if (!digest.SequenceEqual(data[(total - 32)..]))
        {
            return false;
        }

        var offset = 8;
        var formatVersion = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;
        var blobClass = (BlobClass)BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;
        var generation = new KeyGeneration(BinaryPrimitives.ReadUInt32BigEndian(data[offset..]));
        offset += 4;
        var salt = data.Slice(offset, 32).ToArray();
        offset += 32;
        var writerId = WriterId.FromBytes(data.Slice(offset, 16));
        offset += 16;
        var counter = BinaryPrimitives.ReadUInt64BigEndian(data[offset..]);
        offset += 8;
        var blobId = BlobId.FromBytes(data.Slice(offset, 16));
        offset += 16;
        var segProfile = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;
        var segParameter1 = BinaryPrimitives.ReadUInt64BigEndian(data[offset..]);
        offset += 8;
        var segParameter2 = BinaryPrimitives.ReadUInt64BigEndian(data[offset..]);
        offset += 8;
        var segParameter3 = BinaryPrimitives.ReadUInt64BigEndian(data[offset..]);
        offset += 8;
        var compression = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;
        var encryption = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;
        offset += 2; // codec length, already read
        var codecVersion = Encoding.UTF8.GetString(data.Slice(offset, codecLength));
        offset += codecLength;

        byte[]? contentKey = null;
        if (contentKeyLength > 0)
        {
            contentKey = data.Slice(offset, contentKeyLength).ToArray();
        }

        checkpoint = new SpoolCheckpoint(
            formatVersion,
            blobClass,
            generation,
            salt,
            writerId,
            counter,
            blobId,
            new SpoolPinnedConfiguration(
                segProfile, segParameter1, segParameter2, segParameter3, compression, codecVersion, encryption),
            contentKey);

        return true;
    }
}
