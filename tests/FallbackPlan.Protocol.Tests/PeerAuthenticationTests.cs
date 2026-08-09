using FallbackPlan.Protocol;
using FallbackPlan.TestSupport;

namespace FallbackPlan.Protocol.Tests;

/// <summary>
/// Channel-bound authentication (specification peer-protocol 02 §3): the
/// property RFC 7250 was chosen for, obtained without it.
/// </summary>
[TestClass]
public sealed class SessionBindingTests
{
    private static SessionBindingContribution Contribution(PeerIdentity identity, byte fill) =>
        new(identity, Bytes(fill, SessionBinding.TlsPublicKeyHashLength), Bytes((byte)(fill + 1), SessionBinding.NonceLength));

    private static byte[] Bytes(byte fill, int length)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, fill);
        return bytes;
    }

    [TestMethod]
    public void ChannelProof_TheTranscriptBothSidesBuild_Verifies()
    {
        using var initiator = PeerKeypair.Generate();
        using var responder = PeerKeypair.Generate();

        var i = Contribution(initiator.Identity, 0x11);
        var r = Contribution(responder.Identity, 0x22);

        var proof = SessionBinding.Prove(initiator, i, r, PeerSessionRole.Initiator);

        Assert.IsTrue(SessionBinding.Verify(proof, i, r, PeerSessionRole.Initiator));
    }

    [TestMethod]
    public void ChannelProof_CheckedUnderTheOtherRole_FailsToVerify()
    {
        using var keypair = PeerKeypair.Generate();

        var i = Contribution(keypair.Identity, 0x11);
        var r = Contribution(keypair.Identity, 0x22);

        var proof = SessionBinding.Prove(keypair, i, r, PeerSessionRole.Initiator);

        // Role separation. Without it an attacker could open a connection back
        // to a peer and reflect that peer's own proof at it — the peer would
        // verify its own signature and be satisfied.
        Assert.IsFalse(SessionBinding.Verify(proof, i, r, PeerSessionRole.Responder));
    }

    [TestMethod]
    public void ChannelProof_FromAnotherConnection_FailsToVerifyOnThisOne()
    {
        using var initiator = PeerKeypair.Generate();
        using var responder = PeerKeypair.Generate();

        var i = Contribution(initiator.Identity, 0x11);
        var r = Contribution(responder.Identity, 0x22);
        var proof = SessionBinding.Prove(initiator, i, r, PeerSessionRole.Initiator);

        // Same peers, different TLS certificates: a later connection. The whole
        // mechanism is that this fails.
        var laterConnection = r with { TlsPublicKeyHash = Bytes(0x99, SessionBinding.TlsPublicKeyHashLength) };

        Assert.IsFalse(SessionBinding.Verify(proof, i, laterConnection, PeerSessionRole.Initiator));
    }

    [TestMethod]
    public void ChannelProof_NonceChanged_FailsToVerify()
    {
        using var initiator = PeerKeypair.Generate();
        using var responder = PeerKeypair.Generate();

        var i = Contribution(initiator.Identity, 0x11);
        var r = Contribution(responder.Identity, 0x22);
        var proof = SessionBinding.Prove(initiator, i, r, PeerSessionRole.Initiator);

        // The nonces are the belt to the certificate's braces: 02 §1's
        // "never reuse a certificate" is a rule a peer cannot verify about the
        // other side, so freshness that does not depend on it is carried too.
        var replayed = r with { Nonce = Bytes(0x77, SessionBinding.NonceLength) };

        Assert.IsFalse(SessionBinding.Verify(proof, i, replayed, PeerSessionRole.Initiator));
    }

    [TestMethod]
    public void SessionNonce_GeneratedRepeatedly_IsFreshEachTime()
    {
        Assert.AreNotEqual(SessionBinding.Nonce(), SessionBinding.Nonce());
    }

    [TestMethod]
    public void SessionBinding_WrongLength_IsRejectedAtTheBoundary()
    {
        using var keypair = PeerKeypair.Generate();

        var short_ = new SessionBindingContribution(
            keypair.Identity, new byte[4], Bytes(0, SessionBinding.NonceLength));
        var good = Contribution(keypair.Identity, 0x22);

        Assert.ThrowsExactly<ArgumentException>(
            () => SessionBinding.Transcript(short_, good, PeerSessionRole.Initiator));
    }
}

