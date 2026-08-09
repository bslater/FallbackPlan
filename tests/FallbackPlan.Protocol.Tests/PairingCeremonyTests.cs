using System.Security.Cryptography;
using FallbackPlan.Protocol;

namespace FallbackPlan.Protocol.Tests;

/// <summary>
/// The pairing ceremony (specification peer-protocol 01 §2; ADR-0030 §2):
/// two devices that have never met derive the same six characters, and any
/// attacker standing between them derives different ones.
/// </summary>
/// <remarks>
/// The load-bearing property is <b>what the short authentication string is
/// bound to</b>. A derivation that omitted either long-lived identity would let
/// a relay show both humans matching strings while each talked only to it —
/// which is the attack the whole ceremony exists to stop, and the one a test
/// that only checks "both sides agree" would happily pass.
/// </remarks>
[TestClass]
public sealed class PairingCeremonyTests
{
    /// <summary>One side of a ceremony, with everything it contributes.</summary>
    private sealed record Side(PeerKeypair Keypair, PairingExchange Exchange) : IDisposable
    {
        public static Side Create() => new(PeerKeypair.Generate(), PairingExchange.Start());

        public PairingContribution Contribution =>
            new(Keypair.Identity, Exchange.PublicKey, Exchange.Nonce);

        public void Dispose()
        {
            Keypair.Dispose();
            Exchange.Dispose();
        }
    }

    /// <summary>Runs a ceremony between two sides and returns each one's string.</summary>
    private static (string Offerer, string Responder) Ceremony(
        Side offerer, Side responder, ushort version = 1)
    {
        var transcript = PairingTranscript.Build(offerer.Contribution, responder.Contribution, version);

        var offererSecret = offerer.Exchange.DeriveSharedSecret(responder.Exchange.PublicKey);
        var responderSecret = responder.Exchange.DeriveSharedSecret(offerer.Exchange.PublicKey);

        return (
            PairingTranscript.ShortAuthenticationString(
                offererSecret, offerer.Exchange.Nonce, responder.Exchange.Nonce, transcript),
            PairingTranscript.ShortAuthenticationString(
                responderSecret, offerer.Exchange.Nonce, responder.Exchange.Nonce, transcript));
    }

    [TestMethod]
    public void ShortAuthenticationString_BothSidesOfOneCeremony_Agree()
    {
        using var offerer = Side.Create();
        using var responder = Side.Create();

        var (a, b) = Ceremony(offerer, responder);

        Assert.AreEqual(a, b);
        Assert.AreEqual(PairingTranscript.ShortAuthenticationStringCharacters, a.Length);
        foreach (var character in a)
        {
            Assert.Contains(character, "abcdefghijklmnopqrstuvwxyz234567");
        }
    }

    [TestMethod]
    public void ShortAuthenticationString_ARelayBetweenTwoCeremonies_Differs()
    {
        // The attack: Mallory runs one ceremony with Alice and another with Bob,
        // and needs the string Alice sees to equal the string Bob sees. Both of
        // Mallory's ceremonies succeed; what fails is the humans' comparison.
        using var alice = Side.Create();
        using var bob = Side.Create();
        using var malloryToAlice = Side.Create();
        using var malloryToBob = Side.Create();

        var (aliceSees, _) = Ceremony(alice, malloryToAlice);
        var (_, bobSees) = Ceremony(malloryToBob, bob);

        Assert.AreNotEqual(aliceSees, bobSees);
    }

    [TestMethod]
    public void ShortAuthenticationString_EitherIdentitySubstituted_Changes()
    {
        using var offerer = Side.Create();
        using var responder = Side.Create();
        using var impostor = PeerKeypair.Generate();

        var honest = PairingTranscript.Build(offerer.Contribution, responder.Contribution, 1);
        var secret = offerer.Exchange.DeriveSharedSecret(responder.Exchange.PublicKey);

        var expected = PairingTranscript.ShortAuthenticationString(
            secret, offerer.Exchange.Nonce, responder.Exchange.Nonce, honest);

        // Same ephemeral keys, same nonces, same secret — only a long-lived
        // identity swapped. If this passed, the identity would not be pinned by
        // anything the humans check.
        foreach (var forged in new[]
        {
            PairingTranscript.Build(
                offerer.Contribution with { Identity = impostor.Identity }, responder.Contribution, 1),
            PairingTranscript.Build(
                offerer.Contribution, responder.Contribution with { Identity = impostor.Identity }, 1),
        })
        {
            Assert.AreNotEqual(
                expected,
                PairingTranscript.ShortAuthenticationString(
                    secret, offerer.Exchange.Nonce, responder.Exchange.Nonce, forged));
        }
    }

