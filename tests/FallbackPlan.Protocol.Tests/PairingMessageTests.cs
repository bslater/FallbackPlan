using System.Formats.Cbor;
using FallbackPlan.Protocol;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Protocol.Tests;

/// <summary>
/// The pairing messages (specification peer-protocol 01 §2.2): the four frames
/// that carry the ceremony, and one whole ceremony driven through them.
/// </summary>
/// <remarks>
/// The ceremony's cryptography was implemented and tested before these existed,
/// which meant a pairing could be computed and not sent. These tests exist so
/// that gap cannot reopen: the ceremony test below touches the derivation only
/// through encoded frames.
/// </remarks>
[TestClass]
public sealed class PairingMessageTests
{
    private static PairingContribution Contribution(PeerIdentity identity, PairingExchange exchange) =>
        new(identity, exchange.PublicKey, exchange.Nonce);

    [TestMethod]
    public void PairingOffer_EncodedAndDecoded_RoundTrips()
    {
        using var keypair = PeerKeypair.Generate();
        using var exchange = PairingExchange.Start();

        var offer = new PairOffer(Contribution(keypair.Identity, exchange), "Ana's laptop", 3, PeerRole.StoresForUs);

        var (type, body) = PeerFrame.Decode(PeerFrame.Encode(offer));

        Assert.AreEqual(PeerMessageType.PairOffer, type);
        Assert.AreEqual(offer, PairOffer.Read(body));
    }

    [TestMethod]
    public void PairingAcceptance_WithAndWithoutTerms_RoundTrips()
    {
        using var keypair = PeerKeypair.Generate();
        using var exchange = PairingExchange.Start();

        var terms = new PeerTerms(500_000_000_000, "every 1h", 4);
        var destination = new PairAccept(Contribution(keypair.Identity, exchange), "the NAS", 2, terms, PeerRole.StoresHere);
        var source = destination with { Terms = null };

        Assert.AreEqual(destination, PairAccept.Read(PeerFrame.Decode(PeerFrame.Encode(destination)).Body));

        // Terms are present only when the responder is the destination (01 §4),
        // so absent must survive the round trip as absent rather than as empty.
        var read = PairAccept.Read(PeerFrame.Decode(PeerFrame.Encode(source)).Body);
        Assert.IsNull(read.Terms);
        Assert.AreEqual(source, read);
    }

