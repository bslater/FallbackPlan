using System.Security.Cryptography;
using FallbackPlan.Domain;
using FallbackPlan.Domain.Configuration;
using FallbackPlan.Repository.Crypto;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Repository.Tests.Crypto;

/// <summary>
/// The write-only key material (ADR-0042; FR-WOR-001, NFR-SEC-010): one
/// passphrase deterministically reproduces the whole bundle, the derived
/// public key is the wrong-passphrase verifier, every member is an
/// independent one-way domain, and the sealed content key opens only with
/// the matching derived private key under the matching context.
/// </summary>
[TestClass]
public sealed class WriteOnlyDerivationTests
{
    private static readonly Argon2Parameters TinyParameters =
        new() { MemoryKiB = 64, Iterations = 1, Parallelism = 1 };

    private static RepositoryReadAuthority Derive(string passphraseText, byte[] salt)
    {
        using var passphrase = Passphrase.Create(passphraseText);
        return WriteOnlyDerivation.Derive(passphrase, TinyParameters, salt, KdfValidationMode.OpenRepository);
    }

    private static byte[] Salt(byte seed) => [.. Enumerable.Repeat(seed, KekDerivation.SaltLength)];

    [TestMethod]
    public void Derive_TheSamePassphraseAndSalt_ReproducesEveryKeyExactly()
    {
        using var first = Derive("one long passphrase to rule them", Salt(0x11));
        using var second = Derive("one long passphrase to rule them", Salt(0x11));

        // The design's core promise: nothing is stored because nothing needs
        // to be — the passphrase and salt ARE the key material.
        SequenceAssert.AreEqual(first.SealingPrivateKey.ToArray(), second.SealingPrivateKey.ToArray());
        SequenceAssert.AreEqual(first.Credential.ToBytes(), second.Credential.ToBytes());
        SequenceAssert.AreEqual(
            first.Credential.DeriveMetadataKey(new KeyGeneration(3)),
            second.Credential.DeriveMetadataKey(new KeyGeneration(3)));
        SequenceAssert.AreEqual(
            first.Credential.DeriveSigningKeySeed(KeyGeneration.Zero),
            second.Credential.DeriveSigningKeySeed(KeyGeneration.Zero));
    }

    [TestMethod]
    public void Derive_AWrongPassphrase_YieldsADifferentPublicKey()
    {
        using var right = Derive("one long passphrase to rule them", Salt(0x22));
        using var wrong = Derive("one long passphrase to rule them!", Salt(0x22));
        using var otherSalt = Derive("one long passphrase to rule them", Salt(0x23));

        // Derive-and-compare is the v2 wrong-passphrase verifier: no
        // decryption, no oracle beyond equality against the descriptor copy.
        Assert.IsFalse(right.Credential.SealingPublicKey.SequenceEqual(wrong.Credential.SealingPublicKey));
        Assert.IsFalse(right.Credential.SealingPublicKey.SequenceEqual(otherSalt.Credential.SealingPublicKey));
    }

    [TestMethod]
    public void Derive_EveryMember_IsPairwiseDistinct()
    {
        using var authority = Derive("one long passphrase to rule them", Salt(0x33));
        var credential = authority.Credential;

        var members = new List<byte[]>
        {
            authority.SealingPrivateKey.ToArray(),
            credential.SealingPublicKey.ToArray(),
            credential.ContentIdKey.ToArray(),
            credential.KeyIdKey.ToArray(),
            credential.DeriveMetadataKey(KeyGeneration.Zero),
            credential.DeriveMetadataKey(new KeyGeneration(1)),
            credential.DeriveSigningKeySeed(KeyGeneration.Zero),
        };

        // Independent one-way domains (NFR-SEC-010): no member equals any
        // other, generation keys included.
        for (var left = 0; left < members.Count; left++)
        {
            for (var right = left + 1; right < members.Count; right++)
            {
                Assert.IsFalse(members[left].AsSpan().SequenceEqual(members[right]),
                    $"member {left} equals member {right}");
            }
        }
    }

    [TestMethod]
    public void WriteCredential_SerialisationRoundTrip_PreservesEveryDerivation()
    {
        using var authority = Derive("one long passphrase to rule them", Salt(0x44));

        using var reparsed = RepositoryWriteCredential.FromBytes(authority.Credential.ToBytes());

        SequenceAssert.AreEqual(
            authority.Credential.SealingPublicKey.ToArray(), reparsed.SealingPublicKey.ToArray());
        SequenceAssert.AreEqual(authority.Credential.ContentIdKey.ToArray(), reparsed.ContentIdKey.ToArray());
        SequenceAssert.AreEqual(
            authority.Credential.DeriveMetadataKey(new KeyGeneration(7)),
            reparsed.DeriveMetadataKey(new KeyGeneration(7)));
        SequenceAssert.AreEqual(
            authority.Credential.DeriveSigningKeySeed(new KeyGeneration(7)),
            reparsed.DeriveSigningKeySeed(new KeyGeneration(7)));
    }

