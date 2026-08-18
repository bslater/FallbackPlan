using FallbackPlan.Application;
using FallbackPlan.Filesystem.Local;
using FallbackPlan.Repository;
using FallbackPlan.Repository.Catalogue;

namespace FallbackPlan.Agent;

/// <summary>
/// The rescan a reconfigured backup set gets (ADR-0038, FR-SVC-009): a dry
/// walk of its source diffed against its latest snapshot, on demand for the
/// preview verb and fire-and-forget after a material edit — where the
/// finding lands as one durable per-set notice, resolved by the next backup
/// that completes for the set.
/// </summary>
internal static class SetChangeScan
{
    /// <summary>The default per-bucket path sample for the preview verb.</summary>
    internal const int DefaultSampleLimit = 20;

    /// <summary>The most paths any preview bucket may carry — the result crosses the contract.</summary>
    internal const int MaxSampleLimit = 200;

    /// <summary>The notice key for one set's pending-change finding — one notice per set, refreshed per edit.</summary>
    /// <param name="setId">The set's 32-hex identity.</param>
    internal static string NoticeKey(string setId) => $"set-changed:{setId}";

    /// <summary>
    /// Compares a set's source, under the given root and rules, with its
    /// latest snapshot. The set's staging archive is read-opened for the
    /// baseline; a set that has never backed up compares against nothing and
    /// everything reads as new.
    /// </summary>
    /// <param name="runtime">The service.</param>
    /// <param name="set">The set whose archive holds the baseline.</param>
    /// <param name="root">The root to walk — the saved one, or a draft's.</param>
    /// <param name="includeRules">The include rules to judge under.</param>
    /// <param name="excludeRules">The exclude rules to judge under.</param>
    /// <param name="sampleLimit">The most paths any bucket carries.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>The comparison, and the baseline snapshot when one exists.</returns>
    internal static async ValueTask<(SourceComparison Comparison, CatalogueSnapshot? Baseline)> CompareAsync(
        ServiceRuntime runtime,
        BackupSetConfiguration set,
        string root,
        IReadOnlyList<string> includeRules,
        IReadOnlyList<string> excludeRules,
        int sampleLimit,
        CancellationToken cancellationToken)
    {
        var archive = await runtime.ExistingArchiveAsync(set.Id, cancellationToken).ConfigureAwait(false);
        if (archive is null)
        {
            var fresh = await SourceComparer.CompareAsync(
                new LocalFileSystemSource(), root, includeRules, excludeRules,
                catalogue: null, baselineSnapshotId: null, sampleLimit, cancellationToken).ConfigureAwait(false);
            return (fresh, null);
        }

        // A fresh read connection, not the runtime's live one: a backup may be
        // writing through the writer lane's connection while this reads.
        var backupSetId = Convert.FromHexString(set.Id);
        using var catalogue = archive.OpenReadCatalogue();
        var baseline = catalogue.EnumerateSnapshots()
            .FirstOrDefault(row => row.BackupSetId.Span.SequenceEqual(backupSetId));

        var comparison = await SourceComparer.CompareAsync(
            new LocalFileSystemSource(), root, includeRules, excludeRules,
            catalogue, baseline?.SnapshotId, sampleLimit, cancellationToken).ConfigureAwait(false);
        return (comparison, baseline);
    }

    /// <summary>
    /// Queues the after-edit rescan, fire-and-forget on the reader lane, and
    /// raises the set's notice with what it found — counts only; the paths
    /// come from the preview verb. A rescan that cannot read the source
    /// raises the same notice naming the error instead, because a vanished
    /// new root is exactly the kind of edit this exists to catch.
    /// </summary>
    /// <param name="runtime">The service.</param>
    /// <param name="set">The set as just saved.</param>
    internal static void Enqueue(ServiceRuntime runtime, BackupSetConfiguration set)
    {
        runtime.Queue.Enqueue(new QueuedJob(
            $"rescan-{Guid.NewGuid():n}",
            JobLane.Reader,
            UserInitiated: false,
            $"rescan {set.Name}",
            async cancellationToken =>
            {
                var nowMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                try
                {
                    var (comparison, _) = await CompareAsync(
                        runtime, set, set.Root, set.IncludeRules, set.ExcludeRules,
                        sampleLimit: 0, cancellationToken).ConfigureAwait(false);

                    runtime.Notices.Raise(NoticeKey(set.Id), Summarise(set.Name, comparison), nowMs);
                }
                catch (OperationCanceledException)
                {
                    // Service shutdown; the next backup tells the truth anyway.
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    runtime.Notices.Raise(
                        NoticeKey(set.Id),
                        $"Backup set '{set.Name}' was reconfigured, but the rescan could not read the source: " +
                        $"{exception.Message} The next backup will report the truth.",
                        nowMs);
                }
            }));
    }

    /// <summary>One line of counts — what the edit means, for the notice a status line carries.</summary>
    private static string Summarise(string setName, SourceComparison comparison) =>
        $"Backup set '{setName}' was reconfigured: compared with its last backup, " +
        $"{comparison.New.Count} new, {comparison.Updated.Count} updated " +
        $"({comparison.MetadataOnly.Count} metadata-only), {comparison.Moved.Count} moved, " +
        $"{comparison.Deleted.Count} deleted, {comparison.NoLongerIncluded.Count} no longer included" +
        (comparison.Failures > 0 ? $", {comparison.Failures} unreadable" : string.Empty) +
        ". The next backup captures under the new settings; the 'changes' verb lists the paths.";
}
