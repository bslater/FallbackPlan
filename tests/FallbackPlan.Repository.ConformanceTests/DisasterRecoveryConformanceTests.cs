using System.Text.Json;
using FallbackPlan.Domain;
using FallbackPlan.Repository.Crypto;

namespace FallbackPlan.Repository.ConformanceTests;

/// <summary>
/// Drives <c>disaster-recovery.json</c> through the real derivations
/// (specification 11 §5, 03 §4; peer-protocol 07 §5; ADR-0046, ADR-0047):
/// the recovery recipient a set-configuration object is sealed to, and the
/// claim keypair that re-points a replica's attribution. FR-DR-002, FR-DR-003,
/// FR-DR-007.
/// </summary>
/// <remarks>
/// The vectors are computed by the specification's own Python generator, which
/// carries independent Ed25519, X25519 and HKDF implementations. This suite is
/// therefore a cross-check between two implementations rather than the engine
/// asserting its own output back at itself — the posture ADR-0042's sealing
/// was validated under, and the reason a conformance suite exists at all.
/// </remarks>
[TestClass]
public sealed class DisasterRecoveryConformanceTests
{
    private static JsonDocument Vectors { get; } = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "disaster-recovery.json")));

    private static JsonElement Recovery => Vectors.RootElement.GetProperty("recovery_recipient");

    private static JsonElement Claim => Vectors.RootElement.GetProperty("claim_key");

    private static byte[] Hex(JsonElement parent, string name) =>
        Convert.FromHexString(parent.GetProperty(name).GetString()!);

    // ---------------------------------------------------------- recovery key

    [TestMethod]
    public void RecoveryRecipient_TheCommittedVector_Matches()
    {
        var masterKey = Hex(Recovery.GetProperty("inputs"), "master_key");
        var derived = Recovery.GetProperty("derived");

        var scalar = RecoveryRecipient.DeriveScalar(masterKey);

        Assert.AreEqual(
            derived.GetProperty("recovery_scalar").GetString(),
            Convert.ToHexStringLower(scalar));
        Assert.AreEqual(
            derived.GetProperty("recovery_public_key").GetString(),
            Convert.ToHexStringLower(RecoveryRecipient.PublicKeyOf(scalar)));
    }

    [TestMethod]
    public void RecoveryRecipient_TheSameMasterKeyUnderEveryOtherDomain_DerivesSomethingElse()
    {
        // The separation the envelope's confidentiality rests on: a holder of
        // any other derived key must not reach the recovery scalar.
        var masterKey = Hex(Recovery.GetProperty("inputs"), "master_key");
        var scalar = Convert.ToHexStringLower(RecoveryRecipient.DeriveScalar(masterKey));

        using var hierarchy = new KeyHierarchy(masterKey);

        Assert.AreNotEqual(scalar, Convert.ToHexStringLower(hierarchy.DeriveContentIdKey()));
        Assert.AreNotEqual(scalar, Convert.ToHexStringLower(hierarchy.DeriveKeyIdKey()));
        Assert.AreNotEqual(scalar, Convert.ToHexStringLower(hierarchy.DeriveSigningKeySeed(KeyGeneration.Zero)));
        Assert.AreNotEqual(scalar, Convert.ToHexStringLower(hierarchy.DeriveMetadataKey(KeyGeneration.Zero)));
    }

    [TestMethod]
    public void ConfigurationEnvelope_SealedToTheRecoveryRecipient_OpensWithItsScalar()
    {
        var scalar = RecoveryRecipient.DeriveScalar(Hex(Recovery.GetProperty("inputs"), "master_key"));
        var configuration = "the set's shape, as a rebuilt machine would read it"u8.ToArray();

        var envelope = RecoveryRecipient.Seal(RecoveryRecipient.PublicKeyOf(scalar), configuration);

        CollectionAssert.AreEqual(configuration, RecoveryRecipient.Open(scalar, envelope));
    }

    [TestMethod]
    public void ConfigurationEnvelope_OpenedUnderAnotherPurpose_IsRefused()
    {
        // The associated data is what separates a configuration envelope from
        // a provisioning or restore-grant one; they share the construction and
        // differ only here. Opening one as another must fail authentication,
        // not return plausible bytes.
        var scalar = RecoveryRecipient.DeriveScalar(Hex(Recovery.GetProperty("inputs"), "master_key"));
        var envelope = RecoveryRecipient.Seal(RecoveryRecipient.PublicKeyOf(scalar), "configuration"u8);

        Assert.ThrowsExactly<SealedContentException>(
            () => ContentSealing.OpenPayload(scalar, envelope, "fbp/provision/v2"u8));
    }

    [TestMethod]
    public void ConfigurationEnvelope_OpenedWithAnotherRepositorysScalar_IsRefused()
    {
        var scalar = RecoveryRecipient.DeriveScalar(Hex(Recovery.GetProperty("inputs"), "master_key"));
        var stranger = RecoveryRecipient.DeriveScalar([.. Enumerable.Repeat((byte)0x9C, 32)]);
        var envelope = RecoveryRecipient.Seal(RecoveryRecipient.PublicKeyOf(scalar), "configuration"u8);

        Assert.ThrowsExactly<SealedContentException>(() => RecoveryRecipient.Open(stranger, envelope));
    }

    // ------------------------------------------------------------- claim key

    [TestMethod]
    public void ClaimKey_TheCommittedVector_Matches()
    {
        var inputs = Claim.GetProperty("inputs");
        var derived = Claim.GetProperty("derived");

        var seed = ClaimKeyDeriver.DeriveSeed(Hex(inputs, "claim_root"), Hex(inputs, "claim_token"));

        Assert.AreEqual(derived.GetProperty("claim_seed").GetString(), Convert.ToHexStringLower(seed));
        Assert.AreEqual(
            derived.GetProperty("claim_public_key").GetString(),
            Convert.ToHexStringLower(ClaimKeyDeriver.PublicKeyOf(seed)));
    }

    [TestMethod]
    public void ClaimKey_TheProofOverItsBoundMessage_ReproducesTheCommittedSignature()
    {
        var inputs = Claim.GetProperty("inputs");
        var proof = Claim.GetProperty("proof");

        var seed = ClaimKeyDeriver.DeriveSeed(Hex(inputs, "claim_root"), Hex(inputs, "claim_token"));
        using var signer = RepositorySigner.FromSeed(seed, KeyGeneration.Zero);

        Assert.AreEqual(
            proof.GetProperty("signature").GetString(),
            Convert.ToHexStringLower(signer.Sign(Hex(proof, "message"))));
    }

    [TestMethod]
    public void ClaimKey_AnotherDestinationsToken_YieldsADifferentKeypair()
    {
        // The whole reason the token is per destination: a proof captured at
        // one friend's machine must be inert at another's, even though both
        // hold replicas of the same repository under the same passphrase.
        var inputs = Claim.GetProperty("inputs");
        var separation = Claim.GetProperty("separation_checks");
        var root = Hex(inputs, "claim_root");

        var here = ClaimKeyDeriver.DeriveSeed(root, Hex(inputs, "claim_token"));
        var elsewhere = ClaimKeyDeriver.DeriveSeed(root, Hex(separation, "other_claim_token"));

        Assert.AreEqual(
            separation.GetProperty("other_claim_public_key").GetString(),
            Convert.ToHexStringLower(ClaimKeyDeriver.PublicKeyOf(elsewhere)));
        Assert.AreNotEqual(Convert.ToHexStringLower(here), Convert.ToHexStringLower(elsewhere));
    }

    [TestMethod]
    public void ClaimKey_AnInputOfTheWrongLength_IsRefusedByName()
    {
        var root = Hex(Claim.GetProperty("inputs"), "claim_root");
        var token = Hex(Claim.GetProperty("inputs"), "claim_token");

        Assert.ThrowsExactly<ArgumentException>(() => ClaimKeyDeriver.DeriveSeed(root.AsSpan(1).ToArray(), token));
        Assert.ThrowsExactly<ArgumentException>(() => ClaimKeyDeriver.DeriveSeed(root, token.AsSpan(1).ToArray()));
    }
}
