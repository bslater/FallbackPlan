using System.Globalization;
using Bodu;
using FallbackPlan.Repository.Format.Cbor;
using FallbackPlan.Repository.Format.Manifests;
using FallbackPlan.Repository.Format.Resources;

namespace FallbackPlan.Repository.Format.Lifecycle;

/// <summary>
/// A format-upgrade record (specification 11 §5): the signed statement that
/// this repository writes a newer format from now on. The descriptor says
/// what a repository was <em>created</em> at and is never rewritten — a
/// destination commits an object it lacks and keeps the one it has, so a
/// replacement descriptor would move the source alone and leave every copy
/// claiming the older format over newer blobs. An upgrade is therefore an
/// ordinary immutable object, which is the one thing every copy path already
/// moves.
/// </summary>
/// <param name="FromVersion">The version in force when the upgrade was decided.</param>
/// <param name="ToVersion">The version written from the next seal; strictly above <paramref name="FromVersion"/>.</param>
/// <param name="UpgradedAtUnixMilliseconds">
/// Informational only, like a tombstone's <c>tombstoned_at</c>: there is no
/// trusted time source, so nothing decides on it.
/// </param>
/// <param name="WriterId">Who decided — 16 bytes, the writer identity.</param>
public sealed record FormatUpgradeRecord(
    ushort FromVersion,
    ushort ToVersion,
    ulong UpgradedAtUnixMilliseconds,
    ReadOnlyMemory<byte> WriterId);

/// <summary>A decoded upgrade record with the bytes its signature covers.</summary>
/// <param name="Value">The record.</param>
/// <param name="SignedBytes">The canonical encoding of keys 1–5 — what the signature is verified over.</param>
/// <param name="Signature">The Ed25519 signature from key 6.</param>
public sealed record DecodedFormatUpgradeRecord(
    FormatUpgradeRecord Value, ReadOnlyMemory<byte> SignedBytes, ReadOnlyMemory<byte> Signature);

/// <summary>
/// The wire codec for specification 11 §5: canonical CBOR, keys 1–5 signed,
/// the signature at key 6 under the repository's <em>signing</em> key. An
/// upgrade changes what the writer will emit and destroys nothing, so it
/// belongs to the authority that signs publications rather than to the
/// reclaim authority ADR-0055 split out for destruction.
/// </summary>
/// <remarks>
/// A record that fails verification is <b>ignored</b>, never a damage
/// finding: an unverifiable claim about the format is a claim nobody made,
/// and refusing to open a repository over a stranger's file would hand
/// anyone who can write into an archive a denial of service. That verdict
/// belongs to the caller holding the signer.
/// </remarks>
public static class FormatUpgradeRecordCodec
{
    /// <summary>
    /// The key prefix upgrade records live under. A prefix rather than one
    /// fixed key, so a repository can carry 2→3 now and 3→4 later and a
    /// listing answers what it has been through.
    /// </summary>
    public const string KeyPrefix = "format-upgrade/";

    private const ushort SchemaVersion = 1;

    /// <summary>The store key an upgrade to <paramref name="toVersion"/> is written under.</summary>
    public static string KeyFor(ushort toVersion) =>
        KeyPrefix + toVersion.ToString("x4", CultureInfo.InvariantCulture);

    /// <summary>Encodes keys 1–5 — the bytes a signature covers.</summary>
    /// <param name="record">The record to encode.</param>
    /// <returns>The canonical signed prefix.</returns>
    /// <exception cref="ManifestValidationException">The record violates 11 §5.</exception>
    public static byte[] EncodeForSigning(FormatUpgradeRecord record)
    {
        ThrowHelper.ThrowIfNull(record);
        Validate(record);

        var writer = new CanonicalCborWriter();
        WriteBody(writer, record, signature: null);
        return writer.Encode();
    }

    /// <summary>Encodes the stored form: keys 1–5 plus the 64-byte signature at key 6.</summary>
    /// <param name="record">The record to encode.</param>
    /// <param name="signature">The Ed25519 signature over <see cref="EncodeForSigning"/>'s bytes.</param>
    /// <returns>The stored encoding.</returns>
    /// <exception cref="ManifestValidationException">The record or the signature violates 11 §5.</exception>
    public static byte[] Encode(FormatUpgradeRecord record, ReadOnlySpan<byte> signature)
    {
        ThrowHelper.ThrowIfNull(record);
        Validate(record);

        if (signature.Length != 64)
        {
            throw new ManifestValidationException(Strings.FormatUpgradeRecordCodec_SignatureExactlyBytes);
        }

        var writer = new CanonicalCborWriter();
        WriteBody(writer, record, signature.ToArray());
        return writer.Encode();
    }

