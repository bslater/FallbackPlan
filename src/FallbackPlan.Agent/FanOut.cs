using Bodu;
using FallbackPlan.Application;
using FallbackPlan.Replication;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Agent;

/// <summary>
/// Converges one backup set's declared destinations from its archive
/// (ADR-0034 §3) — the staging archive, or, for a direct-ship set, the
/// catch-up pass behind bytes the ship sink already sent (ADR-0046): the
/// copy runs on the transfer lane, at most one queued or
/// running sync per <c>(set, destination)</c>, and every outcome — success,
/// unreachable, refused, not-yet-served — lands in the sync ledger the status
/// surface reads (FR-DEST-002/003/004).
/// </summary>
/// <remarks>
/// Availability is probed by attempting: a local-path destination whose
/// configured directory does not exist is recorded <see cref="DestinationSyncState.Unavailable"/>
/// and the next pass retries under back-off, closing the gap without operator
/// action when the drive returns (FR-DEST-003). The configured directory is
/// deliberately never created — creating it would write a "removable drive"'s
/// bytes onto whatever disk holds the mount point.
/// </remarks>
public static class FanOut
{
    /// <summary>What a source demands of a destination that must prove itself (FR-VER-006).</summary>
    /// <remarks>
    /// Shared with the admission probe so the probe cannot report a
    /// destination as viable that a sync would then refuse at negotiation.
    /// </remarks>
    internal static readonly string[] VerificationRequirement =
        [Protocol.PeerSessionNegotiation.DestinationVerificationFeature];

    /// <summary>The coalescing identity: one active sync per (set, destination).</summary>
    /// <param name="setId">The set's 32-hex identity.</param>
    /// <param name="destinationName">The declared destination name.</param>
    public static string JobIdFor(string setId, string destinationName) => $"sync-{setId}-{destinationName}";

    /// <summary>
    /// Queues one sync per declared destination of the set. A pair whose sync
    /// is already queued or running is skipped — the backlog coalesces.
    /// </summary>
    /// <param name="runtime">The service.</param>
    /// <param name="set">The set whose archive to fan out.</param>
    /// <param name="now">The pass clock.</param>
    /// <param name="userInitiated">Whether a person is waiting.</param>
    /// <returns>One task per queued sync; awaiting them is the caller's choice.</returns>
    public static IReadOnlyList<Task> EnqueueAll(
        ServiceRuntime runtime, BackupSetConfiguration set, DateTimeOffset now, bool userInitiated)
    {
        ThrowHelper.ThrowIfNull(runtime);
        ThrowHelper.ThrowIfNull(set);

        var queued = new List<Task>();
        foreach (var reference in set.Destinations)
        {
            if (Enqueue(runtime, set, reference.Ref, now, userInitiated) is { } task)
            {
                queued.Add(task);
            }
        }

        return queued;
    }

    /// <summary>
    /// Queues one (set, destination) sync on the transfer lane, or returns
    /// null when one is already queued or running.
    /// </summary>
    /// <param name="runtime">The service.</param>
    /// <param name="set">The set whose archive to fan out.</param>
    /// <param name="destinationName">The declared destination.</param>
    /// <param name="now">The pass clock.</param>
    /// <param name="userInitiated">Whether a person is waiting.</param>
    /// <returns>A task completing when the sync has run, or null when coalesced away.</returns>
    public static Task? Enqueue(
        ServiceRuntime runtime, BackupSetConfiguration set, string destinationName,
        DateTimeOffset now, bool userInitiated)
    {
        ThrowHelper.ThrowIfNull(runtime);
        ThrowHelper.ThrowIfNull(set);
        ThrowHelper.ThrowIfNullOrWhiteSpace(destinationName);

        // The destination's declared priority, overridable per set
        // (ADR-0047): among waiting transfers, the higher-priority
        // destination ships first.
        var priority = SetDestinationReference.EffectivePriority(
            set.Destinations.FirstOrDefault(reference =>
                string.Equals(reference.Ref, destinationName, StringComparison.Ordinal)),
            runtime.Configuration.FindDestination(destinationName));

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = runtime.Queue.Enqueue(new QueuedJob(
            JobIdFor(set.Id, destinationName),
            JobLane.Transfer,
            userInitiated,
            $"sync {set.Name} -> {destinationName}",
            async cancellationToken =>
            {
                try
                {
                    await RunAsync(runtime, set, destinationName, (ulong)now.ToUnixTimeMilliseconds(), cancellationToken)
                        .ConfigureAwait(false);
                    completion.SetResult();
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                    throw;
                }
            },
            Priority: priority));

        return accepted ? completion.Task : null;
    }

