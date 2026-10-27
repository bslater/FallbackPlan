using FallbackPlan.Application;

namespace FallbackPlan.Agent;

/// <summary>
/// Says that a restore run the service began did not finish, and how to
/// restore what is left of it by its name alone (FR-RST-008, ADR-0115).
/// </summary>
/// <remarks>
/// <para>
/// Raised whenever a run ends unfinished: stopped, failing files, or cut
/// short with the service, which the next start says. A run whose caller
/// hung up ended where nobody heard it, and one cut short with the machine
/// ended before anything could say so, so a notice is the one place either
/// is learned of.
/// </para>
/// <para>
/// Its text names the run, its snapshot and where it writes, never the
/// paths it was asked for, which are the backup's file names (ADR-0089).
/// It is the same text for the run however often it is said, so an
/// acknowledgement holds through a restart. A run of it that finishes, or a
/// person letting it go, resolves it.
/// </para>
/// </remarks>
internal static class UnfinishedRestoreNotice
{
    /// <summary>The notice's key for a run.</summary>
    /// <param name="run">The run's name.</param>
    public static string KeyOf(string run) => $"restore-unfinished:{run}";

    /// <summary>The notice's text for a run.</summary>
    /// <param name="record">The run, as the journal keeps it.</param>
    public static string MessageOf(RestoreRunRecord record)
    {
        var of = record.SetName is { } set ? $" of set '{set}'" : string.Empty;
        var to = string.IsNullOrEmpty(record.OutputDirectory)
            ? "where its files came from"
            : $"'{record.OutputDirectory}'";
        return $"A restore of snapshot {record.SnapshotId}{of} to {to} did not finish (run {record.Run}). "
            + $"Restore what is left with `restore-resume {record.Run}`, or let it go with "
            + $"`restore-resume {record.Run} --forget`; what it landed stays where it landed either way.";
    }

    /// <summary>Says that a run ended without finishing; said again after an acknowledgement, because a run that fails again is news.</summary>
    /// <param name="runtime">The service.</param>
    /// <param name="record">The run.</param>
    /// <param name="nowMs">The clock.</param>
    public static void Raise(ServiceRuntime runtime, RestoreRunRecord record, ulong nowMs) =>
        runtime.Notices.Raise(KeyOf(record.Run), MessageOf(record), nowMs);

    /// <summary>Withdraws what was said of a run that a run of finished, or that a person let go.</summary>
    /// <param name="runtime">The service.</param>
    /// <param name="run">The run's name.</param>
    /// <param name="nowMs">The clock.</param>
    public static void Resolve(ServiceRuntime runtime, string run, ulong nowMs) =>
        runtime.Notices.Resolve(KeyOf(run), nowMs);
}