    [TestMethod]
    public void PairingConfirmationAndRefusal_EncodedAndDecoded_RoundTrip()
    {
        var confirm = new PairConfirm(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        Assert.AreEqual(confirm, PairConfirm.Read(PeerFrame.Decode(PeerFrame.Encode(confirm)).Body));

        var refuse = new PairRefuse(PeerRefusalReason.PairingDeclined, "The operator declined.");
        var read = PairRefuse.Read(PeerFrame.Decode(PeerFrame.Encode(refuse)).Body);
        Assert.AreEqual(PeerRefusalReason.PairingDeclined, read.Reason);
        Assert.AreEqual("The operator declined.", read.Text);
    }

    [TestMethod]
    public void PairingOffer_LabelExceedsTheLimit_IsRefusedRatherThanTruncated()
    {
        using var keypair = PeerKeypair.Generate();
        using var exchange = PairingExchange.Start();

        var offer = new PairOffer(
            Contribution(keypair.Identity, exchange), new string('x', PairOffer.MaximumLabelBytes + 1), 1,
            PeerRole.StoresForUs);

        // Truncating would put a label on the wire that is not the one the
        // human typed — and the label is how an operator recognises which
        // machine they are approving.
        var refused = Assert.ThrowsExactly<PeerProtocolException>(() => PeerFrame.Encode(offer));
        Assert.AreEqual(PeerRefusalReason.Malformed, refused.Reason);
    }

    [TestMethod]
    [DataRow(257u)]
    [DataRow(258u)]
    [DataRow(65_535u)]
    public void PairOffer_ARoleThatIsNoU8_RefusesAsMalformed_RatherThanWrappingIntoTheVocabulary(uint role)
    {
        // Key 7 is a u8 (01 §2.2). Read through a wider integer and cast, 257
        // became 1 and was accepted as stores-here: a role the peer did not
        // declare, recorded as though it had.
        using var keypair = PeerKeypair.Generate();
        using var exchange = PairingExchange.Start();
        var writer = new CborWriter(CborConformanceMode.Canonical);
        writer.WriteStartMap(7);
        writer.WriteInt32(PeerFrame.MessageTypeKey);
        writer.WriteUInt32((uint)PeerMessageType.PairOffer);
        var contribution = Contribution(keypair.Identity, exchange);
        writer.WriteInt32(1);
        writer.WriteByteString(contribution.Identity.PublicKey);
        writer.WriteInt32(2);
        writer.WriteByteString(contribution.EphemeralPublicKey.Span);
        writer.WriteInt32(3);
        writer.WriteByteString(contribution.Nonce.Span);
        writer.WriteInt32(4);
        writer.WriteTextString("laptop");
        writer.WriteInt32(5);
        writer.WriteUInt32(1);
        writer.WriteInt32(7);
        writer.WriteUInt32(role);
        writer.WriteEndMap();

        var refused = Assert.ThrowsExactly<PeerProtocolException>(
            () => PairOffer.Read(PeerFrame.Decode(writer.Encode()).Body));

        Assert.AreEqual(PeerRefusalReason.Malformed, refused.Reason);
    }

    [TestMethod]
    [DataRow(31)]
    [DataRow(33)]
    public void PairOffer_ContributionFieldIsTheWrongLength_RefusesAsMalformed(int length)
    {
        var writer = new CborWriter(CborConformanceMode.Canonical);
        writer.WriteStartMap(4);
        writer.WriteInt32(PeerFrame.MessageTypeKey);
        writer.WriteUInt32((uint)PeerMessageType.PairOffer);
        writer.WriteInt32(1);
        writer.WriteByteString(new byte[length]);
        writer.WriteInt32(2);
        writer.WriteByteString(new byte[32]);
        writer.WriteInt32(3);
        writer.WriteByteString(new byte[32]);
        writer.WriteEndMap();

        // Checked before the identity type sees it, so a short key surfaces as
        // a protocol refusal rather than an ArgumentException from the crypto.
        var refused = Assert.ThrowsExactly<PeerProtocolException>(
            () => PairOffer.Read(PeerFrame.Decode(writer.Encode()).Body));

        Assert.AreEqual(PeerRefusalReason.Malformed, refused.Reason);
    }

    [TestMethod]
    public void PairingCeremony_RunOverEncodedFrames_LeavesBothSidesAgreeing()
    {
        using var offererKey = PeerKeypair.Generate();
        using var responderKey = PeerKeypair.Generate();
        using var offererExchange = PairingExchange.Start();
        using var responderExchange = PairingExchange.Start();

        // Nothing below touches the other side's objects — only what came off
        // the wire. That is the property these messages exist to make testable.
        var offerFrame = PeerFrame.Encode(
            new PairOffer(Contribution(offererKey.Identity, offererExchange), "laptop", 1, PeerRole.StoresForUs));
        var acceptFrame = PeerFrame.Encode(
            new PairAccept(
                Contribution(responderKey.Identity, responderExchange), "the NAS", 1, PeerTerms.None,
                PeerRole.StoresHere));

        var offer = PairOffer.Read(PeerFrame.Decode(offerFrame).Body);
        var accept = PairAccept.Read(PeerFrame.Decode(acceptFrame).Body);

        var transcript = PairingTranscript.Build(
            offer.Contribution, accept.Contribution, accept.SelectedVersion,
            offer.RoleForResponder, accept.RoleForOfferer);

        // Each side derives the shared secret from the peer's ephemeral key as
        // it arrived, not as it was constructed.
        var offererSecret = offererExchange.DeriveSharedSecret(accept.Contribution.EphemeralPublicKey.Span);
        var responderSecret = responderExchange.DeriveSharedSecret(offer.Contribution.EphemeralPublicKey.Span);
        SequenceAssert.AreEqual(offererSecret, responderSecret);

        var offererString = PairingTranscript.ShortAuthenticationString(
            offererSecret, offer.Contribution.Nonce.Span, accept.Contribution.Nonce.Span, transcript);
        var responderString = PairingTranscript.ShortAuthenticationString(
            responderSecret, offer.Contribution.Nonce.Span, accept.Contribution.Nonce.Span, transcript);

        // What the two humans read to each other.
        Assert.AreEqual(offererString, responderString);
        Assert.AreEqual(PairingTranscript.ShortAuthenticationStringCharacters, offererString.Length);

        // And the confirmation each sends after its human approves, checked
        // against the identity that arrived in the peer's own message.
        var confirmation = PairingTranscript.ConfirmationBytes(transcript);
        var offererConfirm = PairConfirm.Read(
            PeerFrame.Decode(PeerFrame.Encode(new PairConfirm(offererKey.Sign(confirmation)))).Body);

        Assert.IsTrue(offer.Contribution.Identity.Verify(confirmation, offererConfirm.Signature.Span));
        Assert.IsFalse(accept.Contribution.Identity.Verify(confirmation, offererConfirm.Signature.Span));
    }
}
