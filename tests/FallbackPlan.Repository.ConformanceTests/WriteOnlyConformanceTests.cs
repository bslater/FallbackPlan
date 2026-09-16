using System.Security.Cryptography;
using System.Text.Json;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Identifiers;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// Drives <c>write-only.json</c> through the real
/// <see cref="WriteOnlyDerivation"/> and the Bodu X25519 (specification 03
/// §5, §9; ADR-0042; FR-ARCH-008, FR-WOR-001, NFR-SEC-008, NFR-SEC-010): the
/// whole one-way expansion tree from the pinned root, the derived sealing
/// public key against an implementation written from RFC 7748 alone, the
/// per-blob key and its separation checks through the real
/// <see cref="BlobKeyDeriver"/>, and the sealed-content-key key agreement —
/// shared secret and AEAD key — from both sides. Every other vector group
/// that needs a key derives from this file's root, and the identifier and
/// record-cipher suites consume what is proven here.
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

    [TestMethod]
    public void BlobKeyAndSeparationChecks_TheCommittedVectors_MatchThroughTheRealDeriver()
    {
        var group = Vectors.RootElement.GetProperty("blob_key");
        var inputs = group.GetProperty("inputs");
        var writer = WriterId.FromBytes(Convert.FromHexString(inputs.GetProperty("writer_id").GetString()!));
        var blobSalt = Convert.FromHexString(inputs.GetProperty("blob_salt").GetString()!);
        var counter = inputs.GetProperty("blob_counter").GetUInt64();

        // The class key is the metadata key of generation 0 — what a metadata
        // blob, and a sealed data blob's footer, derive under — reached the
        // way a reader reaches it: through the credential the root expands to.
        using var authority = WriteOnlyDerivation.FromRoot(Input("root"));
        var classKey = authority.Credential.DeriveMetadataKey(KeyGeneration.Zero);
        Assert.AreEqual(inputs.GetProperty("class_key").GetString(), Convert.ToHexStringLower(classKey));

        var blobKey = new byte[BlobKeyDeriver.BlobKeyLength];
        BlobKeyDeriver.Derive(classKey, blobSalt, writer, counter, blobKey);
        Assert.AreEqual(group.GetProperty("blob_key").GetString(), Convert.ToHexStringLower(blobKey));

        var separation = group.GetProperty("separation_checks");

        var otherWriter = WriterId.FromBytes(Convert.FromHexString("b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0"));
        var otherWriterKey = new byte[BlobKeyDeriver.BlobKeyLength];
        BlobKeyDeriver.Derive(classKey, blobSalt, otherWriter, counter, otherWriterKey);
        Assert.AreEqual(separation.GetProperty("blob_key_other_writer").GetString(), Convert.ToHexStringLower(otherWriterKey));

        var otherCounterKey = new byte[BlobKeyDeriver.BlobKeyLength];
        BlobKeyDeriver.Derive(classKey, blobSalt, writer, counter + 1, otherCounterKey);
        Assert.AreEqual(separation.GetProperty("blob_key_other_counter").GetString(), Convert.ToHexStringLower(otherCounterKey));

        Assert.AreNotEqual(Convert.ToHexStringLower(blobKey), Convert.ToHexStringLower(otherWriterKey));
        Assert.AreNotEqual(Convert.ToHexStringLower(blobKey), Convert.ToHexStringLower(otherCounterKey));
    }
}
