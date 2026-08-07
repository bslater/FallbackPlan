using System.Formats.Cbor;
using System.Text;
using FallbackPlan.Protocol;

namespace FallbackPlan.Protocol.Tests;

/// <summary>
/// Framing (specification peer-protocol 02 §5): what a reader will accept off a
/// wire it does not trust.
/// </summary>
public sealed class PeerFrameTests
{
    [Fact]
    public async Task A_frame_written_is_a_frame_read()
    {
        var hello = new SessionHello(1, 3, ["a", "b"], ["a"], "1.0.0-test", new PeerTerms(1_024, "every 1h", 4));

        using var stream = new MemoryStream();
        await PeerFrame.WriteAsync(stream, hello, CancellationToken.None);
        stream.Position = 0;

        var frame = await PeerFrame.ReadAsync(stream, CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Equal(PeerMessageType.SessionHello, frame!.Value.Type);
        Assert.Equal(hello, SessionHello.Read(frame.Value.Body));
    }

    [Fact]
    public async Task A_clean_close_is_not_an_error()
    {
        using var stream = new MemoryStream();

        // Nothing at all, and nothing wrong: the peer finished and went away.
        Assert.Null(await PeerFrame.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task A_close_part_way_through_a_frame_is_an_error()
    {
        using var stream = new MemoryStream();
        await PeerFrame.WriteAsync(
            stream, new SessionAccept(1, []), CancellationToken.None);

        var truncated = stream.ToArray()[..^1];
        using var partial = new MemoryStream(truncated);

        var refused = await Assert.ThrowsAsync<PeerProtocolException>(
            async () => await PeerFrame.ReadAsync(partial, CancellationToken.None));

        Assert.Equal(PeerRefusalReason.Malformed, refused.Reason);
    }

    [Fact]
    public async Task An_oversized_frame_is_refused_before_it_is_allocated()
    {
        // Four bytes claiming 4 GiB. A reader that allocates first and checks
        // second dies here, which is exactly the shape T-7 describes — so the
        // stream deliberately carries no payload at all: the refusal must come
        // from the declared length and nothing else.
        var prefix = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
        using var stream = new MemoryStream(prefix);

        var refused = await Assert.ThrowsAsync<PeerProtocolException>(
            async () => await PeerFrame.ReadAsync(stream, CancellationToken.None));

        Assert.Equal(PeerRefusalReason.Malformed, refused.Reason);
        Assert.Contains("over the", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_canonical_cbor_is_refused_rather_than_read_leniently()
    {
        // Keys out of order. Two encodings of one message is the ambiguity
        // deterministic encoding exists to remove, so accepting this would give
        // an attacker a second spelling of every message.
        var writer = new CborWriter(CborConformanceMode.Lax);
        writer.WriteStartMap(2);
        writer.WriteInt32(1);
        writer.WriteInt32(7);
        writer.WriteInt32(PeerFrame.MessageTypeKey);
        writer.WriteUInt32((uint)PeerMessageType.SessionAccept);
        writer.WriteEndMap();

        var refused = Assert.Throws<PeerProtocolException>(() => PeerFrame.Decode(writer.Encode()));
        Assert.Equal(PeerRefusalReason.Malformed, refused.Reason);
    }

    [Fact]
    public void A_body_whose_first_key_is_not_the_message_type_is_refused()
    {
        var writer = new CborWriter(CborConformanceMode.Canonical);
        writer.WriteStartMap(1);
        writer.WriteInt32(1);
        writer.WriteInt32(7);
        writer.WriteEndMap();

        var refused = Assert.Throws<PeerProtocolException>(() => PeerFrame.Decode(writer.Encode()));
        Assert.Equal(PeerRefusalReason.Malformed, refused.Reason);
    }

    [Fact]
    public async Task An_unknown_message_type_reaches_the_caller_to_be_refused()
    {
        using var stream = new MemoryStream();
        await PeerFrame.WriteAsync(stream, new UnknownMessage(), CancellationToken.None);
        stream.Position = 0;

        // 02 §5: not skipped. A protocol that ignores what it does not
        // understand cannot tell a new feature from a corrupted stream, so the
        // frame layer surfaces the type and the session layer refuses it.
        var frame = await PeerFrame.ReadAsync(stream, CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Equal((PeerMessageType)9_001, frame!.Value.Type);
        Assert.False(Enum.IsDefined(frame.Value.Type));
    }

    private sealed record UnknownMessage : IPeerMessage
    {
        public PeerMessageType Type => (PeerMessageType)9_001;

        public int BodyEntryCount => 0;

        public void WriteBody(CborWriter writer)
        {
        }
    }
}

/// <summary>
/// Session messages (specification peer-protocol 02 §2, §6): what survives a
/// round trip and what a reader will not take.
/// </summary>
public sealed class PeerSessionMessageTests
{
    [Fact]
    public void A_hello_without_terms_round_trips()
    {
        var hello = new SessionHello(1, 1, [], [], "1.0.0", Terms: null);

        var (type, body) = PeerFrame.Decode(PeerFrame.Encode(hello));

        Assert.Equal(PeerMessageType.SessionHello, type);
        var read = SessionHello.Read(body);
        Assert.Null(read.Terms);
        Assert.Equal(hello, read);
    }

    [Fact]
    public void A_hello_whose_range_runs_backwards_is_refused()
    {
        var hello = new SessionHello(5, 2, [], [], "1.0.0", Terms: null);

        var refused = Assert.Throws<PeerProtocolException>(
            () => SessionHello.Read(PeerFrame.Decode(PeerFrame.Encode(hello)).Body));

        Assert.Equal(PeerRefusalReason.Malformed, refused.Reason);
    }

    [Fact]
    public void A_hello_that_omits_its_range_is_refused()
    {
        // Absent, not merely zero. A reader that defaults a missing range to
        // 0–0 would negotiate with a peer that never said what it speaks.
        var writer = new CborWriter(CborConformanceMode.Canonical);
        writer.WriteStartMap(2);
        writer.WriteInt32(PeerFrame.MessageTypeKey);
        writer.WriteUInt32((uint)PeerMessageType.SessionHello);
        writer.WriteInt32(5);
        writer.WriteTextString("1.0.0");
        writer.WriteEndMap();

        var refused = Assert.Throws<PeerProtocolException>(
            () => SessionHello.Read(PeerFrame.Decode(writer.Encode()).Body));

        Assert.Equal(PeerRefusalReason.Malformed, refused.Reason);
    }

    [Fact]
    public void A_feature_identifier_that_is_not_lower_case_ascii_is_refused()
    {
        var writer = new CborWriter(CborConformanceMode.Canonical);
        writer.WriteStartMap(4);
        writer.WriteInt32(PeerFrame.MessageTypeKey);
        writer.WriteUInt32((uint)PeerMessageType.SessionHello);
        writer.WriteInt32(1);
        writer.WriteUInt32(1);
        writer.WriteInt32(2);
        writer.WriteUInt32(1);
        writer.WriteInt32(3);
        writer.WriteStartArray(1);
        writer.WriteTextString("Replication");
        writer.WriteEndArray();
        writer.WriteEndMap();

        // Refused rather than folded to lower case: a name two implementations
        // spell differently and one silently normalises is a negotiation that
        // disagrees with itself while both sides believe they agree.
        var refused = Assert.Throws<PeerProtocolException>(
            () => SessionHello.Read(PeerFrame.Decode(writer.Encode()).Body));

        Assert.Equal(PeerRefusalReason.Malformed, refused.Reason);
    }

    [Fact]
    public void An_empty_feature_identifier_is_refused()
    {
        var writer = new CborWriter(CborConformanceMode.Canonical);
        writer.WriteStartMap(4);
        writer.WriteInt32(PeerFrame.MessageTypeKey);
        writer.WriteUInt32((uint)PeerMessageType.SessionHello);
        writer.WriteInt32(1);
        writer.WriteUInt32(1);
        writer.WriteInt32(2);
        writer.WriteUInt32(1);
        writer.WriteInt32(3);
        writer.WriteStartArray(1);
        writer.WriteTextString(string.Empty);
        writer.WriteEndArray();
        writer.WriteEndMap();

        // Nothing supports "", so a peer offering it is either broken or
        // probing. Neither is a reason to carry it into the intersection.
        var refused = Assert.Throws<PeerProtocolException>(
            () => SessionHello.Read(PeerFrame.Decode(writer.Encode()).Body));

        Assert.Equal(PeerRefusalReason.Malformed, refused.Reason);
    }

    [Fact]
    public void A_hello_with_more_features_than_the_limit_is_refused()
    {
        var tooMany = Enumerable.Range(0, SessionHello.MaximumFeatures + 1)
            .Select(i => $"f{i}").ToList();

        var refused = Assert.Throws<PeerProtocolException>(
            () => PeerFrame.Encode(new SessionHello(1, 1, tooMany, [], "1.0.0", null)));

        Assert.Equal(PeerRefusalReason.Malformed, refused.Reason);
    }

    [Fact]
    public void A_key_this_version_does_not_know_is_skipped_within_a_known_message()
    {
        var writer = new CborWriter(CborConformanceMode.Canonical);
        writer.WriteStartMap(4);
        writer.WriteInt32(PeerFrame.MessageTypeKey);
        writer.WriteUInt32((uint)PeerMessageType.SessionHello);
        writer.WriteInt32(1);
        writer.WriteUInt32(1);
        writer.WriteInt32(2);
        writer.WriteUInt32(2);
        writer.WriteInt32(99);
        writer.WriteTextString("something a later version added");
        writer.WriteEndMap();

        // The opposite of the unknown-message-type rule, and deliberately so:
        // the shape here is known, so a field added later cannot be mistaken
        // for a corrupted stream.
        var hello = SessionHello.Read(PeerFrame.Decode(writer.Encode()).Body);

        Assert.Equal(1, hello.MinimumVersion);
        Assert.Equal(2, hello.MaximumVersion);
    }

    [Fact]
    public void An_acceptance_round_trips()
    {
        var accept = new SessionAccept(3, ["one", "two"]);

        var (type, body) = PeerFrame.Decode(PeerFrame.Encode(accept));

        Assert.Equal(PeerMessageType.SessionAccept, type);
        Assert.Equal(accept, SessionAccept.Read(body));
    }

    [Fact]
    public void An_authentication_claim_round_trips()
    {
        using var keypair = PeerKeypair.Generate();
        var auth = SessionAuth.Create(keypair.Identity);

        var (type, body) = PeerFrame.Decode(PeerFrame.Encode(auth));

        Assert.Equal(PeerMessageType.SessionAuth, type);
        Assert.Equal(auth, SessionAuth.Read(body));
    }

    [Fact]
    public void An_authentication_proof_round_trips()
    {
        var proof = new SessionAuthProof(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());

        var (type, body) = PeerFrame.Decode(PeerFrame.Encode(proof));

        Assert.Equal(PeerMessageType.SessionAuthProof, type);
        Assert.Equal(proof, SessionAuthProof.Read(body));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void An_identity_that_is_not_thirty_two_bytes_is_refused(int length)
    {
        var writer = new CborWriter(CborConformanceMode.Canonical);
        writer.WriteStartMap(3);
        writer.WriteInt32(PeerFrame.MessageTypeKey);
        writer.WriteUInt32((uint)PeerMessageType.SessionAuth);
        writer.WriteInt32(1);
        writer.WriteByteString(new byte[length]);
        writer.WriteInt32(2);
        writer.WriteByteString(new byte[SessionBinding.NonceLength]);
        writer.WriteEndMap();

        // Length-checked before it reaches the identity type, so a short key
        // cannot become an ArgumentException surfacing as a crash rather than
        // a refusal.
        var refused = Assert.Throws<PeerProtocolException>(
            () => SessionAuth.Read(PeerFrame.Decode(writer.Encode()).Body));

        Assert.Equal(PeerRefusalReason.Malformed, refused.Reason);
    }

    [Fact]
    public void A_proof_that_is_not_a_signature_length_is_refused()
    {
        var writer = new CborWriter(CborConformanceMode.Canonical);
        writer.WriteStartMap(2);
        writer.WriteInt32(PeerFrame.MessageTypeKey);
        writer.WriteUInt32((uint)PeerMessageType.SessionAuthProof);
        writer.WriteInt32(1);
        writer.WriteByteString(new byte[16]);
        writer.WriteEndMap();

        var refused = Assert.Throws<PeerProtocolException>(
            () => SessionAuthProof.Read(PeerFrame.Decode(writer.Encode()).Body));

        Assert.Equal(PeerRefusalReason.Malformed, refused.Reason);
    }

    [Fact]
    public void A_refusal_carries_the_code_a_client_branches_on()
    {
        var refusal = SessionRefuse.From(
            new PeerProtocolException(PeerRefusalReason.Revoked, "This pairing was revoked."));

        var read = SessionRefuse.Read(PeerFrame.Decode(PeerFrame.Encode(refusal)).Body);

        Assert.Equal(PeerRefusalReason.Revoked, read.Reason);
        Assert.Equal("This pairing was revoked.", read.Text);
    }

    [Fact]
    public void A_refusal_text_is_truncated_on_a_character_boundary()
    {
        // The text is written for this side's operator and served to a stranger,
        // so it is clipped rather than allowed to grow — and clipped without
        // producing the invalid UTF-8 that a naive byte cut would.
        var long_ = string.Concat(Enumerable.Repeat("é", 400));
        var refusal = new SessionRefuse(PeerRefusalReason.Busy, long_);

        var read = SessionRefuse.Read(PeerFrame.Decode(PeerFrame.Encode(refusal)).Body);

        Assert.True(Encoding.UTF8.GetByteCount(read.Text) <= SessionRefuse.MaximumTextBytes);
        Assert.StartsWith("éé", read.Text, StringComparison.Ordinal);
        Assert.DoesNotContain('�', read.Text);
    }
}

/// <summary>
/// Version selection and feature negotiation (specification peer-protocol
/// 02 §3–§4): what two independent implementations must agree on.
/// </summary>
public sealed class PeerSessionNegotiationTests
{
    private static SessionHello Hello(
        ushort minimum, ushort maximum, string[]? offered = null, string[]? required = null) =>
        new(minimum, maximum, offered ?? [], required ?? [], "1.0.0", null);

    [Fact]
    public void The_highest_version_both_sides_speak_wins()
    {
        Assert.Equal(3, PeerSessionNegotiation.SelectVersion(Hello(1, 5), Hello(2, 3)));
        Assert.Equal(3, PeerSessionNegotiation.SelectVersion(Hello(2, 3), Hello(1, 5)));
        Assert.Equal(1, PeerSessionNegotiation.SelectVersion(Hello(1, 1), Hello(1, 1)));
    }

    [Fact]
    public void Selection_does_not_depend_on_which_side_is_asking()
    {
        // Both sides compute this alone, from the same two hellos, and never
        // exchange another message about it. If the function were not symmetric
        // they would open a session at two different versions and find out later.
        var pairs = new[]
        {
            (Hello(1, 4), Hello(3, 9)),
            (Hello(2, 2), Hello(1, 7)),
            (Hello(5, 5), Hello(5, 5)),
        };

        foreach (var (a, b) in pairs)
        {
            Assert.Equal(
                PeerSessionNegotiation.SelectVersion(a, b),
                PeerSessionNegotiation.SelectVersion(b, a));
        }
    }

    [Fact]
    public void Ranges_that_do_not_overlap_are_refused_naming_both()
    {
        var refused = Assert.Throws<PeerProtocolException>(
            () => PeerSessionNegotiation.SelectVersion(Hello(1, 2), Hello(7, 9)));

        Assert.Equal(PeerRefusalReason.VersionUnsupported, refused.Reason);

        // Both ranges, per 02 §3. Without them the operator cannot tell which
        // side needs upgrading, which is the only question this refusal answers.
        Assert.Contains("1–2", refused.Message, StringComparison.Ordinal);
        Assert.Contains("7–9", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Features_in_effect_are_the_intersection()
    {
        var effect = PeerSessionNegotiation.SelectFeatures(
            Hello(1, 1, offered: ["a", "b", "c"]),
            Hello(1, 1, offered: ["b", "c", "d"]));

        Assert.Equal(["b", "c"], effect);
    }

    [Fact]
    public void The_intersection_is_ordered_so_both_sides_write_the_same_list()
    {
        var ours = Hello(1, 1, offered: ["zeta", "alpha", "mu"]);
        var theirs = Hello(1, 1, offered: ["mu", "zeta", "alpha"]);

        // A set has no order, and an unordered accept is one two sides can
        // disagree about while both being right.
        Assert.Equal(
            PeerSessionNegotiation.SelectFeatures(ours, theirs),
            PeerSessionNegotiation.SelectFeatures(theirs, ours));
        Assert.Equal(["alpha", "mu", "zeta"], PeerSessionNegotiation.SelectFeatures(ours, theirs));
    }

    [Fact]
    public void A_required_feature_the_peer_does_not_offer_refuses_the_session()
    {
        var refused = Assert.Throws<PeerProtocolException>(
            () => PeerSessionNegotiation.SelectFeatures(
                Hello(1, 1, offered: ["a"], required: ["b"]),
                Hello(1, 1, offered: ["a"])));

        Assert.Equal(PeerRefusalReason.FeatureUnsupported, refused.Reason);
        Assert.Contains("'b'", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_feature_the_peer_requires_of_us_is_checked_too()
    {
        // They would refuse anyway. Refusing first costs a message and avoids
        // the half-open state 02 §6 says must not exist.
        var refused = Assert.Throws<PeerProtocolException>(
            () => PeerSessionNegotiation.SelectFeatures(
                Hello(1, 1, offered: ["a"]),
                Hello(1, 1, offered: ["a"], required: ["z"])));

        Assert.Equal(PeerRefusalReason.FeatureUnsupported, refused.Reason);
    }

    [Fact]
    public void Requiring_something_both_sides_offer_is_fine()
    {
        var effect = PeerSessionNegotiation.SelectFeatures(
            Hello(1, 1, offered: ["a", "b"], required: ["a"]),
            Hello(1, 1, offered: ["a", "c"], required: ["a"]));

        Assert.Equal(["a"], effect);
    }

    [Fact]
    public void Version_one_offers_no_features_and_two_such_peers_still_agree()
    {
        var hello = PeerSessionNegotiation.Hello("1.0.0");

        var accept = PeerSessionNegotiation.Negotiate(hello, hello);

        // The mechanism exists before any feature does, because retrofitting
        // negotiation onto a deployed protocol means a flag day (02 §4).
        Assert.Equal(PeerSessionNegotiation.CurrentVersion, accept.Version);
        Assert.Empty(accept.Features);
    }

    [Fact]
    public void A_destination_hello_carries_its_terms_and_a_sources_does_not()
    {
        var terms = new PeerTerms(500_000_000_000, "every 1h", 4);

        var destination = PeerSessionNegotiation.Hello("1.0.0", terms);
        var source = PeerSessionNegotiation.Hello("1.0.0");

        Assert.Equal(terms, SessionHello.Read(PeerFrame.Decode(PeerFrame.Encode(destination)).Body).Terms);
        Assert.Null(SessionHello.Read(PeerFrame.Decode(PeerFrame.Encode(source)).Body).Terms);
    }
}
