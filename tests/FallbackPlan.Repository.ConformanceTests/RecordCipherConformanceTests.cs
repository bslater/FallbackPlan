using System.Text.Json;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Records;
using Xunit;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// Drives the <c>aes-gcm.json</c> real-construction case through the actual
/// record path: the blob key re-derived from <c>keys.json</c> inputs, the
/// nonce and 55-byte AAD built by the real builders, sealed by
/// <see cref="RecordCipher"/> — a regression vector for the exact
/// construction records use (specification 04; FR-ARCH-009).
/// </summary>
public sealed class RecordCipherConformanceTests
{
    private static JsonDocument Load(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", name)));

    [Fact]
    public void The_real_record_construction_reproduces_the_pinned_case()
    {
        using var aesGcm = Load("aes-gcm.json");
        using var keys = Load("keys.json");
        using var records = Load("records.json");

        var vectorCase = aesGcm.RootElement.GetProperty("cases").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "record_ordinal_47_real_construction");

        // Re-derive the vector's key from keys.json inputs through the real
        // hierarchy, proving the case's key is the blob key it claims to be.
        var keyInputs = keys.RootElement.GetProperty("inputs");
        using var hierarchy = new KeyHierarchy(Convert.FromHexString(keyInputs.GetProperty("master_key").GetString()!));
        var blobKey = new byte[BlobKeyDeriver.BlobKeyLength];
        BlobKeyDeriver.Derive(
            hierarchy.DeriveDataKey(KeyGeneration.Zero),
            Convert.FromHexString(keyInputs.GetProperty("blob_salt").GetString()!),
            WriterId.FromBytes(Convert.FromHexString(keyInputs.GetProperty("writer_id").GetString()!)),
            keyInputs.GetProperty("blob_counter").GetUInt64(),
            blobKey);

        Assert.Equal(vectorCase.GetProperty("key").GetString(), Convert.ToHexStringLower(blobKey));

        // Build nonce and AAD with the real builders from records.json inputs.
        var recordInputs = records.RootElement.GetProperty("inputs");
        var nonce = new byte[12];
        RecordNonce.Write(47, nonce);
        Assert.Equal(vectorCase.GetProperty("iv").GetString(), Convert.ToHexStringLower(nonce));

        var aad = new byte[RecordAad.Length];
        RecordAad.Write(
            RepositoryId.FromBytes(Convert.FromHexString(recordInputs.GetProperty("repository_id").GetString()!)),
            recordInputs.GetProperty("format_version").GetUInt16(),
            (ObjectType)recordInputs.GetProperty("object_type").GetByte(),
            ObjectId.FromBytes(Convert.FromHexString(recordInputs.GetProperty("object_id").GetString()!)),
            47,
            aad);
        Assert.Equal(vectorCase.GetProperty("aad").GetString(), Convert.ToHexStringLower(aad));

        // Seal through the real cipher and compare the pinned bytes.
        var plaintext = Convert.FromHexString(vectorCase.GetProperty("plaintext").GetString()!);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[RecordCipher.TagLength];
        RecordCipher.Seal(blobKey, nonce, aad, plaintext, ciphertext, tag);

        Assert.Equal(vectorCase.GetProperty("ciphertext").GetString(), Convert.ToHexStringLower(ciphertext));
        Assert.Equal(vectorCase.GetProperty("tag").GetString(), Convert.ToHexStringLower(tag));

        // And open it back.
        var restored = new byte[plaintext.Length];
        Assert.True(RecordCipher.TryOpen(blobKey, nonce, aad, ciphertext, tag, restored));
        Assert.Equal(plaintext, restored);
    }
}
