using Bodu;

namespace FallbackPlan.Protocol;

/// <summary>
/// What a human is shown and asked to approve during a pairing
/// (specification peer-protocol 01 §2.3–§2.4).
/// </summary>
/// <param name="ShortAuthenticationString">
/// The six characters, in two groups of three, to read aloud and compare
/// against the other device's.
/// </param>
/// <param name="PeerIdentity">The peer's long-lived identity — pinned on approval, verified always against the full key.</param>
/// <param name="PeerLabel">The human-chosen label the peer offered, for display only.</param>
/// <param name="TheirRoleForUs">The role the peer declared it will record for this device — part of what the human approves, and inside the signed transcript (ADR-0030 Amendment 2).</param>
public sealed record PairingProspect(
    string ShortAuthenticationString, PeerIdentity PeerIdentity, string PeerLabel, PeerRole TheirRoleForUs);

/// <summary>The result of a pairing attempt.</summary>
/// <param name="Grant">The pinned grant when both sides approved; otherwise null.</param>
/// <param name="Refusal">Why it did not complete, when it did not.</param>
public sealed record PairingResult(PeerGrant? Grant, PairRefuse? Refusal)
{
    /// <summary>Whether both humans approved and a grant was pinned.</summary>
    public bool Approved => Grant is not null;
}

/// <summary>
/// Drives the pairing ceremony over a duplex stream (specification
/// peer-protocol 01 §2; ADR-0030 §2): exchange the ephemeral shares, derive
/// the string both humans compare, and — only after this side's human
/// approves — sign and pin.
/// </summary>
/// <remarks>
/// <para>
/// The connection carrying this MUST NOT be reused for anything else (02 §1):
/// a channel that has only paired has authenticated nobody. The caller opens
/// a fresh connection for the session that follows.
/// </para>
/// <para>
/// The held-confirm rule of 01 §2.4 is structural here rather than a check: a
/// side signs and sends its confirmation only after its own approval callback
/// returns true, and pins only after it has both approved and verified the
/// peer's confirmation. A confirmation the peer sends early is therefore read
/// after this side has already decided, never in place of the deciding.
/// </para>
/// </remarks>
public static class PairingCeremony
{
    /// <summary>Renders the short authentication string in two groups of three (01 §2.3).</summary>
    /// <param name="shortAuthenticationString">The six-character string.</param>
    /// <returns>The two groups joined by a space, for a human to read.</returns>
    public static string Group(string shortAuthenticationString)
    {
        ThrowHelper.ThrowIfNull(shortAuthenticationString);

        return shortAuthenticationString.Length == PairingTranscript.ShortAuthenticationStringCharacters
            ? $"{shortAuthenticationString[..3]} {shortAuthenticationString[3..]}"
            : shortAuthenticationString;
    }

