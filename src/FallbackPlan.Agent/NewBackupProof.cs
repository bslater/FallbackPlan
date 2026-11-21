using System.Globalization;
using Bodu;
using FallbackPlan.Api;
using FallbackPlan.Application;
using FallbackPlan.Domain;
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
    /// <param name="BlobsProven">
    /// Of the blobs <paramref name="Proving"/> needs, how many are proven;
    /// null with no proof under way, and for one under way before the ledger
    /// counted, until it ends.
    /// </param>
    /// <param name="BlobsLeft">Of them, how many are left to prove; null with <paramref name="BlobsProven"/>.</param>
    internal sealed record Step(string? Proven, string? Proving, string? Cursor, int? BlobsProven = null, int? BlobsLeft = null);

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
    /// <param name="blobsProven">
    /// Of the blobs <paramref name="proving"/> needs, how many were proven
    /// before the pass; null where that proof was never counted.
    /// </param>
    /// <returns>The proof's new standing.</returns>
    public static Step Advance(
        string newest, string? proven, string? proving, string? cursor,
        IReadOnlyList<string> asked, IReadOnlySet<string> answered, int remaining, int? blobsProven = 0)
    {
        // A cursor is a place among one backup's blobs: a newer backup's may
        // sort before it, so its proof starts at its own first blob, and
        // counts from none. A proof begun before the ledger counted stays
        // uncounted: counted from none, its whole would read short.
        var same = string.Equals(proving, newest, StringComparison.Ordinal);
        var resumed = same ? cursor : null;
        var counted = same ? blobsProven : 0;
        for (var index = 0; index < asked.Count; index++)
        {
            if (!answered.Contains(asked[index]))
            {
                return new Step(proven, newest, resumed, counted, counted is null ? null : asked.Count - index + remaining);
            }

            resumed = asked[index];
            counted++;
        }

        return remaining == 0
            ? new Step(newest, null, null)
            : new Step(proven, newest, resumed, counted, counted is null ? null : remaining);
    }

    /// <summary>
    /// A backup as a failure names it to a person (ADR-0199 Amendment 4): when
    /// it was taken, in UTC, and the start of its identity, which tells apart
    /// two backups of the same minute. A backup the catalogue no longer holds,
    /// or cannot be read for, is named by the identity its record key carries.
    /// </summary>
    /// <param name="archive">The set's archive.</param>
    /// <param name="set">The set.</param>
    /// <param name="key">The backup's record key, as the ledger names it.</param>
    /// <returns>The backup's name.</returns>
    public static string NameOf(ArchiveHandle archive, BackupSetConfiguration set, string key)
    {
        try
        {
            using var catalogue = archive.OpenReadCatalogue();
            var setId = Convert.FromHexString(set.Id);
            foreach (var row in catalogue.EnumerateSnapshots())
            {
                if (row.BackupSetId.Span.SequenceEqual(setId)
                    && string.Equals(
                        Repository.Packing.MetadataStoreKeys.Snapshot(
                            row.DeviceId.Span, row.BackupSetId.Span, row.SnapshotId.Span).Value,
                        key, StringComparison.Ordinal))
                {
                    var id = Convert.ToHexStringLower(row.SnapshotId.Span);
                    return string.Create(
                        CultureInfo.InvariantCulture,
                        $"the backup of {DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Min(row.CapturedAt, long.MaxValue)):yyyy-MM-dd HH:mm} UTC ({id[..8]})");
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A failure is being reported: a catalogue that cannot be read
            // names the backup by its key rather than hiding the failure.
        }

        return $"backup {IdentityOf(key)}";
    }

    /// <summary>
    /// The identity a backup's record key carries, hex as the snapshot listing
    /// prints it: its last segment decoded, or the segment itself where it
    /// does not decode.
    /// </summary>
    /// <param name="key">The backup's record key.</param>
    /// <returns>The identity.</returns>
    public static string IdentityOf(string key)
    {
        ThrowHelper.ThrowIfNull(key);
        var segment = key[(key.LastIndexOf('/') + 1)..];
        Span<byte> id = stackalloc byte[16];
        return Base32.TryDecode(segment, id, out var written) && written == id.Length
            ? Convert.ToHexStringLower(id)
            : segment;
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
