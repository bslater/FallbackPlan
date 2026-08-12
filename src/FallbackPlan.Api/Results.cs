using System.Text.Json.Serialization;
using FallbackPlan.Domain.Jobs;
using FallbackPlan.Domain.Status;

namespace FallbackPlan.Api;

/// <summary>Why a command did not succeed. A closed set, so a client can branch on it.</summary>
public enum ServiceErrorReason
{
    /// <summary>The command named something that does not exist.</summary>
    NotFound = 0,

    /// <summary>The command was malformed or its arguments were invalid.</summary>
    InvalidArgument = 1,

    /// <summary>The service declined on policy grounds — not a failure, a rule.</summary>
    Refused = 2,

    /// <summary>The service could not do this now, but might later.</summary>
    Unavailable = 3,

    /// <summary>The operation ran and failed.</summary>
    Failed = 4,

    /// <summary>The operation was cancelled.</summary>
    Cancelled = 5,

    /// <summary>The client and service speak incompatible contract versions.</summary>
    VersionMismatch = 6,
}

/// <summary>
/// A command's outcome. Expected outcomes are <b>results, not exceptions</b>
/// (NFR-PORT-004): an exception crossing a process boundary loses its type, its
/// stack means nothing on the other side, and the client is left to parse a
/// message. Every failure a caller might reasonably handle is
/// <see cref="ServiceError"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "result")]
[JsonDerivedType(typeof(ServiceError), "error")]
[JsonDerivedType(typeof(AcknowledgedResult), "acknowledged")]
[JsonDerivedType(typeof(BackupSetsResult), "backup_sets")]
[JsonDerivedType(typeof(JobAcceptedResult), "job_accepted")]
[JsonDerivedType(typeof(JobsResult), "jobs")]
[JsonDerivedType(typeof(SnapshotsResult), "snapshots")]
[JsonDerivedType(typeof(DirectoryResult), "directory")]
[JsonDerivedType(typeof(RestorePlanResult), "restore_plan")]
[JsonDerivedType(typeof(RestoreResult), "restore")]
[JsonDerivedType(typeof(VerificationResult), "verification")]
[JsonDerivedType(typeof(CheckResult), "check")]
[JsonDerivedType(typeof(StatusResult), "status")]
[JsonDerivedType(typeof(ConfigurationResult), "configuration")]
[JsonDerivedType(typeof(ServiceDescriptionResult), "service_description")]
public abstract record ServiceResult;

/// <summary>A command that did not succeed, with a reason a client can branch on.</summary>
/// <param name="Reason">The closed-set reason.</param>
/// <param name="Message">What to tell the user.</param>
public sealed record ServiceError(ServiceErrorReason Reason, string Message) : ServiceResult;

/// <summary>A command that succeeded and has nothing to report.</summary>
public sealed record AcknowledgedResult : ServiceResult;

/// <summary>One configured backup set, as a client sees it.</summary>
/// <param name="Id">The set's 32-hex identity.</param>
/// <param name="Name">The set's name.</param>
/// <param name="Root">The directory it captures.</param>
/// <param name="Schedule">Its schedule expression, or null for manual-only.</param>
/// <param name="IncludeRules">Include rules, in rules-v1 dialect.</param>
/// <param name="ExcludeRules">Exclude rules, in rules-v1 dialect.</param>
/// <param name="Destinations">The declared destination names the set replicates to (FR-DEST-001).</param>
public sealed record BackupSetDescriptor(
    string Id,
    string Name,
    string Root,
    string? Schedule,
    IReadOnlyList<string> IncludeRules,
    IReadOnlyList<string> ExcludeRules,
    IReadOnlyList<string> Destinations);

/// <summary>The configured backup sets.</summary>
/// <param name="Sets">The sets.</param>
public sealed record BackupSetsResult(IReadOnlyList<BackupSetDescriptor> Sets) : ServiceResult;

/// <summary>A job the service has queued.</summary>
/// <param name="JobId">The job's identity, for progress and cancellation.</param>
public sealed record JobAcceptedResult(string JobId) : ServiceResult;

/// <summary>One job, as a client sees it.</summary>
/// <param name="Id">The job's identity.</param>
/// <param name="BackupSetId">The set it belongs to.</param>
/// <param name="State">Its state in the architecture 10 §3 machine.</param>
/// <param name="StartedAt">When it began, Unix milliseconds.</param>
/// <param name="UpdatedAt">When it last transitioned, Unix milliseconds.</param>
/// <param name="SnapshotId">The snapshot it committed, when it did.</param>
/// <param name="Detail">What the service wants the user to know.</param>
public sealed record JobDescriptor(
    string Id,
    string BackupSetId,
    JobState State,
    ulong StartedAt,
    ulong UpdatedAt,
    string? SnapshotId,
    string? Detail);

/// <summary>The known jobs.</summary>
/// <param name="Jobs">The jobs, oldest first.</param>
public sealed record JobsResult(IReadOnlyList<JobDescriptor> Jobs) : ServiceResult;

/// <summary>One committed snapshot.</summary>
/// <param name="SnapshotId">The snapshot's hex identity.</param>
/// <param name="BackupSetId">The set it belongs to.</param>
/// <param name="CapturedAt">When it was captured, Unix milliseconds.</param>
/// <param name="CaptureStatus">1 complete, 2 partial.</param>
/// <param name="Files">How many files it holds.</param>
public sealed record SnapshotDescriptor(
    string SnapshotId,
    string BackupSetId,
    ulong CapturedAt,
    byte CaptureStatus,
    long Files);