    /// <summary>
    /// Proves a sample of the peer's blobs by reading them back through the
    /// retrieval session and authenticating a record in each
    /// ([ADR-0058](../../docs/adr/0058-peer-write-adapter.md) §8).
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a set with a second copy the wire challenge is cheaper and is what
    /// runs; this is for the set that has none, where the alternative is no
    /// proof at all. It costs a second dialled session and a few ranged reads
    /// per sampled blob, paid on the verification cadence rather than per
    /// pass.
    /// </para>
    /// <para>
    /// The sample is drawn from the keys the spoke itself declared, which is
    /// not the examined party choosing the questions: the declaration is also
    /// the push's diff, so a key omitted to avoid being asked about is a key
    /// this same session has already re-shipped.
    /// </para>
    /// <para>
    /// A dial that fails is not a finding. The peer proved nothing and is
    /// accused of nothing; the pass falls through to recording an unproven
    /// sync, exactly as it would for a peer that offers no retrieval at all.
    /// </para>
    /// </remarks>
    /// <param name="runtime">The service, for the ledger and the dial.</param>
    /// <param name="set">The set being verified.</param>
    /// <param name="destination">The peer destination.</param>
    /// <param name="archive">The set's archive, for the repository keys.</param>
    /// <param name="outcome">The push that just finished, for what the spoke declared holding.</param>
    /// <param name="nowMs">The clock.</param>
    /// <param name="cancellationToken">Cancels the read-back.</param>
    /// <returns>Whether this recorded the pair's outcome.</returns>
    private static async ValueTask<bool> ReadBackAsync(
        ServiceRuntime runtime,
        BackupSetConfiguration set,
        DestinationConfiguration destination,
        ArchiveHandle archive,
        ReplicationInitiator.PushOutcome outcome,
        ulong nowMs,
        CancellationToken cancellationToken)
    {
        if (outcome.HeldKeys is not { Count: > 0 } held)
        {
            return false;
        }

        var blobs = held
            .Where(key => key.StartsWith("blobs/", StringComparison.Ordinal))
            .ToList();
        if (blobs.Count == 0)
        {
            return false;
        }

        // Random rather than the first few: rot is not at the front, and a
        // fixed choice is one a damaged replica survives for ever.
        var sample = blobs
            .OrderBy(_ => Random.Shared.Next())
            .Take(VerificationSampler.DefaultBudget)
            .ToList();

        var log = runtime.LoggerFor(typeof(FanOut));
        Replication.VerificationOutcome verification;
        try
        {
            await using var client = await PeerRetrievalClient.DialAsync(
                runtime, destination, archive.Repository.RepositoryId.ToArray(), cancellationToken)
                .ConfigureAwait(false);

            verification = await Replication.ReplicaVerifier.ProveSealedAsync(
                new PeerRetrievalObjectStore(client), sample, archive.Repository, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or Protocol.PeerProtocolException or UnauthorizedAccessException)
        {
            // The replica could not be read back at all. That is this pass
            // failing to prove, not the destination failing a proof.
            Log.ReadBackUnavailable(log, destination.Name, exception.Message);
            return false;
        }

        var ledger = runtime.DestinationSync;
        if (verification.Failed.Count > 0)
        {
            RecordVerificationFailure(runtime, set, destination.Name, verification, sample.Count, nowMs);
            return true;
        }

        if (!verification.ProvedSomething)
        {
            // Every sampled blob was a sealed data plane this service cannot
            // open — a write-only set (FR-WOR-003). The containers held; the
            // payloads were not examined, and claiming them would be a claim
            // nobody checked.
            return false;
        }

        var (syncedSequence, _) = await StagingPublicationSequenceAsync(archive, cancellationToken)
            .ConfigureAwait(false);
        ledger.RecordSuccess(set.Id, destination.Name, outcome.Committed, nowMs, syncedSequence);
        ledger.RecordVerification(
            set.Id, destination.Name, verification.Passed, blobs.Count, syncedSequence, null, nowMs);
        return true;
    }

    /// <summary>Whether the store can answer for anything under a prefix.</summary>
    /// <param name="store">The store to ask.</param>
    /// <param name="prefix">The namespace prefix.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    private static async ValueTask<bool> HoldsAnyAsync(
        Storage.Abstractions.IObjectStore store, string prefix, CancellationToken cancellationToken)
    {
        await foreach (var _ in store
            .ListAsync(
                Storage.Abstractions.ObjectPrefix.Parse(prefix),
                Storage.Abstractions.ListOptions.Default,
                cancellationToken)
            .ConfigureAwait(false))
        {
            return true;
        }

        return false;
    }

