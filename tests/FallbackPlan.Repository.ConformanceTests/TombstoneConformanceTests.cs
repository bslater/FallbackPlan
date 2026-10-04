using System.Text.Json;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.Repository.Format.Manifests;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// Drives every <c>tombstones.json</c> case through the engine's tombstone
/// codec and signer (specification 11 §3): the canonical bytes a signature
/// covers, the signature over them, and the whole encoding, for each reason
/// in the closed vocabulary — <c>requested</c>, the one a person's deletion
/// is recorded under (FR-GC-013), among them — and the encodings a reader
/// must refuse.
/// </summary>
[TestClass]
public sealed class TombstoneConformanceTests
{
    private static JsonDocument Vectors { get; } =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "tombstones.json")));

    [TestMethod]
    public void Tombstone_EveryCase_EncodesSignsAndDecodesAsTheVectorSays()
    {
        var signing = Vectors.RootElement.GetProperty("signing");
        using var signer = RepositorySigner.FromSeed(Hex(signing, "seed"), KeyGeneration.Zero);
        Assert.AreEqual(signing.GetProperty("public_key").GetString(), Convert.ToHexStringLower(signer.PublicKey));

        var reasons = new HashSet<TombstoneReason>();
        foreach (var vectorCase in Vectors.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = vectorCase.GetProperty("name").GetString()!;
            var tombstone = new Tombstone(
                (byte)vectorCase.GetProperty("object_type").GetInt32(),
                Hex(vectorCase, "object_id"),
                (TombstoneReason)vectorCase.GetProperty("reason").GetInt32(),
                Hex(vectorCase, "writer_id"),
                vectorCase.GetProperty("tombstoned_at").GetUInt64(),
                vectorCase.GetProperty("eligible_generation").GetUInt64());

            var prefix = TombstoneCodec.EncodeForSigning(tombstone);
            Assert.AreEqual(vectorCase.GetProperty("signed_prefix").GetString(), Convert.ToHexStringLower(prefix), name);

            var signature = signer.Sign(prefix);
            Assert.AreEqual(vectorCase.GetProperty("signature").GetString(), Convert.ToHexStringLower(signature), name);

            var encoded = TombstoneCodec.Encode(tombstone, signature);
            Assert.AreEqual(vectorCase.GetProperty("encoded").GetString(), Convert.ToHexStringLower(encoded), name);

            // Read back as a reader would: the bytes the signature covers are
            // rebuilt from the decode, and verify against the public key alone.
            var decoded = TombstoneCodec.Decode(encoded);
            Assert.AreEqual(tombstone.Reason, decoded.Value.Reason, name);
            Assert.IsTrue(
                RepositorySigner.VerifyWithPublicKey(signer.PublicKey, decoded.SignedBytes.Span, decoded.Signature.Span),
                name);

            reasons.Add(tombstone.Reason);
        }

        // Every reason the vocabulary holds has a case, so a reason added to
        // the format without a vector is caught here.
        Assert.IsTrue(reasons.SetEquals(Enum.GetValues<TombstoneReason>()), "a reason in the vocabulary has no vector");
    }

    [TestMethod]
    public void Tombstone_EveryRefusedEncoding_IsRefusedAtRead()
    {
        foreach (var vectorCase in Vectors.RootElement.GetProperty("refused").EnumerateArray())
        {
            var encoded = Hex(vectorCase, "encoded");

            Assert.ThrowsExactly<ManifestValidationException>(
                () => TombstoneCodec.Decode(encoded), vectorCase.GetProperty("name").GetString()!);
        }
    }

    [TestMethod]
    public void Tombstone_TheRequestedCase_NamesASnapshotManifest()
    {
        // The request names the snapshot a person chose. What only that
        // snapshot referenced is condemned later as unreferenced.
        var requested = Vectors.RootElement.GetProperty("cases").EnumerateArray()
            .Single(vectorCase => vectorCase.GetProperty("reason").GetInt32() == (int)TombstoneReason.Requested);

        Assert.AreEqual((int)ObjectType.SnapshotManifest, requested.GetProperty("object_type").GetInt32());
    }

    private static byte[] Hex(JsonElement element, string property) =>
        Convert.FromHexString(element.GetProperty(property).GetString()!);
}
