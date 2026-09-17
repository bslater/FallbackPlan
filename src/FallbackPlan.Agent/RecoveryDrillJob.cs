using FallbackPlan.Api;
using FallbackPlan.Application;

namespace FallbackPlan.Agent;

/// <summary>
/// The scheduled restore drill (ADR-0054; FR-DRL-002, NFR-OPS-005): bring a
/// sampled file back out of a destination's own replica, on a cadence, and
/// record what happened so "we could recover" stops being a claim about the
/// last time somebody chose to check.
/// </summary>
/// <remarks>
/// <para>
/// A drill is not a verification, and the two are kept apart because they
/// fail apart. A possession challenge proves a destination still holds
/// authentic bytes; a drill proves the road from those bytes back to a file
/// is open — the index plane rebuilds, the catalogue projects, the manifest
/// decodes, the segments assemble in order, and the reassembly hashes to what
/// the capture recorded. A replica can pass every challenge and fail every
/// one of those.
/// </para>
/// <para>
/// It runs through the ordinary guided-restore verbs rather than a private
/// copy of them, which is the point: what the drill exercises is what a
/// person would use, so the two cannot drift apart. Those verbs open the
/// replica the way a stranger would — its own store, its own repository open,
/// a throwaway catalogue rebuilt from its own index plane (ADR-0041) — so
/// nothing here can be answered by the live archive.
/// </para>
/// <para>
/// It is therefore <b>not</b> queued on a lane. The verbs it calls queue
/// themselves on the reader lane, which has one worker, so a drill holding
/// that worker while waiting for it would wait for ever.
/// </para>
/// </remarks>
internal static class RecoveryDrillJob
{
    /// <summary>Days between drills when a destination states no preference.</summary>
    public const int DefaultIntervalDays = 30;

    /// <summary>Files one drill restores.</summary>
    /// <remarks>
    /// Enough that a single unlucky pick cannot make a broken replica look
    /// sound, few enough that the drill stays a background cost. The bytes
    /// are bounded by the sample, not by the archive: the expensive half is
    /// the catalogue rebuild, which is paid once per drill however many files
    /// follow it.
    /// </remarks>
    public const int SampleFiles = 3;

    /// <summary>How deep the random descent will go looking for a file.</summary>
    private const int MaximumDepth = 24;

    /// <summary>What one drill found.</summary>
    /// <param name="Files">Files restored whole — or, under <paramref name="Limit"/>, proved as far as the sealed content.</param>
    /// <param name="Bytes">What they amounted to; zero under a limit, since nothing was written.</param>
    /// <param name="Failure">Why it did not work, or null when it did.</param>
    /// <param name="Limit">What a passing drill could not prove, or null when it proved everything.</param>
    public sealed record DrillOutcome(int Files, long Bytes, string? Failure, string? Limit = null);

    /// <summary>
    /// The limit a drill states on a write-only set (ADR-0054 Amendment 2):
    /// the service holds no content key, so the road back is proved as far
    /// as the sealed content and no further.
    /// </summary>
    public const string SealedContentLimit =
        "content sealed: this set is write-only, so the service could prove the road back only as far as the "
        + "sealed content — the replica opens, its index and catalogue rebuild, and every sampled file's manifest "
        + "and segment records were found — and could not read the content itself without the passphrase. A "
        + "content drill is the recovery tool with the passphrase (ADR-0054).";

    /// <summary>
    /// Drills one (set, destination) pair and records the result. Never
    /// throws: a drill is a check, and a check that takes the scheduler down
    /// is worse than the condition it was looking for.
    /// </summary>
    /// <param name="runtime">The service.</param>
    /// <param name="set">The set whose replica to read.</param>
    /// <param name="destinationName">The destination holding it.</param>
    /// <param name="nowMs">The clock.</param>
    /// <param name="cancellationToken">Cancels the drill.</param>
    public static async Task<DrillOutcome> RunAsync(
        ServiceRuntime runtime,
        BackupSetConfiguration set,
        string destinationName,
        ulong nowMs,
        CancellationToken cancellationToken)
    {
        var scratch = Path.Combine(
            runtime.RestoreCacheRoot, $"drill-{Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..16]}");
        var handler = new ServiceCommandHandler(runtime, RemoteBindingState.Off);
        string? sourceId = null;

        try
        {
            var outcome = await DrillAsync(
                handler, set, destinationName, scratch, id => sourceId = id, cancellationToken)
                .ConfigureAwait(false);

            runtime.DestinationSync.RecordDrill(
                set.Id, destinationName, outcome.Files, outcome.Bytes, outcome.Failure, outcome.Limit, nowMs);
            Announce(runtime, set, destinationName, outcome, nowMs);
            return outcome;
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            // A drill cut short by shutdown states nothing: it did not pass
            // and it did not find damage, so the row keeps whatever the last
            // completed drill said and the next pass tries again. The filter
            // is deliberately absent: the cancellation that matters here is
            // usually NOT this token's — it is the service's own queue going
            // away underneath a drill in flight, which reaches this method as
            // a cancelled command answer and is translated below. Blaming the
            // destination for a shutdown would put the loudest notice this
            // product can raise on the most ordinary event it has. A disposed
            // object is the same event arriving a moment later, once the
            // runtime has started taking itself apart.
            return new DrillOutcome(0, 0, null);
        }
        catch (Exception exception)
        {
            var outcome = new DrillOutcome(0, 0, $"the drill did not complete: {exception.Message}");
            runtime.DestinationSync.RecordDrill(set.Id, destinationName, 0, 0, outcome.Failure, limit: null, nowMs);
            Announce(runtime, set, destinationName, outcome, nowMs);
            return outcome;
        }
        finally
        {
            if (sourceId is not null)
            {
                await handler.ExecuteAsync(new CloseRestoreSourceCommand(sourceId), CancellationToken.None)
                    .ConfigureAwait(false);
            }

            TryDelete(scratch);
        }
    }

