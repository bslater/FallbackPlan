using System.Text.Json;
using FallbackPlan.Protocol;

namespace FallbackPlan.Protocol.Tests;

/// <summary>
/// The replica-claim exchange (specification peer-protocol 07 §5, 03 §3.2.1;
/// ADR-0046): each frame round-trips, malformed candidates are refused at
/// decode, and the proof is bound to the one session and the one destination
/// that issued it. FR-DR-002, FR-DR-003.
/// </summary>
/// <remarks>
/// The binding tests are the substance. A claim moves a replica's attribution,
/// so a proof that could be lifted out of its session — replayed by an
/// eavesdropper, or carried to a second destination holding the same
/// repository — would turn one captured exchange into a claim on every copy.
/// </remarks>
[TestClass]
public sealed class ReplicaClaimTests
{
    private static TMessage RoundTrip<TMessage>(
        IPeerMessage message, Func<System.Formats.Cbor.CborReader, TMessage> read)
    {
        var (_, body) = PeerFrame.Decode(PeerFrame.Encode(message));
        return read(body);
    }

    private static byte[] Bytes(byte fill, int length) => [.. Enumerable.Repeat(fill, length)];

    private static byte[] RepositoryId => Bytes(0x11, 16);

    private static byte[] Token => Bytes(0xD0, 16);

    private static byte[] Nonce => Bytes(0x77, 32);

    private static byte[] Transcript => Bytes(0x5C, 32);

    private static byte[] Identity => Bytes(0x33, 32);

    private static byte[] Seed => Bytes(0x42, 32);

    // ------------------------------------------------------------- the wire

    [TestMethod]
    public void Request_RoundTrips()
    {
        Assert.IsNotNull(RoundTrip(new ClaimRequest(), ClaimRequest.Read));
    }

    [TestMethod]
    public void Challenge_RoundTripsEveryCandidate()
    {
        var challenge = new ClaimChallenge(
        [
            new ClaimCandidate(RepositoryId, Token, Nonce),
            new ClaimCandidate(Bytes(0x22, 16), Bytes(0xE0, 16), Bytes(0x88, 32)),
        ]);

        var decoded = RoundTrip(challenge, ClaimChallenge.Read);

        Assert.HasCount(2, decoded.Candidates);
        CollectionAssert.AreEqual(RepositoryId, decoded.Candidates[0].RepositoryId.ToArray());
        CollectionAssert.AreEqual(Token, decoded.Candidates[0].ClaimToken.ToArray());
        CollectionAssert.AreEqual(Nonce, decoded.Candidates[0].Nonce.ToArray());
        CollectionAssert.AreEqual(Bytes(0x88, 32), decoded.Candidates[1].Nonce.ToArray());
    }

    [TestMethod]
    public void Challenge_WithNothingToClaim_IsAnEmptyArrayRatherThanARefusal()
    {
        // The ordinary answer to a peer that owns everything it could claim.
        // A refusal here would turn "nothing waiting" into an error the client
        // has to interpret.
        var decoded = RoundTrip(new ClaimChallenge([]), ClaimChallenge.Read);

        Assert.IsEmpty(decoded.Candidates);
    }

    [TestMethod]
    public void Challenge_ACandidateOfTheWrongWidth_IsRefused()
    {
        var frame = PeerFrame.Encode(new ClaimChallenge(
            [new ClaimCandidate(Bytes(0x11, 15), Token, Nonce)]));
        var (_, body) = PeerFrame.Decode(frame);

        Assert.ThrowsExactly<PeerProtocolException>(() => ClaimChallenge.Read(body));
    }

    [TestMethod]
    public void Proof_RoundTripsEveryAnswer()
    {
        var proof = new ClaimProof(
            [new ClaimAnswer(RepositoryId, Bytes(0x9A, 32), Bytes(0x7C, 64))]);

        var decoded = RoundTrip(proof, ClaimProof.Read);

        Assert.ContainsSingle(decoded.Answers);
        CollectionAssert.AreEqual(Bytes(0x9A, 32), decoded.Answers[0].ClaimPublicKey.ToArray());
        CollectionAssert.AreEqual(Bytes(0x7C, 64), decoded.Answers[0].Signature.ToArray());
    }

