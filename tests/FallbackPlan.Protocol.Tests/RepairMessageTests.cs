using FallbackPlan.Protocol;

namespace FallbackPlan.Protocol.Tests;

/// <summary>
/// The repair messages (specification peer-protocol 08): an owner offers a
/// peer a replacement for a blob its sweep found damaged there, and the peer
/// answers each offer with a verdict it reached from its own copy (FR-VER-008).
/// Each frame round-trips through the codec, the state machine admits them
/// only once the session is Open, and a verdict this build does not define is
/// refused rather than guessed at.
/// </summary>
[TestClass]
public sealed class RepairMessageTests
{
    private static TMessage RoundTrip<TMessage>(IPeerMessage message, Func<System.Formats.Cbor.CborReader, TMessage> read)
    {
        var (_, body) = PeerFrame.Decode(PeerFrame.Encode(message));
        return read(body);
    }

    [TestMethod]
    public void OfferAndVerdict_RoundTrip()
    {
        var offer = new RepairOffer(new byte[16], "blobs/data/aaaa/one", 67_108_880);
        Assert.AreEqual(offer, RoundTrip(offer, RepairOffer.Read));

        foreach (var verdict in Enum.GetValues<RepairVerdictCode>())
        {
            var answer = new RepairVerdict("blobs/data/aaaa/one", verdict);
            Assert.AreEqual(answer, RoundTrip(answer, RepairVerdict.Read));
        }
    }

    [TestMethod]
    public void TheVerdicts_AreTheCodesTheSpecificationNumbers()
    {
        // The code is what a source branches on (08 §3); renumbering one
        // would have an older source send bytes a newer peer refused, or
        // read a refusal as a replacement.
        Assert.AreEqual(1, (int)RepairVerdictCode.Send);
        Assert.AreEqual(2, (int)RepairVerdictCode.Replaced);
        Assert.AreEqual(3, (int)RepairVerdictCode.Intact);
        Assert.AreEqual(4, (int)RepairVerdictCode.NotHeld);
        Assert.AreEqual(5, (int)RepairVerdictCode.LengthDiffers);
        Assert.AreEqual(6, (int)RepairVerdictCode.Unmatched);
        Assert.HasCount(6, Enum.GetValues<RepairVerdictCode>());
    }

    [TestMethod]
    public void Offer_RepositoryIdOfTheWrongWidth_IsRefused()
    {
        var (_, body) = PeerFrame.Decode(PeerFrame.Encode(new RepairOffer(new byte[15], "blobs/data/aaaa/one", 64)));

        var refusal = Assert.ThrowsExactly<PeerProtocolException>(() => RepairOffer.Read(body));
        Assert.AreEqual(PeerRefusalReason.Malformed, refusal.Reason);
    }

    [TestMethod]
    public void Offer_NamingNoKey_IsRefused()
    {
        var refusal = Assert.ThrowsExactly<PeerProtocolException>(() => RepairOffer.Read(Body(writer =>
        {
            writer.WriteStartMap(3);
            writer.WriteInt32(0);
            writer.WriteUInt32((uint)PeerMessageType.RepairOffer);
            writer.WriteInt32(1);
            writer.WriteByteString(new byte[16]);
            writer.WriteInt32(3);
            writer.WriteUInt64(64);
            writer.WriteEndMap();
        })));
        Assert.AreEqual(PeerRefusalReason.Malformed, refusal.Reason);
    }

    [TestMethod]
    public void Verdict_ACodeThisBuildDoesNotDefine_IsRefusedRatherThanGuessedAt()
    {
        // A verdict decides whether the source sends a blob. One it cannot
        // read is not one it may treat as any of the six it can.
        var refusal = Assert.ThrowsExactly<PeerProtocolException>(() => RepairVerdict.Read(Body(writer =>
        {
            writer.WriteStartMap(3);
            writer.WriteInt32(0);
            writer.WriteUInt32((uint)PeerMessageType.RepairVerdict);
            writer.WriteInt32(1);
            writer.WriteTextString("blobs/data/aaaa/one");
            writer.WriteInt32(2);
            writer.WriteUInt32(7);
            writer.WriteEndMap();
        })));
        Assert.AreEqual(PeerRefusalReason.Malformed, refusal.Reason);
    }

    [TestMethod]
    public void RepairFrames_ArePermittedOnlyOnceOpen()
    {
        foreach (var type in new[] { PeerMessageType.RepairOffer, PeerMessageType.RepairVerdict })
        {
            Assert.IsTrue(PeerAuthenticator.Permits(PeerSessionState.Open, type), $"{type} once open");
            Assert.IsFalse(PeerAuthenticator.Permits(PeerSessionState.Encrypted, type), $"{type} before auth");
            Assert.IsFalse(PeerAuthenticator.Permits(PeerSessionState.Authenticated, type), $"{type} before accept");
        }
    }

    [TestMethod]
    public void TheRepairTypes_TakeTheFirstTwoNumbersPastRetrieval()
    {
        // 02 §7 reserved 280 onwards for later payload documents.
        Assert.AreEqual(280, (int)PeerMessageType.RepairOffer);
        Assert.AreEqual(281, (int)PeerMessageType.RepairVerdict);
    }

    [TestMethod]
    public void ReplicaRepairFeature_IsOfferedAndNeverRequired()
    {
        // A gate is safe here for the reason 02 §6 permits one: withholding
        // the feature can only leave a peer's damage where it is. The check
        // that defends the peer is made on its own copy, never on the hello.
        Assert.AreEqual("replica-repair", PeerSessionNegotiation.ReplicaRepairFeature);
        Assert.Contains(PeerSessionNegotiation.ReplicaRepairFeature, PeerSessionNegotiation.SupportedFeatures);

        var ours = new SessionHello(1, 1, PeerSessionNegotiation.SupportedFeatures, [], "1.0.0", Terms: null);
        var older = new SessionHello(
            1, 1,
            [.. PeerSessionNegotiation.SupportedFeatures.Where(
                feature => feature != PeerSessionNegotiation.ReplicaRepairFeature)],
            [], "1.0.0", Terms: null);

        var effective = PeerSessionNegotiation.SelectFeatures(ours, older);
        Assert.DoesNotContain(PeerSessionNegotiation.ReplicaRepairFeature, effective);
        Assert.Contains(PeerSessionNegotiation.RetrievalFeature, effective);
    }

    private static System.Formats.Cbor.CborReader Body(Action<System.Formats.Cbor.CborWriter> write)
    {
        var writer = new System.Formats.Cbor.CborWriter(System.Formats.Cbor.CborConformanceMode.Canonical);
        write(writer);
        var (_, body) = PeerFrame.Decode(writer.Encode());
        return body;
    }
}
