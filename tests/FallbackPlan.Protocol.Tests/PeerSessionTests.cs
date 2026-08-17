using System.Formats.Cbor;
using System.Text;
using FallbackPlan.Protocol;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Protocol.Tests;

/// <summary>
/// Framing (specification peer-protocol 02 §5): what a reader will accept off a
/// wire it does not trust.
/// </summary>
[TestClass]
public sealed class PeerFrameTests
{
    [TestMethod]
    public async Task Frame_WrittenThenRead_RoundTrips()
    {
        var hello = new SessionHello(1, 3, ["a", "b"], ["a"], "1.0.0-test", new PeerTerms(1_024, "every 1h", 4));

        using var stream = new MemoryStream();
        await PeerFrame.WriteAsync(stream, hello, CancellationToken.None);
        stream.Position = 0;

        var frame = await PeerFrame.ReadAsync(stream, CancellationToken.None);

        Assert.IsNotNull(frame);
        Assert.AreEqual(PeerMessageType.SessionHello, frame!.Value.Type);
        Assert.AreEqual(hello, SessionHello.Read(frame.Value.Body));
    }

    [TestMethod]
    public async Task FrameReader_StreamClosedCleanly_ReportsEndRatherThanAnError()
    {
        using var stream = new MemoryStream();

        // Nothing at all, and nothing wrong: the peer finished and went away.
        Assert.IsNull(await PeerFrame.ReadAsync(stream, CancellationToken.None));
    }

    [TestMethod]
    public async Task FrameReader_StreamClosedMidFrame_ReportsAnError()
    {
        using var stream = new MemoryStream();
        await PeerFrame.WriteAsync(
            stream, new SessionAccept(1, []), CancellationToken.None);

        var truncated = stream.ToArray()[..^1];
        using var partial = new MemoryStream(truncated);

        var refused = await Assert.ThrowsExactlyAsync<PeerProtocolException>(
            async () => await PeerFrame.ReadAsync(partial, CancellationToken.None));

        Assert.AreEqual(PeerRefusalReason.Malformed, refused.Reason);
    }

    [TestMethod]
    public async Task FrameReader_FrameExceedsTheLimit_RefusesBeforeAllocating()
    {
        // Four bytes claiming 4 GiB. A reader that allocates first and checks
        // second dies here, which is exactly the shape T-7 describes — so the
        // stream deliberately carries no payload at all: the refusal must come
        // from the declared length and nothing else.
        var prefix = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
        using var stream = new MemoryStream(prefix);

        var refused = await Assert.ThrowsExactlyAsync<PeerProtocolException>(
            async () => await PeerFrame.ReadAsync(stream, CancellationToken.None));

        Assert.AreEqual(PeerRefusalReason.Malformed, refused.Reason);
        Assert.Contains("over the", refused.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void FrameReader_BodyIsNonCanonicalCbor_RefusesRatherThanReadingLeniently()
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

        var refused = Assert.ThrowsExactly<PeerProtocolException>(() => PeerFrame.Decode(writer.Encode()));
        Assert.AreEqual(PeerRefusalReason.Malformed, refused.Reason);
    }

    [TestMethod]
    public void FrameReader_FirstKeyIsNotTheMessageType_Refuses()
    {
        var writer = new CborWriter(CborConformanceMode.Canonical);
        writer.WriteStartMap(1);
        writer.WriteInt32(1);
        writer.WriteInt32(7);
        writer.WriteEndMap();

        var refused = Assert.ThrowsExactly<PeerProtocolException>(() => PeerFrame.Decode(writer.Encode()));
        Assert.AreEqual(PeerRefusalReason.Malformed, refused.Reason);
    }

    [TestMethod]
    public async Task FrameReader_MessageTypeIsUnknown_SurfacesItToTheCaller()
    {
        using var stream = new MemoryStream();
        await PeerFrame.WriteAsync(stream, new UnknownMessage(), CancellationToken.None);
        stream.Position = 0;

        // 02 §5: not skipped. A protocol that ignores what it does not
        // understand cannot tell a new feature from a corrupted stream, so the
        // frame layer surfaces the type and the session layer refuses it.
        var frame = await PeerFrame.ReadAsync(stream, CancellationToken.None);

        Assert.IsNotNull(frame);
        Assert.AreEqual((PeerMessageType)9_001, frame!.Value.Type);
        Assert.IsFalse(Enum.IsDefined(frame.Value.Type));
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
[TestClass]
public sealed class PeerSessionMessageTests
{
    [TestMethod]
    public void SessionHello_WithoutTerms_RoundTrips()
    {
        var hello = new SessionHello(1, 1, [], [], "1.0.0", Terms: null);

        var (type, body) = PeerFrame.Decode(PeerFrame.Encode(hello));

        Assert.AreEqual(PeerMessageType.SessionHello, type);
        var read = SessionHello.Read(body);
        Assert.IsNull(read.Terms);
        Assert.AreEqual(hello, read);
    }

    [TestMethod]
    public void SessionHello_VersionRangeRunsBackwards_IsRefused()
    {
        var hello = new SessionHello(5, 2, [], [], "1.0.0", Terms: null);

        var refused = Assert.ThrowsExactly<PeerProtocolException>(
            () => SessionHello.Read(PeerFrame.Decode(PeerFrame.Encode(hello)).Body));

        Assert.AreEqual(PeerRefusalReason.Malformed, refused.Reason);
    }

    [TestMethod]
    public void SessionHello_VersionRangeOmitted_IsRefused()
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

        var refused = Assert.ThrowsExactly<PeerProtocolException>(
            () => SessionHello.Read(PeerFrame.Decode(writer.Encode()).Body));

        Assert.AreEqual(PeerRefusalReason.Malformed, refused.Reason);
    }

    [TestMethod]
    public void SessionHello_FeatureIdentifierIsNotLowerCaseAscii_IsRefused()
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
        var refused = Assert.ThrowsExactly<PeerProtocolException>(
            () => SessionHello.Read(PeerFrame.Decode(writer.Encode()).Body));

        Assert.AreEqual(PeerRefusalReason.Malformed, refused.Reason);
    }

    [TestMethod]
    public void SessionHello_FeatureIdentifierIsEmpty_IsRefused()
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
        var refused = Assert.ThrowsExactly<PeerProtocolException>(
            () => SessionHello.Read(PeerFrame.Decode(writer.Encode()).Body));

        Assert.AreEqual(PeerRefusalReason.Malformed, refused.Reason);
    }