    [TestMethod]
    public void Proof_ASignatureOfTheWrongWidth_IsRefused()
    {
        var frame = PeerFrame.Encode(new ClaimProof(
            [new ClaimAnswer(RepositoryId, Bytes(0x9A, 32), Bytes(0x7C, 63))]));
        var (_, body) = PeerFrame.Decode(frame);

        Assert.ThrowsExactly<PeerProtocolException>(() => ClaimProof.Read(body));
    }

    [TestMethod]
    public void Result_RoundTripsTheSetIdsARecoveringHubNeeds()
    {
        var result = new ClaimResult(
            [new ClaimedReplica(RepositoryId, [Bytes(0xA1, 16), Bytes(0xA2, 16)])]);

        var decoded = RoundTrip(result, ClaimResult.Read);

        Assert.ContainsSingle(decoded.Claimed);
        Assert.HasCount(2, decoded.Claimed[0].BackupSetIds);
        CollectionAssert.AreEqual(Bytes(0xA2, 16), decoded.Claimed[0].BackupSetIds[1].ToArray());
    }

    [TestMethod]
    public void Register_RoundTrips()
    {
        var register = new ClaimRegister(RepositoryId, Bytes(0x9A, 32));

        var decoded = RoundTrip(register, ClaimRegister.Read);

        CollectionAssert.AreEqual(RepositoryId, decoded.RepositoryId.ToArray());
        CollectionAssert.AreEqual(Bytes(0x9A, 32), decoded.ClaimPublicKey.ToArray());
    }

    [TestMethod]
    public void Register_APublicKeyOfTheWrongWidth_IsRefused()
    {
        var frame = PeerFrame.Encode(new ClaimRegister(RepositoryId, Bytes(0x9A, 31)));
        var (_, body) = PeerFrame.Decode(frame);

        Assert.ThrowsExactly<PeerProtocolException>(() => ClaimRegister.Read(body));
    }

    [TestMethod]
    [DataRow(PeerMessageType.ClaimRequest)]
    [DataRow(PeerMessageType.ClaimChallenge)]
    [DataRow(PeerMessageType.ClaimProof)]
    [DataRow(PeerMessageType.ClaimResult)]
    [DataRow(PeerMessageType.ClaimRegister)]
    public void EveryClaimFrame_IsAdmittedOnlyOnceTheSessionIsOpen(PeerMessageType type)
    {
        // A claim frame before Open is a stranger asking what a destination
        // holds. The state machine's default is to refuse, so a frame added to
        // the enum and not to 02 §2's Open list is silently unreachable — which
        // is a whole feature that never runs rather than a test that fails.
        Assert.IsTrue(PeerAuthenticator.Permits(PeerSessionState.Open, type));

        foreach (var state in Enum.GetValues<PeerSessionState>().Where(s => s != PeerSessionState.Open))
        {
            Assert.IsFalse(
                PeerAuthenticator.Permits(state, type),
                $"{type} must not be admitted while {state}");
        }
    }

    // ---------------------------------------------------------- the binding

    [TestMethod]
    public void Proof_SignedAndVerified_RoundTripsUnderItsOwnPublicKey()
    {
        var message = ReplicaClaimProof.Message(RepositoryId, Token, Nonce, Transcript, Identity);
        var signature = ReplicaClaimProof.Sign(Seed, message);

        Assert.IsTrue(ReplicaClaimProof.Verify(ReplicaClaimProof.PublicKeyOf(Seed), message, signature));
    }

    [TestMethod]
    public void Proof_CarriedToAnotherDestination_DoesNotVerify()
    {
        // Another destination minted another token, so the message it rebuilds
        // differs — and it rebuilds from its OWN copy, never from anything the
        // claimant sent. This is what the per-destination token buys.
        var here = ReplicaClaimProof.Message(RepositoryId, Token, Nonce, Transcript, Identity);
        var elsewhere = ReplicaClaimProof.Message(
            RepositoryId, Bytes(0xE0, 16), Nonce, Transcript, Identity);

        var signature = ReplicaClaimProof.Sign(Seed, here);
        var publicKey = ReplicaClaimProof.PublicKeyOf(Seed);

        Assert.IsTrue(ReplicaClaimProof.Verify(publicKey, here, signature));
        Assert.IsFalse(ReplicaClaimProof.Verify(publicKey, elsewhere, signature));
    }

