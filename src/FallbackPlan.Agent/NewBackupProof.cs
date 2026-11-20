using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain.Diagnostics;
using FallbackPlan.Replication;

namespace FallbackPlan.Agent;

/// <summary>
/// A backup proven at each destination it lands at
/// ([ADR-0199](../../docs/adr/0199-a-backup-is-proven-at-each-destination-it-lands-at.md)):
/// which of the newest backup's blobs a pass asks about, and where the proof
/// stands once it has.
/// </summary>
/// <remarks>
/// A blob is named by its keyed store name alone, the last segment of its
/// store key, as <see cref="SnapshotClosure"/> names it: the catalogue does
/// not always know a blob's class, and the sampler finds the whole key in the
/// listing it already reads.
/// </remarks>
internal static class NewBackupProof
{
    /// <summary>Where a destination's proof stands.</summary>
    /// <param name="Proven">The newest backup proven there whole.</param>
    /// <param name="Proving">The backup whose proof is under way, or null.</param>
    /// <param name="Cursor">The last of its blobs proven with none before it unproven, or null.</param>
    internal sealed record Step(string? Proven, string? Proving, string? Cursor);

    /// <summary>
    /// What the pass asks about: the blobs <paramref name="newest"/> needs
    /// that the backup last proven at the destination did not, after where a
    /// proof of it under way stopped. Null when there is nothing to ask:
    /// the destination is proven through it already, or the catalogue does not
    /// hold it yet and so cannot say what it needs.
    /// </summary>
    /// <param name="runtime">The service, for its log.</param>
    /// <param name="set">The set being synced.</param>
    /// <param name="destination">The destination's name.</param>
    /// <param name="archive">The set's archive.</param>
    /// <param name="newest">The newest backup's record key, as the pass carries it.</param>
    /// <param name="previous">The pair's ledger row before the pass.</param>
    /// <param name="budget">The most of the backup's blobs one pass asks about.</param>
    public static VerificationSampler.NewBackupRequest? Request(
        ServiceRuntime runtime, BackupSetConfiguration set, string destination, ArchiveHandle archive,
        string? newest, DestinationSyncRecord? previous, int budget)
    {
        if (newest is null || string.Equals(previous?.ProvenSnapshot, newest, StringComparison.Ordinal))
        {
            return null;
        }

        IReadOnlySet<string>? owed;
        try
        {
            owed = Owed(archive, set, newest, previous?.ProvenSnapshot);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.NewBackupProofUnavailable(
                runtime.LoggerFor(typeof(NewBackupProof)), new LogLabel(set.Name), new LogLabel(destination), exception.Message);
            return null;
        }

        if (owed is null)
        {
            return null;
        }

        var after = string.Equals(previous?.ProvingSnapshot, newest, StringComparison.Ordinal)
            ? previous?.ProvingCursor
            : null;
        return new VerificationSampler.NewBackupRequest(owed, after, budget);
    }

    /// <summary>
    /// Where the proof stands after a pass asked about <paramref name="asked"/>
    /// and had <paramref name="answered"/> of them proven or passed over as
    /// archived. It moves on past each blob in key order up to the first
    /// left unproven, so a blob the digest tier's budget left over is asked
    /// again; it is proven through <paramref name="newest"/> once every blob
    /// asked was answered and none remains.
    /// </summary>
    /// <param name="newest">The backup the pass asked about.</param>
    /// <param name="proven">The backup proven before the pass.</param>
    /// <param name="proving">The backup whose proof was under way before the pass, or null.</param>
    /// <param name="cursor">Where that proof stood, or null.</param>
    /// <param name="asked">The blobs the pass asked about, in key order.</param>
    /// <param name="answered">Those proven, or held only archived.</param>
    /// <param name="remaining">The backup's blobs left after those asked.</param>
    /// <returns>The proof's new standing.</returns>
    public static Step Advance(
        string newest, string? proven, string? proving, string? cursor,
        IReadOnlyList<string> asked, IReadOnlySet<string> answered, int remaining)
    {
        // A cursor is a place among one backup's blobs: a newer backup's may
        // sort before it, so its proof starts at its own first blob.
        var resumed = string.Equals(proving, newest, StringComparison.Ordinal) ? cursor : null;
        foreach (var key in asked)
        {
            if (!answered.Contains(key))
            {
                return new Step(proven, newest, resumed);
            }

            resumed = key;
        }

        return remaining == 0 ? new Step(newest, null, null) : new Step(proven, newest, resumed);
    }

    /// <summary>
    /// The keyed store names of the blobs <paramref name="newest"/> needs and
    /// <paramref name="proven"/> did not, or null when the catalogue does not
    /// hold <paramref name="newest"/>. A proven backup the catalogue no longer
    /// holds, its retention having let it go, subtracts nothing.
    /// </summary>
    private static HashSet<string>? Owed(ArchiveHandle archive, BackupSetConfiguration set, string newest, string? proven)
    {
        using var catalogue = archive.OpenReadCatalogue();
        var setId = Convert.FromHexString(set.Id);
        var snapshots = catalogue.EnumerateSnapshots()
            .Where(row => row.BackupSetId.Span.SequenceEqual(setId))
            .ToDictionary(
                row => Repository.Packing.MetadataStoreKeys.Snapshot(
                    row.DeviceId.Span, row.BackupSetId.Span, row.SnapshotId.Span).Value,
                row => row.SnapshotId,
                StringComparer.Ordinal);
        if (!snapshots.TryGetValue(newest, out var target))
        {
            return null;
        }

        using var derive = new Repository.Crypto.StoreBlobKeyDeriver(archive.Repository.Keys.KeyIdKey);
        IEnumerable<string> NamesOf(ReadOnlyMemory<byte> snapshotId) =>
            catalogue.ClosureOf(snapshotId.Span).Select(blob => (blob.StoreBlobKey ?? derive.Derive(blob.BlobId)).ToBase32());

        var owed = new HashSet<string>(NamesOf(target), StringComparer.Ordinal);
        if (proven is not null && snapshots.TryGetValue(proven, out var before))
        {
            owed.ExceptWith(NamesOf(before));
        }

        return owed;
    }
}
