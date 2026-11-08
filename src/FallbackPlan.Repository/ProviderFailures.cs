namespace FallbackPlan.Repository;

/// <summary>
/// What a cloud provider failed first in a run, a file's fetch or a folder's
/// listing, and why. From then on the run asks no provider for either, since
/// each would wait out the same time-out (ADR-0185 Decision 2 and
/// Amendment 7).
/// </summary>
/// <remarks>
/// The walk that counts the run and the walk that captures it share one, so
/// a folder whose listing failed while the run counted is not asked for
/// again while it captures. The run reads it between events, never during
/// one, so a walk that asks after the event that failed sees the failure.
/// </remarks>
internal sealed class ProviderFailures
{
    /// <summary>The path that failed first, why, and whether it was a folder's listing; null while none has.</summary>
    public (string Path, string Reason, bool Listing)? First { get; private set; }

    /// <summary>Whether a provider has failed in this run.</summary>
    public bool Any => First is not null;

    /// <summary>Records a failure, unless one came first.</summary>
    public void Record(string path, string reason, bool listing) => First ??= (path, reason, listing);
}
