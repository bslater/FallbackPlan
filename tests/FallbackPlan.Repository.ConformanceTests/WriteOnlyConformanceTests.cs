using System.Security.Cryptography;
using System.Text.Json;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// Drives <c>write-only.json</c> through the real
/// <see cref="WriteOnlyDerivation"/> and the Bodu X25519 (specification 03
/// §9; ADR-0042; FR-WOR-001, NFR-SEC-010): the whole one-way expansion tree
/// from the pinned root, the derived sealing public key against an
/// implementation written from RFC 7748 alone, and the sealed-content-key
/// key agreement — shared secret and AEAD key — from both sides.
/// </summary>
[TestClass]
public sealed class WriteOnlyConformanceTests
{
    private static JsonDocument Vectors { get; } =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "write-only.json")));

    private static byte[] Input(string name) =>
        Convert.FromHexString(Vectors.RootElement.GetProperty("inputs").GetProperty(name).GetString()!);

    private static string Derived(string name) =>
        Vectors.RootElement.GetProperty("derived").GetProperty(name).GetString()!;

    private static JsonElement Sealing => Vectors.RootElement.GetProperty("content_key_sealing");

    [TestMethod]
    public void WriteOnlyDerivation_TheCommittedVectors_MatchEveryMember()
    {
        using var authority = WriteOnlyDerivation.FromRoot(Input("root"));

        Assert.AreEqual(Derived("sealing_scalar"), Convert.ToHexStringLower(authority.SealingPrivateKey));
        Assert.AreEqual(
            Derived("sealing_public_key"), Convert.ToHexStringLower(authority.Credential.SealingPublicKey));
        Assert.AreEqual(Derived("content_id_key"), Convert.ToHexStringLower(authority.Credential.ContentIdKey));
        Assert.AreEqual(Derived("key_id_key"), Convert.ToHexStringLower(authority.Credential.KeyIdKey));
        Assert.AreEqual(
            Derived("metadata_key_generation_0"),
            Convert.ToHexStringLower(authority.Credential.DeriveMetadataKey(KeyGeneration.Zero)));
        Assert.AreEqual(
            Derived("metadata_key_generation_1"),
            Convert.ToHexStringLower(authority.Credential.DeriveMetadataKey(new KeyGeneration(1))));
        Assert.AreEqual(
            Derived("signing_seed_generation_0"),
            Convert.ToHexStringLower(authority.Credential.DeriveSigningKeySeed(KeyGeneration.Zero)));
    }

    [TestMethod]
    public void ContentKeySealing_TheKeyAgreement_MatchesFromBothSides()
    {
        using var authority = WriteOnlyDerivation.FromRoot(Input("root"));
        var ephemeralScalar = Convert.FromHexString(Sealing.GetProperty("ephemeral_scalar").GetString()!);
        var expectedEphemeralPublic = Sealing.GetProperty("ephemeral_public_key").GetString()!;
        var expectedShared = Sealing.GetProperty("shared_secret").GetString()!;
        var expectedAeadKey = Sealing.GetProperty("aead_key").GetString()!;

        // The sender's side: the pinned ephemeral scalar against the derived
        // repository public key, through the real Bodu X25519.
        using var ephemeral = Bodu.Security.Cryptography.X25519.Create();
        ephemeral.ImportPrivateKey(ephemeralScalar);
        var ephemeralPublic = ephemeral.ExportPublicKey();
        Assert.AreEqual(expectedEphemeralPublic, Convert.ToHexStringLower(ephemeralPublic));
        var shared = ephemeral.DeriveSharedSecret(authority.Credential.SealingPublicKey);
        Assert.AreEqual(expectedShared, Convert.ToHexStringLower(shared));

        // The recipient's side must agree — this is the property a restore
        // grant depends on.
        using var recipient = Bodu.Security.Cryptography.X25519.Create();
        recipient.ImportPrivateKey(authority.SealingPrivateKey);
        Assert.AreEqual(expectedShared, Convert.ToHexStringLower(recipient.DeriveSharedSecret(ephemeralPublic)));

        // The AEAD key: extract-then-expand, salted by both public shares —
        // replicated here from the specification's text (05 §2.1), which is
        // the point: an implementer holding only the spec computes this.
        var salt = ephemeralPublic.Concat(authority.Credential.SealingPublicKey.ToArray()).ToArray();
        var aeadKey = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, aeadKey, salt, "fbp/seal-content/v2"u8);
        Assert.AreEqual(expectedAeadKey, Convert.ToHexStringLower(aeadKey));
    }
}