    [TestMethod]
    public void WriteCredential_NotACredential_IsRefused()
    {
        Assert.ThrowsExactly<ArgumentException>(() => RepositoryWriteCredential.FromBytes(new byte[10]));

        using var authority = Derive("one long passphrase to rule them", Salt(0x55));
        var bytes = authority.Credential.ToBytes();
        bytes[0] ^= 0xFF;
        Assert.ThrowsExactly<ArgumentException>(() => RepositoryWriteCredential.FromBytes(bytes));
    }

    [TestMethod]
    public void Derive_CreationModeBelowMinimums_IsRefusedExactlyAsV1()
    {
        using var passphrase = Passphrase.Create("one long passphrase to rule them");
        Assert.ThrowsExactly<ArgumentException>(() => WriteOnlyDerivation.Derive(
            passphrase, TinyParameters, Salt(0x66), KdfValidationMode.CreateRepository));
    }

    [TestMethod]
    public void ContentSealing_SealThenOpen_RoundTripsUnderTheDerivedPair()
    {
        using var authority = Derive("one long passphrase to rule them", Salt(0x77));
        var contentKey = RandomNumberGenerator.GetBytes(32);
        var context = "repo+blob context"u8.ToArray();

        var sealedBytes = ContentSealing.Seal(authority.Credential.SealingPublicKey, contentKey, context);
        Assert.AreEqual(ContentSealing.SealedLength, sealedBytes.Length);

        SequenceAssert.AreEqual(
            contentKey, ContentSealing.Open(authority.SealingPrivateKey, sealedBytes, context));

        // Fresh ephemeral share per seal: two seals of one key never repeat.
        var again = ContentSealing.Seal(authority.Credential.SealingPublicKey, contentKey, context);
        Assert.IsFalse(sealedBytes.AsSpan().SequenceEqual(again));
    }

    [TestMethod]
    public void ContentSealing_AnyWrongInput_RefusesIndistinguishably()
    {
        using var authority = Derive("one long passphrase to rule them", Salt(0x88));
        using var other = Derive("a different passphrase entirely!", Salt(0x88));
        var contentKey = RandomNumberGenerator.GetBytes(32);
        var context = "repo+blob context"u8.ToArray();
        var sealedBytes = ContentSealing.Seal(authority.Credential.SealingPublicKey, contentKey, context);

        // The wrong private key, the wrong context, and a flipped byte are
        // the same refusal — nothing distinguishes "wrong repository" from
        // "tampered" (the KeyUnwrapFailedException posture).
        Assert.ThrowsExactly<SealedContentException>(
            () => ContentSealing.Open(other.SealingPrivateKey, sealedBytes, context));
        Assert.ThrowsExactly<SealedContentException>(
            () => ContentSealing.Open(authority.SealingPrivateKey, sealedBytes, "other context"u8.ToArray()));

        var tampered = sealedBytes.ToArray();
        tampered[^1] ^= 0x01;
        Assert.ThrowsExactly<SealedContentException>(
            () => ContentSealing.Open(authority.SealingPrivateKey, tampered, context));
    }

    [TestMethod]
    public void ContentSealing_AShapeThatCannotBeRight_IsRefusedByName()
    {
        using var authority = Derive("one long passphrase to rule them", Salt(0x99));
        var contentKey = RandomNumberGenerator.GetBytes(32);
        var context = "repo+blob context"u8.ToArray();

        Assert.ThrowsExactly<ArgumentException>(
            () => ContentSealing.Seal(new byte[16], contentKey, context));
        Assert.ThrowsExactly<ArgumentException>(
            () => ContentSealing.Seal(authority.Credential.SealingPublicKey, new byte[16], context));
        Assert.ThrowsExactly<ArgumentException>(
            () => ContentSealing.Open(authority.SealingPrivateKey, new byte[10], context));

        // An all-zero ephemeral share is a low-order point: refused before
        // any ECDH runs, mirroring the pairing ceremony's guard.
        var lowOrder = new byte[ContentSealing.SealedLength];
        Assert.ThrowsExactly<ArgumentException>(
            () => ContentSealing.Open(authority.SealingPrivateKey, lowOrder, context));
    }