    /// <summary>
    /// The effective format version (specification 11 §5.1): the highest
    /// <c>to_version</c> among <paramref name="records"/> that decodes, that
    /// <paramref name="verify"/> accepts, and that names a version above
    /// <paramref name="descriptorVersion"/> — else the descriptor's own.
    /// </summary>
    /// <param name="descriptorVersion">What the repository was created at; the floor, never moved below.</param>
    /// <param name="records">The stored bytes under <see cref="KeyPrefix"/>, in any order.</param>
    /// <param name="verify">
    /// Checks the signature. It is handed the decoded record together with
    /// the signed bytes and the signature, rather than being left to rebuild
    /// the signed bytes itself — a caller that rebuilt them differently would
    /// be applying a different rule while appearing to share this one.
    /// </param>
    /// <remarks>
    /// The decision lives here, and the listing lives with each caller,
    /// because the two parties that need it cannot share an object store:
    /// the engine reads through <c>RepositoryLifecycle</c>, and the recovery
    /// tool's dependency closure deliberately stops short of it. What must
    /// not differ between them is which records count.
    /// </remarks>
    public static ushort EffectiveVersion(
        ushort descriptorVersion,
        IEnumerable<ReadOnlyMemory<byte>> records,
        Func<DecodedFormatUpgradeRecord, bool> verify)
    {
        ThrowHelper.ThrowIfNull(records);
        ThrowHelper.ThrowIfNull(verify);

        var effective = descriptorVersion;

        foreach (var content in records)
        {
            DecodedFormatUpgradeRecord decoded;
            try
            {
                decoded = Decode(content);
            }
            catch (ManifestValidationException)
            {
                // A record that does not decode is a claim nobody made. It is
                // not damage to report: refusing to open a repository over a
                // file anyone able to write into it could have left would be
                // a denial of service (§5.1).
                continue;
            }

            if (decoded.Value.ToVersion > effective && verify(decoded))
            {
                effective = decoded.Value.ToVersion;
            }
        }

        return effective;
    }

    /// <summary>Decodes a stored record, rebuilding the signed prefix for the caller to verify.</summary>
    /// <param name="data">The stored bytes.</param>
    /// <returns>The decoded record.</returns>
    /// <exception cref="ManifestValidationException">The bytes violate 11 §5.</exception>
    public static DecodedFormatUpgradeRecord Decode(ReadOnlyMemory<byte> data)
    {
        try
        {
            return DecodeCore(data);
        }
        catch (CborFormatException exception)
        {
            throw new ManifestValidationException(
                Strings.FormatFormatUpgradeRecordCodec_NotCanonicalCbor(exception.Message), exception);
        }
    }

    private static DecodedFormatUpgradeRecord DecodeCore(ReadOnlyMemory<byte> data)
    {
        var reader = new CanonicalCborReader(data);
        var count = reader.ReadStartMap();

        ushort? version = null;
        ushort? fromVersion = null;
        ushort? toVersion = null;
        ulong upgradedAt = 0;
        byte[]? writerId = null;
        byte[]? signature = null;

        for (var index = 0; index < count; index++)
        {
            switch (reader.ReadKey())
            {
                case 1:
                    version = reader.ReadUInt16();
                    break;
                case 2:
                    fromVersion = reader.ReadUInt16();
                    break;
                case 3:
                    toVersion = reader.ReadUInt16();
                    break;
                case 4:
                    upgradedAt = reader.ReadUnsignedInteger();
                    break;
                case 5:
                    writerId = reader.ReadFixedByteString(16);
                    break;
                case 6:
                    signature = reader.ReadFixedByteString(64);
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        }

        reader.ReadEndMap();

        // One record is one document. Bytes after it are either damage or an
        // attempt to have two readers disagree about what was signed.
        reader.AssertEndOfDocument();

        if (version is null || fromVersion is null || toVersion is null || writerId is null || signature is null)
        {
            throw new ManifestValidationException(Strings.FormatFormatUpgradeRecordCodec_RequiredKeyMissing(
                version is null ? 1 : fromVersion is null ? 2 : toVersion is null ? 3
                : writerId is null ? 5 : 6));
        }

        if (version != SchemaVersion)
        {
            throw new ManifestValidationException(
                Strings.FormatFormatUpgradeRecordCodec_SchemaVersionUnsupported(version.Value));
        }

        var record = new FormatUpgradeRecord(fromVersion.Value, toVersion.Value, upgradedAt, writerId);
        Validate(record);

        return new DecodedFormatUpgradeRecord(record, EncodeForSigning(record), signature);
    }

    private static void Validate(FormatUpgradeRecord record)
    {
        // A record naming a version at or below the one it came from is not
        // an upgrade. Admitting one would let a file argue a repository
        // backwards, which is the one direction the effective version must
        // never take.
        if (record.ToVersion <= record.FromVersion)
        {
            throw new ManifestValidationException(
                Strings.FormatFormatUpgradeRecordCodec_NotAnUpgrade(record.FromVersion, record.ToVersion));
        }

        if (record.WriterId.Length != 16)
        {
            throw new ManifestValidationException(
                Strings.FormatFormatUpgradeRecordCodec_WriterIdentityExactlyBytes(record.WriterId.Length));
        }
    }

    private static void WriteBody(CanonicalCborWriter writer, FormatUpgradeRecord record, byte[]? signature)
    {
        writer.WriteStartMap(5 + (signature is not null ? 1 : 0));
        writer.WriteKey(1);
        writer.WriteUnsignedInteger(SchemaVersion);
        writer.WriteKey(2);
        writer.WriteUnsignedInteger(record.FromVersion);
        writer.WriteKey(3);
        writer.WriteUnsignedInteger(record.ToVersion);
        writer.WriteKey(4);
        writer.WriteUnsignedInteger(record.UpgradedAtUnixMilliseconds);
        writer.WriteKey(5);
        writer.WriteByteString(record.WriterId.Span);

        if (signature is not null)
        {
            writer.WriteKey(6);
            writer.WriteByteString(signature);
        }

        writer.WriteEndMap();
    }
}
