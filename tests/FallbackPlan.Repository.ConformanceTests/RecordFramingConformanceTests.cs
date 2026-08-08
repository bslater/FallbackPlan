using System.Text.Json;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Format.Records;
using Xunit;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// Drives <c>records.json</c> through the real nonce and AAD builders —
/// Wave B5's byte-level acceptance (specification 04 §3–§4, 05 §3;
/// FR-ARCH-009, NFR-SEC-003): the record AAD is exactly 55 bytes, the footer
/// AAD exactly 38, and every committed case reproduces bit for bit.
/// </summary>
public sealed class RecordFramingConformanceTests
{
    private static JsonDocument Vectors { get; } =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "records.json")));

    [Fact]
    public void Nonces_and_aad_match_every_committed_ordinal()
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
            Assert.Equal(vectorCase.GetProperty("nonce_aes_gcm").GetString(), Convert.ToHexStringLower(aesNonce));

            var aad = new byte[RecordAad.Length];
            RecordAad.Write(repositoryId, formatVersion, objectType, objectId, ordinal, aad);
            Assert.Equal(vectorCase.GetProperty("aad").GetString(), Convert.ToHexStringLower(aad));
            Assert.Equal(RecordAad.Length, vectorCase.GetProperty("aad_length").GetInt32());
        }
    }

    [Fact]
    public void The_footer_nonce_and_aad_match_the_committed_vector()
    {
        var inputs = Vectors.RootElement.GetProperty("inputs");
        var repositoryId = RepositoryId.FromBytes(Convert.FromHexString(inputs.GetProperty("repository_id").GetString()!));
        var formatVersion = inputs.GetProperty("format_version").GetUInt16();
        var footer = Vectors.RootElement.GetProperty("footer");

        var nonce = new byte[12];
        RecordNonce.WriteFooterNonce(nonce);
        Assert.Equal(footer.GetProperty("nonce").GetString(), Convert.ToHexStringLower(nonce));

        var blobId = BlobId.FromBytes(Convert.FromHexString(footer.GetProperty("blob_id").GetString()!));
        var recordCount = footer.GetProperty("record_count").GetUInt32();

        var aad = new byte[FooterAad.Length];
        FooterAad.Write(repositoryId, formatVersion, blobId, recordCount, aad);
        Assert.Equal(footer.GetProperty("aad").GetString(), Convert.ToHexStringLower(aad));
        Assert.Equal(FooterAad.Length, footer.GetProperty("aad_length").GetInt32());
    }
}
