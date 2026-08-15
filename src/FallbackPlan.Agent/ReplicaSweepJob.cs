using FallbackPlan.Application;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Repository;
using FallbackPlan.Storage.Local;

namespace FallbackPlan.Agent;

/// <summary>
/// Runs one segment of a replica's deep sweep as a queued job: re-read the next
/// bounded run of stored blobs and confirm they are still what was sealed
/// (FR-VER-002, ADR-0034 §5).
/// </summary>
/// <remarks>
/// <para>
/// On the <b>transfer</b> lane, with fan-out, and not the reader lane despite
/// that lane's name mentioning verification. A sweep reads a replica; a
/// convergence writes and deletes in the same replica; the reader lane runs
/// alongside the transfer lane by design. Sweeping there would read a replica
/// mid-convergence and report damage that never existed — and a verification
/// failure sets the pair <c>Failed</c> and raises a durable notice, so a false
/// one is expensive to unsay.
/// </para>
/// <para>
/// The transfer lane has one worker for the whole process, which is what makes
/// the per-segment budget load-bearing rather than tidy: an unbounded sweep of
/// a large archive would stall fan-out to every destination of every set for as
/// long as it took.
/// </para>
/// </remarks>
internal static class ReplicaSweepJob
{
    /// <summary>Days between segments when a destination states no preference.</summary>
    public const int DefaultIntervalDays = 7;

    /// <summary>The job identity, distinct from the pair's sync job so the two never displace each other.</summary>
    public static string JobIdFor(string setId, string destinationName) =>
        $"sweep-{setId}-{destinationName}";

    /// <summary>Queues one segment; null when a segment for this pair is already queued or running.</summary>
    public static Task? Enqueue(
        ServiceRuntime runtime,
        BackupSetConfiguration set,
        string destinationName,
        DateTimeOffset now,
        bool userInitiated)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = runtime.Queue.Enqueue(new QueuedJob(
            JobIdFor(set.Id, destinationName),
            JobLane.Transfer,
            userInitiated,
            $"verify {set.Name} -> {destinationName}",
            async token =>
            {
                try
                {
                    await RunAsync(runtime, set, destinationName, (ulong)now.ToUnixTimeMilliseconds(), token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    completion.TrySetResult();
                }
            }));

        return queued ? completion.Task : null;
    }

    /// <summary>Reads the next segment and records what it found.</summary>
    public static async Task RunAsync(
        ServiceRuntime runtime,
        BackupSetConfiguration set,
        string destinationName,
        ulong nowMs,
        CancellationToken cancellationToken)
    {
        if (runtime.Configuration.FindDestination(destinationName) is not
            { Kind: DestinationKind.LocalPath } destination)
        {
            return;
        }

        var archive = await runtime.ExistingArchiveAsync(set.Id, cancellationToken).ConfigureAwait(false);
        if (archive is null)
        {
            return;
        }

        var replicaRoot = Path.Combine(destination.Path!, archive.Repository.RepositoryId.ToString());
        if (!Directory.Exists(replicaRoot))
        {
            // Unreachable right now. Fan-out owns saying so — its shortfall and
            // availability signals already cover a replica that has gone, and
            // a second voice saying it would be a second notice to acknowledge.
            return;
        }

        // The set gate, for the same reason fan-out takes it: a retention apply
        // mutates staging, and the length comparison reads staging.
        var gate = runtime.SetGate(set.Id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ledger = runtime.DestinationSync;
            var previous = ledger.Find(set.Id, destinationName);

            // In-memory progress only. A journal entry would be keyed by set,
            // and the scheduler reads the journal's last-completed back as the
            // BACKUP anchor — a sweep writing there would move the next
            // backup's due-ness. So `Verifying` is reported and not recorded,
            // which is also why nothing polls for it to settle.
            runtime.Progress.Report(new JobProgress(
                JobIdFor(set.Id, destinationName), JobState.Verifying, 0, 0, 0, 0, 0, 0));

            var result = await ReplicaSweep.RunAsync(
                archive.Repository.RepositoryId,
                archive.Repository.Keys,
                new LocalFileSystemObjectStore(replicaRoot),
                archive.Store,
                previous?.SweepCursor,
                ReplicaSweep.DefaultBudget,
                cancellationToken).ConfigureAwait(false);

            if (result.Findings.Count > 0)
            {
                // FR-VER-005: a verification failure degrades the pair and
                // raises a warning requiring action. The cursor still advances
                // — a damaged blob must not park the sweep on itself forever,
                // re-reporting the same object while the rest goes unchecked.
                ledger.RecordSweep(
                    set.Id, destinationName, result.NextCursor, result.Examined, result.CompletedCircuit, nowMs);
                ledger.RecordFailure(
                    set.Id, destinationName, DestinationSyncState.Failed,
                    $"deep verification found {result.Findings.Count} damaged object(s): {result.Findings[0]}", nowMs);
                runtime.Notices.Raise(
                    $"deep-verify-failed:{set.Id}:{destinationName}",
                    $"destination '{destinationName}' of set '{set.Name}' holds {result.Findings.Count} object(s) "
                    + $"that no longer match what was sealed: {string.Join("; ", result.Findings.Take(3))}. "
                    + "Those bytes cannot be restored from there — treat this destination as damaged until the "
                    + "objects are re-copied and re-verified.",
                    nowMs);
                return;
            }

            ledger.RecordSweep(
                set.Id, destinationName, result.NextCursor, result.Examined, result.CompletedCircuit, nowMs);

            // A clean circuit withdraws an earlier finding: the objects that
            // were damaged have been re-read and are sound now. A partial
            // segment does not — it has not looked at everything yet.
            if (result.CompletedCircuit)
            {
                runtime.Notices.Resolve($"deep-verify-failed:{set.Id}:{destinationName}", nowMs);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The disk went away mid-segment. The cursor is not advanced, so
            // the next pass re-reads the same run; fan-out reports the
            // destination's availability.
        }
        finally
        {
            gate.Release();
        }
    }
}