    [TestMethod]
    public void ContentSealing_ALowOrderRecipient_IsRefusedAtSealTime()
    {
        // Sealing TO a low-order point derives an attacker-known secret —
        // the descriptor is the only place a sealing key arrives from, and a
        // hostile store could serve one. The refusal mirrors the guard the
        // open side applies to the ephemeral share.
        var lowOrder = new byte[ContentSealing.KeyLength];
        var contentKey = RandomNumberGenerator.GetBytes(32);

        var sealRefusal = Assert.ThrowsExactly<ArgumentException>(
            () => ContentSealing.Seal(lowOrder, contentKey, "repo+blob context"u8.ToArray()));
        Assert.Contains("low-order", sealRefusal.Message, StringComparison.Ordinal);

        var payloadRefusal = Assert.ThrowsExactly<ArgumentException>(
            () => ContentSealing.SealPayload(lowOrder, contentKey, "fbp/provision/v2"u8.ToArray()));
        Assert.Contains("low-order", payloadRefusal.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void OpenProvision_AWellSealedEnvelopeHidingGarbage_IsRefusedIndistinguishably()
    {
        // The outer envelope opens and its magic and length are right, but
        // the embedded credential is not one. The refusal must be the SAME
        // SealedContentException as any tampered envelope — never a leaked
        // parse detail, and never an untyped escape past a handler that
        // catches only the sealed-refusal type.
        var recipientPrivate = Enumerable.Repeat((byte)0x60, 32).ToArray();
        var recipientPublic = ContentSealing.PublicKeyOf(recipientPrivate);

        var payload = new byte[8 + RepositoryWriteCredential.SerializedLength + KekDerivation.SaltLength + 9];
        "FBPPROV1"u8.CopyTo(payload);
        payload.AsSpan(8).Fill(0xEE);
        var sealedBytes = ContentSealing.SealPayload(recipientPublic, payload, "fbp/provision/v2"u8.ToArray());

        Assert.ThrowsExactly<SealedContentException>(
            () => WriteOnlyProvisioning.OpenProvision(recipientPrivate, sealedBytes));
    }

    [TestMethod]
    public void ClaimRoot_SealedToTheRecipient_OpensBackToTheSameBytes()
    {
        var recipientPrivate = Enumerable.Repeat((byte)0x61, 32).ToArray();
        var recipientPublic = ContentSealing.PublicKeyOf(recipientPrivate);
        var root = Enumerable.Repeat((byte)0x7C, KekDerivation.KekLength).ToArray();

        var opened = WriteOnlyProvisioning.OpenClaimRoot(
            recipientPrivate, WriteOnlyProvisioning.SealClaimRoot(recipientPublic, root));

        SequenceAssert.AreEqual(root, opened);
    }

    [TestMethod]
    public void ClaimRoot_OfTheWrongLength_IsRefusedBeforeItIsSealed()
    {
        var recipientPublic = ContentSealing.PublicKeyOf(Enumerable.Repeat((byte)0x62, 32).ToArray());

        Assert.ThrowsExactly<ArgumentException>(
            () => WriteOnlyProvisioning.SealClaimRoot(recipientPublic, new byte[31]));
    }

    [TestMethod]
    public void ClaimRootAndRestoreGrant_AreNotInterchangeable_EvenForTheSameRecipient()
    {
        // The two envelopes carry the same 32 bytes to the same recipient and
        // authorise entirely different things: a grant reads one repository's
        // content, while a claim root can re-point that repository's
        // attribution at a new device on somebody else's disk. Only the
        // associated data separates them, so this is the test that the
        // separation is real rather than intended (ADR-0046).
        var recipientPrivate = Enumerable.Repeat((byte)0x63, 32).ToArray();
        var recipientPublic = ContentSealing.PublicKeyOf(recipientPrivate);
        var material = Enumerable.Repeat((byte)0x7D, KekDerivation.KekLength).ToArray();

        var asClaimRoot = WriteOnlyProvisioning.SealClaimRoot(recipientPublic, material);
        var asGrant = WriteOnlyProvisioning.SealGrant(recipientPublic, material);

        Assert.ThrowsExactly<SealedContentException>(
            () => WriteOnlyProvisioning.OpenGrant(recipientPrivate, asClaimRoot),
            "a claim root must not open as a restore grant");
        Assert.ThrowsExactly<SealedContentException>(
            () => WriteOnlyProvisioning.OpenClaimRoot(recipientPrivate, asGrant),
            "a restore grant must not open as a claim root");
    }
}