    /// <summary>Runs the offerer (dialling) side of the ceremony.</summary>
    /// <param name="stream">The pairing-only connection.</param>
    /// <param name="keypair">This device's peer keypair.</param>
    /// <param name="grants">The grant store to pin into on success.</param>
    /// <param name="label">This device's human-chosen label.</param>
    /// <param name="role">The role to record for the peer on this device (01 §3).</param>
    /// <param name="approve">Shows the prospect to a human and returns their decision.</param>
    /// <param name="nowUnixMilliseconds">The pairing timestamp to record.</param>
    /// <param name="cancellationToken">Cancels the ceremony.</param>
    /// <returns>The result — a grant when both approved, a refusal otherwise.</returns>
    public static async ValueTask<PairingResult> OfferAsync(
        Stream stream,
        PeerKeypair keypair,
        PeerGrantStore grants,
        string label,
        PeerRole role,
        Func<PairingProspect, CancellationToken, ValueTask<bool>> approve,
        ulong nowUnixMilliseconds,
        CancellationToken cancellationToken = default)
    {
        ThrowHelper.ThrowIfNull(stream);
        ThrowHelper.ThrowIfNull(keypair);
        ThrowHelper.ThrowIfNull(grants);
        ThrowHelper.ThrowIfNull(approve);

        using var exchange = PairingExchange.Start();
        var contribution = new PairingContribution(keypair.Identity, exchange.PublicKey, exchange.Nonce);
        await PeerFrame.WriteAsync(
            stream, new PairOffer(contribution, label, PeerSessionNegotiation.CurrentVersion, role), cancellationToken)
            .ConfigureAwait(false);

        var accept = await ReadAsync(stream, PeerMessageType.PairAccept, PairAccept.Read, cancellationToken)
            .ConfigureAwait(false);
        if (accept is null)
        {
            return Refused(cancellationToken);
        }

        var responder = accept.Contribution;
        var transcript = PairingTranscript.Build(
            contribution, responder, accept.SelectedVersion, offererPins: role, responderPins: accept.RoleForOfferer);

        return await CompleteAsync(
            stream, keypair, grants, exchange, responder, weAreOfferer: true, transcript,
            accept.Label, accept.Terms ?? PeerTerms.None, role, accept.RoleForOfferer,
            approve, nowUnixMilliseconds, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Runs the responder (accepting) side of the ceremony.</summary>
    /// <param name="stream">The pairing-only connection.</param>
    /// <param name="keypair">This device's peer keypair.</param>
    /// <param name="grants">The grant store to pin into on success.</param>
    /// <param name="label">This device's human-chosen label.</param>
    /// <param name="role">The role to record for the peer on this device (01 §3).</param>
    /// <param name="terms">Terms this device offers when it is the destination (01 §4).</param>
    /// <param name="approve">Shows the prospect to a human and returns their decision.</param>
    /// <param name="nowUnixMilliseconds">The pairing timestamp to record.</param>
    /// <param name="cancellationToken">Cancels the ceremony.</param>
    /// <returns>The result — a grant when both approved, a refusal otherwise.</returns>
    public static async ValueTask<PairingResult> AcceptAsync(
        Stream stream,
        PeerKeypair keypair,
        PeerGrantStore grants,
        string label,
        PeerRole role,
        PeerTerms? terms,
        Func<PairingProspect, CancellationToken, ValueTask<bool>> approve,
        ulong nowUnixMilliseconds,
        CancellationToken cancellationToken = default)
    {
        ThrowHelper.ThrowIfNull(stream);
        ThrowHelper.ThrowIfNull(keypair);
        ThrowHelper.ThrowIfNull(grants);
        ThrowHelper.ThrowIfNull(approve);

        var offer = await ReadAsync(stream, PeerMessageType.PairOffer, PairOffer.Read, cancellationToken)
            .ConfigureAwait(false);
        if (offer is null)
        {
            return Refused(cancellationToken);
        }

        var version = Math.Min(PeerSessionNegotiation.CurrentVersion, offer.HighestVersion);
        if (version < PeerSessionNegotiation.OldestSupportedVersion)
        {
            var refusal = new PairRefuse(
                PeerRefusalReason.VersionUnsupported,
                $"This device speaks pairing versions {PeerSessionNegotiation.OldestSupportedVersion}"
                + $"–{PeerSessionNegotiation.CurrentVersion} and was offered {offer.HighestVersion}.");
            await PeerFrame.WriteAsync(stream, refusal, cancellationToken).ConfigureAwait(false);
            return new PairingResult(null, refusal);
        }

        using var exchange = PairingExchange.Start();
        var contribution = new PairingContribution(keypair.Identity, exchange.PublicKey, exchange.Nonce);
        await PeerFrame.WriteAsync(
            stream, new PairAccept(contribution, label, (ushort)version, terms, role), cancellationToken)
            .ConfigureAwait(false);

        var offerer = offer.Contribution;
        var transcript = PairingTranscript.Build(
            offerer, contribution, (ushort)version, offererPins: offer.RoleForResponder, responderPins: role);

        return await CompleteAsync(
            stream, keypair, grants, exchange, offerer, weAreOfferer: false, transcript,
            offer.Label, terms ?? PeerTerms.None, role, offer.RoleForResponder,
            approve, nowUnixMilliseconds, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<PairingResult> CompleteAsync(
        Stream stream,
        PeerKeypair keypair,
        PeerGrantStore grants,
        PairingExchange exchange,
        PairingContribution peer,
        bool weAreOfferer,
        byte[] transcript,
        string peerLabel,
        PeerTerms terms,
        PeerRole role,
        PeerRole theirRoleForUs,
        Func<PairingProspect, CancellationToken, ValueTask<bool>> approve,
        ulong nowUnixMilliseconds,
        CancellationToken cancellationToken)
    {
        var secret = exchange.DeriveSharedSecret(peer.EphemeralPublicKey.Span);
        try
        {
            // The string's salt is offerer-nonce-first, matching the transcript's
            // role order — so which nonce is "offerer" follows from this side's role.
            var offererNonce = weAreOfferer ? exchange.Nonce : peer.Nonce.ToArray();
            var responderNonce = weAreOfferer ? peer.Nonce.ToArray() : exchange.Nonce;
            var sas = PairingTranscript.ShortAuthenticationString(secret, offererNonce, responderNonce, transcript);

            // 01 §2.4: this side's human decides FIRST. Nothing is signed, sent
            // or pinned until this returns true — which is what makes a peer's
            // early confirmation something that is held, never something that
            // approves on the human's behalf.
            var prospect = new PairingProspect(Group(sas), peer.Identity, peerLabel, theirRoleForUs);
            if (!await approve(prospect, cancellationToken).ConfigureAwait(false))
            {
                var refusal = new PairRefuse(PeerRefusalReason.PairingDeclined, "The operator declined the pairing.");
                await PeerFrame.WriteAsync(stream, refusal, cancellationToken).ConfigureAwait(false);
                return new PairingResult(null, refusal);
            }

            var confirmation = PairingTranscript.ConfirmationBytes(transcript);

            // This side approved and sends its confirmation. The peer may
            // already have declined and closed — both sides confirm or refuse
            // on their own human's word, without waiting — so a write or read
            // that fails on a closed connection is that decline reaching us,
            // not an error: the pairing simply did not complete.
            (PeerMessageType Type, System.Formats.Cbor.CborReader Body)? frame;
            try
            {
                await PeerFrame.WriteAsync(stream, new PairConfirm(keypair.Sign(confirmation)), cancellationToken)
                    .ConfigureAwait(false);
                frame = await PeerFrame.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return Refused(cancellationToken);
            }

            if (frame is null)
            {
                return Refused(cancellationToken);
            }

            var (type, body) = frame.Value;
            if (type == PeerMessageType.PairRefuse)
            {
                return new PairingResult(null, PairRefuse.Read(body));
            }

            if (type != PeerMessageType.PairConfirm)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed, $"Expected a pairing confirmation; the peer sent a {type}.");
            }

            var theirConfirm = PairConfirm.Read(body);
            if (!peer.Identity.Verify(confirmation, theirConfirm.Signature.Span))
            {
                // The peer could not prove possession of the key it offered.
                // This is not the human declining; it is a key that is wrong.
                var refusal = new PairRefuse(
                    PeerRefusalReason.AuthenticationFailed,
                    "The peer did not prove possession of the identity it offered.");
                await PeerFrame.WriteAsync(stream, refusal, cancellationToken).ConfigureAwait(false);
                throw new PeerProtocolException(refusal.Reason, refusal.Text);
            }

            var grant = new PeerGrant(peer.Identity, peerLabel, role, terms, nowUnixMilliseconds);
            grants.Pin(grant);
            return new PairingResult(grant, null);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static async ValueTask<T?> ReadAsync<T>(
        Stream stream,
        PeerMessageType expected,
        Func<System.Formats.Cbor.CborReader, T> read,
        CancellationToken cancellationToken)
        where T : class
    {
        var frame = await PeerFrame.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        if (frame is null)
        {
            return null;
        }

        var (type, body) = frame.Value;
        if (type == PeerMessageType.PairRefuse)
        {
            return null;
        }

        if (type != expected)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed, $"Expected a {expected}; the peer sent a {type}.");
        }

        return read(body);
    }

    private static PairingResult Refused(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new PairingResult(
            null, new PairRefuse(PeerRefusalReason.PairingDeclined, "The peer closed before completing the ceremony."));
    }
}
