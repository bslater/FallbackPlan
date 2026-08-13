using Bodu;
using FallbackPlan.Application;
using FallbackPlan.Replication;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Agent;

/// <summary>
/// Fans one backup set's staging archive out to its declared destinations
/// (ADR-0034 §3): the copy runs on the transfer lane, at most one queued or
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
            }));

        return accepted ? completion.Task : null;
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

        var grants = Protocol.PeerGrantStore.Open(runtime.Options.StateDirectory);
        var grant = grants.Grants.FirstOrDefault(candidate =>
            string.Equals(candidate.Identity.Fingerprint, destination.Fingerprint, StringComparison.Ordinal));
        if (grant is null)
        {
            ledger.RecordFailure(
                set.Id, destination.Name, DestinationSyncState.Failed,
                $"no pairing matches fingerprint '{destination.Fingerprint}' — pair with the peer first", nowMs);
            return;
        }

        var endpoint = destination.Endpoint!;
        var separator = endpoint.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(endpoint[(separator + 1)..], out var port))
        {
            ledger.RecordFailure(
                set.Id, destination.Name, DestinationSyncState.Failed,
                $"endpoint '{endpoint}' is not host:port", nowMs);
            return;
        }

        try
        {
            using var keypair = Protocol.PeerKeypairStore.Open(runtime.Options.StateDirectory);
            await using var connection = await Protocol.PeerTlsConnection.DialAsync(
                endpoint[..separator], port, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            var session = await Protocol.PeerSessionDriver.DialAsync(
                connection, keypair, grants, grant.Identity, "fallbackplan-agent", terms: null, cancellationToken)
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
            var syncedSequence = await StagingPublicationSequenceAsync(archive, cancellationToken)
                .ConfigureAwait(false);

            // A peer under a retention policy converges like a local path
            // does (FR-GC-010): the hub computes the keep filter, pushes only
            // what it keeps, and instructs the spoke to drop the rest — when
            // the spoke offers the feature, and never past its floor. A pass
            // whose staging graph will not walk cleanly pushes whole.
            var effective = set.Destinations
                .FirstOrDefault(reference => string.Equals(reference.Ref, destination.Name, StringComparison.Ordinal))
                ?.Retention ?? set.Retention;
            var keeps = Retention.DestinationConvergence.HasRules(effective)
                    && session.Supports(Protocol.PeerSessionNegotiation.RetentionInstructionFeature)
                ? await Retention.DestinationConvergence.ComputeKeepsAsync(
                    archive.Store, archive.Repository, effective!,
                    DateTimeOffset.FromUnixTimeMilliseconds((long)nowMs), cancellationToken).ConfigureAwait(false)
                : null;

            var outcome = await ReplicationInitiator.PushAndConvergeAsync(
                archive.Store, archive.Repository.RepositoryId.ToArray(), session.Stream, keeps, cancellationToken)
                .ConfigureAwait(false);

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
    /// The staging archive's highest snapshot publication sequence — the
    /// replication gate's currency (FR-GC-009). Read from the standalone
    /// snapshot records' cleartext counters: the one per-publication
    /// monotonic a single-writer staging archive has, needing no keys and
    /// no catalogue.
    /// </summary>
    private static async ValueTask<ulong> StagingPublicationSequenceAsync(
        ArchiveHandle archive, CancellationToken cancellationToken)
    {
        var highest = 0UL;
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
                highest = Math.Max(
                    highest, Repository.Format.Records.StandaloneRecordFraming.Parse(memory.ToArray()).Counter);
            }
            catch (FormatException)
            {
                // An unparseable snapshot object claims nothing here; the
                // collector's survey will veto deletion over it anyway.
            }
        }

        return highest;
    }

    private static async ValueTask CopyToLocalPathAsync(
        ServiceRuntime runtime, BackupSetConfiguration set, DestinationConfiguration destination,
        ArchiveHandle archive, ulong nowMs, CancellationToken cancellationToken)
    {
        var ledger = runtime.DestinationSync;

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
            Directory.CreateDirectory(replicaRoot);

            // The sequence is read BEFORE the copy starts: a success then
            // proves the destination holds everything published at or before
            // it, which is what the replication gate compares snapshots to
            // (FR-GC-009). A snapshot publishing mid-copy may or may not have
            // crossed, so the claim stops at the pre-copy sequence.
            var syncedSequence = await StagingPublicationSequenceAsync(archive, cancellationToken)
                .ConfigureAwait(false);
            var replica = new LocalFileSystemObjectStore(replicaRoot);

            // A destination under a retention policy holds exactly its
            // keep-set's closure, converged in one operation with the copy so
            // fan-out and retention cannot disagree (FR-GC-010). One without
            // a policy — and any pass where the staging graph will not walk
            // cleanly — gets the conservative whole copy.
            var effective = set.Destinations
                .FirstOrDefault(reference => string.Equals(reference.Ref, destination.Name, StringComparison.Ordinal))
                ?.Retention ?? set.Retention;
            var keeps = Retention.DestinationConvergence.HasRules(effective)
                ? await Retention.DestinationConvergence.ComputeKeepsAsync(
                    archive.Store, archive.Repository, effective!,
                    DateTimeOffset.FromUnixTimeMilliseconds((long)nowMs), cancellationToken).ConfigureAwait(false)
                : null;

            long copied;
            if (keeps is not null)
            {
                var converged = await StoreToStoreCopier.ConvergeAsync(
                    archive.Store, replica, keeps, cancellationToken).ConfigureAwait(false);
                copied = converged.Copied;
            }
            else
            {
                var outcome = await StoreToStoreCopier.CopyAsync(
                    archive.Store, replica, cancellationToken).ConfigureAwait(false);
                copied = outcome.Copied;
            }

            ledger.RecordSuccess(set.Id, destination.Name, copied, nowMs, syncedSequence);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ledger.RecordFailure(
                set.Id, destination.Name, DestinationSyncState.Failed, exception.Message, nowMs);
        }
    }
}