    /// <summary>Runs one (set, destination) sync and records what happened.</summary>
    private static async ValueTask RunAsync(
        ServiceRuntime runtime, BackupSetConfiguration set, string destinationName,
        ulong nowMs, CancellationToken cancellationToken)
    {
        var ledger = runtime.DestinationSync;
        var destination = runtime.Configuration.FindDestination(destinationName);
        if (destination is null)
        {
            ledger.RecordFailure(
                set.Id, destinationName, DestinationSyncState.Failed,
                $"destination '{destinationName}' is no longer declared", nowMs);
            return;
        }

        ArchiveHandle? archive;
        try
        {
            archive = await runtime.ExistingArchiveAsync(set.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The staging archive itself refused to open — damage, or a
            // passphrase that no longer matches. The pair's row carries the
            // reason; a sync failure must never take the pass down with it.
            ledger.RecordFailure(set.Id, destinationName, DestinationSyncState.Failed, exception.Message, nowMs);
            return;
        }

        if (archive is null)
        {
            // Nothing captured yet: nothing to converge, nothing to record.
            return;
        }

        // The set gate (ADR-0029 Amendment 2): a retention apply may be
        // mutating this set's staging, and a convergence computed against a
        // moving staging archive can conspire with the trim to delete a
        // blob's last copy. The sync waits — retention passes are minutes.
        var gate = runtime.SetGate(set.Id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            switch (destination.Kind)
            {
                case DestinationKind.LocalPath:
                    await CopyToLocalPathAsync(runtime, set, destination, archive, nowMs, cancellationToken)
                        .ConfigureAwait(false);
                    return;

                case DestinationKind.Peer:
                    await PushToPeerAsync(runtime, set, destination, archive, nowMs, cancellationToken)
                        .ConfigureAwait(false);
                    return;

                default:
                    // The reserved cloud kinds (FR-DEST-005): configuration models
                    // them, the runtime does not serve them yet.
                    ledger.RecordFailure(
                        set.Id, destinationName, DestinationSyncState.NotSupported,
                        $"destination kind '{destination.Kind}' is not yet supported", nowMs);
                    return;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Pushes the set's archive to a paired peer over the replication
    /// exchange (peer-protocol 03), driven by the hub (ADR-0034 §3) — the
    /// `sync` verb rides this same path on demand. The endpoint comes
    /// from the configuration and the key from the grant: the address book
    /// and the trust decision live in different places on purpose
    /// (FR-DEST-006, ADR-0030).
    /// </summary>
    private static async ValueTask PushToPeerAsync(
        ServiceRuntime runtime, BackupSetConfiguration set, DestinationConfiguration destination,
        ArchiveHandle archive, ulong nowMs, CancellationToken cancellationToken)
    {
        var ledger = runtime.DestinationSync;

        // Shared with the admission probe, so "declared but unreachable" is
        // one judgement worded one way rather than two that drift.
        if (!PeerAddress.TryResolve(runtime, destination, out var address, out var unreachable))
        {
            ledger.RecordFailure(
                set.Id, destination.Name, DestinationSyncState.Failed, unreachable!, nowMs);
            return;
        }

        var (grants, grant, host, port) = address!;

        try
        {
            using var keypair = Protocol.PeerKeypairStore.Open(runtime.Options.StateDirectory);
            await using var connection = await Protocol.PeerTlsConnection.DialAsync(
                host, port, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            // A destination that will not prove possession is refused at
            // negotiation, before a byte crosses (04 §1, FR-VER-006): the
            // mitigation T-8 names cannot be one the defended-against party
            // declines in silence. Excusing it takes the acknowledged word in
            // the configuration.
            var session = await Protocol.PeerSessionDriver.DialAsync(
                connection, keypair, grants, grant.Identity, "fallbackplan-agent", terms: null,
                requiredFeatures: destination.RequiresVerification ? VerificationRequirement : null,
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // The destination's hello carries its current terms; they are
            // adopted as the ones in force, and a narrowing is told to the
            // human before the first refusal arrives rather than after
            // (peer-protocol 05 §6).
            if (session.TheirTerms is { } offered && grants.ApplyTerms(grant.Identity, offered))
            {
                runtime.Notices.Raise(
                    $"terms-narrowed:{destination.Fingerprint}",
                    $"Peer '{destination.Name}' narrowed its terms — it now lends "
                    + $"{(offered.QuotaBytes > 0 ? $"{offered.QuotaBytes} bytes" : "unbounded space")}. "
                    + "Replication continues under the new terms; review retention if they no longer fit.",
                    nowMs);
            }

            // Read before the push begins, same as the local-path copy: the
            // gate's claim is "everything at or before this sequence is
            // there" (FR-GC-009).
            var (syncedSequence, newestSnapshot) = await StagingPublicationSequenceAsync(archive, cancellationToken)
                .ConfigureAwait(false);

            // A peer under a retention policy converges like a local path
            // does (FR-GC-010): the hub computes the keep filter, pushes only
            // what it keeps, and instructs the spoke to drop the rest — when
            // the spoke offers the feature, and never past its floor.
            //
            // Three different situations end in a whole push, and they used to
            // be spelled identically: no policy is configured, the spoke does
            // not offer the retention feature, and the staging graph would not
            // walk. Only the third is a fault, and it is the one that leaves
            // the spoke holding history it was told to drop until somebody
            // repairs staging — so it is named.
            var effective = set.Destinations
                .FirstOrDefault(reference => string.Equals(reference.Ref, destination.Name, StringComparison.Ordinal))
                ?.Retention ?? set.Retention;
            Func<string, bool>? keeps = null;
            if (Retention.DestinationConvergence.HasRules(effective)
                && session.Supports(Protocol.PeerSessionNegotiation.RetentionInstructionFeature))
            {
                var convergence = await Retention.DestinationConvergence.ComputeKeepsAsync(
                    archive.Store, archive.Repository, effective!,
                    DateTimeOffset.FromUnixTimeMilliseconds((long)nowMs), cancellationToken).ConfigureAwait(false);
                keeps = convergence.Keeps;
                ReportConvergence(runtime, set, destination.Name, convergence.Refusal, nowMs);
            }

            // Samples are drawn from the pre-push listing under the set gate:
            // every key listed here is carried by the push that follows, so a
            // failed proof afterwards can only mean the destination's copy is
            // wrong — never that the sample raced a publication.
            //
            // The rotation resumes where the last passed challenge stopped, so
            // coverage accumulates instead of re-drawing the same objects for
            // the life of the destination (FR-VER-002). A peer keeps part of
            // its budget random: it answers the challenge itself, so a wholly
            // predictable rotation would tell it exactly which objects it can
            // afford to lose.
            var previous = ledger.Find(set.Id, destination.Name);

            // A challenge is answered by the peer and judged against bytes this
            // side reads for itself, so a source with no content plane of its
            // own can only ever challenge metadata — and a stamp drawn from
            // that population would report a verified replica while the part a
            // restore actually needs went unexamined. That is the emptiness
            // slice 3.3 found in the local-path path, arriving here by a new
            // road: a direct-ship set (ADR-0046) whose only destination is this
            // peer holds no blob anywhere else, so the sink can offer none
            // ([ADR-0058](../../docs/adr/0058-peer-write-adapter.md)).
            //
            // The pass therefore challenges nothing rather than challenging the
            // part that is cheap to hold, and says so where a human will see
            // it. A set with a local sibling is unaffected: the sink answers
            // blob reads from the sibling, which is a genuinely independent
            // copy.
            var contentSampleable = await HoldsAnyAsync(archive.Store, "blobs/", cancellationToken)
                .ConfigureAwait(false);

            var plan = contentSampleable
                && session.Supports(Protocol.PeerSessionNegotiation.DestinationVerificationFeature)
                ? await VerificationSampler.SampleAsync(
                    archive.Store, keeps, newestSnapshot, previous?.SampleCursor,
                    VerificationSampler.DefaultBudget, VerificationSampler.PeerReservoirShare,
                    Protocol.VerificationChallenge.MaximumLength, cancellationToken)
                    .ConfigureAwait(false)
                : new VerificationSampler.SamplePlan([], 0, previous?.SampleCursor);

            var priorSuccess = previous?.LastSuccessAt is not null;
            // The reclaim public key travels with every offer and is kept by
            // the destination at first attribution (ADR-0055 §5). Read from
            // wherever this repository can reach it — derived for v1, off the
            // write credential for v2 — so a write-only set, which cannot
            // derive the private half at all, still tells its peers which key
            // to check deletion instructions against.
            var reclaimPublicKey = archive.Repository.Hierarchy.ReclaimPublicKey(
                archive.Repository.CurrentMetadataGeneration);

            // The claim public key rides the same offer (ADR-0053 §1) and
            // takes no generation: the destination records it once and never
            // replaces it, so a key that turned over would go stale with no
            // way to say so.
            var claimPublicKey = archive.Repository.Hierarchy.ClaimPublicKey();

            // The commander signs each retention page under the reclaim key
            // (ADR-0055 §5) so the spoke can tell an authorised deletion from
            // one sent by whoever merely holds this session. A write-only set
            // cannot derive the key and signs nothing here; its peer retention
            // waits on a grant, exactly as its local collection does.
            Func<byte[], byte[]>? reclaimSigner = null;
            if (!archive.Repository.Keys.WriteOnly)
            {
                reclaimSigner = signed =>
                {
                    var generation = archive.Repository.CurrentMetadataGeneration;
                    var seed = archive.Repository.Hierarchy.DeriveReclaimKeySeed(generation);
                    try
                    {
                        using var signer = Repository.Crypto.RepositorySigner.FromSeed(seed, generation);
                        return signer.Sign(signed);
                    }
                    finally
                    {
                        System.Security.Cryptography.CryptographicOperations.ZeroMemory(seed);
                    }
                };
            }

            // The binding rides only to a spoke that says it verifies over one
            // (peer-protocol 02 §6): a current commander talking to an older
            // spoke signs the encoding that spoke can check, rather than having
            // every page refused. The reverse has no such accommodation and
            // must not — a spoke that accepted both encodings would be
            // accepting the replayable one.
            var sessionBinding =
                session.Supports(Protocol.PeerSessionNegotiation.SessionBoundRetentionFeature)
                    ? session.Binding
                    : default;

            var outcome = await ReplicationInitiator.PushAndConvergeAsync(
                archive.Store, archive.Repository.RepositoryId.ToArray(), session.Stream, keeps, cancellationToken,
                reclaimPublicKey, reclaimSigner,
                session.Supports(Protocol.PeerSessionNegotiation.PartialObjectResumeFeature),
                runtime.LoggerFor(typeof(ReplicationInitiator)),
                sessionBinding,
                claimPublicKey)
                .ConfigureAwait(false);

            ReportShortfall(
                runtime, set, destination.Name, priorSuccess, replicaRootMissing: false,
                outcome.HeldAtStart, outcome.Committed, nowMs);
            ReportHeadroom(runtime, destination, session.TheirTerms?.QuotaBytes ?? 0, outcome.Headroom, nowMs);

            if (plan.Samples.Count > 0)
            {
                // Challenges ride after the acknowledgement and after any
                // retention exchange (peer-protocol 04 §1): what this proves
                // is what the session leaves behind, not what it deletes.
                var verification = await ReplicationInitiator.ChallengeAsync(
                    archive.Store, archive.Repository.RepositoryId.ToArray(), session.Stream, plan.Samples,
                    cancellationToken).ConfigureAwait(false);
                if (verification.Failed.Count > 0)
                {
                    RecordVerificationFailure(runtime, set, destination.Name, verification, plan.Samples.Count, nowMs);
                    return;
                }

                ledger.RecordSuccess(set.Id, destination.Name, outcome.Committed, nowMs, syncedSequence);
                if (verification.ProvedSomething)
                {
                    ledger.RecordVerification(
                        set.Id, destination.Name, verification.Passed, plan.Population, syncedSequence,
                        plan.NextCursor, nowMs);
                }

                // Else: every sample was skipped because staging could not read
                // its own ground truth — a pass that established nothing. It is
                // not a destination fault, so the sync stands; but it is not
                // proof either, so no stamp is written and the trim gate stays
                // shut until one is.
                return;
            }

            // No ground truth to challenge against, but the replica can be
            // opened where it sits (ADR-0058 §8): a record read back through
            // the retrieval session authenticates under the repository's own
            // key, which this peer has never held, so nothing here needs a
            // second copy. This is the whole of a peer-only direct-ship set's
            // proof, and without it such a set is never checked at all.
            if (!contentSampleable)
            {
                if (session.Supports(Protocol.PeerSessionNegotiation.RetrievalFeature)
                    && await ReadBackAsync(runtime, set, destination, archive, outcome, nowMs, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return;
                }

                // The read-back is this set's only route to a proof, so a peer
                // that will not serve it — or that could not be reached to try
                // — leaves the content unexamined. Said where a human will see
                // it, because the alternative is a set that is quietly never
                // checked and looks no different from one that is.
                runtime.Notices.Raise(
                    $"content-unverifiable:{set.Id}:{destination.Name}",
                    $"Set '{set.Name}' keeps its file content only at peer '{destination.Name}', and that "
                    + "peer would not serve the read-back this installation uses to prove it — so the "
                    + "content is unchecked. The replica is whole and restores; what is missing is the "
                    + "proof. Upgrade that peer, or add a second destination.",
                    nowMs);
            }

            // Excused from proving (04 §1's acknowledged opt-out), or nothing
            // eligible to sample: the sync stands, unproven, and says so.
            ledger.RecordSuccess(set.Id, destination.Name, outcome.Committed, nowMs, syncedSequence);
        }
        catch (Protocol.PeerProtocolException refusal)
            when (refusal.Reason == Protocol.PeerRefusalReason.StorageExhausted)
        {
            // The lender's storage is faulty or full — a fault its side
            // fixes, retried under back-off until it does (05 §4/§5).
            ledger.RecordFailure(
                set.Id, destination.Name, DestinationSyncState.Unavailable,
                $"the peer cannot store right now: {refusal.Message}", nowMs);
        }
        catch (Protocol.PeerProtocolException refusal)
        {
            // Reached and refused — a stated reason, not an outage.
            ledger.RecordFailure(
                set.Id, destination.Name, DestinationSyncState.Failed,
                $"the peer refused replication: {refusal.Reason} — {refusal.Message}", nowMs);

            if (refusal.Reason == Protocol.PeerRefusalReason.Revoked)
            {
                // The peer ended the peering while this hub was away — the
                // refusal is the fallback delivery of that fact (ADR-0030
                // Amendment 2), and it must survive until a human sees it.
                runtime.Notices.Raise(
                    $"peering-terminated:{destination.Fingerprint}",
                    $"Peer '{destination.Name}' ended the peering — this set no longer replicates there. "
                    + "Remove or replace the destination in the configuration.",
                    nowMs);
            }

            if (refusal.Reason == Protocol.PeerRefusalReason.FeatureUnsupported)
            {
                // The destination cannot prove it holds what it is given, so
                // this hub will not build on it (FR-VER-006). Name both ways
                // out, because the right one is the human's call: fix the
                // destination, or take the unverifiable copy knowingly.
                runtime.Notices.Raise(
                    $"verification-unsupported:{set.Id}:{destination.Name}",
                    $"Destination '{destination.Name}' cannot answer verification challenges, so set "
                    + $"'{set.Name}' is not replicating there: an unprovable copy is not protection. "
                    + "Upgrade that peer, or — knowing it can never be proven and can never license "
                    + "reclaiming local space — set its `verification` to `acknowledged-none`.",
                    nowMs);
            }

            if (refusal.Reason == Protocol.PeerRefusalReason.TermsRefused)
            {
                // The lender's terms said no — a quota exhausted (05 §5) or a
                // retention floor defended (06 §3). Local protection
                // continues; the human decides what changes.
                runtime.Notices.Raise(
                    $"terms-refused:{destination.Fingerprint}",
                    $"Peer '{destination.Name}' refused this set: {refusal.Message}",
                    nowMs);
            }
        }
        catch (Exception exception) when (exception
            is System.Net.Sockets.SocketException or IOException or System.Security.Authentication.AuthenticationException)
        {
            // Could not be reached: the gap closes itself when the peer
            // returns (FR-DEST-003).
            ledger.RecordFailure(
                set.Id, destination.Name, DestinationSyncState.Unavailable, exception.Message, nowMs);
        }
    }

    /// <summary>
    /// The archive's highest snapshot publication sequence — the
    /// replication gate's currency (FR-GC-009) — and the store key of the
    /// snapshot carrying it, which every verification sample includes so the
    /// newest recovery point is the best-verified one (FR-VER-002). Read
    /// from the standalone snapshot records' cleartext counters: the one
    /// per-publication monotonic a single-writer archive has — staging or
    /// direct-ship — needing no keys and no catalogue.
    /// </summary>
    /// <summary>
    /// The keep-set rendering of a destination whose policy keeps everything.
    /// </summary>
    /// <remarks>
    /// A value rather than null, because the ledger reads null as "this pass
    /// computed none" and carries the last one forward (ADR-0056). Keeping
    /// everything is a keep-set, and a destination whose policy is removed has
    /// to be able to say so.
    /// </remarks>
    private const string KeepsEverything = "keeps-all";

    private static async ValueTask<(ulong Sequence, string? NewestSnapshotKey)> StagingPublicationSequenceAsync(
        ArchiveHandle archive, CancellationToken cancellationToken)
    {
        var highest = 0UL;
        string? newestKey = null;
        await foreach (var entry in archive.Store.ListAsync(
            Storage.Abstractions.ObjectPrefix.Parse("snapshots/"),
            Storage.Abstractions.ListOptions.Default, cancellationToken).ConfigureAwait(false))
        {
            using var read = await archive.Store.OpenReadAsync(entry.Key, range: null, cancellationToken)
                .ConfigureAwait(false);
            if (read.Outcome != Storage.Abstractions.OpenReadOutcome.Found)
            {
                continue;
            }

            using var memory = new MemoryStream();
            await read.Content!.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            try
            {
                var counter = Repository.Format.Records.StandaloneRecordFraming.Parse(memory.ToArray()).Counter;
                if (newestKey is null || counter > highest)
                {
                    highest = Math.Max(highest, counter);
                    newestKey = entry.Key.Value;
                }
            }
            catch (FormatException)
            {
                // An unparseable snapshot object claims nothing here; the
                // collector's survey will veto deletion over it anyway.
            }
        }

        return (highest, newestKey);
    }

    private static async ValueTask CopyToLocalPathAsync(
        ServiceRuntime runtime, BackupSetConfiguration set, DestinationConfiguration destination,
        ArchiveHandle archive, ulong nowMs, CancellationToken cancellationToken)
    {
        var ledger = runtime.DestinationSync;

        // A declaration the product itself calls defective (a relative path,
        // hand-edited into the file) must not reach Directory.Exists and
        // Path.Combine below — those resolve against the process working
        // directory, which is how a replica tree once appeared beside the
        // logs. Failed rather than Unavailable: this needs a person to fix
        // the declaration, not a retry.
        if (destination.AddressDefect is { } addressDefect)
        {
            ledger.RecordFailure(set.Id, destination.Name, DestinationSyncState.Failed, addressDefect, nowMs);
            return;
        }

        if (!Directory.Exists(destination.Path))
        {
            ledger.RecordFailure(
                set.Id, destination.Name, DestinationSyncState.Unavailable,
                $"destination path '{destination.Path}' does not exist", nowMs);
            return;
        }

        try
        {
            // The archive lands under its repository id, the same shape a peer
            // responder gives replicas — the destination directory can hold
            // several sets' archives side by side, each independently
            // restorable by pointing the recovery tool at it.
            var replicaRoot = Path.Combine(destination.Path!, archive.Repository.RepositoryId.ToString());

            // Asked BEFORE the directory is created, which is the only moment
            // the answer exists. A replica root that has gone since a recorded
            // success is unambiguous loss — and it catches the case
            // Directory.Exists on the parent cannot: a different drive mounted
            // at the same point, where the destination path is present and the
            // archive under it is somebody else's.
            var replicaRootMissing = !Directory.Exists(replicaRoot);
            Directory.CreateDirectory(replicaRoot);

            // Read before this pass writes over it: "did we believe this
            // destination held our data a moment ago" is the question the
            // shortfall check rests on, and the rotation cursor below is the
            // other half of the same before-picture.
            var previous = ledger.Find(set.Id, destination.Name);
            var priorSuccess = previous?.LastSuccessAt is not null;

            // The sequence is read BEFORE the copy starts: a success then
            // proves the destination holds everything published at or before
            // it, which is what the replication gate compares snapshots to
            // (FR-GC-009). A snapshot publishing mid-copy may or may not have
            // crossed, so the claim stops at the pre-copy sequence.
            var (syncedSequence, newestSnapshot) = await StagingPublicationSequenceAsync(archive, cancellationToken)
                .ConfigureAwait(false);
            var replica = new LocalFileSystemObjectStore(replicaRoot);

            // Filling a destination volume to zero is a harm to the machine,
            // not just to this backup: logs stop, temp files fail, and on the
            // source's own volume the next capture cannot even stage. The copy
            // is held off before it starts rather than after it has taken the
            // last of the space.
            if (DestinationCapacity.FloorShortfall(replicaRoot, AvailableBytesOn(replicaRoot)) is { } shortOfSpace)
            {
                // Unavailable, not Failed: deleting something frees the space
                // and the next pass simply succeeds (FR-DEST-003). Nothing
                // here needs a human's decision, only room.
                ledger.RecordFailure(
                    set.Id, destination.Name, DestinationSyncState.Unavailable, shortOfSpace, nowMs);
                return;
            }

            // A destination under a retention policy holds exactly its
            // keep-set's closure, converged in one operation with the copy so
            // fan-out and retention cannot disagree (FR-GC-010). One without a
            // policy gets the conservative whole copy — and so does a pass
            // whose staging graph will not walk, but that second case is a
            // fault rather than a choice and now says so.
            var effective = set.Destinations
                .FirstOrDefault(reference => string.Equals(reference.Ref, destination.Name, StringComparison.Ordinal))
                ?.Retention ?? set.Retention;
            Func<string, bool>? keeps = null;

            // "Keeps everything" is a keep-set and has a rendering of its own:
            // null would mean "nobody computed one", which is what the ledger
            // carries forward rather than compares (ADR-0056).
            var keepFingerprint = KeepsEverything;
            if (Retention.DestinationConvergence.HasRules(effective))
            {
                var convergence = await Retention.DestinationConvergence.ComputeKeepsAsync(
                    archive.Store, archive.Repository, effective!,
                    DateTimeOffset.FromUnixTimeMilliseconds((long)nowMs), cancellationToken).ConfigureAwait(false);
                keeps = convergence.Keeps;
                keepFingerprint = convergence.Fingerprint ?? KeepsEverything;
                ReportConvergence(runtime, set, destination.Name, convergence.Refusal, nowMs);
            }

            // What this pass has to do, decided before it reads anything
            // (ADR-0056). A pair the last pass left level, with nothing
            // published since and its keep-set unmoved, is one this pass can
            // answer from what that pass wrote down — and the reading-through
            // it wrote down expires, so the answer cannot go stale for ever.
            // A migrating direct-ship set keeps its staging archive until
            // retirement, and the ledger cannot speak for the history only
            // that archive holds: its runs record success for what they
            // shipped (ADR-0046 §3). Until the archive is gone, every pass
            // reads through — which is what seeds the destination and what
            // lets retirement establish that nothing would be lost.
            var stagingRemains = archive.ShipSink is not null
                && File.Exists(Path.Combine(
                    runtime.ArchivePath(set.Id), Repository.RepositoryLifecycle.DescriptorKey.Value));

            var scope = ReconciliationGate.Decide(
                previous, syncedSequence, keepFingerprint, nowMs,
                ReconciliationGate.DefaultIntervalMilliseconds, stagingRemains);

            // Samples come from the pre-copy listing, filtered like the copy
            // itself: everything sampled is carried by the copy below, so a
            // mismatch afterwards is the replica's fault, never a race with a
            // publication that had not crossed yet.
            //
            // The rotation resumes after the last passed challenge, so
            // coverage accumulates across syncs (FR-VER-002). No reservoir
            // share here: this hub reads the replica's bytes off its own disk,
            // so there is nobody on the other side who could arrange to hold
            // only the objects it expects to be asked about.
            var plan = await VerificationSampler.SampleAsync(
                archive.Store, keeps, newestSnapshot, previous?.SampleCursor,
                VerificationSampler.DefaultBudget, reservoirShare: 0,
                Protocol.VerificationChallenge.MaximumLength, cancellationToken)
                .ConfigureAwait(false);

            // The converge spare (FR-GC-009's direct-ship shape): under
            // direct-ship the replicas are the only holders, so before this
            // destination's policy may drop anything, the closure of every
            // snapshot a sibling is still owed is set aside — a narrow
            // override must not delete the last copy of history a wide
            // sibling has not received yet. A staging set needs none of
            // this: the gate holds the staging copy until every entitled
            // destination provably has its own (FR-GC-009), so a trimmed
            // replica is re-seedable from staging.
            Func<string, bool>? spares = null;
            if (keeps is not null && archive.ShipSink is not null)
            {
                var sparePlan = await Retention.DestinationConvergence.ComputeSparesAsync(
                    archive.Store, archive.Repository, set.Destinations, set.Retention,
                    name => ledger.Find(set.Id, name), nowMs, cancellationToken).ConfigureAwait(false);
                spares = sparePlan.Spares;

                // Folded into the fingerprint the gate compares, because a
                // spare set moves when a sibling catches up and nothing about
                // that is published: a pass that skipped over it would keep
                // holding copies whose only reason to exist had been
                // delivered (ADR-0056).
                keepFingerprint = $"{keepFingerprint}/{sparePlan.Fingerprint}";
            }

            // Held against owed, in bytes, as the copy discovers it. Kept
            // here rather than returned by the copier because the figure
            // matters most when the pass does NOT finish: a drive pulled
            // halfway leaves a destination genuinely part-full, and a number
            // that only survived success could never say so. Recorded below
            // on every exit, which is why it is captured and not written as
            // it arrives — a ledger write per object would be thousands.
            CopyProgress? completeness = null;
            var counting = new Progress<CopyProgress>(latest => completeness = latest);

            long copied = 0;
            long alreadyHeld = 0;
            if (scope == SyncScope.Skip)
            {
                // Nothing published since this pair was last read through, its
                // keep-set has not moved, and the reading-through is still
                // good: there is nothing a listing could discover, so the pass
                // costs the sequence read that established it. The completeness
                // figures and the shortfall check both belong to a pass that
                // counted something, and this one counted nothing.
                var skipLog = runtime.LoggerFor(typeof(FanOut));
                Log.SyncSkipped(skipLog, set.Name, destination.Name, syncedSequence);
                alreadyHeld = previous?.Objects ?? 0;
            }
            else
            {
                var copyScope = scope == SyncScope.Reconcile ? CopyScope.Reconcile : CopyScope.Incremental;
                try
                {
                    if (keeps is not null)
                    {
                        var converged = await StoreToStoreCopier.ConvergeAsync(
                            archive.Store, replica, keeps, cancellationToken,
                            destination.Name, runtime.LoggerFor(typeof(StoreToStoreCopier)),
                            spares, counting, copyScope).ConfigureAwait(false);
                        copied = converged.Copied;
                        alreadyHeld = converged.AlreadyHeld;
                    }
                    else
                    {
                        var outcome = await StoreToStoreCopier.CopyAsync(
                            archive.Store, replica, cancellationToken,
                            destination.Name, runtime.LoggerFor(typeof(StoreToStoreCopier)),
                            counting, copyScope).ConfigureAwait(false);
                        copied = outcome.Copied;
                        alreadyHeld = outcome.AlreadyHeld;
                    }
                }
                finally
                {
                    if (completeness is { } counted)
                    {
                        ledger.RecordCompleteness(
                            set.Id, destination.Name, counted.HeldBytes, counted.OwedBytes, nowMs);
                    }
                }

                ReportShortfall(
                    runtime, set, destination.Name, priorSuccess, replicaRootMissing, alreadyHeld, copied, nowMs);
            }

            // Only a pass that read both inventories through may say so: the
            // stamp is what a later pass skips on, and an incremental pass has
            // not looked at the parts it did not walk.
            var reconciled = scope == SyncScope.Reconcile;

            if (plan.Samples.Count > 0)
            {
                // The local twin of the peer challenge (peer-protocol 04):
                // both destination kinds earn "verified" from bytes read back
                // off the destination's own disk, never from a copy having
                // reported success (FR-VER-001).
                // The repository is handed in so the blob half can be proved
                // at the replica by its own AEAD tags. Without it the only
                // proof is a comparison, and a direct-ship set has nothing
                // independent to compare against — archive.Store reads blobs
                // back from the destinations themselves (ADR-0046), so the
                // comparison would put this replica against itself.
                var verification = await Replication.ReplicaVerifier.VerifyAsync(
                    archive.Store, replica, plan.Samples, cancellationToken, archive.Repository)
                    .ConfigureAwait(false);
                if (verification.Failed.Count > 0)
                {
                    RecordVerificationFailure(runtime, set, destination.Name, verification, plan.Samples.Count, nowMs);
                    return;
                }

                ledger.RecordSuccess(
                    set.Id, destination.Name, copied, nowMs, syncedSequence,
                    keepFingerprint, reconciled, newestSnapshot);
                if (verification.ProvedSomething)
                {
                    ledger.RecordVerification(
                        set.Id, destination.Name, verification.Passed, plan.Population, syncedSequence,
                        plan.NextCursor, nowMs);
                }

                return;
            }

            ledger.RecordSuccess(
                set.Id, destination.Name, copied, nowMs, syncedSequence,
                keepFingerprint, reconciled, newestSnapshot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ledger.RecordFailure(
                set.Id, destination.Name, DestinationSyncState.Failed, exception.Message, nowMs);
        }
    }

    /// <summary>
    /// What the platform says is free on the volume holding a path, or null
    /// when it will not say.
    /// </summary>
    /// <remarks>
    /// Only the measurement lives here; what the number means is
    /// <see cref="DestinationCapacity"/>'s, which is testable without a disk.
    /// A platform that will not answer answers null, and the policy reads that
    /// as room — this guard exists to stop a disk being filled, and must never
    /// be the reason a healthy destination stops receiving backups.
    /// </remarks>
    private static long? AvailableBytesOn(string replicaRoot)
    {
        try
        {
            // ProbeRootFor, not Path.GetPathRoot: on Unix the latter answers
            // "/" for every absolute path, so the floor was measured on the
            // OS volume — precisely wrong for a destination on a different
            // volume, which is a destination's whole job.
            return new DriveInfo(DestinationCapacity.ProbeRootFor(Path.GetFullPath(replicaRoot)))
                .AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException
            or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// How little of a peer's loan may be left before the operator is told.
    /// </summary>
    /// <remarks>
    /// A tenth, rather than an absolute number of bytes, because the loan
    /// itself is whatever two people agreed: a gigabyte left is comfortable
    /// under a hundred-gigabyte quota and nearly nothing under a hundred
    /// megabytes.
    /// </remarks>
    private const int HeadroomWarningPercent = 10;

    /// <summary>
    /// Warns when a peer's loan is nearly spent, and takes the warning back
    /// when it is not (05 §4).
    /// </summary>
    /// <remarks>
    /// A warning rather than a refusal, deliberately. The boundary stop
    /// already refuses the exact object that would cross the line, with exact
    /// numbers, at the exact moment — and it preserves everything copied
    /// before it, which <c>StoreToStoreCopier</c> is built around. Refusing
    /// the whole session early would throw that partial progress away to say
    /// something less precise, sooner. What was missing was only that nobody
    /// heard about it until it happened.
    /// </remarks>
    private static void ReportHeadroom(
        ServiceRuntime runtime, DestinationConfiguration destination, ulong quota, ulong? headroom, ulong nowMs)
    {
        var key = $"destination-headroom:{destination.Name}";
        if (quota == 0 || headroom is not { } free)
        {
            // No ceiling, or a destination whose build does not say. Neither
            // is a finding, and a resolve here clears a warning left by a
            // quota that has since been lifted.
            runtime.Notices.Resolve(key, nowMs);
            return;
        }

        if (free > quota / HeadroomWarningPercent)
        {
            runtime.Notices.Resolve(key, nowMs);
            return;
        }

        runtime.Notices.Raise(
            key,
            $"Destination '{destination.Name}' has {free} byte(s) left of the {quota} it lends — under "
            + $"{HeadroomWarningPercent}%. Syncs will keep succeeding until the next object does not fit, "
            + "and then stop at exactly that object. Free space there, ask for more, or tighten this "
            + "destination's retention.",
            nowMs);
    }

    /// <summary>
    /// Records that a destination was found empty despite having held this
    /// set's data before — it lost what it was given, and the sync that just
    /// ran quietly put it back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the failure fan-out was blind to. A destination declares what
    /// it holds at the start of every sync, the source pushes the difference,
    /// and the pass reports success — so a destination that was wiped between
    /// passes is simply re-seeded, and the ledger's next row looks exactly
    /// like a healthy one. The evidence was there each time and thrown away:
    /// the peer's inventory count, and the local copier's already-held tally.
    /// </para>
    /// <para>
    /// The signal is that tally collapsing to zero, not "objects were copied
    /// while nothing new was published" — which is what it first looks like it
    /// should be. That version fires on an interrupted sync resuming (the
    /// failure path records nothing, so the sequence has not moved), on a
    /// widened keep-set legitimately re-pushing what convergence dropped, and
    /// on a peer that has just gained the retention feature. All three leave
    /// already-held LARGE. Only an emptied destination reports zero — and a
    /// live replica always holds at least <c>repository-format</c> and
    /// <c>keys/</c>, so zero really does mean nothing is there.
    /// </para>
    /// <para>
    /// The pass is <b>not</b> recorded as a failure. The copy genuinely
    /// succeeded and the data genuinely is there now; marking it failed would
    /// start the back-off that delays the next sync, which is the repair. What
    /// must not be lost is the fact that this destination lost data once —
    /// which is a finding, so it goes in a notice and is deliberately not
    /// resolved when the next sync goes well. A destination that eats backups
    /// does not stop having done so.
    /// </para>
    /// </remarks>
    private static void ReportShortfall(
        ServiceRuntime runtime, BackupSetConfiguration set, string destinationName,
        bool priorSuccess, bool replicaRootMissing, long heldAtStart, long copied, ulong nowMs)
    {
        if (!priorSuccess || (heldAtStart > 0 && !replicaRootMissing) || copied <= 0)
        {
            return;
        }

        var cause = replicaRootMissing
            ? "its replica directory was gone"
            : "it declared holding nothing";
        runtime.Notices.Raise(
            $"destination-shortfall:{set.Id}:{destinationName}",
            $"destination '{destinationName}' of set '{set.Name}' had lost this set's data — {cause}, though a "
            + $"previous sync had succeeded against it. {copied} object(s) were copied back, so the destination is "
            + "current again, but it discarded a backup once and may do so again: check the device, the filesystem, "
            + "and anything else that writes there before counting on it.",
            nowMs);
    }

    /// <summary>
    /// Says whether this destination's convergence filter could be computed,
    /// and withdraws the notice once it can be again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A destination whose filter cannot be built receives the whole archive
    /// instead of its keep-set. That is the safe answer — a keep decision
    /// resting on a graph nobody can walk would drop objects it merely could
    /// not see — but it is not the *intended* one, and it silently undoes the
    /// destination's retention policy: history it was configured to shed keeps
    /// arriving, and keeps being kept, for as long as staging stays damaged.
    /// Nothing said so, because the code path is shared with the perfectly
    /// ordinary "this destination has no retention rules".
    /// </para>
    /// <para>
    /// This is a notice rather than a status warning because it outlives the
    /// pass that saw it: the next sync may find the graph walking again and
    /// converge normally, leaving no trace that a whole copy was ever taken.
    /// It resolves itself the moment a filter computes, so it names a
    /// condition that is true now, not one that once was (Z0c's rule).
    /// </para>
    /// </remarks>
    private static void ReportConvergence(
        ServiceRuntime runtime, BackupSetConfiguration set, string destinationName,
        Retention.ConvergenceRefusal? refusal, ulong nowMs)
    {
        var key = $"convergence-unavailable:{set.Id}:{destinationName}";
        if (refusal is not { } reason)
        {
            runtime.Notices.Resolve(key, nowMs);
            return;
        }

        var cause = reason == Retention.ConvergenceRefusal.UndecodableSnapshots
            ? "the staging archive holds snapshots it cannot decode"
            : "the keep-set's closure would not walk cleanly in the staging archive";
        runtime.Notices.Raise(
            key,
            $"destination '{destinationName}' of set '{set.Name}' received a whole copy instead of its "
            + $"retention keep-set: {cause}. Its policy is not being applied and it will keep history it was "
            + "configured to drop until staging is repaired — run `check` to find the damage.",
            nowMs);
    }

    /// <summary>
    /// A failed proof is a durable finding, never a silent retry (FR-VER-005):
    /// the pair's row goes <see cref="DestinationSyncState.Failed"/> — so the
    /// destination stops counting toward protection and the trim gate — and a
    /// notice names the destination and the first unproven key until a human
    /// acknowledges it.
    /// </summary>
    private static void RecordVerificationFailure(
        ServiceRuntime runtime, BackupSetConfiguration set, string destinationName,
        Replication.VerificationOutcome verification, int sampled, ulong nowMs)
    {
        var summary = $"verification failed: {verification.Failed.Count} of {sampled} sampled range(s) "
            + $"could not be proven, first '{verification.Failed[0]}'";
        runtime.DestinationSync.RecordFailure(
            set.Id, destinationName, DestinationSyncState.Failed, summary, nowMs);
        runtime.Notices.Raise(
            $"verification-failed:{set.Id}:{destinationName}",
            $"Destination '{destinationName}' failed verification for set '{set.Name}': "
            + $"{verification.Failed.Count} of {sampled} sampled range(s) unproven (first: '{verification.Failed[0]}'). "
            + "Its copy may be damaged or withheld; it does not count toward protection until a sync verifies clean.",
            nowMs);
    }
}