/// <summary>
/// The session state machine and its one route to authenticated
/// (specification peer-protocol 02 §2–§3).
/// </summary>
[TestClass]
public sealed class PeerAuthenticatorTests : IDisposable
{
    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "fbp-auth-tests", Guid.NewGuid().ToString("n"));

    private readonly PeerKeypair _dialler = PeerKeypair.Generate();
    private readonly PeerKeypair _answerer = PeerKeypair.Generate();

    private readonly byte[] _diallerBinding = Fill(0x01);
    private readonly byte[] _answererBinding = Fill(0x02);

    private static byte[] Fill(byte value)
    {
        var bytes = new byte[SessionBinding.TlsPublicKeyHashLength];
        Array.Fill(bytes, value);
        return bytes;
    }

    private PeerGrantStore Store(string name)
    {
        var store = PeerGrantStore.Open(Path.Combine(_stateDirectory, name));
        return store;
    }

    private static void Pin(PeerGrantStore store, PeerIdentity identity) =>
        store.Pin(new PeerGrant(identity, "a peer", PeerRole.Both, PeerTerms.None, 1_722_600_000_000));

    /// <summary>Runs both sides of 02 §3.1 against each other.</summary>
    private static (PeerAuthenticator Initiator, PeerAuthenticator Responder) Handshake(
        PeerAuthenticator initiator, PeerAuthenticator responder)
    {
        // Both send their claim at once, neither waiting: this is why
        // authentication costs one round trip rather than two.
        var initiatorOffer = initiator.Offer();
        var responderOffer = responder.Offer();

        var initiatorProof = initiator.Accept(responderOffer);
        var responderProof = responder.Accept(initiatorOffer);

        initiator.Verify(responderProof);
        responder.Verify(initiatorProof);

        return (initiator, responder);
    }

    private PeerAuthenticator Initiator(PeerGrantStore store, PeerIdentity? expected = null) =>
        new(_dialler, store, PeerSessionRole.Initiator, _diallerBinding, _answererBinding, expected);

    private PeerAuthenticator Responder(PeerGrantStore store) =>
        new(_answerer, store, PeerSessionRole.Responder, _answererBinding, _diallerBinding);

    [TestMethod]
    public void PeerAuthentication_TwoPairedPeers_AuthenticateEachOther()
    {
        var diallerStore = Store("dialler");
        var answererStore = Store("answerer");
        Pin(diallerStore, _answerer.Identity);
        Pin(answererStore, _dialler.Identity);

        var (initiator, responder) = Handshake(
            Initiator(diallerStore, _answerer.Identity), Responder(answererStore));

        Assert.AreEqual(PeerSessionState.Authenticated, initiator.State);
        Assert.AreEqual(PeerSessionState.Authenticated, responder.State);
        Assert.AreEqual(_answerer.Identity, initiator.Peer!.Identity);
        Assert.AreEqual(_dialler.Identity, responder.Peer!.Identity);
    }

    [TestMethod]
    public void PeerSession_JustOpened_IsEncryptedAndUnauthenticated()
    {
        // The state 02 §2 exists to name: TLS is done, and it has established
        // nobody's identity.
        var authenticator = Initiator(Store("dialler"));

        Assert.AreEqual(PeerSessionState.Encrypted, authenticator.State);
        Assert.IsNull(authenticator.Peer);
    }

    [TestMethod]
    public void PeerSession_HelloSentBeforeAuthentication_IsRefused()
    {
        var diallerStore = Store("dialler");
        var answererStore = Store("answerer");
        Pin(diallerStore, _answerer.Identity);
        Pin(answererStore, _dialler.Identity);

        var authenticator = Initiator(diallerStore, _answerer.Identity);

        // A stranger has no business learning what features this device
        // supports or what terms it offers.
        Assert.IsFalse(authenticator.Permits(PeerMessageType.SessionHello));
        Assert.IsTrue(authenticator.Permits(PeerMessageType.SessionAuth));

        Handshake(authenticator, Responder(answererStore));

        Assert.IsTrue(authenticator.Permits(PeerMessageType.SessionHello));
        Assert.IsFalse(authenticator.Permits(PeerMessageType.SessionAuth));
    }

    [TestMethod]
    public void PeerSession_AnyState_PermitsARefusal()
    {
        // The alternative to being allowed to say "no" in a state that forbids
        // saying anything is hanging up silently.
        foreach (var state in Enum.GetValues<PeerSessionState>())
        {
            Assert.IsTrue(PeerAuthenticator.Permits(state, PeerMessageType.SessionRefuse));
        }
    }

    [TestMethod]
    public void PeerAuthentication_PeerIsUnpaired_RefusesBeforeCheckingAnySignature()
    {
        var refused = Assert.ThrowsExactly<PeerProtocolException>(
            () => Responder(Store("answerer")).Accept(SessionAuth.Create(_dialler.Identity)));

        Assert.AreEqual(PeerRefusalReason.NotPaired, refused.Reason);
    }

    [TestMethod]
    public void PeerAuthentication_DiallerReachesTheWrongDevice_SaysSoInTheRefusal()
    {
        using var stranger = PeerKeypair.Generate();

        var store = Store("dialler");
        Pin(store, _answerer.Identity);
        Pin(store, stranger.Identity);

        // Both are paired, so this is not a pairing failure — the operator
        // asked to reach one device and a different one answered (01 §2.5).
        var refused = Assert.ThrowsExactly<PeerProtocolException>(
            () => Initiator(store, _answerer.Identity).Accept(SessionAuth.Create(stranger.Identity)));

        Assert.AreEqual(PeerRefusalReason.IdentityChanged, refused.Reason);
        Assert.Contains(_answerer.Identity.Fingerprint, refused.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void PeerAuthentication_ResponderExpectsNobody_ReportsNotPairedRatherThanGuessing()
    {
        using var stranger = PeerKeypair.Generate();

        var store = Store("answerer");
        Pin(store, _dialler.Identity);

        // It cannot honestly say "changed": the only thing this stranger
        // offered is the key that is wrong, and which pairing it *meant* to be
        // is not something an inbound connection reveals.
        var refused = Assert.ThrowsExactly<PeerProtocolException>(
            () => Responder(store).Accept(SessionAuth.Create(stranger.Identity)));

        Assert.AreEqual(PeerRefusalReason.NotPaired, refused.Reason);
    }

    [TestMethod]
    public void PeerAuthentication_ManInTheMiddleRelaysValidProofs_IsCaughtByBothSides()
    {
        var diallerStore = Store("dialler");
        var answererStore = Store("answerer");
        Pin(diallerStore, _answerer.Identity);
        Pin(answererStore, _dialler.Identity);

        // The attacker terminates TLS twice, so each genuine peer sees the
        // attacker's certificate rather than the other's.
        var attackerToDialler = Fill(0xAA);
        var attackerToAnswerer = Fill(0xBB);

        var dialler = new PeerAuthenticator(
            _dialler, diallerStore, PeerSessionRole.Initiator,
            _diallerBinding, attackerToDialler, _answerer.Identity);

        var answerer = new PeerAuthenticator(
            _answerer, answererStore, PeerSessionRole.Responder,
            _answererBinding, attackerToAnswerer);

        // Each genuine peer produces a perfectly valid proof — for its own leg.
        var diallerProof = dialler.Accept(answerer.Offer());
        var answererProof = answerer.Accept(dialler.Offer());

        // Relaying them is all the attacker can do: it holds neither private
        // key, so it cannot make the proof the other leg's transcript needs.
        var caughtByDialler = Assert.ThrowsExactly<PeerProtocolException>(() => dialler.Verify(answererProof));
        var caughtByAnswerer = Assert.ThrowsExactly<PeerProtocolException>(() => answerer.Verify(diallerProof));

        Assert.AreEqual(PeerRefusalReason.AuthenticationFailed, caughtByDialler.Reason);
        Assert.AreEqual(PeerRefusalReason.AuthenticationFailed, caughtByAnswerer.Reason);
        Assert.AreEqual(PeerSessionState.Encrypted, dialler.State);
        Assert.AreEqual(PeerSessionState.Encrypted, answerer.State);
    }

    [TestMethod]
    public void PeerAuthentication_ProofArrivesBeforeItsClaim_IsRefused()
    {
        var store = Store("dialler");
        Pin(store, _answerer.Identity);

        var refused = Assert.ThrowsExactly<PeerProtocolException>(
            () => Initiator(store, _answerer.Identity).Verify(new SessionAuthProof(new byte[64])));

        Assert.AreEqual(PeerRefusalReason.Malformed, refused.Reason);
    }

    [TestMethod]
    public void PeerSession_AlreadyAuthenticated_RefusesASecondAuthentication()
    {
        var diallerStore = Store("dialler");
        var answererStore = Store("answerer");
        Pin(diallerStore, _answerer.Identity);
        Pin(answererStore, _dialler.Identity);

        var (initiator, _) = Handshake(Initiator(diallerStore, _answerer.Identity), Responder(answererStore));

        initiator.Open();
        Assert.AreEqual(PeerSessionState.Open, initiator.State);

        Assert.ThrowsExactly<PeerProtocolException>(() => initiator.Accept(SessionAuth.Create(_answerer.Identity)));
    }

    public void Dispose()
    {
        _dialler.Dispose();
        _answerer.Dispose();

        if (Directory.Exists(_stateDirectory))
        {
            Directory.Delete(_stateDirectory, recursive: true);
        }
    }
}

/// <summary>
/// The certificate the platform's TLS insists on and this design trusts for
/// nothing (specification peer-protocol 02 §1).
/// </summary>
[TestClass]
public sealed class EphemeralTlsCertificateTests
{
    private static EphemeralTlsCertificate Create()
    {
        var now = new DateTimeOffset(2026, 8, 7, 0, 0, 0, TimeSpan.Zero);
        return EphemeralTlsCertificate.Create(now, now.AddDays(1));
    }

    [TestMethod]
    public void PeerSession_EachConnection_DerivesADifferentKey()
    {
        using var first = Create();
        using var second = Create();

        // 02 §1 requires this, and the requirement is load-bearing: reuse would
        // let one connection's binding describe another.
        Assert.AreNotEqual(first.PublicKeyHash, second.PublicKeyHash);
    }

    [TestMethod]
    public void SessionBinding_AnyCertificate_IsTheHashOfItsPublicKey()
    {
        using var certificate = Create();

        // The peer has the certificate, not the object that made it, so it must
        // reach the same 32 bytes from the certificate alone.
        SequenceAssert.AreEqual(certificate.PublicKeyHash, EphemeralTlsCertificate.BindingOf(certificate.Certificate));
        Assert.AreEqual(SessionBinding.TlsPublicKeyHashLength, certificate.PublicKeyHash.Length);
    }

    [TestMethod]
    public void SessionCertificate_AnyCertificate_CarriesNoIdentityAReaderCouldMistakeForOne()
    {
        using var certificate = Create();

        // Self-signed, and the subject is a fixed placeholder shared by every
        // connection this build makes. Nothing checks it, and a hostname or a
        // fingerprint here would invite someone to — the identity is checked
        // in 02 §3.
        Assert.AreEqual(certificate.Certificate.Subject, certificate.Certificate.Issuer);
        Assert.AreEqual("CN=fallbackplan-peer-session", certificate.Certificate.Subject);

        using var other = Create();
        Assert.AreEqual(other.Certificate.Subject, certificate.Certificate.Subject);
    }
}
