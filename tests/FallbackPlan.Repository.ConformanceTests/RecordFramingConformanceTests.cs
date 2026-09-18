using FallbackPlan.Repository.Crypto;
using System.Text.Json;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Records;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// Drives <c>records.json</c> through the real nonce and AAD builders —
/// Wave B5's byte-level acceptance (specification 04 §3–§4, 05 §3;
/// FR-ARCH-009, NFR-SEC-003): the record AAD is exactly 55 bytes, the footer
/// AAD exactly 38, and every committed case reproduces bit for bit.
/// </summary>
[TestClass]
public sealed class RecordFramingConformanceTests
{
    private static JsonDocument Vectors { get; } =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "records.json")));

    [TestMethod]
    public void RecordNonceAndAad_EveryCommittedOrdinal_Match()
    {
        var inputs = Vectors.RootElement.GetProperty("inputs");
        var repositoryId = RepositoryId.FromBytes(Convert.FromHexString(inputs.GetProperty("repository_id").GetString()!));
        var formatVersion = inputs.GetProperty("format_version").GetUInt16();
        var objectType = (ObjectType)inputs.GetProperty("object_type").GetByte();
        var objectId = ObjectId.FromBytes(Convert.FromHexString(inputs.GetProperty("object_id").GetString()!));

        foreach (var vectorCase in Vectors.RootElement.GetProperty("cases").EnumerateArray())
        {
            var ordinal = vectorCase.GetProperty("ordinal").GetUInt32();

            var aesNonce = new byte[12];
            RecordNonce.Write(ordinal, aesNonce);
            Assert.AreEqual(vectorCase.GetProperty("nonce_aes_gcm").GetString(), Convert.ToHexStringLower(aesNonce));

            var aad = new byte[RecordAad.Length];
            RecordAad.Write(repositoryId, formatVersion, objectType, objectId, ordinal, aad);
            Assert.AreEqual(vectorCase.GetProperty("aad").GetString(), Convert.ToHexStringLower(aad));
            Assert.AreEqual(RecordAad.Length, vectorCase.GetProperty("aad_length").GetInt32());
        }
    }

    [TestMethod]
    public void FooterNonceAndAad_TheCommittedVector_Match()
    {
        var inputs = Vectors.RootElement.GetProperty("inputs");
        var repositoryId = RepositoryId.FromBytes(Convert.FromHexString(inputs.GetProperty("repository_id").GetString()!));
        var formatVersion = inputs.GetProperty("format_version").GetUInt16();
        var footer = Vectors.RootElement.GetProperty("footer");

        var nonce = new byte[12];
        RecordNonce.WriteFooterNonce(nonce);
        Assert.AreEqual(footer.GetProperty("nonce").GetString(), Convert.ToHexStringLower(nonce));

        var blobId = BlobId.FromBytes(Convert.FromHexString(footer.GetProperty("blob_id").GetString()!));
        var recordCount = footer.GetProperty("record_count").GetUInt32();

        var aad = new byte[FooterAad.Length];
        FooterAad.Write(repositoryId, formatVersion, blobId, recordCount, aad);
        Assert.AreEqual(footer.GetProperty("aad").GetString(), Convert.ToHexStringLower(aad));
        Assert.AreEqual(FooterAad.Length, footer.GetProperty("aad_length").GetInt32());
    }

    [TestMethod]
    public void RecordKeyAndAadV3_TheCommittedVectors_Match()
    {
        // records-v3.json (04 §2–§4, 03 §5.4): the record key from the real
        // deriver under write-only.json's generation-0 metadata key, the
        // separation checks, the 51-byte AAD from the real builder, and the
        // writer's seed derivation.
        using var vectors = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "records-v3.json")));
        using var keys = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "write-only.json")));
        var inputs = vectors.RootElement.GetProperty("inputs");
        var repositoryId = RepositoryId.FromBytes(Convert.FromHexString(inputs.GetProperty("repository_id").GetString()!));
        var formatVersion = inputs.GetProperty("format_version").GetUInt16();
        var objectType = (ObjectType)inputs.GetProperty("object_type").GetByte();
        var objectId = ObjectId.FromBytes(Convert.FromHexString(inputs.GetProperty("object_id").GetString()!));
        var classKey = Convert.FromHexString(inputs.GetProperty("class_key").GetString()!);

        using var authority = WriteOnlyDerivation.FromRoot(
            Convert.FromHexString(keys.RootElement.GetProperty("inputs").GetProperty("root").GetString()!));
        Assert.AreEqual(
            Convert.ToHexStringLower(classKey),
            Convert.ToHexStringLower(authority.Credential.DeriveMetadataKey(KeyGeneration.Zero)),
            "the vector's class key is write-only.json's generation-0 metadata key");

        var recordKey = new byte[RecordKeyDeriver.RecordKeyLength];
        RecordKeyDeriver.Derive(classKey, objectType, objectId, recordKey);
        var expected = vectors.RootElement.GetProperty("record_key");
        Assert.AreEqual(expected.GetProperty("record_key").GetString(), Convert.ToHexStringLower(recordKey));

        var checks = expected.GetProperty("separation_checks");
        var otherType = new byte[RecordKeyDeriver.RecordKeyLength];
        RecordKeyDeriver.Derive(classKey, (ObjectType)0x02, objectId, otherType);
        Assert.AreEqual(checks.GetProperty("record_key_other_type").GetString(), Convert.ToHexStringLower(otherType));
        Assert.AreNotEqual(checks.GetProperty("record_key_other_object").GetString(), Convert.ToHexStringLower(recordKey));

        var aad = new byte[RecordAad.RelocatableLength];
        RecordAad.WriteRelocatable(repositoryId, formatVersion, objectType, objectId, aad);
        Assert.AreEqual(vectors.RootElement.GetProperty("aad").GetString(), Convert.ToHexStringLower(aad));
        Assert.AreEqual(RecordAad.RelocatableLength, vectors.RootElement.GetProperty("aad_length").GetInt32());

        var seedGroup = vectors.RootElement.GetProperty("seed_derivation");
        var seedKey = new byte[RecordKeyDeriver.RecordKeyLength];
        RecordKeyDeriver.DeriveFromSeed(Convert.FromHexString(seedGroup.GetProperty("seed").GetString()!), objectId, seedKey);
        Assert.AreEqual(seedGroup.GetProperty("record_content_key").GetString(), Convert.ToHexStringLower(seedKey));
    }
}
