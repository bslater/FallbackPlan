using System.Globalization;
using FallbackPlan.Application;

namespace FallbackPlan.Agent;

/// <summary>
/// Says that a destination's store holds some of a set's objects only in an
/// archival tier, which serves nothing until they are rehydrated, as a reader
/// of the replica meets them (FR-VER-001, FR-VER-002; ADR-0104).
/// </summary>
/// <remarks>
/// <para>
/// A sync's read-back and the deep sweep both say it, one notice for the
/// pair. Its text names when the run of such meetings began, so it is the
/// same on every pass of one run and an acknowledgement holds through it, and
/// a run that starts after an earlier one ended is news again.
/// </para>
/// <para>
/// Only a sweep circuit that closes having met nothing archived ends a run,
/// because only a circuit reads every blob. A read-back that met none proves
/// nothing of the blobs it did not sample.
/// </para>
/// </remarks>
internal static class ArchivedReplicaNotice
{
    /// <summary>The notice's key for the pair.</summary>
    /// <param name="setId">The backup set.</param>
    /// <param name="destinationName">The destination.</param>
    public static string KeyOf(string setId, string destinationName) => $"replica-archived:{setId}:{destinationName}";

    /// <summary>Raises the notice while the ledger's row says a run stands, and resolves it once none does.</summary>
    /// <param name="runtime">The service.</param>
    /// <param name="set">The backup set.</param>
    /// <param name="destinationName">The destination.</param>
    /// <param name="row">The pair's ledger row, as last recorded.</param>
    /// <param name="nowMs">The clock.</param>
    public static void Reflect(
        ServiceRuntime runtime, BackupSetConfiguration set, string destinationName, DestinationSyncRecord row,
        ulong nowMs)
    {
        if (row.ArchivedSince is not { } since)
        {
            runtime.Notices.Resolve(KeyOf(set.Id, destinationName), nowMs);
            return;
        }

        var began = DateTimeOffset.FromUnixTimeMilliseconds((long)since).UtcDateTime;
        runtime.Notices.RaiseUnlessAcknowledged(
            KeyOf(set.Id, destinationName),
            string.Create(
                CultureInfo.InvariantCulture,
                $"Since {began:yyyy-MM-dd} the store at destination '{destinationName}' has held some of set "
                + $"'{set.Name}''s objects only in an archival tier, which serves nothing of them until they are "
                + $"rehydrated there. They are not read back or re-read, so nothing proves them, and nothing is "
                + $"known to be altered. A restore from there names the files that need them: rehydrate those "
                + $"first, or restore from another copy."),
            nowMs);
    }
}
