namespace FallbackPlan.Domain.Jobs;

/// <summary>
/// The job state machine (architecture 10 §3). It lives in Domain rather than
/// Application because three layers need the same vocabulary: the engine emits
/// it (ADR-0029 §5), the application stores it, and the command contract
/// carries it to clients (ADR-0028 §7). A state that is never emitted is a
/// state that is not implemented — so every value here is a promise.
/// </summary>
public enum JobState
{
    /// <summary>Queued, not yet started.</summary>
    Pending = 0,

    /// <summary>Walking the source tree.</summary>
    Scanning = 1,

    /// <summary>Reading file content.</summary>
    Reading = 2,

    /// <summary>Splitting content into segments.</summary>
    Segmenting = 3,

    /// <summary>Sealing records into blobs.</summary>
    Packing = 4,

    /// <summary>Transferring sealed blobs to the destination.</summary>
    Uploading = 5,

    /// <summary>Running the publication order (architecture 04 §5).</summary>
    Publishing = 6,

    /// <summary>Checking what was written.</summary>
    Verifying = 7,

    /// <summary>Finished, with a committed snapshot.</summary>
    Complete = 8,

    /// <summary>
    /// Suspended at a file boundary by the scheduler, its in-memory state
    /// held, so a higher-priority run can use its pool slot (ADR-0047 Amendment 1).
    /// Not terminal: the run resumes unattended when a slot frees, degrades
    /// to <see cref="Cancelled"/> on shutdown, and self-cancels past the
    /// max-pause age.
    /// </summary>
    Paused = 9,

    /// <summary>Retrying after a recoverable failure.</summary>
    Retrying = 10,

    /// <summary>Stopped by a client command (ADR-0029 §4).</summary>
    Cancelled = 11,

    /// <summary>Resolves itself or resumes — the service retries on its next pass (10 §3).</summary>
    FailedRecoverable = 12,

    /// <summary>Needs intervention; never silently retried.</summary>
    FailedPermanent = 13,

    /// <summary>
    /// Finished, with a committed snapshot that does not hold everything that
    /// was asked for — at least one entry could not be captured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is a distinct terminal state rather than <see cref="Complete"/> with
    /// a note, because "backed up your 40 000 files" and "backed up 39 998 of
    /// your 40 000 files" are different outcomes and a caller must be able to
    /// tell them apart without reading English. Specification 02 §8 makes the
    /// same argument about wire refusals — the code carries the meaning and
    /// the message is explicitly not for parsing — and a job state a console
    /// or a script reads is the same kind of surface.
    /// </para>
    /// <para>
    /// The snapshot is real and committed, so this is an outcome and not a
    /// failure: the run is the set's most recent backup and anchors its
    /// schedule exactly as <see cref="Complete"/> does. What it is not is a
    /// success — the command surface reports it as one that did not do
    /// everything it was asked to.
    /// </para>
    /// <para>
    /// The surveyed changelog carries three separate fixes for the absence of
    /// this distinction — an operation reporting success although it failed,
    /// one error masking another, and compression errors being hidden — spread
    /// over five years, which is what makes it the worst class of backup
    /// defect: nothing is reported wrong until somebody restores.
    /// </para>
    /// </remarks>
    CompletedWithFailures = 14,
}
