using System.Globalization;
using FallbackPlan.Application;
using FallbackPlan.Retention;

namespace FallbackPlan.Agent;

/// <summary>
/// The notice a set's implausible capture times raise (FR-GC-012, ADR-0078).
/// Retention keeps those snapshots and never expires them, and a person
/// should learn that the clock which took them was wrong. There is one
/// notice per set, withdrawn once none is left.
/// </summary>
/// <remarks>
/// A finding like this can stay true for good: the misdated snapshots are
/// still misdated once the clock is put right. So an acknowledged finding is
/// not raised again while it says the same thing, and one that grows says so
/// in new words, which is news again.
/// </remarks>
internal static class ImplausibleCaptureNotice
{
    /// <summary>How many snapshots the message names before it counts the rest.</summary>
    private const int Named = 5;

    /// <summary>The notice's key for a set.</summary>
    public static string KeyFor(string setId) => $"capture-time-implausible:{setId}";

    /// <summary>
    /// Raises, refreshes or withdraws the set's notice from what a selection
    /// found.
    /// </summary>
    /// <param name="notices">The notice ledger.</param>
    /// <param name="set">The set the snapshots belong to.</param>
    /// <param name="implausible">
    /// What the selection found, or null where none was computed, which leaves
    /// the notice as it stands rather than withdrawing it on no evidence.
    /// </param>
    /// <param name="nowUnixMilliseconds">When it was observed.</param>
    public static void Report(
        NoticeStore notices,
        BackupSetConfiguration set,
        IReadOnlyList<ImplausibleCapture>? implausible,
        ulong nowUnixMilliseconds)
    {
        if (implausible is null)
        {
            return;
        }

        var key = KeyFor(set.Id);
        if (implausible.Count == 0)
        {
            notices.Resolve(key, nowUnixMilliseconds);
            return;
        }

        notices.RaiseUnlessAcknowledged(key, Message(set.Name, implausible), nowUnixMilliseconds);
    }

    /// <summary>The notice's words. The same finding renders the same text, which is what lets an acknowledgement hold.</summary>
    internal static string Message(string setName, IReadOnlyList<ImplausibleCapture> implausible)
    {
        var named = string.Join("; ", implausible.Take(Named).Select(Describe));
        var more = implausible.Count > Named
            ? string.Create(CultureInfo.InvariantCulture, $"; and {implausible.Count - Named} more")
            : string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Set '{setName}' keeps {implausible.Count} snapshot(s) whose capture time does not fit the order they were published in: {named}{more}. Retention keeps them and never expires them, and they fill no min_generations place. The clock of the machine that took them was probably wrong at the time; check its date and time synchronisation (FR-GC-012).");
    }

    private static string Describe(ImplausibleCapture finding)
    {
        var direction = finding.Direction == ImplausibleCaptureTime.Behind
            ? "dated before snapshots published ahead of it"
            : "dated after snapshots published after it";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Short(finding.Snapshot.SnapshotId)} recorded {Recorded(finding.Snapshot.CapturedAtUnixMilliseconds)}, {direction}");
    }

    private static string Short(string snapshotId) => snapshotId.Length > 12 ? snapshotId[..12] : snapshotId;

    private static string Recorded(ulong unixMilliseconds) =>
        unixMilliseconds > (ulong)DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
            ? "beyond the calendar"
            : DateTimeOffset.FromUnixTimeMilliseconds((long)unixMilliseconds)
                .ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
