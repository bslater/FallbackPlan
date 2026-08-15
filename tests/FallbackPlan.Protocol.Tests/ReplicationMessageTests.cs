using FallbackPlan.Protocol;

namespace FallbackPlan.Protocol.Tests;

/// <summary>
/// The replication messages (specification peer-protocol 03 §3): each frame
/// round-trips through the codec, the state machine admits them only when the
/// session is Open, and malformed or oversized bodies are refused.
/// </summary>
[TestClass]
public sealed class ReplicationMessageTests
{
    private static TMessage RoundTrip<TMessage>(IPeerMessage message, Func<System.Formats.Cbor.CborReader, TMessage> read)
    {
        var (_, body) = PeerFrame.Decode(PeerFrame.Encode(message));
        return read(body);
    }

    [TestMethod]
    public void Offer_RoundTrips()
    {
        var offer = new ReplicationOffer(new byte[16], 5, "all");
        Assert.AreEqual(offer, RoundTrip(offer, ReplicationOffer.Read));
    }

    [TestMethod]
    public void Inventory_WithKeysAndEmpty_RoundTrips()
    {
        var page = new ReplicationInventory(["blobs/data/aaaa/one", "snapshots/x/y/z"], More: true);
        Assert.AreEqual(page, RoundTrip(page, ReplicationInventory.Read));

        var empty = new ReplicationInventory([], More: false);
        var read = RoundTrip(empty, ReplicationInventory.Read);
        Assert.AreEqual(0, read.Keys.Count);
        Assert.IsFalse(read.More);
    }

    [TestMethod]
    public void Inventory_Headroom_RoundTripsAndStaysAbsentWhenThereIsNoCeiling()
    {
        // Key 3 is additive (05 §1): a destination under no quota omits it,
        // and a destination speaking an older build omits it too. Both must
        // read back as "not told" — never as zero, which would say the
        // opposite of what is true.
        var bounded = new ReplicationInventory(["blobs/data/aaaa/one"], More: false, Headroom: 4_096);
        Assert.AreEqual(bounded, RoundTrip(bounded, ReplicationInventory.Read));
        Assert.AreEqual(4_096UL, RoundTrip(bounded, ReplicationInventory.Read).Headroom);

        var unbounded = new ReplicationInventory(["blobs/data/aaaa/one"], More: false);
        Assert.IsNull(RoundTrip(unbounded, ReplicationInventory.Read).Headroom);

        // A destination with a quota and nothing left says zero, which is a
        // different statement from saying nothing.
        Assert.AreEqual(
            0UL,
            RoundTrip(new ReplicationInventory([], More: false, Headroom: 0), ReplicationInventory.Read).Headroom);
    }

    [TestMethod]
    public void RetentionOfferAndAck_RoundTrip()
    {
        // The commander's drop-list and the spoke's answer (peer-protocol 06
        // §4): pinned before either side speaks it in production.
        var offer = new RetentionOffer(
            new byte[16], ["snapshots/x/y/z", "blobs/data/aaaa/one"], More: true);
        Assert.AreEqual(offer, RoundTrip(offer, RetentionOffer.Read));

        var ack = new RetentionAck(7);
        Assert.AreEqual(ack, RoundTrip(ack, RetentionAck.Read));
    }

    [TestMethod]
    public void RetentionOffer_RepositoryIdOfTheWrongWidth_IsRefused()
    {
        var wrong = new RetentionOffer(new byte[15], ["snapshots/x"], More: false);

        Assert.ThrowsExactly<PeerProtocolException>(() => PeerFrame.Encode(wrong));
    }

    [TestMethod]
    public void ObjectHeaderAndChunk_RoundTrip()
    {
        var header = new ReplicationObject("blobs/data/aaaa/one", 4096);
        Assert.AreEqual(header, RoundTrip(header, ReplicationObject.Read));

        var chunk = new ReplicationChunk(1024, new byte[] { 1, 2, 3, 4 });
        Assert.AreEqual(chunk, RoundTrip(chunk, ReplicationChunk.Read));
    }

