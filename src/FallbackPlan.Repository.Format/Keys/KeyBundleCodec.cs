using FallbackPlan.Domain;
using FallbackPlan.Repository.Format.Cbor;

namespace FallbackPlan.Repository.Format.Keys;

/// <summary>
/// Encodes and decodes the key bundle's deterministic CBOR map (specification
/// 03 §3.1): 1 <c>master_key</c> bytes[32] · 2 <c>current_data_generation</c>
/// u32 · 3 <c>current_metadata_generation</c> u32 · 4 <c>created_at</c> u64.
/// Unknown keys are skipped per specification 00 §4.3.
/// </summary>
public static class KeyBundleCodec
{
    private const uint MasterKeyKey = 1;
    private const uint DataGenerationKey = 2;
    private const uint MetadataGenerationKey = 3;
    private const uint CreatedAtKey = 4;

    /// <summary>Encodes the bundle as deterministic CBOR.</summary>
    public static byte[] Encode(KeyBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        var writer = new CanonicalCborWriter();
        writer.WriteStartMap(4);
        writer.WriteKey(MasterKeyKey);
        writer.WriteByteString(bundle.MasterKey);
        writer.WriteKey(DataGenerationKey);
        writer.WriteUnsignedInteger(bundle.CurrentDataGeneration);
        writer.WriteKey(MetadataGenerationKey);
        writer.WriteUnsignedInteger(bundle.CurrentMetadataGeneration);
        writer.WriteKey(CreatedAtKey);
        writer.WriteUnsignedInteger(bundle.CreatedAt);
        writer.WriteEndMap();

        return writer.Encode();
    }

    /// <summary>
    /// Decodes a bundle, enforcing the size limit before parsing
    /// (specification 00 §8), the deterministic profile throughout, and the
    /// presence of every required field.
    /// </summary>
    /// <exception cref="KeyObjectFormatException">A required field is missing or malformed.</exception>
    /// <exception cref="CborFormatException">The bytes violate the deterministic CBOR profile.</exception>
    public static KeyBundle Decode(ReadOnlyMemory<byte> cbor)
    {
        if (cbor.Length > FormatLimits.MaxKeyBundleCborLength)
        {
            throw new KeyObjectFormatException(
                $"Key bundle is {cbor.Length} bytes; the limit is {FormatLimits.MaxKeyBundleCborLength} (specification 00 §8).");
        }

        var reader = new CanonicalCborReader(cbor);

        byte[]? masterKey = null;
        uint? dataGeneration = null;
        uint? metadataGeneration = null;
        ulong? createdAt = null;

        var entryCount = reader.ReadStartMap();
        for (var i = 0; i < entryCount; i++)
        {
            switch (reader.ReadKey())
            {
                case MasterKeyKey:
                    masterKey = reader.ReadFixedByteString(32);
                    break;

                case DataGenerationKey:
                    dataGeneration = reader.ReadUInt32();
                    break;

                case MetadataGenerationKey:
                    metadataGeneration = reader.ReadUInt32();
                    break;

                case CreatedAtKey:
                    createdAt = reader.ReadUnsignedInteger();
                    break;

                default:
                    // Unknown key: skip, still holding the value to the full
                    // deterministic profile (specification 00 §4.3).
                    reader.SkipValue();
                    break;
            }
        }

        reader.ReadEndMap();
        reader.AssertEndOfDocument();

        if (masterKey is null || dataGeneration is null || metadataGeneration is null || createdAt is null)
        {
            throw new KeyObjectFormatException("Key bundle is missing a required field (specification 03 §3.1).");
        }

        var bundle = new KeyBundle(masterKey, dataGeneration.Value, metadataGeneration.Value, createdAt.Value);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(masterKey);

        return bundle;
    }
}
