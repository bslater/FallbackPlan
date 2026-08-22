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

    /// <summary>
    /// Runs the offerer side of the invite-authenticated ceremony (01 §2.7,
    /// ADR-0030 Amendment 3). No human is prompted here: the operator approved
    /// by entering the code, and the code — proven by MAC over the transcript
    /// and the channel bindings, in both directions — stands in for the string
    /// comparison.
    /// </summary>
    /// <param name="stream">The pairing-only connection.</param>
    /// <param name="keypair">This device's peer keypair.</param>
    /// <param name="grants">The grant store to pin into on success.</param>
    /// <param name="label">This device's human-chosen label.</param>
    /// <param name="invite">The parsed invite code the far operator issued.</param>
    /// <param name="initiatorBinding">The TLS initiator's channel binding.</param>
    /// <param name="responderBinding">The TLS responder's channel binding.</param>
    /// <param name="nowUnixMilliseconds">The pairing timestamp to record.</param>
    /// <param name="cancellationToken">Cancels the ceremony.</param>
    /// <returns>The result — a grant when both sides proved the code, a refusal otherwise.</returns>
    public static async ValueTask<PairingResult> OfferWithInviteAsync(
        Stream stream,
        PeerKeypair keypair,
        PeerGrantStore grants,
        string label,
        PairingInviteCode invite,
        ReadOnlyMemory<byte> initiatorBinding,
        ReadOnlyMemory<byte> responderBinding,
        ulong nowUnixMilliseconds,
        CancellationToken cancellationToken = default)
    {
        ThrowHelper.ThrowIfNull(stream);
        ThrowHelper.ThrowIfNull(keypair);
        ThrowHelper.ThrowIfNull(grants);
        ThrowHelper.ThrowIfNull(invite);

        // The code carries the issuer's committed role; this side records the
        // complement. One thing for two operators to agree on, not two.
        var role = PeerRoles.Complement(invite.IssuerRole);
        var inviteKey = invite.DeriveKey();

        try
        {
            using var exchange = PairingExchange.Start();
            var contribution = new PairingContribution(keypair.Identity, exchange.PublicKey, exchange.Nonce);
            await PeerFrame.WriteAsync(
                stream,
                new PairOffer(contribution, label, PeerSessionNegotiation.CurrentVersion, role, invite.InviteId),
                cancellationToken).ConfigureAwait(false);

            // Read the acceptance with the refusal kept whole: the responder's
            // InviteUnknown is the answer the operator acts on ("ask for a
            // fresh code"), and folding it into a generic decline would hide
            // exactly the sentence they need.
            var acceptFrame = await ReadFrameOrNullAsync(stream, cancellationToken).ConfigureAwait(false);
            if (acceptFrame is null)
            {
                return Refused(cancellationToken);
            }

            if (acceptFrame.Value.Type == PeerMessageType.PairRefuse)
            {
                return new PairingResult(null, PairRefuse.Read(acceptFrame.Value.Body));
            }

            if (acceptFrame.Value.Type != PeerMessageType.PairAccept)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed,
                    $"Expected a pairing acceptance; the peer sent a {acceptFrame.Value.Type}.");
            }

            var accept = PairAccept.Read(acceptFrame.Value.Body);
            var transcript = PairingTranscript.Build(
                contribution, accept.Contribution, accept.SelectedVersion,
                offererPins: role, responderPins: accept.RoleForOfferer);
            var confirmation = PairingTranscript.ConfirmationBytes(transcript);

            await PeerFrame.WriteAsync(
                stream,
                new PairConfirm(
                    keypair.Sign(confirmation),
                    PairingTranscript.InviteMac(
                        inviteKey, offerer: true, transcript, initiatorBinding.Span, responderBinding.Span)),
                cancellationToken).ConfigureAwait(false);

            var frame = await ReadFrameOrNullAsync(stream, cancellationToken).ConfigureAwait(false);
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
            var expectedMac = PairingTranscript.InviteMac(
                inviteKey, offerer: false, transcript, initiatorBinding.Span, responderBinding.Span);

            // Both proofs are required: the signature says "I hold the key I
            // offered"; the MAC says "and I am the service your invite came
            // from" — a signature alone is any service at that address.
            if (!accept.Contribution.Identity.Verify(confirmation, theirConfirm.Signature.Span)
                || theirConfirm.InviteMac is not { } mac
                || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(mac.Span, expectedMac))
            {
                var refusal = new PairRefuse(
                    PeerRefusalReason.AuthenticationFailed,
                    "The peer did not prove the invite this code belongs to.");
                await PeerFrame.WriteAsync(stream, refusal, cancellationToken).ConfigureAwait(false);
                throw new PeerProtocolException(refusal.Reason, refusal.Text);
            }

            var grant = new PeerGrant(
                accept.Contribution.Identity, accept.Label, role, accept.Terms ?? PeerTerms.None,
                nowUnixMilliseconds);
            grants.Pin(grant);
            return new PairingResult(grant, null);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(inviteKey);
        }
    }

    /// <summary>
    /// Runs the responder side of the invite-authenticated ceremony against a
    /// pre-read offer (01 §2.7) — pre-read because the always-on listener must
    /// see the first frame before it knows whether it holds a pairing or a
    /// session (ADR-0030 Amendment 3).
    /// </summary>
    /// <param name="stream">The connection the offer arrived on.</param>
    /// <param name="offer">The offer the listener already read.</param>
    /// <param name="keypair">This device's peer keypair.</param>
    /// <param name="grants">The grant store to pin into on success.</param>
    /// <param name="label">This device's human-chosen label.</param>
    /// <param name="invites">The pending invites this service will honour.</param>
    /// <param name="initiatorBinding">The TLS initiator's channel binding.</param>
    /// <param name="responderBinding">The TLS responder's channel binding.</param>
    /// <param name="nowUnixMilliseconds">The pairing timestamp to record.</param>
    /// <param name="cancellationToken">Cancels the ceremony.</param>
    /// <returns>The result — a grant when the dialler proved the code, a refusal otherwise.</returns>
    public static async ValueTask<PairingResult> AcceptWithInviteAsync(
        Stream stream,
        PairOffer offer,
        PeerKeypair keypair,
        PeerGrantStore grants,
        string label,
        PairingInviteStore invites,
        ReadOnlyMemory<byte> initiatorBinding,
        ReadOnlyMemory<byte> responderBinding,
        ulong nowUnixMilliseconds,
        CancellationToken cancellationToken = default)
    {
        ThrowHelper.ThrowIfNull(stream);
        ThrowHelper.ThrowIfNull(offer);
        ThrowHelper.ThrowIfNull(keypair);
        ThrowHelper.ThrowIfNull(grants);
        ThrowHelper.ThrowIfNull(invites);

        // One refusal for every way an invite can fail to exist — unknown,
        // expired, consumed, role-mismatched. Which it was is the issuer's
        // business, not the dialler's (01 §2.7).
        if (offer.InviteId is not { } inviteId)
        {
            return await RefuseInviteAsync(
                stream,
                "This service pairs by invite. Ask its operator to issue one, or run the attended ceremony.",
                cancellationToken).ConfigureAwait(false);
        }

        var invite = invites.FindPending(inviteId.Span, nowUnixMilliseconds);
        if (invite is null || offer.RoleForResponder != PeerRoles.Complement(invite.Role))
        {
            return await RefuseInviteAsync(
                stream,
                "No pending invite matches this code. It may have expired or already been redeemed; ask for a fresh one.",
                cancellationToken).ConfigureAwait(false);
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
        var terms = invite.Role is PeerRole.StoresHere or PeerRole.Both ? invite.Terms : null;
        await PeerFrame.WriteAsync(
            stream, new PairAccept(contribution, label, (ushort)version, terms, invite.Role), cancellationToken)
            .ConfigureAwait(false);

        var transcript = PairingTranscript.Build(
            offer.Contribution, contribution, (ushort)version,
            offererPins: offer.RoleForResponder, responderPins: invite.Role);
        var confirmation = PairingTranscript.ConfirmationBytes(transcript);
        var inviteKey = invite.InviteKey.ToArray();

        try
        {
            // The offerer confirms first in this mode: nothing is committed
            // here — no consumption, no pin, no confirmation — until its
            // proof has verified, so a failed redemption spends nothing.
            var frame = await ReadFrameOrNullAsync(stream, cancellationToken).ConfigureAwait(false);
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
            var expectedMac = PairingTranscript.InviteMac(
                inviteKey, offerer: true, transcript, initiatorBinding.Span, responderBinding.Span);

            if (!offer.Contribution.Identity.Verify(confirmation, theirConfirm.Signature.Span)
                || theirConfirm.InviteMac is not { } mac
                || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(mac.Span, expectedMac))
            {
                return await RefuseInviteAsync(
                    stream,
                    "The invite proof did not verify — the code entered was not the one this invite was issued with.",
                    cancellationToken).ConfigureAwait(false);
            }

            // First redeemer wins, exactly once, before anything is pinned.
            if (!invites.Consume(invite.InviteIdHex, offer.Contribution.Identity.Fingerprint, nowUnixMilliseconds))
            {
                return await RefuseInviteAsync(
                    stream,
                    "No pending invite matches this code. It may have expired or already been redeemed; ask for a fresh one.",
                    cancellationToken).ConfigureAwait(false);
            }

            await PeerFrame.WriteAsync(
                stream,
                new PairConfirm(
                    keypair.Sign(confirmation),
                    PairingTranscript.InviteMac(
                        inviteKey, offerer: false, transcript, initiatorBinding.Span, responderBinding.Span)),
                cancellationToken).ConfigureAwait(false);

            var grant = new PeerGrant(
                offer.Contribution.Identity, invite.Label, invite.Role, invite.Terms, nowUnixMilliseconds);
            grants.Pin(grant);
            return new PairingResult(grant, null);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(inviteKey);
        }
    }

    private static async ValueTask<PairingResult> RefuseInviteAsync(
        Stream stream, string text, CancellationToken cancellationToken)
    {
        var refusal = new PairRefuse(PeerRefusalReason.InviteUnknown, text);
        await PeerFrame.WriteAsync(stream, refusal, cancellationToken).ConfigureAwait(false);
        return new PairingResult(null, refusal);
    }

    /// <summary>One frame, with a closed connection answered as null rather than thrown.</summary>
    private static async ValueTask<(PeerMessageType Type, System.Formats.Cbor.CborReader Body)?> ReadFrameOrNullAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            return await PeerFrame.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
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
