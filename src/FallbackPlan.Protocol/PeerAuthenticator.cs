using Bodu;

namespace FallbackPlan.Protocol;

/// <summary>
/// Drives one session from <see cref="PeerSessionState.Encrypted"/> to
/// <see cref="PeerSessionState.Authenticated"/>
/// (specification peer-protocol 02 §2–§3).
/// </summary>
/// <remarks>
/// <para>
/// TLS has completed and has authenticated nobody. This is the thing that
/// decides whether there is a peer here or a stranger, and it is deliberately
/// the only route to <see cref="PeerSessionState.Authenticated"/>.
/// </para>
/// <para>
/// The order is fixed by 02 §3.1: send this side's <see cref="SessionAuth"/>
/// immediately, without waiting; on the peer's, send the proof; on the peer's
/// proof, verify. Both sides do the same thing at the same time, which is why
/// authentication costs one round trip rather than two.
/// </para>
/// </remarks>
public sealed class PeerAuthenticator
{
    private readonly PeerKeypair _keypair;
    private readonly PeerGrantStore _grants;
    private readonly byte[] _ourBinding;
    private readonly byte[] _theirBinding;
    private readonly SessionAuth _ours;
    private readonly PeerIdentity? _expected;

    private SessionAuth? _theirs;

    /// <summary>Starts authentication for a connection whose TLS has completed.</summary>
    /// <param name="keypair">This device's peer keypair.</param>
    /// <param name="grants">The pinned pairings this device holds.</param>
    /// <param name="role">Which end of the connection this side is.</param>
    /// <param name="ourTlsPublicKeyHash">This side's channel binding.</param>
    /// <param name="theirTlsPublicKeyHash">The peer's channel binding, from the certificate it presented.</param>
    /// <param name="expected">
    /// The peer this side dialled, when it dialled one. An initiator always has
    /// this; a responder never does, because an inbound connection announces
    /// nothing until <see cref="Accept"/>.
    /// </param>
    public PeerAuthenticator(
        PeerKeypair keypair,
        PeerGrantStore grants,
        PeerSessionRole role,
        ReadOnlySpan<byte> ourTlsPublicKeyHash,
        ReadOnlySpan<byte> theirTlsPublicKeyHash,
        PeerIdentity? expected = null)
    {
        ThrowHelper.ThrowIfNull(keypair);
        ThrowHelper.ThrowIfNull(grants);

        _keypair = keypair;
        _grants = grants;
        Role = role;
        _ourBinding = ourTlsPublicKeyHash.ToArray();
        _theirBinding = theirTlsPublicKeyHash.ToArray();
        _expected = expected;
        _ours = SessionAuth.Create(keypair.Identity);
    }

    /// <summary>Which end of the connection this side is.</summary>
    public PeerSessionRole Role { get; }

    /// <summary>How far this session has got (02 §2).</summary>
    public PeerSessionState State { get; private set; } = PeerSessionState.Encrypted;

    /// <summary>The grant the peer authenticated as, once it has.</summary>
    public PeerGrant? Peer { get; private set; }

    /// <summary>This side's claim, to send at once (02 §3.1).</summary>
    /// <returns>The message.</returns>
    public SessionAuth Offer() => _ours;

    /// <summary>
    /// Takes the peer's claim and returns this side's proof.
    /// </summary>
    /// <param name="theirs">What the peer sent.</param>
    /// <returns>The proof to send.</returns>
    /// <exception cref="PeerProtocolException">
    /// The peer is not paired, was revoked, or presented a key that is not the
    /// pinned one (02 §3.3 (1)).
    /// </exception>
    /// <remarks>
    /// <para>
    /// The grant is resolved here, before any signature is checked, because
    /// there is no point verifying a proof from a device this one has never
    /// agreed to talk to. The lookup is by full key, so a peer whose key
    /// differs from the pinned one is simply not found.
    /// </para>
    /// <para>
    /// <c>identity_changed</c> is reported only where this side had an
    /// expectation to violate — that is, where it dialled a particular peer.
    /// A responder taking an inbound connection has none, so an unrecognised
    /// key there is <c>not_paired</c> and cannot honestly be anything else:
    /// distinguishing them would mean knowing which pairing a stranger *meant*
    /// to be, and the only thing a stranger offers is the key that is wrong.
    /// </para>
    /// </remarks>
    public SessionAuthProof Accept(SessionAuth theirs)
    {
        ThrowHelper.ThrowIfNull(theirs);
        Require(PeerSessionState.Encrypted);

        if (_expected is not null && theirs.Identity != _expected)
        {
            // 01 §2.5. This is the case that matters: the operator asked to
            // reach one device and something else answered.
            throw new PeerProtocolException(
                PeerRefusalReason.IdentityChanged,
                $"Peer {_expected.Fingerprint} was expected; {theirs.Identity.Fingerprint} answered.");
        }

        var grant = _grants.Find(theirs.Identity)
            ?? throw new PeerProtocolException(
                PeerRefusalReason.NotPaired,
                $"No grant exists for peer {theirs.Identity.Fingerprint}.");

        _theirs = theirs;
        Peer = grant;

        var (initiator, responder) = Contributions(theirs);
        return SessionBinding.Prove(_keypair, initiator, responder, Role);
    }