    [TestMethod]
    public void ShortAuthenticationString_ProtocolVersionChanged_Changes()
    {
        using var offerer = Side.Create();
        using var responder = Side.Create();

        var secret = offerer.Exchange.DeriveSharedSecret(responder.Exchange.PublicKey);

        // A downgrade that left the string untouched would be a downgrade the
        // humans could not see.
        Assert.AreNotEqual(
            PairingTranscript.ShortAuthenticationString(
                secret, offerer.Exchange.Nonce, responder.Exchange.Nonce,
                PairingTranscript.Build(offerer.Contribution, responder.Contribution, 1)),
            PairingTranscript.ShortAuthenticationString(
                secret, offerer.Exchange.Nonce, responder.Exchange.Nonce,
                PairingTranscript.Build(offerer.Contribution, responder.Contribution, 2)));
    }

    [TestMethod]
    public void ShortAuthenticationString_RolesSwapped_Changes()
    {
        using var one = Side.Create();
        using var two = Side.Create();

        var secret = one.Exchange.DeriveSharedSecret(two.Exchange.PublicKey);

        // Role order is fixed by the specification, so a transcript built with
        // the roles reversed is a different transcript. Both sides agreeing on
        // who offered is part of what they are confirming.
        Assert.AreNotEqual(
            PairingTranscript.ShortAuthenticationString(
                secret, one.Exchange.Nonce, two.Exchange.Nonce,
                PairingTranscript.Build(one.Contribution, two.Contribution, 1)),
            PairingTranscript.ShortAuthenticationString(
                secret, two.Exchange.Nonce, one.Exchange.Nonce,
                PairingTranscript.Build(two.Contribution, one.Contribution, 1)));
    }

    [TestMethod]
    public void PairingConfirmation_SignedByOnePeer_VerifiesOnlyAgainstThatSigner()
    {
        using var offerer = Side.Create();
        using var responder = Side.Create();

        var transcript = PairingTranscript.Build(offerer.Contribution, responder.Contribution, 1);
        var bytes = PairingTranscript.ConfirmationBytes(transcript);
        var signature = offerer.Keypair.Sign(bytes);

        Assert.IsTrue(offerer.Keypair.Identity.Verify(bytes, signature));
        Assert.IsFalse(responder.Keypair.Identity.Verify(bytes, signature));
    }

    [TestMethod]
    public void PairingConfirmation_OverADifferentTranscript_FailsToVerify()
    {
        using var offerer = Side.Create();
        using var responder = Side.Create();
        using var impostor = PeerKeypair.Generate();

        var honest = PairingTranscript.Build(offerer.Contribution, responder.Contribution, 1);
        var forged = PairingTranscript.Build(
            offerer.Contribution, responder.Contribution with { Identity = impostor.Identity }, 1);

        var signature = offerer.Keypair.Sign(PairingTranscript.ConfirmationBytes(honest));

        // Replaying a genuine confirmation into a ceremony it was not made for
        // is the cheapest way to fake approval, so it has to fail on the bytes.
        Assert.IsFalse(offerer.Keypair.Identity.Verify(PairingTranscript.ConfirmationBytes(forged), signature));
    }

    [TestMethod]
    public void PairingConfirmation_SharingATranscriptWithTheDerivation_UsesADistinctLabel()
    {
        using var offerer = Side.Create();
        using var responder = Side.Create();

        var transcript = PairingTranscript.Build(offerer.Contribution, responder.Contribution, 1);

        // Domain separation (00 §4): the bytes signed must not be the bare
        // transcript, or a signature could be replayed wherever the transcript
        // is used unsigned.
        Assert.AreNotEqual(transcript, PairingTranscript.ConfirmationBytes(transcript));
        Assert.IsTrue(PairingTranscript.ConfirmationBytes(transcript).Length > transcript.Length);
    }

