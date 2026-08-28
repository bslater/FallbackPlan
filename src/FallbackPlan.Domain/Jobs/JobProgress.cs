namespace FallbackPlan.Domain.Jobs;

/// <summary>
/// One progress observation for a running job (ADR-0029 §5).
/// </summary>
/// <remarks>
/// <para>
/// Two things this deliberately is not. It is <b>not telemetry</b>: it carries
/// job identity because it travels to an authenticated local caller or a paired
/// remote client and is shown to the person whose data it is, while the
/// OpenTelemetry instruments keep their closed four-attribute allowlist
/// (ADR-0027 §3, NFR-PRIV-002). And it is <b>not a path feed</b>: counts only,
/// no filename, no path, no object identifier — so the privacy question that
/// forced the telemetry allowlist never has to be re-litigated here.
/// </para>
/// <para>
/// <see cref="State"/> is the latest stage the job has entered, not a claim
/// that nothing else is happening. Once uploads leave the archive loop
/// (ADR-0029 §2) a job is genuinely in several stages at once; the counts carry
/// that detail and the state carries the headline.
/// </para>
/// </remarks>
/// <param name="JobId">The job this observation belongs to.</param>
/// <param name="State">The latest stage entered.</param>
/// <param name="FilesSeen">
/// Files the run has discovered so far: the counting pass's running tally
/// while it walks, the processed count once archiving is under way.
/// </param>
/// <param name="FilesDone">Files fully processed — captured and reused alike.</param>
/// <param name="FilesReused">Files whose content was reused unchanged (a subset of <paramref name="FilesDone"/>).</param>
/// <param name="FilesFailed">Files that could not be captured.</param>
/// <param name="BytesSeen">Logical bytes presented to segmentation.</param>
/// <param name="BytesStored">Bytes actually written after reuse and compression.</param>
/// <param name="TotalFiles">
/// The run's plan: how many files it will process, fixed by the counting
/// pass before archiving begins. Null until the count completes, and for
/// producers that never count — the single-stream path, verification
/// sweeps, services predating contract 1.20. A client derives % complete
/// and a time estimate only when this is present.
/// </param>
/// <param name="TotalBytes">The planned files' logical bytes, on the same terms.</param>
public sealed record JobProgress(
    string JobId,
    JobState State,
    long FilesSeen,
    long FilesDone,
    long FilesReused,
    long FilesFailed,
    long BytesSeen,
    long BytesStored,
    long? TotalFiles = null,
    long? TotalBytes = null);

/// <summary>
/// Where a running job reports progress. Distinct from
/// <c>IPublicationObserver</c>, which is the interruption harness's kill-point
/// seam and carries no payload at all — a ten-hour tree backup fires nine of
/// those, which is why it could never be bent into a progress feed.
/// </summary>
public interface IJobProgressReporter
{
    /// <summary>Reports one observation. Implementations must not block the caller.</summary>
    /// <param name="progress">The observation.</param>
    void Report(JobProgress progress);
}
