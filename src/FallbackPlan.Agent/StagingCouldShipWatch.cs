using FallbackPlan.Application;

namespace FallbackPlan.Agent;

/// <summary>
/// Names, in a notice, each staging set whose destinations are all object
/// stores: a run writes through the stores of a set that has nothing else,
/// so such a set no longer needs the staging archive it keeps here
/// (ADR-0196 Amendment 4).
/// </summary>
/// <remarks>
/// A person decides a set's shape, so this says what could change and where,
/// and changes nothing. The notice reads the same on every pass, so an
/// acknowledgement holds until the set's shape or destinations change.
/// </remarks>
internal static class StagingCouldShipWatch
{
    private const string NoticeKind = "staging-could-ship";

    /// <summary>
    /// Raises the notice for every staging set that names only stores, and
    /// resolves every other.
    /// </summary>
    /// <param name="runtime">The service.</param>
    /// <param name="configuration">The configuration this pass loaded.</param>
    /// <param name="now">The clock.</param>
    public static void Observe(ServiceRuntime runtime, ClientConfiguration configuration, DateTimeOffset now)
    {
        var nowMs = (ulong)now.ToUnixTimeMilliseconds();
        var standing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var set in configuration.BackupSets)
        {
            if (set.DirectShip
                || set.Destinations.Count == 0
                || !set.Destinations.All(reference =>
                    configuration.FindDestination(reference.Ref)?.Kind.IsObjectStore() == true))
            {
                continue;
            }

            var key = $"{NoticeKind}:{set.Id}";
            standing.Add(key);
            runtime.Notices.RaiseUnlessAcknowledged(
                key,
                $"Set '{set.Name}' names only S3 or Azure stores, which a backup writes through as it runs, but it "
                + "still keeps a staging archive of its data on this machine. To keep none here, tick \"ship "
                + "straight to destinations\" in the set's Other settings; its staging archive then stays as a "
                + "read-only seed until you retire it from the notice that follows (ADR-0196).",
                nowMs);
        }

        foreach (var notice in runtime.Notices.Unacknowledged)
        {
            if (notice.Kind == NoticeKind && !standing.Contains(notice.Key))
            {
                runtime.Notices.Resolve(notice.Key, nowMs);
            }
        }
    }
}