    /// <summary>Verifies the peer's proof and, if it holds, authenticates the session.</summary>
    /// <param name="proof">What the peer sent.</param>
    /// <returns>The grant the peer authenticated as.</returns>
    /// <exception cref="PeerProtocolException">
    /// The signature does not verify against the transcript this side built from
    /// the certificates actually exchanged here (02 §3.3 (2)–(3)).
    /// </exception>
    public PeerGrant Verify(SessionAuthProof proof)
    {
        ThrowHelper.ThrowIfNull(proof);
        Require(PeerSessionState.Encrypted);

        if (_theirs is null || Peer is null)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, "A proof arrived before the claim it proves.");
        }

        var (initiator, responder) = Contributions(_theirs);
        var peerRole = Role == PeerSessionRole.Initiator ? PeerSessionRole.Responder : PeerSessionRole.Initiator;

        if (!SessionBinding.Verify(proof, initiator, responder, peerRole))
        {
            // Which check failed is not said, on the wire or here. A bad
            // signature and a binding that does not match this connection both
            // mean the peer did not prove what it claimed, and separating them
            // for a stranger is reconnaissance (02 §8).
            throw new PeerProtocolException(
                PeerRefusalReason.AuthenticationFailed,
                $"Peer {_theirs.Identity.Fingerprint} did not prove possession of the identity it presented.");
        }

        State = PeerSessionState.Authenticated;
        return Peer;
    }

    /// <summary>Records that 02 §4 completed, opening the session.</summary>
    /// <exception cref="PeerProtocolException">The session is not authenticated.</exception>
    public void Open()
    {
        Require(PeerSessionState.Authenticated);
        State = PeerSessionState.Open;
    }

    /// <summary>Whether a message type may be sent or received in the current state (02 §2).</summary>
    /// <param name="type">The message type.</param>
    /// <returns><see langword="true"/> when it is permitted now.</returns>
    public bool Permits(PeerMessageType type) => Permits(State, type);

    /// <summary>Whether a message type is permitted in a given state (02 §2).</summary>
    /// <param name="state">The state.</param>
    /// <param name="type">The message type.</param>
    /// <returns><see langword="true"/> when it is permitted.</returns>
    public static bool Permits(PeerSessionState state, PeerMessageType type) => type switch
    {
        // A refusal is permitted everywhere, because the alternative to saying
        // "no" in a state that forbids saying anything is hanging up silently.
        PeerMessageType.SessionRefuse => true,

        PeerMessageType.PairOffer or PeerMessageType.PairAccept
            or PeerMessageType.PairConfirm or PeerMessageType.PairRefuse
            or PeerMessageType.SessionAuth or PeerMessageType.SessionAuthProof =>
            state == PeerSessionState.Encrypted,

        PeerMessageType.SessionHello or PeerMessageType.SessionAccept =>
            state == PeerSessionState.Authenticated,

        // The payload documents (03–05) apply once the session is Open, and
        // only then. A replication frame before Open is a stranger trying to
        // move objects, which is exactly what the state machine forbids.
        PeerMessageType.ReplicationOffer or PeerMessageType.ReplicationInventory
            or PeerMessageType.ReplicationObject or PeerMessageType.ReplicationChunk
            or PeerMessageType.ReplicationComplete or PeerMessageType.ReplicationAck =>
            state == PeerSessionState.Open,

        _ => false,
    };

    private (SessionBindingContribution Initiator, SessionBindingContribution Responder) Contributions(
        SessionAuth theirs)
    {
        var ours = new SessionBindingContribution(_keypair.Identity, _ourBinding, _ours.Nonce);
        var them = new SessionBindingContribution(theirs.Identity, _theirBinding, theirs.Nonce);

        return Role == PeerSessionRole.Initiator ? (ours, them) : (them, ours);
    }

    private void Require(PeerSessionState state)
    {
        if (State != state)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, $"This step is not permitted in the {State} state.");
        }
    }
}
