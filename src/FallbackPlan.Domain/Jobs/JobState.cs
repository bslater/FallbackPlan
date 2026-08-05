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

    /// <summary>Suspended by request; resumable.</summary>
    Paused = 9,

    /// <summary>Retrying after a recoverable failure.</summary>
    Retrying = 10,

    /// <summary>Stopped by a client command (ADR-0029 §4).</summary>
    Cancelled = 11,

    /// <summary>Resolves itself or resumes — the service retries on its next pass (10 §3).</summary>
    FailedRecoverable = 12,

    /// <summary>Needs intervention; never silently retried.</summary>
    FailedPermanent = 13,
}
