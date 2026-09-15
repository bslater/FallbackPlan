using Bodu;
using FallbackPlan.Protocol;
using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Agent;

/// <summary>
/// The destination half of replication (specification peer-protocol 03): over an
/// open peer session, accept a source's offer, declare what it already holds,
/// and receive the objects it lacks — committing each whole, or not at all
/// (03 §5). It writes ciphertext it cannot read into a replica store it chooses
/// the location of (03 §8).
/// </summary>
internal static class ReplicationResponder
{
    /// <summary>The result of serving one replication session.</summary>
    /// <param name="RepositoryId">The repository whose objects were received, hex.</param>
    /// <param name="Committed">How many objects were committed.</param>
    /// <param name="Termination">Present when the peer announced the peering's end instead of replicating (01 §3).</param>
    /// <param name="RetentionDeleted">Objects deleted under a retention instruction this session (06).</param>
    public sealed record Outcome(
        string RepositoryId, long Committed, PeeringTermination? Termination = null, long RetentionDeleted = 0);

    /// <summary>Serves one replication session from a source.</summary>
    /// <param name="replicasRoot">The directory under which per-repository replica stores live.</param>
    /// <param name="spoolRoot">A scratch directory for objects being received.</param>
    /// <param name="stream">The open session stream.</param>
    /// <param name="peer">The authenticated source's grant — its terms are what this side enforces (05 §1).</param>
    /// <param name="owners">The replica attribution store (05 §2).</param>
    /// <param name="retentionNegotiated">Whether the session's features admit a retention instruction (06 §1).</param>
    /// <param name="verificationNegotiated">Whether the session's features admit verification challenges (04 §1).</param>
    /// <param name="resumeNegotiated">
    /// Whether the session's features admit a transfer beginning part-way
    /// through an object (03 §5; [ADR-0057](../../docs/adr/0057-resumable-object-transfer.md)).
    /// Without it nothing is declared, nothing is kept, and a resume offset is
    /// refused — which is this build's behaviour towards every older peer.
    /// </param>
    /// <param name="cancellationToken">Cancels serving.</param>
    /// <param name="preread">The first payload frame, when the caller already read it to route the session (ADR-0041).</param>
    /// <returns>What was received.</returns>
    public static async Task<Outcome> ServeAsync(
        string replicasRoot, string spoolRoot, Stream stream,
        Protocol.PeerGrant peer, FallbackPlan.Application.ReplicaOwnerStore owners,
        bool retentionNegotiated,
        bool verificationNegotiated,
        bool resumeNegotiated,
        CancellationToken cancellationToken,
        (PeerMessageType Type, System.Formats.Cbor.CborReader Body)? preread = null)
    {
        ThrowHelper.ThrowIfNullOrWhiteSpace(replicasRoot);
        ThrowHelper.ThrowIfNullOrWhiteSpace(spoolRoot);
        ThrowHelper.ThrowIfNull(stream);
        ThrowHelper.ThrowIfNull(peer);
        ThrowHelper.ThrowIfNull(owners);

        try
        {
            // The first frame is the offer — or, under the termination-notice
            // feature, the announcement that there will never be another
            // (ADR-0030 Amendment 2). The caller raises the durable notice;
            // this layer only recognises the message. A caller that already
            // read it to route the payload (retrieval vs replication) hands
            // it in.
            var first = preread
                ?? await PeerFrame.ReadAsync(stream, cancellationToken).ConfigureAwait(false)
                ?? throw new PeerProtocolException(
                    PeerRefusalReason.Malformed, "The peer closed before sending a replication offer.");
            if (first.Type == PeerMessageType.PeeringTermination)
            {
                return new Outcome(string.Empty, 0, PeeringTermination.Read(first.Body));
            }

            if (first.Type != PeerMessageType.ReplicationOffer)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed,
                    $"Expected a replication offer; the peer sent a {first.Type}.");
            }

            var offer = ReplicationOffer.Read(first.Body);