    [TestMethod]
    public void CompleteAndAck_RoundTrip()
    {
        Assert.AreEqual(new ReplicationComplete(42), RoundTrip(new ReplicationComplete(42), ReplicationComplete.Read));
        Assert.AreEqual(new ReplicationAck(41), RoundTrip(new ReplicationAck(41), ReplicationAck.Read));
    }

    [TestMethod]
    public void ReplicationTypes_ArePermittedOnlyWhenOpen()
    {
        foreach (var type in new[]
        {
            PeerMessageType.ReplicationOffer, PeerMessageType.ReplicationInventory,
            PeerMessageType.ReplicationObject, PeerMessageType.ReplicationChunk,
            PeerMessageType.ReplicationComplete, PeerMessageType.ReplicationAck,
        })
        {
            Assert.IsTrue(PeerAuthenticator.Permits(PeerSessionState.Open, type), $"{type} should be permitted when Open");
            Assert.IsFalse(
                PeerAuthenticator.Permits(PeerSessionState.Authenticated, type),
                $"{type} must not be permitted before Open");
            Assert.IsFalse(
                PeerAuthenticator.Permits(PeerSessionState.Encrypted, type),
                $"{type} must not be permitted in Encrypted");
        }
    }

    [TestMethod]
    public void Offer_WithAWrongLengthRepositoryId_IsRefused()
    {
        var offer = new ReplicationOffer(new byte[8], 5, "all");
        var body = PeerFrame.Decode(PeerFrame.Encode(offer)).Body;

        var refusal = Assert.ThrowsExactly<PeerProtocolException>(() => ReplicationOffer.Read(body));
        Assert.AreEqual(PeerRefusalReason.Malformed, refusal.Reason);
    }

    [TestMethod]
    public void Chunk_OverTheByteLimit_IsRefusedOnWrite()
    {
        var chunk = new ReplicationChunk(0, new byte[ReplicationChunk.MaximumBytes + 1]);

        var refusal = Assert.ThrowsExactly<PeerProtocolException>(() => PeerFrame.Encode(chunk));
        Assert.AreEqual(PeerRefusalReason.Malformed, refusal.Reason);
    }

    [TestMethod]
    public void Inventory_OverTheKeyLimit_IsRefusedOnWrite()
    {
        var page = new ReplicationInventory(
            Enumerable.Range(0, ReplicationInventory.MaximumKeys + 1).Select(i => $"k{i}").ToList(), More: false);

        var refusal = Assert.ThrowsExactly<PeerProtocolException>(() => PeerFrame.Encode(page));
        Assert.AreEqual(PeerRefusalReason.Malformed, refusal.Reason);
    }

    [TestMethod]
    public void VerificationChallengeAndProof_RoundTrip()
    {
        // The verifier's question and both possible answers (peer-protocol
        // 04 §4): pinned before either side speaks them in production.
        var challenge = new VerificationChallenge(
            new byte[16], "blobs/data/aa/one", Offset: 4096, Length: 512,
            new byte[VerificationChallenge.NonceLength], new byte[VerificationChallenge.ChallengeKeyLength]);
        Assert.AreEqual(challenge, RoundTrip(challenge, VerificationChallenge.Read));

        var held = new VerificationProof(Held: true, new byte[VerificationProof.ProofLength]);
        Assert.AreEqual(held, RoundTrip(held, VerificationProof.Read));

        var cannotProve = new VerificationProof(Held: false, ReadOnlyMemory<byte>.Empty);
        Assert.AreEqual(cannotProve, RoundTrip(cannotProve, VerificationProof.Read));
    }

    [TestMethod]
    public void VerificationChallenge_ViolatingAWidthOrBound_IsRefusedOnWrite()
    {
        var nonce = new byte[VerificationChallenge.NonceLength];
        var key = new byte[VerificationChallenge.ChallengeKeyLength];

        foreach (var wrong in new VerificationChallenge[]
        {
            new(new byte[15], "blobs/data/aa/one", 0, 1, nonce, key),
            new(new byte[16], "", 0, 1, nonce, key),
            new(new byte[16], "blobs/data/aa/one", 0, 0, nonce, key),
            new(new byte[16], "blobs/data/aa/one", 0, VerificationChallenge.MaximumLength + 1, nonce, key),
            new(new byte[16], "blobs/data/aa/one", 0, 1, new byte[15], key),
            new(new byte[16], "blobs/data/aa/one", 0, 1, nonce, new byte[31]),
        })
        {
            var refusal = Assert.ThrowsExactly<PeerProtocolException>(() => PeerFrame.Encode(wrong));
            Assert.AreEqual(PeerRefusalReason.Malformed, refusal.Reason);
        }
    }