    [TestMethod]
    public void DeriveSharedSecret_WhenTheContributionIsALowOrderPoint_ShouldThrow()
    {
        using var side = Side.Create();

        // All-zero is the canonical low-order point: agreeing with it fixes the
        // shared secret to a value the sender knows, which fixes the string the
        // humans compare.
        var refused = Assert.ThrowsExactly<ArgumentException>(
            () => side.Exchange.DeriveSharedSecret(new byte[PairingTranscript.ExchangeKeyLength]));

        Assert.Contains("low-order", refused.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void DeriveSharedSecret_WhenTheContributionIsTheWrongLength_ShouldThrow()
    {
        using var keypair = PeerKeypair.Generate();
        var good = new byte[PairingTranscript.ExchangeKeyLength];
        good[0] = 9;

        Assert.ThrowsExactly<ArgumentException>(() => PairingTranscript.Build(
            new PairingContribution(keypair.Identity, new byte[31], new byte[PairingTranscript.NonceLength]),
            new PairingContribution(keypair.Identity, good, new byte[PairingTranscript.NonceLength]),
            1));

        Assert.ThrowsExactly<ArgumentException>(() => PairingTranscript.Build(
            new PairingContribution(keypair.Identity, good, new byte[8]),
            new PairingContribution(keypair.Identity, good, new byte[PairingTranscript.NonceLength]),
            1));
    }
}

/// <summary>
/// Peer identity and its keypair (specification peer-protocol 01 §1;
/// ADR-0030 §1).
/// </summary>
[TestClass]
public sealed class PeerIdentityTests
{
    [TestMethod]
    public void PeerKeyPair_RestoredFromItsPrivateSeed_RoundTrips()
    {
        using var original = PeerKeypair.Generate();
        var seed = original.ExportPrivateKey();

        using var reloaded = PeerKeypair.FromPrivateKey(seed);

        Assert.AreEqual(original.Identity, reloaded.Identity);
        Assert.AreEqual(original.Identity.Fingerprint, reloaded.Identity.Fingerprint);

        // And it still signs as the same peer, which is what "the same identity"
        // has to mean for a grant pinned before a restart.
        var data = "durable across a restart"u8.ToArray();
        Assert.IsTrue(original.Identity.Verify(data, reloaded.Sign(data)));
    }

    [TestMethod]
    public void PeerKeyPair_GeneratedTwice_ProducesDifferentPeers()
    {
        using var one = PeerKeypair.Generate();
        using var two = PeerKeypair.Generate();

        Assert.AreNotEqual(one.Identity, two.Identity);
        Assert.AreNotEqual(one.Identity.Fingerprint, two.Identity.Fingerprint);
    }

    [TestMethod]
    public void PeerFingerprint_TheSameIdentity_IsStableAndRenderedBase32()
    {
        using var keypair = PeerKeypair.Generate();

        var fingerprint = keypair.Identity.Fingerprint;

        Assert.AreEqual(fingerprint, keypair.Identity.Fingerprint);
        Assert.AreEqual(26, fingerprint.Length);
        foreach (var character in fingerprint)
        {
            Assert.Contains(character, "abcdefghijklmnopqrstuvwxyz234567");
        }
    }

    [TestMethod]
    public void PeerIdentity_TwoIdentitiesWithTheSameKey_AreEqual()
    {
        using var keypair = PeerKeypair.Generate();

        var copied = PeerIdentity.FromPublicKey(keypair.Identity.PublicKey);

        // Equality is over the key, because the key is the identity. Anything
        // else here would be a second thing a peer could be identified by.
        Assert.AreEqual(keypair.Identity, copied);
        Assert.AreEqual(keypair.Identity.GetHashCode(), copied.GetHashCode());
    }

    [TestMethod]
    public void PeerIdentity_WhenTheKeyIsTheWrongLength_ShouldThrow()
    {
        Assert.ThrowsExactly<ArgumentException>(() => PeerIdentity.FromPublicKey(new byte[31]));
        Assert.ThrowsExactly<ArgumentException>(() => PeerIdentity.FromPublicKey(RandomNumberGenerator.GetBytes(64)));
    }

    [TestMethod]
    public void Verify_WhenTheSignatureIsTheWrongLength_ShouldReturnFalse()
    {
        using var keypair = PeerKeypair.Generate();
        var data = "x"u8.ToArray();

        Assert.IsFalse(keypair.Identity.Verify(data, new byte[63]));
        Assert.IsFalse(keypair.Identity.Verify(data, []));
    }
}