    /// <summary>
    /// Turns a command refused because the service is stopping back into the
    /// cancellation it was, so the drill records nothing.
    /// </summary>
    /// <remarks>
    /// <see cref="ServiceCommandHandler"/> answers every cancellation as a
    /// <see cref="ServiceErrorReason.Cancelled"/> error rather than throwing,
    /// which is right for a client and wrong for this caller: a drill that
    /// treated it as an answer about the replica would write "your backups may
    /// not be restorable" every time the service stopped while one was in
    /// flight.
    /// </remarks>
    /// <param name="error">The refusal to inspect.</param>
    private static void ThrowIfCancelled(ServiceError error)
    {
        if (error.Reason == ServiceErrorReason.Cancelled)
        {
            throw new OperationCanceledException(error.Message);
        }
    }

    private static async Task<DrillOutcome> DrillAsync(
        ServiceCommandHandler handler,
        BackupSetConfiguration set,
        string destinationName,
        string scratch,
        Action<string> keepSourceId,
        CancellationToken cancellationToken)
    {
        var opened = await handler.ExecuteAsync(
            new OpenRestoreSourceCommand(set.Name, destinationName), cancellationToken).ConfigureAwait(false);
        if (opened is ServiceError openFailure)
        {
            ThrowIfCancelled(openFailure);
            return new DrillOutcome(0, 0, $"the replica would not open: {openFailure.Message}");
        }

        if (opened is not RestoreSourceOpenedResult source)
        {
            return new DrillOutcome(0, 0, $"opening the replica answered {opened.GetType().Name}.");
        }

        keepSourceId(source.SourceId);

        // The newest snapshot of this set. Newest rather than a random one
        // because it is the one a person would reach for, and because an
        // older snapshot's segments may legitimately have been trimmed from a
        // destination under its own retention (FR-GC-010) — a drill that
        // sampled those would report damage about a policy working.
        var newest = source.Snapshots
            .Where(snapshot => string.Equals(snapshot.BackupSetId, set.Id, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(snapshot => snapshot.CapturedAt)
            .FirstOrDefault();
        if (newest is null)
        {
            return new DrillOutcome(0, 0, "the replica holds no snapshot of this set.");
        }

        var paths = await SampleAsync(handler, source.SourceId, newest.SnapshotId, cancellationToken)
            .ConfigureAwait(false);
        if (paths.Count == 0)
        {
            // A snapshot of an empty tree is not a failure to restore from —
            // there is nothing in it to bring back, and calling that a broken
            // recovery would raise an alarm about a set that captured nothing.
            return newest.Files == 0
                ? new DrillOutcome(0, 0, null)
                : new DrillOutcome(0, 0, $"snapshot {newest.SnapshotId} lists {newest.Files} file(s), and none could be sampled.");
        }

        var files = 0;
        var bytes = 0L;
        var sealedFiles = 0;
        foreach (var path in paths)
        {
            var restored = await handler.ExecuteAsync(
                new RunRestoreCommand(
                    newest.SnapshotId, path, Path.Combine(scratch, $"{files}"), source.SourceId, InPlace: true),
                cancellationToken).ConfigureAwait(false);

            switch (restored)
            {
                case ServiceError error:
                    ThrowIfCancelled(error);
                    return new DrillOutcome(0, 0, $"'{path}' would not restore: {error.Message}");

                // The whole-file hash is checked inside the restore, after
                // reassembly and before a byte is emitted (specification 06
                // §4.2), so a complete restore is the proof — there is no
                // second comparison to make here and inventing one would only
                // be able to disagree with the engine.
                case RestoreResult { Outcome: "complete", Failed: 0 } ok:
                    files++;
                    bytes += ok.Restored > 0 ? BytesUnder(Path.Combine(scratch, $"{files - 1}")) : 0;
                    break;

                // A write-only set's content is sealed to a key the service
                // does not hold (ADR-0042 §7), so the engine reached every
                // segment record and stopped there. That is the road back
                // proved as far as it can be from inside the service, and a
                // stated limit rather than damage — provided the plan finds
                // every segment the manifest names, which a missing or
                // unreadable record would fail.
                case RestoreResult sealedOnly when SealedOnly(sealedOnly):
                    var planned = await handler.ExecuteAsync(
                        new PlanRestoreCommand(newest.SnapshotId, path, Source: source.SourceId), cancellationToken)
                        .ConfigureAwait(false);
                    switch (planned)
                    {
                        case ServiceError error:
                            ThrowIfCancelled(error);
                            return new DrillOutcome(0, 0, $"'{path}' would not plan: {error.Message}");

                        case RestorePlanResult { MissingObjects.Count: 0 }:
                            sealedFiles++;
                            break;

                        case RestorePlanResult missing:
                            return new DrillOutcome(
                                0, 0,
                                $"'{path}' is missing {missing.MissingObjects.Count} object(s) at the replica: "
                                + $"{missing.MissingObjects[0]}");

                        default:
                            return new DrillOutcome(0, 0, $"planning '{path}' answered {planned.GetType().Name}.");
                    }

                    break;

                case RestoreResult other:
                    return new DrillOutcome(
                        0, 0,
                        $"'{path}' restored {other.Outcome} — {other.Failed} failed"
                        + (other.FailedSample is { Count: > 0 } sample ? $": {sample[0]}" : "."));

                default:
                    return new DrillOutcome(0, 0, $"restoring '{path}' answered {restored.GetType().Name}.");
            }
        }

        return sealedFiles > 0
            ? new DrillOutcome(files + sealedFiles, bytes, null, SealedContentLimit)
            : new DrillOutcome(files, bytes, null);
    }

    /// <summary>
    /// Whether a restore failed for no reason other than sealed content:
    /// nothing written, and every failure the engine reported — all of them,
    /// not a sample of a longer list — names <c>ContentSealed</c>.
    /// </summary>
    private static bool SealedOnly(RestoreResult result) =>
        result.Restored == 0
        && result.Failed > 0
        && result.FailedSample is { Count: > 0 } sample
        && sample.Count == result.Failed
        && sample.All(line => line.Contains("ContentSealed", StringComparison.Ordinal));

    /// <summary>
    /// Picks files to restore by descending the snapshot at random.
    /// </summary>
    /// <remarks>
    /// A random descent rather than a full enumeration, because a drill must
    /// cost the same on a snapshot of eleven files and one of eleven million.
    /// Random rather than first, because a fixed choice is one a damaged
    /// replica could survive for ever — the same reason the possession
    /// challenge draws its record at random.
    /// </remarks>
    private static async Task<List<string>> SampleAsync(
        ServiceCommandHandler handler, string sourceId, string snapshotId, CancellationToken cancellationToken)
    {
        var chosen = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var attempt = 0; attempt < SampleFiles * 3 && chosen.Count < SampleFiles; attempt++)
        {
            var path = string.Empty;
            for (var depth = 0; depth < MaximumDepth; depth++)
            {
                var listed = await handler.ExecuteAsync(
                    new ListDirectoryCommand(snapshotId, path.Length == 0 ? null : path, sourceId),
                    cancellationToken).ConfigureAwait(false);
                if (listed is not DirectoryResult directory || directory.Entries.Count == 0)
                {
                    break;
                }

                var entry = directory.Entries[Random.Shared.Next(directory.Entries.Count)];
                var next = path.Length == 0 ? entry.Name : $"{path}/{entry.Name}";
                if (entry.Kind == "directory")
                {
                    path = next;
                    continue;
                }

                // Only a file proves anything: a symlink or a device node
                // restores its record, not content, so a drill that sampled
                // one would pass without reading a segment.
                if (entry.Kind == "file" && seen.Add(next))
                {
                    chosen.Add(next);
                }

                break;
            }
        }

        return chosen;
    }

    private static long BytesUnder(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length)
                : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Raises or clears the pair's drill notice. A recovery that would not
    /// work is the loudest thing this product has to say, and it has to be
    /// said before somebody needs it rather than during.
    /// </summary>
    private static void Announce(
        ServiceRuntime runtime, BackupSetConfiguration set, string destinationName,
        DrillOutcome outcome, ulong nowMs)
    {
        var key = $"drill-failed:{set.Id}:{destinationName}";
        if (outcome.Failure is null)
        {
            runtime.Notices.Resolve(key, nowMs);
            return;
        }

        runtime.Notices.Raise(
            key,
            $"A restore drill against '{destinationName}' could not bring back a file from set '{set.Name}': "
            + $"{outcome.Failure} The destination may still hold every byte it was sent — a drill tests the "
            + "path back, not the copy out — so check this before you need it.",
            nowMs);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The restore cache is purged at start-up; a drill's scratch
            // outliving it by one run is not worth failing the drill over.
        }
    }
}
