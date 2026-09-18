using FallbackPlan.Protocol;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Protocol.Tests;

/// <summary>
/// The replication messages (specification peer-protocol 03 §3): each frame
/// round-trips through the codec, the state machine admits them only when the
/// session is Open, and malformed or oversized bodies are refused.
/// <para>
/// Establishes part of FR-GC-008 (ADR-0055) on the wire: the offer carries
/// the reclaim public key a keyless destination checks against, a retention
/// page carries the signature under it, the signed bytes are length-prefixed
/// so two drop-lists cannot collide, and a key or signature of the wrong
/// width is malformed rather than quietly dropped.
/// </para>
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
        Assert.IsTrue(read.ClaimPublicKey.IsEmpty, "a source may publish one key and not the other");
        Assert.AreEqual(4, offer.BodyEntryCount);
    }

    [TestMethod]
    public void Offer_WithNeitherPublishedKey_SaysThreeEntriesAndReadsEmpty()
    {
        // A source with no key to publish — an older build, or a write-only
        // set provisioned before the decision — writes the offer it always
        // wrote, and a reader that predates key 4 skips it like any other
        // unknown key.
        var offer = new ReplicationOffer(new byte[16], 5, "all");

        Assert.AreEqual(3, offer.BodyEntryCount);
        var read = RoundTrip(offer, ReplicationOffer.Read);
        Assert.IsTrue(read.ReclaimPublicKey.IsEmpty);
        Assert.IsTrue(read.ClaimPublicKey.IsEmpty);
    }

    [TestMethod]
    public void Offer_WithAClaimPublicKey_RoundTripsItBesideTheReclaimOne()
    {
        // Key 5 (ADR-0053 §1): what a keyless destination checks a CLAIM
        // against — a rebuilt machine proving the replica is its own. Carried
        // on the same offer as key 4 and recorded by the same attribution,
        // because both answer the same question for a party that holds no
        // repository keys of its own.
        var reclaim = new byte[ReplicationOffer.ReclaimPublicKeyLength];
        reclaim.AsSpan().Fill(0xA7);
        var claim = new byte[ReplicationOffer.ClaimPublicKeyLength];
        claim.AsSpan().Fill(0xB3);

        var offer = new ReplicationOffer(new byte[16], 5, "all", reclaim, claim);
        var read = RoundTrip(offer, ReplicationOffer.Read);

        Assert.IsTrue(read.ReclaimPublicKey.Span.SequenceEqual(reclaim));
        Assert.IsTrue(read.ClaimPublicKey.Span.SequenceEqual(claim));
        Assert.AreEqual(5, offer.BodyEntryCount);
    }

    [TestMethod]
    public void Offer_AClaimKeyOfTheWrongWidth_IsMalformedRatherThanIgnored()
    {
        // Same rule as key 4's, for the same reason: a destination that
        // dropped a malformed claim key would go on believing it had one to
        // check against, and would then refuse the owner's own claim.
        var reclaim = new byte[ReplicationOffer.ReclaimPublicKeyLength];
        var offer = new ReplicationOffer(new byte[16], 5, "all", reclaim, new byte[16]);

        Assert.ThrowsExactly<PeerProtocolException>(() => RoundTrip(offer, ReplicationOffer.Read));
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
    public void ClaimOpen_CarriesNothing_AndSkipsWhatALaterVersionAdds()
    {
        // A claimant that has lost everything has nothing to say yet: no
        // repository id, no salt, no key. The open is the question "what
        // should I derive against?", and its body is empty by design.
        var open = new ReplicationClaimOpen();
        Assert.AreEqual(0, open.BodyEntryCount);
        _ = RoundTrip(open, ReplicationClaimOpen.Read);

        var writer = new System.Formats.Cbor.CborWriter(System.Formats.Cbor.CborConformanceMode.Canonical);
        writer.WriteStartMap(1);
        writer.WriteInt32(7);
        writer.WriteTextString("something a later version says");
        writer.WriteEndMap();
        _ = ReplicationClaimOpen.Read(new System.Formats.Cbor.CborReader(writer.Encode()));
    }

    [TestMethod]
    public void ClaimParameters_RoundTripTheSaltsAndCosts_SortedBySalt()
    {
        // Four parallel arrays and no room for anything else: a salt and
        // three public Argon2id costs per claimable derivation. The order is
        // the salts' own, so the answer says nothing about when each
        // attribution was recorded.
        var first = Enumerable.Repeat((byte)0x10, ReplicationClaimParameters.SaltLength).ToArray();
        var second = Enumerable.Repeat((byte)0x20, ReplicationClaimParameters.SaltLength).ToArray();
        var parameters = new ReplicationClaimParameters(
            [first, second], [65_536, 262_144], [3, 4], [4, 1]);

        var read = RoundTrip(parameters, ReplicationClaimParameters.Read);

        Assert.AreEqual(parameters, read);
        Assert.AreEqual(4, parameters.BodyEntryCount);
        Assert.IsTrue(read.Salts[0].Span.SequenceEqual(first));
        Assert.AreEqual(262_144u, read.MemoryKiB[1]);
        Assert.AreEqual(4u, read.Iterations[1]);
        Assert.AreEqual(1, read.Parallelism[1]);
    }

    [TestMethod]
    public void ClaimParameters_Empty_RoundTrips()
    {
        // "Nothing here is claimable" is an answer, not a refusal: it is what
        // a destination says to a paired peer whose replicas were all
        // attributed before the claim key existed. The claimant reads it and
        // stops, rather than being told a key it never sent was wrong.
        var read = RoundTrip(new ReplicationClaimParameters([], [], [], []), ReplicationClaimParameters.Read);

        Assert.IsEmpty(read.Salts);
    }

    [TestMethod]
    public void ClaimParameters_UnsortedOrRepeatedSalts_AreMalformed()
    {
        var low = Enumerable.Repeat((byte)0x10, ReplicationClaimParameters.SaltLength).ToArray();
        var high = Enumerable.Repeat((byte)0x20, ReplicationClaimParameters.SaltLength).ToArray();

        Assert.ThrowsExactly<PeerProtocolException>(() => RoundTrip(
            new ReplicationClaimParameters([high, low], [1, 1], [1, 1], [1, 1]), ReplicationClaimParameters.Read));
        Assert.ThrowsExactly<PeerProtocolException>(() => RoundTrip(
            new ReplicationClaimParameters([low, low], [1, 1], [1, 1], [1, 1]), ReplicationClaimParameters.Read));
    }

    [TestMethod]
    public void ClaimParameters_UnequalArraysOrAWrongSaltWidth_AreMalformed()
    {
        // Pairing a salt with somebody else's costs is not a derivation.
        var salt = new byte[ReplicationClaimParameters.SaltLength];

        Assert.ThrowsExactly<PeerProtocolException>(() => RoundTrip(
            new ReplicationClaimParameters([salt], [1, 2], [1], [1]), ReplicationClaimParameters.Read));
        Assert.ThrowsExactly<PeerProtocolException>(() => RoundTrip(
            new ReplicationClaimParameters([new byte[8]], [1], [1], [1]), ReplicationClaimParameters.Read));
    }

    [TestMethod]
    public void ClaimParameters_CostsOutsideTheCaps_AreMalformedNotClamped()
    {
        // The claimant runs Argon2id on parameters the destination chose. A
        // destination naming a terabyte of memory is naming a denial of
        // service; a reader that clamped would run a derivation the
        // destination never recorded and then be refused for it.
        var salt = new byte[ReplicationClaimParameters.SaltLength];

        Assert.ThrowsExactly<PeerProtocolException>(() => RoundTrip(
            new ReplicationClaimParameters([salt], [ReplicationClaimParameters.MaximumMemoryKiB + 1], [1], [1]),
            ReplicationClaimParameters.Read));
        Assert.ThrowsExactly<PeerProtocolException>(() => RoundTrip(
            new ReplicationClaimParameters([salt], [1], [ReplicationClaimParameters.MaximumIterations + 1], [1]),
            ReplicationClaimParameters.Read));
        Assert.ThrowsExactly<PeerProtocolException>(() => RoundTrip(
            new ReplicationClaimParameters([salt], [1], [1], [ReplicationClaimParameters.MaximumParallelism + 1]),
            ReplicationClaimParameters.Read));
        Assert.ThrowsExactly<PeerProtocolException>(() => RoundTrip(
            new ReplicationClaimParameters([salt], [0], [1], [1]), ReplicationClaimParameters.Read));

        // And the caps themselves are inside.
        _ = RoundTrip(
            new ReplicationClaimParameters(
                [salt],
                [ReplicationClaimParameters.MaximumMemoryKiB],
                [ReplicationClaimParameters.MaximumIterations],
                [ReplicationClaimParameters.MaximumParallelism]),
            ReplicationClaimParameters.Read);
    }

    [TestMethod]
    public void ClaimParameters_MoreEntriesThanTheBound_AreMalformed()
    {
        var count = ReplicationClaimParameters.MaximumEntries + 1;
        var salts = Enumerable.Range(0, count)
            .Select(index => (ReadOnlyMemory<byte>)Enumerable.Repeat((byte)index, ReplicationClaimParameters.SaltLength).ToArray())
            .ToList();
        var ones = Enumerable.Repeat(1u, count).ToList();
        var parallelism = Enumerable.Repeat((byte)1, count).ToList();

        Assert.ThrowsExactly<PeerProtocolException>(() => RoundTrip(
            new ReplicationClaimParameters(salts, ones, ones, parallelism), ReplicationClaimParameters.Read));
    }

    [TestMethod]
    public void Claim_RoundTripsOneEntryPerServedDerivation()
    {
        // The claim names no repository, and that is the decision rather than
        // an omission. A machine that has lost everything holds a passphrase
        // and nothing else — so it could not name one if the message asked.
        // The claim public key IS the selector: the destination re-attributes
        // whatever it recorded that key against. One entry per derivation the
        // destination served, because the claimant cannot tell which salt is
        // its own.
        var first = Enumerable.Repeat((byte)0xC1, ReplicationClaim.ClaimPublicKeyLength).ToArray();
        var second = Enumerable.Repeat((byte)0xC2, ReplicationClaim.ClaimPublicKeyLength).ToArray();
        var signature = Enumerable.Repeat((byte)0xD2, ReplicationClaim.SignatureLength).ToArray();

        var claim = new ReplicationClaim([first, second], [signature, signature]);
        var read = RoundTrip(claim, ReplicationClaim.Read);

        Assert.HasCount(2, read.ClaimPublicKeys);
        Assert.IsTrue(read.ClaimPublicKeys[1].Span.SequenceEqual(second));
        Assert.IsTrue(read.Signatures[0].Span.SequenceEqual(signature));
        Assert.AreEqual(2, claim.BodyEntryCount);
    }

    [TestMethod]
    public void Claim_WithNoEntries_IsMalformed()
    {
        // A claim under no key is not a claim that matched nothing — it is
        // not a claim. Refused as a shape so it never reaches the ledger.
        Assert.ThrowsExactly<PeerProtocolException>(
            () => RoundTrip(new ReplicationClaim([], []), ReplicationClaim.Read));
    }

    [TestMethod]
    public void Claim_UnequalArraysOrTheWrongWidths_AreMalformed()
    {
        var key = new byte[ReplicationClaim.ClaimPublicKeyLength];
        var signature = new byte[ReplicationClaim.SignatureLength];

        Assert.ThrowsExactly<PeerProtocolException>(
            () => RoundTrip(new ReplicationClaim([key, key], [signature]), ReplicationClaim.Read));
        Assert.ThrowsExactly<PeerProtocolException>(
            () => RoundTrip(new ReplicationClaim([new byte[16]], [signature]), ReplicationClaim.Read));
        Assert.ThrowsExactly<PeerProtocolException>(
            () => RoundTrip(new ReplicationClaim([key], [new byte[16]]), ReplicationClaim.Read));
    }

    [TestMethod]
    public void Claim_MoreEntriesThanTheDestinationCouldHaveServed_IsMalformed()
    {
        var count = ReplicationClaim.MaximumEntries + 1;
        var keys = Enumerable.Repeat((ReadOnlyMemory<byte>)new byte[ReplicationClaim.ClaimPublicKeyLength], count).ToList();
        var signatures = Enumerable.Repeat((ReadOnlyMemory<byte>)new byte[ReplicationClaim.SignatureLength], count).ToList();

        Assert.ThrowsExactly<PeerProtocolException>(
            () => RoundTrip(new ReplicationClaim(keys, signatures), ReplicationClaim.Read));
    }

    [TestMethod]
    public void ClaimAccepted_CarriesWhatWasReattributed()
    {
        // The answer is what the claimant could not have known to ask for.
        // Having proved the key, it learns which repositories were re-pointed
        // to it — which is also the list it then opens for retrieval.
        var accepted = new ReplicationClaimAccepted([new byte[16], Enumerable.Repeat((byte)7, 16).ToArray()]);
        var read = RoundTrip(accepted, ReplicationClaimAccepted.Read);

        Assert.HasCount(2, read.RepositoryIds);
        Assert.IsTrue(read.RepositoryIds[1].Span.SequenceEqual(Enumerable.Repeat((byte)7, 16).ToArray()));
    }

    [TestMethod]
    public void ClaimAccepted_ARepositoryIdOfTheWrongWidth_IsMalformed()
    {
        Assert.ThrowsExactly<PeerProtocolException>(
            () => RoundTrip(new ReplicationClaimAccepted([new byte[8]]), ReplicationClaimAccepted.Read));
    }

    [TestMethod]
    public void ClaimSignedBytes_AreDomainSeparatedAndBoundToTheSession()
    {
        // The session identifier (02 §3.5) is what makes a captured claim
        // useless in a later connection — the same reason the retention
        // instruction is bound to it. A claim is worth more than a retention
        // page, because it re-points ownership rather than deleting one page.
        var fingerprint = "aa".PadRight(64, 'b');
        var session = Enumerable.Repeat((byte)0x30, SessionBinding.SessionIdLength).ToArray();
        var other = Enumerable.Repeat((byte)0x31, SessionBinding.SessionIdLength).ToArray();

        var bytes = ReplicationClaim.EncodeForSigning(session, fingerprint);

        Assert.IsFalse(
            bytes.AsSpan().SequenceEqual(ReplicationClaim.EncodeForSigning(other, fingerprint)),
            "a claim made in one session must not verify in another");
        Assert.IsFalse(
            bytes.AsSpan().SequenceEqual(
                ReplicationClaim.EncodeForSigning(session, "cc".PadRight(64, 'd'))),
            "a claim names the device it is asking to be attributed to");
        Assert.Contains(
            "fbp-peer-v1:replica-claim",
            System.Text.Encoding.UTF8.GetString(bytes.AsSpan(0, 25)),
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void ClaimTypes_ArePermittedOnlyWhenOpen()
    {
        foreach (var type in new[]
        {
            PeerMessageType.ReplicationClaimOpen,
            PeerMessageType.ReplicationClaimParameters,
            PeerMessageType.ReplicationClaim,
            PeerMessageType.ReplicationClaimAccepted,
        })
        {
            Assert.IsTrue(PeerAuthenticator.Permits(PeerSessionState.Open, type));
            Assert.IsFalse(PeerAuthenticator.Permits(PeerSessionState.Authenticated, type));
            Assert.IsFalse(PeerAuthenticator.Permits(PeerSessionState.Encrypted, type));
        }
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
        Assert.AreEqual(1, ack.BodyEntryCount, "an ack without a receipt is the one-entry body it always was");
        Assert.IsTrue(RoundTrip(ack, RetentionAck.Read).Receipt.IsEmpty);
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
    public void RetentionOffer_TheSignedBytes_BoundToASession_ExtendTheUnboundOnes()
    {
        // The bound encoding is the unbound one with the session's name after
        // it, fixed-length and last. That shape is what stops the two being
        // confusable: an unbound page is exactly the prefix, and a tail is
        // always 32 bytes or none, so no page signed for one session can be
        // read as a page signed for no session at all.
        var page = new RetentionOffer(new byte[16], ["blobs/data/aa/one"], More: false);
        var binding = new byte[SessionBinding.SessionIdLength];
        Array.Fill(binding, (byte)0x5a);

        var unbound = page.EncodeForSigning();
        var bound = page.EncodeForSigning(binding);

        Assert.HasCount(unbound.Length + binding.Length, bound);
        SequenceAssert.AreEqual(unbound, bound[..unbound.Length]);
        SequenceAssert.AreEqual(binding, bound[unbound.Length..]);
    }

    [TestMethod]
    public void RetentionOffer_TheSignedBytes_DifferPerSession()
    {
        // The property the binding exists for: the same drop-list authorised
        // in two sessions is two different signatures, so a recording of one
        // verifies against nothing in the other.
        var page = new RetentionOffer(new byte[16], ["blobs/data/aa/one"], More: false);
        var first = new byte[SessionBinding.SessionIdLength];
        var second = new byte[SessionBinding.SessionIdLength];
        Array.Fill(first, (byte)0x01);
        Array.Fill(second, (byte)0x02);

        Assert.IsFalse(page.EncodeForSigning(first).AsSpan().SequenceEqual(page.EncodeForSigning(second)));
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

    [TestMethod]
    public void Object_WithoutAResumeOffset_CarriesTheTwoKeysItAlwaysDid()
    {
        // The compatibility rule at the wire: a whole-object transfer looks
        // exactly as it did before resumption existed, so an older destination
        // — which skips keys it does not know — reads the same message
        // (ADR-0057).
        var header = new ReplicationObject("blobs/data/0000/object-a", 4096);

        Assert.AreEqual(2, header.BodyEntryCount);
        Assert.AreEqual(0UL, RoundTrip(header, ReplicationObject.Read).ResumeOffset);
    }

    [TestMethod]
    public void Object_WithAResumeOffset_RoundTripsIt()
    {
        var header = new ReplicationObject("blobs/data/0000/object-a", 4096, 1024);

        Assert.AreEqual(3, header.BodyEntryCount);
        Assert.AreEqual(header, RoundTrip(header, ReplicationObject.Read));
    }

    [TestMethod]
    public void Object_ResumingPastItsOwnLength_IsMalformed()
    {
        // A gap no later chunk can fill: the commit would publish bytes nobody
        // sent, which is the one outcome resumption must never produce.
        var header = new ReplicationObject("blobs/data/0000/object-a", 4096, 8192);

        Assert.ThrowsExactly<PeerProtocolException>(() => RoundTrip(header, ReplicationObject.Read));
    }

    [TestMethod]
    public void Partial_RoundTripsEveryEntry()
    {
        var partial = new ReplicationPartial(
            ["blobs/data/0000/object-a", "blobs/data/0001/object-b"],
            [1024, 2048],
            [new byte[32], Enumerable.Repeat((byte)7, 32).ToArray()]);

        var read = RoundTrip(partial, ReplicationPartial.Read);

        Assert.AreEqual(2, read.Keys.Count);
        Assert.AreEqual("blobs/data/0001/object-b", read.Keys[1]);
        Assert.AreEqual(2048UL, read.StagedLengths[1]);
        Assert.IsTrue(read.Digests[1].Span.SequenceEqual(Enumerable.Repeat((byte)7, 32).ToArray()));
    }

    [TestMethod]
    public void Partial_DeclaringNothing_IsStillAnAnswer()
    {
        // The destination sends this frame whether or not it part holds
        // anything: a source that expected a frame and did not get one would
        // read the next message in its place.
        var read = RoundTrip(new ReplicationPartial([], [], []), ReplicationPartial.Read);

        Assert.IsEmpty(read.Keys);
    }

    [TestMethod]
    public void Partial_WithArraysOfDifferentLengths_IsMalformed()
    {
        // Pairing a key with somebody else's digest is how a resume lands
        // bytes in the wrong object, so a declaration that does not line up is
        // refused rather than trimmed to the shortest arm.
        var partial = new ReplicationPartial(
            ["blobs/data/0000/object-a", "blobs/data/0001/object-b"], [1024], [new byte[32]]);

        Assert.ThrowsExactly<PeerProtocolException>(() => PeerFrame.Encode(partial));
    }

    [TestMethod]
    public void Partial_WithADigestOfTheWrongWidth_IsMalformed()
    {
        // Malformed rather than skipped, for the reason the reclaim key is:
        // the digest is the check this message exists for, and dropping a
        // broken one quietly would turn a verification into a shrug.
        var partial = new ReplicationPartial(["blobs/data/0000/object-a"], [1024], [new byte[16]]);

        var refusal = Assert.ThrowsExactly<PeerProtocolException>(
            () => RoundTrip(partial, ReplicationPartial.Read));
        Assert.Contains("16 bytes", refusal.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Partial_DeclaringMoreThanTheCap_IsMalformed()
    {
        var keys = Enumerable.Range(0, ReplicationPartial.MaximumEntries + 1)
            .Select(index => $"blobs/data/0000/object-{index}").ToArray();
        var partial = new ReplicationPartial(
            keys,
            [.. keys.Select(_ => 1024UL)],
            [.. keys.Select(_ => (ReadOnlyMemory<byte>)new byte[32])]);

        Assert.ThrowsExactly<PeerProtocolException>(() => RoundTrip(partial, ReplicationPartial.Read));
    }

    // ------------------------------------------------------- deletion receipt

    private static DeletionReceipt Receipt(int listed = 2, int pages = 1) => new(
        SessionId: Enumerable.Repeat((byte)0xAB, DeletionReceipt.SessionIdLength).ToArray(),
        RepositoryId: Enumerable.Repeat((byte)0x01, ReplicationOffer.RepositoryIdLength).ToArray(),
        CommanderPublicKey: Enumerable.Repeat((byte)0xC0, PeerIdentity.KeyLength).ToArray(),
        IssuedAtUnixMilliseconds: 1_722_600_000_123,
        FloorGenerations: 2,
        ReclaimPublicKey: Enumerable.Repeat((byte)0x5E, DeletionReceipt.ReclaimPublicKeyLength).ToArray(),
        PageDigests: [.. Enumerable.Range(0, pages).Select(page => (ReadOnlyMemory<byte>)Enumerable.Repeat((byte)page, DeletionReceipt.DigestLength).ToArray())],
        DeletedCount: (ulong)listed,
        Deleted: [.. Enumerable.Range(0, listed).Select(index => $"snapshots/aa/bb/deleted-{index}")],
        NotHeld: 1);

    [TestMethod]
    public void DeletionReceipt_EncodesAndParsesBackExactly()
    {
        var receipt = Receipt(listed: 3, pages: 2);

        var bytes = receipt.EncodeForSigning();
        var parsed = DeletionReceipt.Parse(bytes);

        Assert.AreEqual(receipt, parsed);
        Assert.IsTrue(bytes.AsSpan().StartsWith("fbp-peer-v1:deletion-receipt"u8), "the label leads, so no other signed statement can collide");
    }

    [TestMethod]
    public void DeletionReceipt_WithoutAReclaimKey_SaysSoRatherThanZeroFilling()
    {
        var receipt = Receipt() with { ReclaimPublicKey = ReadOnlyMemory<byte>.Empty };

        var parsed = DeletionReceipt.Parse(receipt.EncodeForSigning());

        Assert.IsTrue(parsed.ReclaimPublicKey.IsEmpty, "an instruction the spoke could check against no key is recorded as that, not as a key of zeros");
        Assert.AreEqual(receipt, parsed);
    }

    [TestMethod]
    public void DeletionReceipt_TrailingBytes_AreRefusedNotIgnored()
    {
        // A signed statement is exactly its bytes. A parser that stopped
        // early would let a receipt carry unsigned baggage that a reader
        // might display as though it were attested.
        var bytes = Receipt().EncodeForSigning();
        var longer = bytes.Concat(new byte[] { 0 }).ToArray();

        var refusal = Assert.ThrowsExactly<PeerProtocolException>(() => DeletionReceipt.Parse(longer));
        Assert.AreEqual(PeerRefusalReason.Malformed, refusal.Reason);
        Assert.ThrowsExactly<PeerProtocolException>(() => DeletionReceipt.Parse(bytes.AsSpan(0, bytes.Length - 1).ToArray()));
    }

    [TestMethod]
    public void DeletionReceipt_ListingMoreKeysThanTheCap_IsMalformed()
    {
        var receipt = Receipt(listed: DeletionReceipt.MaximumListedKeys + 1);

        var refusal = Assert.ThrowsExactly<PeerProtocolException>(() => receipt.EncodeForSigning());
        Assert.AreEqual(PeerRefusalReason.Malformed, refusal.Reason);
    }

    [TestMethod]
    public void DeletionReceipt_ADigestOfTheWrongWidth_IsMalformed()
    {
        var receipt = Receipt() with { PageDigests = [new byte[16]] };

        var refusal = Assert.ThrowsExactly<PeerProtocolException>(() => receipt.EncodeForSigning());
        Assert.AreEqual(PeerRefusalReason.Malformed, refusal.Reason);
    }

    private static ReplicationReceipt Replicated(int listed = 2, ulong committed = 2) => new(
        SessionId: Enumerable.Repeat((byte)0xAB, ReplicationReceipt.SessionIdLength).ToArray(),
        RepositoryId: Enumerable.Repeat((byte)0x01, ReplicationOffer.RepositoryIdLength).ToArray(),
        CommanderPublicKey: Enumerable.Repeat((byte)0xC0, PeerIdentity.KeyLength).ToArray(),
        IssuedAtUnixMilliseconds: 1_722_600_000_123,
        CommittedCount: committed,
        Committed: [.. Enumerable.Range(0, listed).Select(index => $"blobs/data/aa/committed-{index}")],
        HeldObjects: Math.Max(40, committed),
        HeldBytes: 123_456_789);

    [TestMethod]
    public void ReplicationReceipt_EncodesAndParsesBackExactly()
    {
        var receipt = Replicated(listed: 3, committed: 3);

        var bytes = receipt.EncodeForSigning();
        var parsed = ReplicationReceipt.Parse(bytes);

        Assert.AreEqual(receipt, parsed);
        Assert.IsTrue(bytes.AsSpan().StartsWith("fbp-peer-v1:replication-receipt"u8), "the label leads, so no other signed statement can collide");
        Assert.IsFalse(bytes.AsSpan().StartsWith("fbp-peer-v1:deletion-receipt"u8));
    }

    [TestMethod]
    public void ReplicationReceipt_CountsMoreThanItLists_WhenTheSessionOutranTheCap()
    {
        // Beyond the cap the count carries the rest: the listing is for a
        // reader, the count is the statement.
        var receipt = Replicated(listed: 2, committed: 10_000);

        Assert.AreEqual(receipt, ReplicationReceipt.Parse(receipt.EncodeForSigning()));
    }

    [TestMethod]
    public void ReplicationReceipt_TrailingBytes_AreRefusedNotIgnored()
    {
        var bytes = Replicated().EncodeForSigning();
        var padded = new byte[bytes.Length + 1];
        bytes.CopyTo(padded, 0);

        var refusal = Assert.ThrowsExactly<PeerProtocolException>(() => ReplicationReceipt.Parse(padded));

        Assert.AreEqual(PeerRefusalReason.Malformed, refusal.Reason);
    }

    [TestMethod]
    public void ReplicationReceipt_ListingMoreKeysThanItCounts_IsMalformed()
    {
        var overstated = Replicated(listed: 3, committed: 2);

        var refusal = Assert.ThrowsExactly<PeerProtocolException>(() => overstated.EncodeForSigning());

        Assert.AreEqual(PeerRefusalReason.Malformed, refusal.Reason);
    }

    [TestMethod]
    public void ReplicationReceipt_ListingMoreKeysThanTheCap_IsMalformed()
    {
        var tooMany = Replicated(listed: ReplicationReceipt.MaximumListedKeys + 1, committed: 10_000);

        var refusal = Assert.ThrowsExactly<PeerProtocolException>(() => tooMany.EncodeForSigning());

        Assert.AreEqual(PeerRefusalReason.Malformed, refusal.Reason);
    }

    [TestMethod]
    public void ReplicationAck_WithoutAReceipt_IsOneEntryAsBefore()
    {
        var ack = new ReplicationAck(41);

        Assert.AreEqual(1, ack.BodyEntryCount);
        var read = RoundTrip(ack, ReplicationAck.Read);
        Assert.AreEqual(41UL, read.Count);
        Assert.IsTrue(read.Receipt.IsEmpty);
        Assert.IsTrue(read.Signature.IsEmpty);
    }

    [TestMethod]
    public void ReplicationAck_CarriesAReceiptAndItsSignature()
    {
        var receipt = Replicated();
        var signed = receipt.EncodeForSigning();
        var signature = Enumerable.Repeat((byte)9, ReplicationReceipt.SignatureLength).ToArray();

        var ack = new ReplicationAck(2, signed, signature);
        var read = RoundTrip(ack, ReplicationAck.Read);

        Assert.AreEqual(3, ack.BodyEntryCount);
        Assert.AreEqual(2UL, read.Count);
        Assert.IsTrue(read.Receipt.Span.SequenceEqual(signed));
        Assert.IsTrue(read.Signature.Span.SequenceEqual(signature));
        Assert.AreEqual(receipt, ReplicationReceipt.Parse(read.Receipt.Span));
    }

    [TestMethod]
    public void ReplicationAck_AReceiptWithoutItsSignature_IsMalformed()
    {
        var ack = new ReplicationAck(2, Replicated().EncodeForSigning(), ReadOnlyMemory<byte>.Empty);

        var refusal = Assert.ThrowsExactly<PeerProtocolException>(() => RoundTrip(ack, ReplicationAck.Read));

        Assert.AreEqual(PeerRefusalReason.Malformed, refusal.Reason);
    }

    [TestMethod]
    public void ReplicationAck_AReceiptThatDoesNotParse_IsMalformedOnRead()
    {
        // A deletion receipt where a replication receipt belongs: the label
        // is what keeps the two statements from ever being confused.
        var wrongKind = Receipt().EncodeForSigning();
        var signature = Enumerable.Repeat((byte)9, ReplicationReceipt.SignatureLength).ToArray();

        var refusal = Assert.ThrowsExactly<PeerProtocolException>(
            () => RoundTrip(new ReplicationAck(2, wrongKind, signature), ReplicationAck.Read));

        Assert.AreEqual(PeerRefusalReason.Malformed, refusal.Reason);
    }

    [TestMethod]
    public void RetentionAck_CarriesAReceiptAndItsSignature()
    {
        var receipt = Receipt();
        var signed = receipt.EncodeForSigning();
        var signature = Enumerable.Repeat((byte)9, DeletionReceipt.SignatureLength).ToArray();

        var ack = new RetentionAck(2, signed, signature);
        var read = RoundTrip(ack, RetentionAck.Read);

        Assert.AreEqual(3, ack.BodyEntryCount);
        Assert.AreEqual(2UL, read.Deleted);
        Assert.IsTrue(read.Receipt.Span.SequenceEqual(signed));
        Assert.IsTrue(read.Signature.Span.SequenceEqual(signature));
        Assert.AreEqual(receipt, DeletionReceipt.Parse(read.Receipt.Span));
    }

    [TestMethod]
    public void RetentionAck_AReceiptWithoutItsSignature_IsMalformed()
    {
        // One without the other is refused on both sides: an unsigned
        // receipt is a claim nobody made, and a signature over nothing is
        // noise. Malformed rather than dropped, for the reclaim key's reason.
        var signed = Receipt().EncodeForSigning();

        Assert.ThrowsExactly<PeerProtocolException>(() => PeerFrame.Encode(new RetentionAck(2, signed, ReadOnlyMemory<byte>.Empty)));
        Assert.ThrowsExactly<PeerProtocolException>(() => PeerFrame.Encode(new RetentionAck(2, ReadOnlyMemory<byte>.Empty, new byte[64])));
        Assert.ThrowsExactly<PeerProtocolException>(() => PeerFrame.Encode(new RetentionAck(2, signed, new byte[63])));
    }

    [TestMethod]
    public void RetentionAck_AReceiptThatDoesNotParse_IsMalformedOnRead()
    {
        var ack = new RetentionAck(2, new byte[] { 1, 2, 3 }, new byte[64]);

        var refusal = Assert.ThrowsExactly<PeerProtocolException>(() => RoundTrip(ack, RetentionAck.Read));
        Assert.AreEqual(PeerRefusalReason.Malformed, refusal.Reason);
    }
}
