using System.Collections.Concurrent;
using Bodu;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Filesystem.Local;
using FallbackPlan.Repository;

namespace FallbackPlan.Agent;

/// <summary>
/// How many changed files each set's next backup will take, as last counted
/// (ADR-0192, FR-SNP-013). A set's status reads the last count and asks for
/// another once it is a minute old; the count runs on the reader lane, one at
/// a time for a set, so a console asking every few seconds walks a set's
/// changed folders at most once a minute.
/// </summary>
/// <param name="runtime">The service whose sets are counted.</param>
internal sealed class PendingChangesBoard(ServiceRuntime runtime)
{
    private readonly ConcurrentDictionary<string, PendingChangesDescriptor> _counted = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _counting = new(StringComparer.Ordinal);

    // Each set's last snapshot's failures, by that snapshot: a snapshot never
    // changes, so they are read once for it, not once a count, from a store
    // that may be a destination billed by the request.
    private readonly ConcurrentDictionary<string, (string Snapshot, IReadOnlyList<string> Failed)> _failed =
        new(StringComparer.Ordinal);

    /// <summary>How long a count stands before a status asks for another.</summary>
    internal static TimeSpan FreshFor { get; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The last count for <paramref name="set"/>, asking for another where
    /// there is none, it is <see cref="FreshFor"/> old at
    /// <paramref name="now"/>, or it was counted after <paramref name="now"/>.
    /// Null for a set whose backups ask no change journal, and until the
    /// first count is done.
    /// </summary>
    public PendingChangesDescriptor? Describe(BackupSetConfiguration set, DateTimeOffset now)
    {
        ThrowHelper.ThrowIfNull(set);
        if (runtime.Options.ChangeJournal is null)
        {
            return null;
        }

        _counted.TryGetValue(set.Id, out var last);
        var at = (ulong)Math.Max(0, now.ToUnixTimeMilliseconds());
        if (last is null || at < last.CountedAt || at - last.CountedAt >= (ulong)FreshFor.TotalMilliseconds)
        {
            Begin(set, now);
        }

        return last;
    }

    /// <summary>The count under way for a set, or null where none is.</summary>
    public Task? Counting(string setId) => _counting.TryGetValue(setId, out var counting) ? counting : null;

    /// <summary>Counts <paramref name="set"/> now, and keeps the count for its status.</summary>
    public async ValueTask<PendingChangesDescriptor?> RefreshAsync(
        BackupSetConfiguration set, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ThrowHelper.ThrowIfNull(set);
        var counted = await CountAsync(set, now, cancellationToken).ConfigureAwait(false);
        if (counted is null)
        {
            _counted.TryRemove(set.Id, out _);
        }
        else
        {
            _counted[set.Id] = counted;
        }

        return counted;
    }

    private void Begin(BackupSetConfiguration set, DateTimeOffset now)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_counting.TryAdd(set.Id, done.Task))
        {
            return;
        }

        void Settle()
        {
            _counting.TryRemove(set.Id, out _);
            done.TrySetResult();
        }

        var queued = runtime.Queue.Enqueue(new QueuedJob(
            $"count-changes-{set.Id}",
            JobLane.Reader,
            UserInitiated: false,
            $"count changes {set.Name}",
            async cancellationToken =>
            {
                try
                {
                    await RefreshAsync(set, now, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The service is stopping; the next status asks again.
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or ArgumentException or FormatException)
                {
                    // The last count stands until one reads; the next backup
                    // reports what it could not.
                }
                finally
                {
                    Settle();
                }
            },
            OnCancelledBeforeStart: Settle));
        if (!queued)
        {
            Settle();
        }
    }

    // What the next backup will take: the journal's answer, as that backup
    // will be given it (ChangeHints.WhatChanged), walked as it will walk.
    private async ValueTask<PendingChangesDescriptor?> CountAsync(
        BackupSetConfiguration set, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (runtime.Options.ChangeJournal is not { } journal)
        {
            return null;
        }

        var countedAt = (ulong)Math.Max(0, now.ToUnixTimeMilliseconds());
        if (await runtime.ExistingArchiveAsync(set.Id, cancellationToken).ConfigureAwait(false) is not { } archive)
        {
            return new PendingChangesDescriptor(null, ChangesSinceLast.WalksInFull("the set has no backup yet").WalksInFullBecause, countedAt);
        }

        using var catalogue = archive.OpenReadCatalogue();
        var setId = Convert.FromHexString(set.Id);
        var latest = catalogue.EnumerateSnapshots().FirstOrDefault(row => row.BackupSetId.Span.SequenceEqual(setId));
        var changes = ChangeHints.WhatChanged(
            journal, new JournalPlaceStore(runtime.Options.StateDirectory).Read(set.Id), set, latest?.SnapshotId, now);
        if (changes.Folders is not { } folders)
        {
            return new PendingChangesDescriptor(null, changes.WalksInFullBecause, countedAt);
        }

        // The next backup also walks every folder its last one failed a file
        // in, to try it again, and walks in full where it cannot read which
        // (ADR-0186). The count enters them alike (ADR-0192 Amendment 1).
        var snapshot = Convert.ToHexString(latest!.SnapshotId.Span);
        if (!_failed.TryGetValue(set.Id, out var known) || known.Snapshot != snapshot)
        {
            if (await SnapshotFailures.PathsAsync(
                    archive.Store, archive.Repository.RepositoryId, archive.Repository.Keys, catalogue, latest.ObjectId,
                    cancellationToken).ConfigureAwait(false) is not { } read)
            {
                return new PendingChangesDescriptor(null, "its last backup's failures would not read", countedAt);
            }

            known = (snapshot, read);
            _failed[set.Id] = known;
        }

        var failed = known.Failed;

        var count = await SourceComparer.CountChangedAsync(
            new LocalFileSystemSource(), SetChangeScan.ScanRootsOf(set), set.IncludeRules, set.ExcludeRules,
            catalogue, latest.SnapshotId, [.. folders, .. failed], cancellationToken).ConfigureAwait(false);
        return new PendingChangesDescriptor(count.Total, null, countedAt);
    }
}
