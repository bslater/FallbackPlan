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

        switch (destination.Kind)
        {
            case DestinationKind.LocalPath:
                await CopyToLocalPathAsync(runtime, set, destination, archive, nowMs, cancellationToken)
                    .ConfigureAwait(false);
                return;

            case DestinationKind.Peer:
                // Served by the peer-destination slice; the manual `replicate`
                // verb covers the gap meanwhile. Stated, not failed.
                ledger.RecordFailure(
                    set.Id, destinationName, DestinationSyncState.NotSupported,
                    "peer destinations are not yet fanned out automatically — `replicate` covers the gap", nowMs);
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

            var outcome = await StoreToStoreCopier.CopyAsync(
                archive.Store, new LocalFileSystemObjectStore(replicaRoot), cancellationToken).ConfigureAwait(false);

            ledger.RecordSuccess(set.Id, destination.Name, outcome.Copied, nowMs);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ledger.RecordFailure(
                set.Id, destination.Name, DestinationSyncState.Failed, exception.Message, nowMs);
        }
    }
}
