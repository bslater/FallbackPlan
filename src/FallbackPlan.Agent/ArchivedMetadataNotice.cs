using System.Globalization;
using FallbackPlan.Application;

namespace FallbackPlan.Agent;

/// <summary>
/// Says that a destination's store holds an object of the replica's own, not
/// a data blob, only in an archival tier, so nothing restores from there
/// until it is rehydrated (FR-DEST-020, ADR-0112).
/// </summary>
/// <remarks>
/// <para>
/// Apart from <see cref="ArchivedReplicaNotice"/>, which a data blob held so
/// raises: that one costs the files that need it, which a restore's plan
/// names, while this one costs every restore, because every opening of the
/// replica reads its metadata before it can name a file.
/// </para>
/// <para>
/// Its text names the object and when it was first met, so it is the same on
/// every pass while that object stands and an acknowledgement holds through
/// it. Only that object found served again, by the next sync's look, resolves
/// it.
/// </para>
/// </remarks>
internal static class ArchivedMetadataNotice
{
    /// <summary>The prefix under a replica's root of the one kind of object a store may hold archived.</summary>
    internal const string DataBlobs = "blobs/data/";

    /// <summary>The notice's key for the pair.</summary>
    /// <param name="setId">The backup set.</param>
    /// <param name="destinationName">The destination.</param>
    public static string KeyOf(string setId, string destinationName) => $"replica-metadata-archived:{setId}:{destinationName}";

    /// <summary>Whether an object of a replica, keyed under its root, is one no opening of the replica can do without.</summary>
    /// <param name="key">The object's key under the replica's root.</param>
    public static bool IsMetadata(string key) => !key.StartsWith(DataBlobs, StringComparison.Ordinal);

    /// <summary>
    /// Records the first metadata object among those a reader found held only
    /// in an archival tier, if any was, and says so.
    /// </summary>
    /// <param name="runtime">The service.</param>
    /// <param name="set">The backup set.</param>
    /// <param name="destinationName">The destination.</param>
    /// <param name="archivedKeys">The objects the reader found so, keyed under the replica's root.</param>
    /// <param name="nowMs">The clock.</param>
    public static void Met(
        ServiceRuntime runtime, BackupSetConfiguration set, string destinationName, IEnumerable<string> archivedKeys,
        ulong nowMs)
    {
        if (archivedKeys.FirstOrDefault(IsMetadata) is { } key)
        {
            Reflect(runtime, set, destinationName, runtime.DestinationSync.RecordMetadataArchived(set.Id, destinationName, key, nowMs), nowMs);
        }
    }

    /// <summary>Raises the notice while the ledger's row names such an object, and resolves it once none is named.</summary>
    /// <param name="runtime">The service.</param>
    /// <param name="set">The backup set.</param>
    /// <param name="destinationName">The destination.</param>
    /// <param name="row">The pair's ledger row, as last recorded.</param>
    /// <param name="nowMs">The clock.</param>
    public static void Reflect(
        ServiceRuntime runtime, BackupSetConfiguration set, string destinationName, DestinationSyncRecord row,
        ulong nowMs)
    {
        if (row is not { MetadataArchivedKey: { } key, MetadataArchivedSince: { } since })
        {
            runtime.Notices.Resolve(KeyOf(set.Id, destinationName), nowMs);
            return;
        }

        var began = DateTimeOffset.FromUnixTimeMilliseconds((long)since).UtcDateTime;
        runtime.Notices.RaiseUnlessAcknowledged(
            KeyOf(set.Id, destinationName),
            string.Create(
                CultureInfo.InvariantCulture,
                $"Since {began:yyyy-MM-dd} the store at destination '{destinationName}' has held '{key}' only in an "
                + $"archival tier. Set '{set.Name}''s replica there cannot be opened without it, so nothing can be "
                + $"restored from there, not one file and not the set on a clean machine, until it is rehydrated, "
                + $"however in sync the copy reads. Rehydrate it at the store, and leave everything under the replica "
                + $"but {DataBlobs} out of the store's lifecycle rule: only the data blobs may be held archived, and a "
                + $"restore's plan names the files they cost."),
            nowMs);
    }
}
