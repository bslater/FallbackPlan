using System.Formats.Cbor;
using FallbackPlan.Protocol;

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
public sealed class PairingMessageTests
{
    private static PairingContribution Contribution(PeerIdentity identity, PairingExchange exchange) =>
        new(identity, exchange.PublicKey, exchange.Nonce);

    [Fact]
    public void An_offer_round_trips()
    {
        using var keypair = PeerKeypair.Generate();
        using var exchange = PairingExchange.Start();

        var offer = new PairOffer(Contribution(keypair.Identity, exchange), "Ana's laptop", 3);

        var (type, body) = PeerFrame.Decode(PeerFrame.Encode(offer));

        Assert.Equal(PeerMessageType.PairOffer, type);
        Assert.Equal(offer, PairOffer.Read(body));
    }

    [Fact]
    public void An_acceptance_round_trips_with_and_without_terms()
    {
        using var keypair = PeerKeypair.Generate();
        using var exchange = PairingExchange.Start();

        var terms = new PeerTerms(500_000_000_000, "every 1h", 4);
        var destination = new PairAccept(Contribution(keypair.Identity, exchange), "the NAS", 2, terms);
        var source = destination with { Terms = null };

        Assert.Equal(destination, PairAccept.Read(PeerFrame.Decode(PeerFrame.Encode(destination)).Body));

        // Terms are present only when the responder is the destination (01 §4),
        // so absent must survive the round trip as absent rather than as empty.
        var read = PairAccept.Read(PeerFrame.Decode(PeerFrame.Encode(source)).Body);
        Assert.Null(read.Terms);
        Assert.Equal(source, read);
    }

    [Fact]
    public void A_confirmation_and_a_refusal_round_trip()
    {
        var confirm = new PairConfirm(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        Assert.Equal(confirm, PairConfirm.Read(PeerFrame.Decode(PeerFrame.Encode(confirm)).Body));

        var refuse = new PairRefuse(PeerRefusalReason.PairingDeclined, "The operator declined.");
        var read = PairRefuse.Read(PeerFrame.Decode(PeerFrame.Encode(refuse)).Body);
        Assert.Equal(PeerRefusalReason.PairingDeclined, read.Reason);
        Assert.Equal("The operator declined.", read.Text);
    }

    [Fact]
    public void A_label_longer_than_the_limit_is_refused_rather_than_truncated()
    {
        using var keypair = PeerKeypair.Generate();
        using var exchange = PairingExchange.Start();

        var offer = new PairOffer(
            Contribution(keypair.Identity, exchange), new string('x', PairOffer.MaximumLabelBytes + 1), 1);

        // Truncating would put a label on the wire that is not the one the
        // human typed — and the label is how an operator recognises which
        // machine they are approving.
        var refused = Assert.Throws<PeerProtocolException>(() => PeerFrame.Encode(offer));
        Assert.Equal(PeerRefusalReason.Malformed, refused.Reason);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void A_contribution_field_of_the_wrong_length_is_refused(int length)
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
        var refused = Assert.Throws<PeerProtocolException>(
            () => PairOffer.Read(PeerFrame.Decode(writer.Encode()).Body));

        Assert.Equal(PeerRefusalReason.Malformed, refused.Reason);
    }

    [Fact]
    public void A_whole_ceremony_runs_through_encoded_frames_and_both_sides_agree()
    {
        using var offererKey = PeerKeypair.Generate();
        using var responderKey = PeerKeypair.Generate();
        using var offererExchange = PairingExchange.Start();
        using var responderExchange = PairingExchange.Start();

        // Nothing below touches the other side's objects — only what came off
        // the wire. That is the property these messages exist to make testable.
        var offerFrame = PeerFrame.Encode(
            new PairOffer(Contribution(offererKey.Identity, offererExchange), "laptop", 1));
        var acceptFrame = PeerFrame.Encode(
            new PairAccept(Contribution(responderKey.Identity, responderExchange), "the NAS", 1, PeerTerms.None));

        var offer = PairOffer.Read(PeerFrame.Decode(offerFrame).Body);
        var accept = PairAccept.Read(PeerFrame.Decode(acceptFrame).Body);

        var transcript = PairingTranscript.Build(offer.Contribution, accept.Contribution, accept.SelectedVersion);

        // Each side derives the shared secret from the peer's ephemeral key as
        // it arrived, not as it was constructed.
        var offererSecret = offererExchange.DeriveSharedSecret(accept.Contribution.EphemeralPublicKey.Span);
        var responderSecret = responderExchange.DeriveSharedSecret(offer.Contribution.EphemeralPublicKey.Span);
        Assert.Equal(offererSecret, responderSecret);

        var offererString = PairingTranscript.ShortAuthenticationString(
            offererSecret, offer.Contribution.Nonce.Span, accept.Contribution.Nonce.Span, transcript);
        var responderString = PairingTranscript.ShortAuthenticationString(
            responderSecret, offer.Contribution.Nonce.Span, accept.Contribution.Nonce.Span, transcript);

        // What the two humans read to each other.
        Assert.Equal(offererString, responderString);
        Assert.Equal(PairingTranscript.ShortAuthenticationStringCharacters, offererString.Length);

        // And the confirmation each sends after its human approves, checked
        // against the identity that arrived in the peer's own message.
        var confirmation = PairingTranscript.ConfirmationBytes(transcript);
        var offererConfirm = PairConfirm.Read(
            PeerFrame.Decode(PeerFrame.Encode(new PairConfirm(offererKey.Sign(confirmation)))).Body);

        Assert.True(offer.Contribution.Identity.Verify(confirmation, offererConfirm.Signature.Span));
        Assert.False(accept.Contribution.Identity.Verify(confirmation, offererConfirm.Signature.Span));
    }
}