    [TestMethod]
    public void VerificationProof_WhoseStatusAndPayloadDisagree_IsRefused()
    {
        // Status 0 promises a 32-byte MAC; status 1 promises nothing. A body
        // saying both, or neither, is malformed (04 §4.2).
        var shortProof = new VerificationProof(Held: true, new byte[16]);
        var writeRefusal = Assert.ThrowsExactly<PeerProtocolException>(() => PeerFrame.Encode(shortProof));
        Assert.AreEqual(PeerRefusalReason.Malformed, writeRefusal.Reason);

        // A held=false body carrying a proof anyway must be refused on read;
        // the honest writer cannot produce one, so it is hand-built here.
        var writer = new System.Formats.Cbor.CborWriter(System.Formats.Cbor.CborConformanceMode.Canonical);
        writer.WriteStartMap(2);
        writer.WriteInt32(1);
        writer.WriteUInt32(1);
        writer.WriteInt32(2);
        writer.WriteByteString(new byte[VerificationProof.ProofLength]);
        writer.WriteEndMap();
        var reader = new System.Formats.Cbor.CborReader(
            writer.Encode(), System.Formats.Cbor.CborConformanceMode.Canonical);
        var readRefusal = Assert.ThrowsExactly<PeerProtocolException>(() => VerificationProof.Read(reader));
        Assert.AreEqual(PeerRefusalReason.Malformed, readRefusal.Reason);
    }

    [TestMethod]
    public void VerificationTypes_ArePermittedOnlyWhenOpen()
    {
        foreach (var type in new[] { PeerMessageType.VerificationChallenge, PeerMessageType.VerificationProof })
        {
            Assert.IsTrue(PeerAuthenticator.Permits(PeerSessionState.Open, type), $"{type} should be permitted when Open");
            Assert.IsFalse(
                PeerAuthenticator.Permits(PeerSessionState.Authenticated, type),
                $"{type} must not be permitted before Open");
        }
    }

    [TestMethod]
    public void RangeChallenge_IsDeterministicAndFreshnessSensitive()
    {
        var challengeKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var nonce = Enumerable.Range(0, 16).Select(i => (byte)(i + 100)).ToArray();
        var bytes = "the exact stored bytes"u8.ToArray();

        var proof = RangeChallenge.Compute(challengeKey, nonce, "blobs/data/aa/one", 7, (uint)bytes.Length, bytes);
        var again = RangeChallenge.Compute(challengeKey, nonce, "blobs/data/aa/one", 7, (uint)bytes.Length, bytes);
        Assert.HasCount(32, proof);
        CollectionAssert.AreEqual(proof, again, "the same challenge over the same bytes must reproduce");

        var otherNonce = (byte[])nonce.Clone();
        otherNonce[0] ^= 1;
        CollectionAssert.AreNotEqual(
            proof,
            RangeChallenge.Compute(challengeKey, otherNonce, "blobs/data/aa/one", 7, (uint)bytes.Length, bytes),
            "a fresh nonce must produce a fresh proof — nothing precomputed survives it");

        var tampered = (byte[])bytes.Clone();
        tampered[^1] ^= 1;
        CollectionAssert.AreNotEqual(
            proof,
            RangeChallenge.Compute(challengeKey, nonce, "blobs/data/aa/one", 7, (uint)tampered.Length, tampered),
            "one flipped byte in the range must change the proof");

        Assert.ThrowsExactly<ArgumentException>(() =>
            RangeChallenge.Compute(challengeKey, nonce, "blobs/data/aa/one", 7, (uint)bytes.Length + 1, bytes));
    }
}
