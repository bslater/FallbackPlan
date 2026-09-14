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
    public void Offer_WithAReclaimPublicKey_RoundTripsIt()
    {
        // Key 4 (ADR-0055 §5): what a keyless destination checks a deletion
        // instruction against, published on the offer that first attributes
        // the repository to this peer.
        var published = new byte[ReplicationOffer.ReclaimPublicKeyLength];
        published.AsSpan().Fill(0xA7);

        var offer = new ReplicationOffer(new byte[16], 5, "all", published);
        var read = RoundTrip(offer, ReplicationOffer.Read);

        Assert.IsTrue(read.ReclaimPublicKey.Span.SequenceEqual(published));
        Assert.AreEqual(4, offer.BodyEntryCount);
    }

    [TestMethod]
    public void Offer_WithoutOne_SaysThreeEntriesAndReadsEmpty()
    {
        // A source with no key to publish — an older build, or a write-only
        // set provisioned before the decision — writes the offer it always
        // wrote, and a reader that predates key 4 skips it like any other
        // unknown key.
        var offer = new ReplicationOffer(new byte[16], 5, "all");

        Assert.AreEqual(3, offer.BodyEntryCount);
        Assert.IsTrue(RoundTrip(offer, ReplicationOffer.Read).ReclaimPublicKey.IsEmpty);
    }

    [TestMethod]
    public void Offer_AReclaimKeyOfTheWrongWidth_IsMalformedRatherThanIgnored()
    {
        // Dropping it quietly would leave a destination accepting unsigned
        // deletion instructions while believing it holds a key to check them.
        var offer = new ReplicationOffer(new byte[16], 5, "all", new byte[16]);

        Assert.ThrowsExactly<PeerProtocolException>(() => RoundTrip(offer, ReplicationOffer.Read));
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

    [TestMethod]
    public void RetentionOffer_WithASignature_RoundTripsIt()
    {
        var signature = new byte[RetentionOffer.SignatureLength];
        signature.AsSpan().Fill(0x5C);

        var page = new RetentionOffer(new byte[16], ["blobs/data/aa/one"], More: false, signature);
        var read = RoundTrip(page, RetentionOffer.Read);

        Assert.IsTrue(read.Signature.Span.SequenceEqual(signature));
        Assert.AreEqual(4, page.BodyEntryCount);
    }

    [TestMethod]
    public void RetentionOffer_WithoutOne_StaysTheBodyItAlwaysWas()
    {
        var page = new RetentionOffer(new byte[16], ["blobs/data/aa/one"], More: false);

        Assert.AreEqual(3, page.BodyEntryCount);
        Assert.IsTrue(RoundTrip(page, RetentionOffer.Read).Signature.IsEmpty);
    }

    [TestMethod]
    public void RetentionOffer_ASignatureOfTheWrongWidth_IsMalformedRatherThanIgnored()
    {
        // Dropping it quietly would fall back to accepting the instruction
        // unsigned, which is the check the feature exists to make.
        var page = new RetentionOffer(new byte[16], ["blobs/data/aa/one"], More: false, new byte[32]);

        Assert.ThrowsExactly<PeerProtocolException>(() => RoundTrip(page, RetentionOffer.Read));
    }

    [TestMethod]
    public void RetentionOffer_TheSignedBytes_SeparateKeysThatConcatenateTheSame()
    {
        // Length-prefixed, not delimited. Two different drop-lists whose
        // concatenations coincide must not produce identical signed bytes, or
        // one page's signature would authorise the other's deletions.
        var first = new RetentionOffer(new byte[16], ["blobs/aa", "bb"], More: false);
        var second = new RetentionOffer(new byte[16], ["blobs/aabb"], More: false);

        Assert.IsFalse(first.EncodeForSigning().AsSpan().SequenceEqual(second.EncodeForSigning()));
    }

    [TestMethod]
    public void RetentionOffer_TheSignedBytes_CoverTheRepositoryAndTheContinuation()
    {
        var keys = new[] { "blobs/data/aa/one" };
        var baseline = new RetentionOffer(new byte[16], keys, More: false).EncodeForSigning();

        var otherRepository = new byte[16];
        otherRepository[0] = 9;
        Assert.IsFalse(
            baseline.AsSpan().SequenceEqual(
                new RetentionOffer(otherRepository, keys, More: false).EncodeForSigning()),
            "a page signed for one repository must not authorise deletions in another");

        Assert.IsFalse(
            baseline.AsSpan().SequenceEqual(new RetentionOffer(new byte[16], keys, More: true).EncodeForSigning()),
            "the continuation flag is part of the instruction and must be signed with it");
    }
}