    [TestMethod]
    public void SessionHello_MoreFeaturesThanTheLimit_IsRefused()
    {
        var tooMany = Enumerable.Range(0, SessionHello.MaximumFeatures + 1)
            .Select(i => $"f{i}").ToList();

        var refused = Assert.ThrowsExactly<PeerProtocolException>(
            () => PeerFrame.Encode(new SessionHello(1, 1, tooMany, [], "1.0.0", null)));

        Assert.AreEqual(PeerRefusalReason.Malformed, refused.Reason);
    }

    [TestMethod]
    public void SessionHello_CarriesAKeyThisVersionDoesNotKnow_SkipsItAndDecodesTheRest()
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

        Assert.AreEqual(1, hello.MinimumVersion);
        Assert.AreEqual(2, hello.MaximumVersion);
    }

    [TestMethod]
    public void SessionAccept_EncodedAndDecoded_RoundTrips()
    {
        var accept = new SessionAccept(3, ["one", "two"]);

        var (type, body) = PeerFrame.Decode(PeerFrame.Encode(accept));

        Assert.AreEqual(PeerMessageType.SessionAccept, type);
        Assert.AreEqual(accept, SessionAccept.Read(body));
    }

    [TestMethod]
    public void AuthenticationClaim_EncodedAndDecoded_RoundTrips()
    {
        using var keypair = PeerKeypair.Generate();
        var auth = SessionAuth.Create(keypair.Identity);

        var (type, body) = PeerFrame.Decode(PeerFrame.Encode(auth));

        Assert.AreEqual(PeerMessageType.SessionAuth, type);
        Assert.AreEqual(auth, SessionAuth.Read(body));
    }

    [TestMethod]
    public void AuthenticationProof_EncodedAndDecoded_RoundTrips()
    {
        var proof = new SessionAuthProof(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());

        var (type, body) = PeerFrame.Decode(PeerFrame.Encode(proof));

        Assert.AreEqual(PeerMessageType.SessionAuthProof, type);
        Assert.AreEqual(proof, SessionAuthProof.Read(body));
    }

    [TestMethod]
    [DataRow(31)]
    [DataRow(33)]
    public void SessionAuth_IdentityIsNotThirtyTwoBytes_RefusesAsMalformed(int length)
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
        var refused = Assert.ThrowsExactly<PeerProtocolException>(
            () => SessionAuth.Read(PeerFrame.Decode(writer.Encode()).Body));

        Assert.AreEqual(PeerRefusalReason.Malformed, refused.Reason);
    }

    [TestMethod]
    public void AuthenticationProof_NotASignatureLength_IsRefused()
    {
        var writer = new CborWriter(CborConformanceMode.Canonical);
        writer.WriteStartMap(2);
        writer.WriteInt32(PeerFrame.MessageTypeKey);
        writer.WriteUInt32((uint)PeerMessageType.SessionAuthProof);
        writer.WriteInt32(1);
        writer.WriteByteString(new byte[16]);
        writer.WriteEndMap();

        var refused = Assert.ThrowsExactly<PeerProtocolException>(
            () => SessionAuthProof.Read(PeerFrame.Decode(writer.Encode()).Body));

        Assert.AreEqual(PeerRefusalReason.Malformed, refused.Reason);
    }

    [TestMethod]
    public void SessionRefusal_AnyRefusal_CarriesTheCodeAClientBranchesOn()
    {
        var refusal = SessionRefuse.From(
            new PeerProtocolException(PeerRefusalReason.Revoked, "This pairing was revoked."));

        var read = SessionRefuse.Read(PeerFrame.Decode(PeerFrame.Encode(refusal)).Body);

        Assert.AreEqual(PeerRefusalReason.Revoked, read.Reason);
        Assert.AreEqual("This pairing was revoked.", read.Text);
    }

    [TestMethod]
    public void SessionRefusal_TextExceedsTheLimit_TruncatesOnACharacterBoundary()
    {
        // The text is written for this side's operator and served to a stranger,
        // so it is clipped rather than allowed to grow — and clipped without
        // producing the invalid UTF-8 that a naive byte cut would.
        var long_ = string.Concat(Enumerable.Repeat("é", 400));
        var refusal = new SessionRefuse(PeerRefusalReason.Busy, long_);

        var read = SessionRefuse.Read(PeerFrame.Decode(PeerFrame.Encode(refusal)).Body);

        Assert.IsTrue(Encoding.UTF8.GetByteCount(read.Text) <= SessionRefuse.MaximumTextBytes);
        Assert.StartsWith("éé", read.Text, StringComparison.Ordinal);
        Assert.DoesNotContain('�', read.Text);
    }
}