            if (offer.FormatCapability != ReplicationInitiator.FormatCapability)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.FeatureUnsupported,
                    $"The offered repository format capability {offer.FormatCapability} is not implemented.");
            }

            if (!string.Equals(offer.Scope, "all", StringComparison.Ordinal))
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed, $"The scope '{offer.Scope}' is not one this build understands.");
            }

            var repositoryIdHex = Convert.ToHexStringLower(offer.RepositoryId.Span);

            // The attribution is what makes the quota's denominator — "the
            // total this peer stores here" — computable across sessions
            // (05 §2). A repository another peer owns here is refused rather
            // than counted against the wrong household's ledger.
            // The reclaim public key rides the offer and is recorded here,
            // at the moment the destination first admits the repository is
            // this peer's (ADR-0055 §5). Recorded once and never replaced by
            // a later offer: a key the sender can change is a check the
            // sender controls.
            var publishedReclaimKey = offer.ReclaimPublicKey.IsEmpty
                ? null
                : Convert.ToHexStringLower(offer.ReclaimPublicKey.Span);

            if (!owners.TryAttribute(repositoryIdHex, peer.Identity.Fingerprint, publishedReclaimKey))
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.TermsRefused,
                    $"Repository {repositoryIdHex} is already stored here for another peer.");
            }

            var replicaPath = Path.Combine(replicasRoot, repositoryIdHex);
            try
            {
                Directory.CreateDirectory(replicaPath);
                Directory.CreateDirectory(spoolRoot);
            }
            catch (Exception storage) when (storage is IOException or UnauthorizedAccessException)
            {
                throw CannotStore(storage);
            }

            var replica = new LocalFileSystemObjectStore(replicaPath);

            // Quota 0 declares no ceiling (05 §1); anything above it bounds
            // the peer's committed bytes across every repository it owns
            // here, so usage is summed before the first object crosses.
            var quota = peer.Terms.QuotaBytes;
            // Staged prefixes count too (ADR-0057): they are real disk this
            // peer is costing its host, and leaving them out would let a peer
            // park bytes outside the ceiling it agreed to by starting
            // transfers it never finishes.
            var owned = owners.OwnedBy(peer.Identity.Fingerprint);
            var usage = quota > 0
                ? await UsageAsync(replicasRoot, owned, cancellationToken).ConfigureAwait(false)
                    + PartialSpool.StagedBytes(spoolRoot, owned)
                : 0UL;

            // The same two numbers the boundary stop is enforced from, told
            // to the source up front so it learns the ceiling is close before
            // a push runs into it rather than only when one does (05 §4).
            await SendInventoryAsync(
                replica, stream, quota > 0 ? quota - Math.Min(usage, quota) : null, cancellationToken)
                .ConfigureAwait(false);

            // What this side part holds, after what it wholly holds: a source
            // that agreed to resumption learns it can finish an object rather
            // than start it again (ADR-0057). Everything unresumable is swept
            // by the survey rather than declared — an unnameable file, a stale
            // one, one whose object has since arrived by another route.
            var spoolDirectory = PartialSpool.DirectoryFor(spoolRoot, repositoryIdHex);
            if (resumeNegotiated)
            {
                await SendPartialsAsync(replica, spoolDirectory, stream, cancellationToken).ConfigureAwait(false);
            }

            var committed = await ReceiveAsync(
                replica, spoolDirectory, stream, quota, usage, resumeNegotiated, cancellationToken)
                .ConfigureAwait(false);

            await PeerFrame.WriteAsync(stream, new ReplicationAck((ulong)committed), cancellationToken)
                .ConfigureAwait(false);

            var retentionDeleted = await ServeAfterAckAsync(
                replica, offer.RepositoryId, peer, retentionNegotiated, verificationNegotiated,
                stream, owners, cancellationToken)
                .ConfigureAwait(false);

            return new Outcome(repositoryIdHex, committed, RetentionDeleted: retentionDeleted);
        }
        catch (PeerProtocolException exception)
        {
            if (!exception.ReceivedFromPeer)
            {
                await ReplicationWire.TryRefuseAsync(stream, exception).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// Serves whatever follows the acknowledgement: an optional retention
    /// instruction (peer-protocol 06), then any number of verification
    /// challenges (04) — in that order, because verifying a key the same
    /// session then deletes proves nothing anyone keeps. The session ends
    /// when the peer closes.
    /// </summary>
    private static async Task<long> ServeAfterAckAsync(
        LocalFileSystemObjectStore replica,
        ReadOnlyMemory<byte> offeredRepositoryId,
        Protocol.PeerGrant peer,
        bool retentionNegotiated,
        bool verificationNegotiated,
        Stream stream,
        FallbackPlan.Application.ReplicaOwnerStore owners,
        CancellationToken cancellationToken)
    {
        var retentionDeleted = 0L;
        var retentionServed = false;
        var challengeServed = false;

        while (true)
        {
            // The session may simply end here — everything after the
            // acknowledgement is optional.
            var frame = await PeerFrame.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                return retentionDeleted;
            }

            switch (frame.Value.Type)
            {
                case PeerMessageType.RetentionOffer when !retentionServed && !challengeServed:
                    if (!retentionNegotiated)
                    {
                        // Sending a gated message outside the negotiated
                        // intersection is a violation, not a capability
                        // probe (02 §6).
                        throw new PeerProtocolException(
                            PeerRefusalReason.FeatureUnsupported,
                            "A retention instruction arrived without the retention-instruction feature in effect.");
                    }

                    retentionDeleted = await ServeRetentionAsync(
                        replica, offeredRepositoryId, peer, frame.Value.Body, stream,
                        owners, cancellationToken)
                        .ConfigureAwait(false);
                    retentionServed = true;
                    break;

                case PeerMessageType.VerificationChallenge:
                    if (!verificationNegotiated)
                    {
                        throw new PeerProtocolException(
                            PeerRefusalReason.FeatureUnsupported,
                            "A verification challenge arrived without the destination-verification feature in effect.");
                    }

                    await ServeChallengeAsync(replica, offeredRepositoryId, frame.Value.Body, stream, cancellationToken)
                        .ConfigureAwait(false);
                    challengeServed = true;
                    break;

                default:
                    throw new PeerProtocolException(
                        PeerRefusalReason.Malformed,
                        $"A {frame.Value.Type} is not permitted after the replication acknowledgement.");
            }
        }
    }

    /// <summary>
    /// Answers one keyed random-range challenge (peer-protocol 04): read
    /// exactly the named range from the stored copy and prove it, or answer
    /// honestly that this side cannot — which is the answer the challenge
    /// exists to force into the open. No keys are involved: the proof is
    /// over ciphertext this side already holds.
    /// </summary>
    private static async Task ServeChallengeAsync(
        LocalFileSystemObjectStore replica,
        ReadOnlyMemory<byte> offeredRepositoryId,
        System.Formats.Cbor.CborReader body,
        Stream stream,
        CancellationToken cancellationToken)
    {
        var challenge = VerificationChallenge.Read(body);

        if (!challenge.RepositoryId.Span.SequenceEqual(offeredRepositoryId.Span))
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                "A verification challenge names a repository other than the one this session replicated.");
        }

        if (challenge.Key.StartsWith("tombstones/", StringComparison.Ordinal)
            || challenge.Key.StartsWith("leases/", StringComparison.Ordinal))
        {
            throw new PeerProtocolException(
                PeerRefusalReason.Malformed,
                $"A verification challenge may not name '{challenge.Key}' — lifecycle keys never replicate (04 §4.1).");
        }

        // The exact challenged range, or null for every honest inability
        // (04 §4.2) — the same reader the source uses on its own bytes, so
        // the two ends cannot disagree about what "cannot produce it" means.
        var bytes = await Replication.RangeReader.ReadAsync(
            replica, challenge.Key, challenge.Offset, challenge.Length, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            await PeerFrame.WriteAsync(
                stream, new VerificationProof(Held: false, ReadOnlyMemory<byte>.Empty), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var proof = RangeChallenge.Compute(
            challenge.ChallengeKey.Span, challenge.Nonce.Span, challenge.Key,
            challenge.Offset, challenge.Length, bytes);
        await PeerFrame.WriteAsync(stream, new VerificationProof(Held: true, proof), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses a retention page this spoke cannot prove came from the
    /// repository's reclaim authority
    /// ([ADR-0055](../../docs/adr/0055-reclaim-authority.md) §5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The refusal is total and deletes nothing, exactly as the floor breach
    /// is: a partially-honoured instruction whose authorship is in doubt is
    /// the worst of both answers.
    /// </para>
    /// <para>
    /// <b>Gated on the recorded key, not on the negotiated feature.</b> It was
    /// gated on the feature, and that was a hole rather than a compatibility
    /// surface: negotiation is an intersection of what the two sides offer and
    /// a listener cannot require anything, so the party this check exists to
    /// defend against was the party deciding whether it applied. A source that
    /// simply omitted <c>signed-retention</c> from its hello had its UNSIGNED
    /// instruction obeyed — forgery rather than replay, an arbitrary drop-list
    /// needing no reclaim key at all, which is the hole the whole reclaim
    /// split exists to close reached by declining to participate in it.
    /// </para>
    /// <para>
    /// The gate is now the durable fact the spoke wrote down for itself: the
    /// reclaim public key recorded at first attribution
    /// ([05 §2](../../specifications/peer-protocol/05-quotas.md#2-ownership)).
    /// Holding one means this repository's owner published a reclaim
    /// authority, and that is not something a later session can take back.
    /// A peering established before the key existed records none, has nothing
    /// to check against, and is unaffected — it cannot manufacture a verdict
    /// from an absence, and refusing it would strand it.
    /// </para>
    /// <para>
    /// This is [ADR-0055](../../docs/adr/0055-reclaim-authority.md) §4's own
    /// argument, carried to the wire: it chose a *required* descriptor feature
    /// over a per-object schema version because "a per-object version is a
    /// per-object choice, and the attacker chooses". An optional negotiated
    /// feature is a per-session choice.
    /// </para>
    /// </remarks>
    private static void RequireReclaimSignature(
        RetentionOffer page,
        string repositoryIdHex,
        FallbackPlan.Application.ReplicaOwnerStore owners)
    {
        if (owners.Find(repositoryIdHex)?.ReclaimPublicKey is not { Length: > 0 } publicKeyHex)
        {
            return;
        }

        if (page.Signature.IsEmpty)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.TermsRefused,
                "A retention instruction arrived unsigned for a repository whose reclaim public key this replica "
                + "recorded — it deletes only on that key's authority, and no session may ask it to stop "
                + "(06 §3).");
        }

        byte[] publicKey;
        try
        {
            publicKey = Convert.FromHexString(publicKeyHex);
        }
        catch (FormatException)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.TermsRefused,
                "This replica's recorded reclaim public key is unreadable, so no instruction can be proven (06 §3).");
        }

        if (!Repository.Crypto.RepositorySigner.VerifyWithPublicKey(
            publicKey, page.EncodeForSigning(), page.Signature.Span))
        {
            throw new PeerProtocolException(
                PeerRefusalReason.TermsRefused,
                "A retention instruction's signature does not verify against this replica's recorded reclaim "
                + "public key — it was not authorised by the repository's reclaim authority (06 §3).");
        }
    }

    /// <summary>
    /// Serves a retention instruction (peer-protocol 06): the commander
    /// computed, this side deletes exactly what it is told — bounded below
    /// by the granted retention floor, which is the one safeguard that holds
    /// when the hub is compromised. The floor check needs no decryption:
    /// snapshot objects are counted by prefix, so a spoke that cannot read a
    /// single manifest can still refuse to breach it.
    /// </summary>
    private static async Task<long> ServeRetentionAsync(
        LocalFileSystemObjectStore replica,
        ReadOnlyMemory<byte> offeredRepositoryId,
        Protocol.PeerGrant peer,
        System.Formats.Cbor.CborReader firstBody,
        Stream stream,
        FallbackPlan.Application.ReplicaOwnerStore owners,
        CancellationToken cancellationToken)
    {
        // Every page is read before anything is deleted: the floor check is
        // over the whole instruction, or a piecewise pass could breach it in
        // total (06 §4.1).
        var drops = new List<string>();
        var page = RetentionOffer.Read(firstBody);
        while (true)
        {
            if (!page.RepositoryId.Span.SequenceEqual(offeredRepositoryId.Span))
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed,
                    "A retention page names a repository other than the one this session replicated.");
            }

            RequireReclaimSignature(page, Convert.ToHexStringLower(offeredRepositoryId.Span), owners);

            foreach (var key in page.Keys)
            {
                if (key is "repository-format"
                    || key.StartsWith("keys/", StringComparison.Ordinal)
                    || key.StartsWith("tombstones/", StringComparison.Ordinal)
                    || key.StartsWith("leases/", StringComparison.Ordinal))
                {
                    throw new PeerProtocolException(
                        PeerRefusalReason.TermsRefused,
                        $"A retention instruction may not name '{key}' — identity and lifecycle keys are never deletable by instruction (06 §3).");
                }

                drops.Add(key);
            }

            if (!page.More)
            {
                break;
            }

            page = await ReplicationWire.ReadAsync(
                stream, PeerMessageType.RetentionOffer, RetentionOffer.Read, cancellationToken).ConfigureAwait(false);
        }

        // The floor (06 §3, FR-GC-010): counted on ciphertext, refused whole.
        var heldSnapshots = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var entry in replica.ListAsync(
            ObjectPrefix.Parse("snapshots/"), ListOptions.Default, cancellationToken).ConfigureAwait(false))
        {
            heldSnapshots.Add(entry.Key.Value);
        }

        var droppedSnapshots = drops.Count(key => heldSnapshots.Contains(key));
        var remaining = heldSnapshots.Count - droppedSnapshots;
        if (remaining < peer.Terms.RetentionFloorGenerations)
        {
            throw new PeerProtocolException(
                PeerRefusalReason.TermsRefused,
                $"The instruction would leave {remaining} snapshot(s); this grant's retention floor is "
                + $"{peer.Terms.RetentionFloorGenerations} (06 §3).");
        }

        var deleted = 0L;
        foreach (var key in drops)
        {
            var outcome = await replica.DeleteAsync(
                Storage.Abstractions.ObjectKey.Parse(key), DeleteConditions.None, cancellationToken)
                .ConfigureAwait(false);
            if (outcome.Outcome == DeleteOutcome.Deleted)
            {
                deleted++;
            }
        }

        await PeerFrame.WriteAsync(stream, new RetentionAck((ulong)deleted), cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    private static async Task SendInventoryAsync(
        LocalFileSystemObjectStore replica, Stream stream, ulong? headroom, CancellationToken cancellationToken)
    {
        var page = new List<string>(ReplicationInventory.MaximumKeys);
        await foreach (var entry in replica.ListAsync(ObjectPrefix.All, ListOptions.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            page.Add(entry.Key.Value);
            if (page.Count == ReplicationInventory.MaximumKeys)
            {
                await PeerFrame.WriteAsync(
                    stream, new ReplicationInventory([.. page], More: true, headroom), cancellationToken)
                    .ConfigureAwait(false);
                page.Clear();
            }
        }

        // The final (or only) page carries whatever remains and closes the inventory.
        await PeerFrame.WriteAsync(
            stream, new ReplicationInventory([.. page], More: false, headroom), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Declares what this replica part holds, so a cut object can be finished.</summary>
    /// <param name="replica">The replica store, so a prefix of a committed object is dropped rather than offered.</param>
    /// <param name="spoolDirectory">This repository's staged prefixes.</param>
    /// <param name="stream">The session stream.</param>
    /// <param name="cancellationToken">Stops the survey.</param>
    private static async Task SendPartialsAsync(
        LocalFileSystemObjectStore replica, string spoolDirectory, Stream stream,
        CancellationToken cancellationToken)
    {
        var declarations = await PartialSpool.SurveyAsync(
            spoolDirectory, replica, DateTimeOffset.UtcNow, ReplicationPartial.MaximumEntries, cancellationToken)
            .ConfigureAwait(false);

        // Sent even when empty: "I part hold nothing" is an answer, and a
        // source expecting one frame that did not arrive would read the first
        // object header in its place.
        await PeerFrame.WriteAsync(
            stream,
            new ReplicationPartial(
                [.. declarations.Select(declaration => declaration.Key)],
                [.. declarations.Select(declaration => declaration.Staged)],
                [.. declarations.Select(declaration => (ReadOnlyMemory<byte>)declaration.Digest)]),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> ReceiveAsync(
        LocalFileSystemObjectStore replica, string spoolDirectory, Stream stream,
        ulong quota, ulong usage, bool resumeNegotiated, CancellationToken cancellationToken)
    {
        var committed = 0L;
        Incoming? current = null;
        try
        {
            while (true)
            {
                var (type, body) = await ReplicationWire.ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
                switch (type)
                {
                    case PeerMessageType.ReplicationComplete:
                        ReplicationComplete.Read(body);
                        if (current is not null)
                        {
                            throw new PeerProtocolException(
                                PeerRefusalReason.Malformed, "Replication completed with an object still unfinished.");
                        }

                        return committed;

                    case PeerMessageType.ReplicationObject:
                        if (current is not null)
                        {
                            throw new PeerProtocolException(
                                PeerRefusalReason.Malformed, "A new object began before the previous one finished.");
                        }

                        var header = ReplicationObject.Read(body);

                        // The quota check runs here, where the length is
                        // declared and nothing is yet spooled — the clean
                        // stop at the object boundary (05 §3). Everything
                        // committed so far stays committed.
                        // What this object still costs, which for a resumed one
                        // is its tail: the staged prefix is already counted in
                        // usage, and charging for it twice would refuse a peer
                        // for bytes it has.
                        var owing = header.Length - header.ResumeOffset;
                        if (quota > 0 && usage + owing > quota)
                        {
                            throw new PeerProtocolException(
                                PeerRefusalReason.TermsRefused,
                                $"The quota of {quota} bytes is exhausted: {usage} bytes are stored"
                                + $" and the next object declares {header.Length}.");
                        }

                        try
                        {
                            // A resume offset from a peer that never agreed to
                            // resumption is a chunk stream that would not add
                            // up; refusing it is the same answer this side has
                            // always given to an offset it did not expect.
                            if (header.ResumeOffset > 0 && !resumeNegotiated)
                            {
                                throw new PeerProtocolException(
                                    PeerRefusalReason.Malformed,
                                    $"An object resumed at {header.ResumeOffset} without "
                                    + $"'{PeerSessionNegotiation.PartialObjectResumeFeature}' in force.");
                            }

                            current = new Incoming(
                                spoolDirectory, header.Key, header.Length, header.ResumeOffset, resumeNegotiated)
                            {
                                Resumed = header.ResumeOffset,
                            };
                            if (current.Complete)
                            {
                                await current.CommitAsync(replica, cancellationToken).ConfigureAwait(false);
                                committed++;
                                usage += owing;
                                current.Dispose();
                                current = null;
                            }
                        }
                        catch (Exception storage) when (storage is IOException or UnauthorizedAccessException)
                        {
                            throw CannotStore(storage);
                        }

                        break;

                    case PeerMessageType.ReplicationChunk:
                        if (current is null)
                        {
                            throw new PeerProtocolException(
                                PeerRefusalReason.Malformed, "A chunk arrived with no object to append it to.");
                        }

                        try
                        {
                            await current.AppendAsync(ReplicationChunk.Read(body), cancellationToken).ConfigureAwait(false);
                            if (current.Complete)
                            {
                                await current.CommitAsync(replica, cancellationToken).ConfigureAwait(false);
                                committed++;
                                usage += current.Length - current.Resumed;
                                current.Dispose();
                                current = null;
                            }
                        }
                        catch (Exception storage) when (storage is IOException or UnauthorizedAccessException)
                        {
                            throw CannotStore(storage);
                        }

                        break;

                    default:
                        throw new PeerProtocolException(
                            PeerRefusalReason.Malformed, $"A {type} is not part of the replication payload here.");
                }
            }
        }
        finally
        {
            current?.Dispose();
        }
    }

    /// <summary>
    /// The peer's committed bytes across every repository attributed to it —
    /// the quota's denominator (05 §1). Summed from the stores themselves, so
    /// losing no separate counter can ever disagree with what is held.
    /// </summary>
    private static async ValueTask<ulong> UsageAsync(
        string replicasRoot, IReadOnlyList<string> repositories, CancellationToken cancellationToken)
    {
        var total = 0UL;
        foreach (var repositoryIdHex in repositories)
        {
            var path = Path.Combine(replicasRoot, repositoryIdHex);
            if (!Directory.Exists(path))
            {
                continue;
            }

            var store = new LocalFileSystemObjectStore(path);
            await foreach (var entry in store.ListAsync(ObjectPrefix.All, ListOptions.Default, cancellationToken)
                .ConfigureAwait(false))
            {
                total += (ulong)entry.Length;
            }
        }

        return total;
    }

    /// <summary>
    /// Disk trouble spoken as itself: <c>storage_exhausted</c>, never
    /// <c>terms_refused</c> — the quota said yes and the hardware said no,
    /// and the two send the human to different fixes (05 §4).
    /// </summary>
    private static PeerProtocolException CannotStore(Exception storage) =>
        new(PeerRefusalReason.StorageExhausted, $"The destination cannot store: {storage.Message}", storage);

    /// <summary>An object being received: spooled to a temp file, committed whole (03 §5).</summary>
    private sealed class Incoming : IDisposable
    {
        private readonly string _key;
        private readonly ulong _length;
        private readonly string _path;
        private readonly FileStream _spool;
        private readonly bool _keepIfInterrupted;
        private ulong _received;
        private bool _committed;

        /// <summary>Opens the staged file for an object about to arrive.</summary>
        /// <param name="directory">This repository's spool directory.</param>
        /// <param name="key">The object key.</param>
        /// <param name="length">The object's declared total length.</param>
        /// <param name="resumeOffset">Where the source says it will begin.</param>
        /// <param name="keepIfInterrupted">
        /// Whether a cut leaves the staged bytes behind for a later session.
        /// False when the pair did not negotiate resumption, which keeps an
        /// un-negotiated session's disk behaviour exactly as it was.
        /// </param>
        public Incoming(string directory, string key, ulong length, ulong resumeOffset, bool keepIfInterrupted)
        {
            _key = key;
            _length = length;
            _keepIfInterrupted = keepIfInterrupted;
            _path = PartialSpool.PathFor(directory, key);
            _spool = PartialSpool.Open(directory, key, length, resumeOffset);
            _received = resumeOffset;
        }

        public bool Complete => _received == _length;

        public ulong Length => _length;

        /// <summary>How many bytes of this object this session did not have to receive.</summary>
        public ulong Resumed { get; init; }

        public async ValueTask AppendAsync(ReplicationChunk chunk, CancellationToken cancellationToken)
        {
            // Still strictly sequential, and still anchored to what this side
            // holds — the anchor simply no longer has to start at zero
            // (ADR-0057). A chunk that does not continue the staged prefix is
            // as malformed as it ever was.
            if (chunk.Offset != _received)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed, $"A chunk at offset {chunk.Offset} arrived; {_received} was expected.");
            }

            var next = _received + (ulong)chunk.Bytes.Length;
            if (next > _length)
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed, "An object's bytes overran the length it declared.");
            }

            await _spool.WriteAsync(chunk.Bytes, cancellationToken).ConfigureAwait(false);
            _received = next;
        }

        public async ValueTask CommitAsync(LocalFileSystemObjectStore replica, CancellationToken cancellationToken)
        {
            if (!ObjectKey.TryParse(_key, out var objectKey))
            {
                throw new PeerProtocolException(
                    PeerRefusalReason.Malformed, $"'{_key}' is not a valid object key.");
            }

            await _spool.FlushAsync(cancellationToken).ConfigureAwait(false);

            // A create-if-absent write makes commit atomic and re-runs idempotent
            // (03 §5): an object already held is identical to the one offered.
            // The read handle must share with _spool, which is still open with
            // write access — a default-share open is a sharing violation on
            // Windows, and the commit would fail for every object ever spooled.
            await replica.PutAsync(
                objectKey,
                _ => new ValueTask<Stream>(
                    new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)),
                PutConditions.IfNotExists,
                cancellationToken).ConfigureAwait(false);

            _committed = true;
        }

        public void Dispose()
        {
            _spool.Dispose();

            // Kept only when the bytes could still be finished by a later
            // session: committed means they are in the store, and an
            // un-negotiated pair would never be offered them back, so in both
            // cases the file is scratch that has served its purpose.
            if (_committed || !_keepIfInterrupted)
            {
                PartialSpool.Discard(_path);
            }
        }
    }
}