    [TestMethod]
    public void Proof_ReplayedIntoAnotherSession_DoesNotVerify()
    {
        // A fresh nonce and a different transcript both break it: an
        // eavesdropper cannot lift a proof out of the connection that carried
        // it.
        var original = ReplicaClaimProof.Message(RepositoryId, Token, Nonce, Transcript, Identity);
        var signature = ReplicaClaimProof.Sign(Seed, original);
        var publicKey = ReplicaClaimProof.PublicKeyOf(Seed);

        Assert.IsFalse(ReplicaClaimProof.Verify(
            publicKey,
            ReplicaClaimProof.Message(RepositoryId, Token, Bytes(0x99, 32), Transcript, Identity),
            signature),
            "a fresh nonce must break a replayed proof");

        Assert.IsFalse(ReplicaClaimProof.Verify(
            publicKey,
            ReplicaClaimProof.Message(RepositoryId, Token, Nonce, Bytes(0x6D, 32), Identity),
            signature),
            "a different session transcript must break a replayed proof");
    }

    [TestMethod]
    public void Proof_RedirectedToAnotherClaimingIdentity_DoesNotVerify()
    {
        // The identity key names the identity the attribution would move TO, so
        // a third party cannot present someone else's proof as its own.
        var original = ReplicaClaimProof.Message(RepositoryId, Token, Nonce, Transcript, Identity);
        var signature = ReplicaClaimProof.Sign(Seed, original);

        Assert.IsFalse(ReplicaClaimProof.Verify(
            ReplicaClaimProof.PublicKeyOf(Seed),
            ReplicaClaimProof.Message(RepositoryId, Token, Nonce, Transcript, Bytes(0x44, 32)),
            signature));
    }

    [TestMethod]
    public void Verify_AMalformedKeyOrSignature_IsFalseRatherThanThrowing()
    {
        // Which check caught it is not a claimant's business (07 §5.7), so the
        // verifier answers rather than raising.
        var message = ReplicaClaimProof.Message(RepositoryId, Token, Nonce, Transcript, Identity);

        Assert.IsFalse(ReplicaClaimProof.Verify(Bytes(0x9A, 31), message, Bytes(0x7C, 64)));
        Assert.IsFalse(ReplicaClaimProof.Verify(Bytes(0x9A, 32), message, Bytes(0x7C, 63)));
    }

    [TestMethod]
    public void Message_AndItsSignature_MatchTheCommittedVector()
    {
        // The oracle is the specification's own Python generator, which builds
        // this byte string from the field order of 07 §5.6 and signs it with an
        // independent Ed25519. A slip in either would otherwise surface only
        // when two implementations disagreed over a real claim.
        using var vectors = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "disaster-recovery.json")));
        var claim = vectors.RootElement.GetProperty("claim_key");
        var proof = claim.GetProperty("proof");

        byte[] Hex(JsonElement parent, string name) =>
            Convert.FromHexString(parent.GetProperty(name).GetString()!);

        var message = ReplicaClaimProof.Message(
            Hex(proof, "repository_id"),
            Hex(claim.GetProperty("inputs"), "claim_token"),
            Hex(proof, "nonce"),
            Hex(proof, "transcript_hash"),
            Hex(proof, "claimant_identity"));

        Assert.AreEqual(proof.GetProperty("message").GetString(), Convert.ToHexStringLower(message));

        var seed = Hex(claim.GetProperty("derived"), "claim_seed");
        Assert.AreEqual(
            proof.GetProperty("signature").GetString(),
            Convert.ToHexStringLower(ReplicaClaimProof.Sign(seed, message)));
        Assert.AreEqual(
            claim.GetProperty("derived").GetProperty("claim_public_key").GetString(),
            Convert.ToHexStringLower(ReplicaClaimProof.PublicKeyOf(seed)));
    }

    [TestMethod]
    public void Message_AComponentOfTheWrongWidth_IsRefusedByName()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => ReplicaClaimProof.Message(Bytes(0x11, 15), Token, Nonce, Transcript, Identity));
        Assert.ThrowsExactly<ArgumentException>(
            () => ReplicaClaimProof.Message(RepositoryId, Bytes(0xD0, 15), Nonce, Transcript, Identity));
        Assert.ThrowsExactly<ArgumentException>(
            () => ReplicaClaimProof.Message(RepositoryId, Token, Bytes(0x77, 31), Transcript, Identity));
    }
}