/// <summary>
/// Version selection and feature negotiation (specification peer-protocol
/// 02 §3–§4): what two independent implementations must agree on.
/// </summary>
[TestClass]
public sealed class PeerSessionNegotiationTests
{
    private static SessionHello Hello(
        ushort minimum, ushort maximum, string[]? offered = null, string[]? required = null) =>
        new(minimum, maximum, offered ?? [], required ?? [], "1.0.0", null);

    [TestMethod]
    public void VersionSelection_RangesOverlap_ChoosesTheHighestBothSidesSpeak()
    {
        Assert.AreEqual(3, PeerSessionNegotiation.SelectVersion(Hello(1, 5), Hello(2, 3)));
        Assert.AreEqual(3, PeerSessionNegotiation.SelectVersion(Hello(2, 3), Hello(1, 5)));
        Assert.AreEqual(1, PeerSessionNegotiation.SelectVersion(Hello(1, 1), Hello(1, 1)));
    }

    [TestMethod]
    public void VersionSelection_ArgumentsSwapped_ChoosesTheSameVersion()
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
            Assert.AreEqual(
                PeerSessionNegotiation.SelectVersion(a, b),
                PeerSessionNegotiation.SelectVersion(b, a));
        }
    }

    [TestMethod]
    public void VersionSelection_RangesDoNotOverlap_RefusesNamingBoth()
    {
        var refused = Assert.ThrowsExactly<PeerProtocolException>(
            () => PeerSessionNegotiation.SelectVersion(Hello(1, 2), Hello(7, 9)));

        Assert.AreEqual(PeerRefusalReason.VersionUnsupported, refused.Reason);

        // Both ranges, per 02 §3. Without them the operator cannot tell which
        // side needs upgrading, which is the only question this refusal answers.
        Assert.Contains("1–2", refused.Message, StringComparison.Ordinal);
        Assert.Contains("7–9", refused.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void FeatureNegotiation_BothSidesOfferFeatures_KeepsTheIntersection()
    {
        var effect = PeerSessionNegotiation.SelectFeatures(
            Hello(1, 1, offered: ["a", "b", "c"]),
            Hello(1, 1, offered: ["b", "c", "d"]));

        SequenceAssert.AreEqual(["b", "c"], effect);
    }

    [TestMethod]
    public void FeatureNegotiation_TheSameIntersection_IsOrderedIdenticallyOnBothSides()
    {
        var ours = Hello(1, 1, offered: ["zeta", "alpha", "mu"]);
        var theirs = Hello(1, 1, offered: ["mu", "zeta", "alpha"]);

        // A set has no order, and an unordered accept is one two sides can
        // disagree about while both being right.
        SequenceAssert.AreEqual(
            PeerSessionNegotiation.SelectFeatures(ours, theirs),
            PeerSessionNegotiation.SelectFeatures(theirs, ours));
        SequenceAssert.AreEqual(["alpha", "mu", "zeta"], PeerSessionNegotiation.SelectFeatures(ours, theirs));
    }

    [TestMethod]
    public void FeatureNegotiation_PeerDoesNotOfferARequiredFeature_RefusesTheSession()
    {
        var refused = Assert.ThrowsExactly<PeerProtocolException>(
            () => PeerSessionNegotiation.SelectFeatures(
                Hello(1, 1, offered: ["a"], required: ["b"]),
                Hello(1, 1, offered: ["a"])));

        Assert.AreEqual(PeerRefusalReason.FeatureUnsupported, refused.Reason);
        Assert.Contains("'b'", refused.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void FeatureNegotiation_PeerRequiresAFeatureOfUs_ChecksItToo()
    {
        // They would refuse anyway. Refusing first costs a message and avoids
        // the half-open state 02 §6 says must not exist.
        var refused = Assert.ThrowsExactly<PeerProtocolException>(
            () => PeerSessionNegotiation.SelectFeatures(
                Hello(1, 1, offered: ["a"]),
                Hello(1, 1, offered: ["a"], required: ["z"])));

        Assert.AreEqual(PeerRefusalReason.FeatureUnsupported, refused.Reason);
    }

    [TestMethod]
    public void FeatureNegotiation_RequiredFeatureIsOfferedByBoth_AcceptsTheSession()
    {
        var effect = PeerSessionNegotiation.SelectFeatures(
            Hello(1, 1, offered: ["a", "b"], required: ["a"]),
            Hello(1, 1, offered: ["a", "c"], required: ["a"]));

        SequenceAssert.AreEqual(["a"], effect);
    }

    [TestMethod]
    public void FeatureNegotiation_TwoCurrentBuilds_AgreeOnTheFeaturesThisBuildOffers()
    {
        var hello = PeerSessionNegotiation.Hello("1.0.0");

        var accept = PeerSessionNegotiation.Negotiate(hello, hello);

        // The mechanism predates its first feature (02 §6) and now carries it:
        // two current builds agree on exactly what this build offers, and a
        // hello offering nothing still negotiates — the intersection is empty,
        // never an error.
        Assert.AreEqual(PeerSessionNegotiation.CurrentVersion, accept.Version);
        SequenceAssert.AreEqual(PeerSessionNegotiation.SupportedFeatures.Order(StringComparer.Ordinal), accept.Features);

        var bare = PeerSessionNegotiation.Negotiate(
            hello, hello with { FeaturesOffered = [], FeaturesRequired = [] });
        Assert.IsEmpty(bare.Features);
    }

    [TestMethod]
    public void SessionHello_SentByADestination_CarriesTermsWhereASourcesDoesNot()
    {
        var terms = new PeerTerms(500_000_000_000, "every 1h", 4);

        var destination = PeerSessionNegotiation.Hello("1.0.0", terms);
        var source = PeerSessionNegotiation.Hello("1.0.0");

        Assert.AreEqual(terms, SessionHello.Read(PeerFrame.Decode(PeerFrame.Encode(destination)).Body).Terms);
        Assert.IsNull(SessionHello.Read(PeerFrame.Decode(PeerFrame.Encode(source)).Body).Terms);
    }
}