/// <summary>The committed snapshots.</summary>
/// <param name="Snapshots">The snapshots, oldest first.</param>
public sealed record SnapshotsResult(IReadOnlyList<SnapshotDescriptor> Snapshots) : ServiceResult;

/// <summary>One entry inside a snapshot directory.</summary>
/// <param name="Name">The entry's name.</param>
/// <param name="Kind">One of <c>file</c>, <c>directory</c>, <c>symlink</c>, <c>special</c>.</param>
/// <param name="Length">The logical length, for files.</param>
public sealed record DirectoryEntryDescriptor(string Name, string Kind, long Length);

/// <summary>One directory's contents.</summary>
/// <param name="Path">The directory listed.</param>
/// <param name="Entries">Its entries.</param>
public sealed record DirectoryResult(string Path, IReadOnlyList<DirectoryEntryDescriptor> Entries) : ServiceResult;

/// <summary>What a restore would do.</summary>
/// <param name="Files">How many files the plan covers.</param>
/// <param name="Bytes">How many logical bytes it would write.</param>
/// <param name="MissingObjects">Objects the plan needs and cannot find.</param>
public sealed record RestorePlanResult(long Files, long Bytes, IReadOnlyList<string> MissingObjects) : ServiceResult;

/// <summary>What a restore did.</summary>
/// <param name="Restored">Files written.</param>
/// <param name="Failed">Files that could not be written.</param>
/// <param name="OutputDirectory">Where they were written, on the service's machine.</param>
/// <param name="Outcome">
/// The receipt outcome — <c>complete</c>, <c>partial</c>, <c>failed</c> or
/// <c>cancelled</c> (FR-RST-005). Carried explicitly because a caller cannot
/// reconstruct it from <paramref name="Failed"/>: a restore that skipped a
/// required item failed nothing yet is not complete, and a remote client told
/// only <c>Failed = 0</c> would report success for it.
/// </param>
public sealed record RestoreResult(long Restored, long Failed, string OutputDirectory, string Outcome) : ServiceResult;

/// <summary>What a verification run found.</summary>
/// <param name="ObjectsChecked">How many objects were examined.</param>
/// <param name="Failures">How many failed.</param>
/// <param name="Level">The level that was run.</param>
public sealed record VerificationResult(long ObjectsChecked, long Failures, string Level) : ServiceResult;

/// <summary>What a health check found.</summary>
/// <param name="Findings">The findings, in the order they matter.</param>
public sealed record CheckResult(IReadOnlyList<string> Findings) : ServiceResult;

/// <summary>One destination's row in a set's status matrix (FR-DEST-004).</summary>
/// <param name="Name">The destination's declared name.</param>
/// <param name="Kind">Its declared kind, in the configuration's spelling.</param>
/// <param name="State">Where the pair stands: <c>in-sync</c>, <c>behind</c>, <c>unavailable</c>, <c>failed</c>, or <c>not-supported</c>.</param>
/// <param name="LastSuccessAt">When it last synced, Unix milliseconds; null when never.</param>
/// <param name="Detail">What the last failure said, or null.</param>
public sealed record DestinationStatusDescriptor(
    string Name,
    string Kind,
    string State,
    ulong? LastSuccessAt,
    string? Detail);

/// <summary>One set's derived protection status, with the per-destination matrix beneath it.</summary>
/// <param name="SetName">The set's name.</param>
/// <param name="Status">The derived status — computed from the matrix, never beside it (ADR-0028 §8).</param>
/// <param name="NextRun">When the schedule next fires, ISO-8601, or null for manual-only.</param>
/// <param name="Destinations">The matrix rows, in declaration order.</param>
public sealed record BackupSetStatusDescriptor(
    string SetName,
    BackupSetStatus Status,
    string? NextRun,
    IReadOnlyList<DestinationStatusDescriptor> Destinations);

/// <summary>
/// One machine's status. Always the per-set detail: a summary is derived from
/// this and never stored beside it, so the never-merge rules (NFR-OPS-002) hold
/// wherever the summary is computed.
/// </summary>
/// <param name="MachineName">The machine this service speaks for.</param>
/// <param name="Sets">Per-set detail.</param>
/// <param name="ObservedAt">When the service produced this, Unix milliseconds.</param>
public sealed record StatusResult(
    string MachineName,
    IReadOnlyList<BackupSetStatusDescriptor> Sets,
    ulong ObservedAt) : ServiceResult;

/// <summary>The client configuration as JSON.</summary>
/// <param name="Json">The configuration document.</param>
public sealed record ConfigurationResult(string Json) : ServiceResult;

/// <summary>What a service is, for a client deciding whether it can talk to it.</summary>
/// <param name="ContractVersion">The contract version the service speaks.</param>
/// <param name="ServiceVersion">The service build's version.</param>
/// <param name="MachineName">The machine it speaks for.</param>
/// <param name="StateDirectory">The state directory whose writer role it holds.</param>
/// <param name="RemoteBindingEnabled">Whether the remote binding is on.</param>
/// <param name="ActiveJobs">How many jobs are running now.</param>
public sealed record ServiceDescriptionResult(
    string ContractVersion,
    string ServiceVersion,
    string MachineName,
    string StateDirectory,
    bool RemoteBindingEnabled,
    int ActiveJobs) : ServiceResult;
