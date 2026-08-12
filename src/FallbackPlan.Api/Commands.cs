using System.Text.Json.Serialization;

namespace FallbackPlan.Api;

/// <summary>
/// One operation a front end invokes (ADR-0028 §7). The surface is specified in
/// commands, results, and an event stream — never in terms of a transport
/// binding, so the same contract serves the local socket today and a remote
/// binding later without changing.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "command")]
[JsonDerivedType(typeof(ListBackupSetsCommand), "list_backup_sets")]
[JsonDerivedType(typeof(UpsertBackupSetCommand), "upsert_backup_set")]
[JsonDerivedType(typeof(RunBackupCommand), "run_backup")]
[JsonDerivedType(typeof(CancelJobCommand), "cancel_job")]
[JsonDerivedType(typeof(ListJobsCommand), "list_jobs")]
[JsonDerivedType(typeof(ListSnapshotsCommand), "list_snapshots")]
[JsonDerivedType(typeof(ListDirectoryCommand), "list_directory")]
[JsonDerivedType(typeof(PlanRestoreCommand), "plan_restore")]
[JsonDerivedType(typeof(RunRestoreCommand), "run_restore")]
[JsonDerivedType(typeof(VerifyCommand), "verify")]
[JsonDerivedType(typeof(CheckCommand), "check")]
[JsonDerivedType(typeof(RetentionCommand), "retention")]
[JsonDerivedType(typeof(GetStatusCommand), "get_status")]
[JsonDerivedType(typeof(ExportConfigurationCommand), "export_configuration")]
[JsonDerivedType(typeof(DescribeServiceCommand), "describe_service")]
public abstract record ServiceCommand;

/// <summary>Enumerates the configured backup sets.</summary>
public sealed record ListBackupSetsCommand : ServiceCommand;

/// <summary>Adds or replaces one backup set.</summary>
/// <param name="Set">The set to store.</param>
public sealed record UpsertBackupSetCommand(BackupSetDescriptor Set) : ServiceCommand;

/// <summary>Runs a backup now, outside the schedule.</summary>
/// <param name="SetName">The set to run; null runs the default set.</param>
/// <param name="Full">Whether to ignore prior versions and re-capture everything.</param>
public sealed record RunBackupCommand(string? SetName, bool Full) : ServiceCommand;

/// <summary>
/// Stops a running job. Cancellation is a first-class command (ADR-0029 §4),
/// not a signal — a cancelled job records <c>Cancelled</c> rather than staying
/// in whatever state it happened to reach.
/// </summary>
/// <param name="JobId">The job to stop.</param>
public sealed record CancelJobCommand(string JobId) : ServiceCommand;

/// <summary>Lists jobs, most recent last.</summary>
/// <param name="ActiveOnly">Whether to omit finished jobs.</param>
public sealed record ListJobsCommand(bool ActiveOnly) : ServiceCommand;

/// <summary>Lists committed snapshots.</summary>
public sealed record ListSnapshotsCommand : ServiceCommand;

/// <summary>Lists one directory inside a snapshot.</summary>
/// <param name="SnapshotId">The snapshot, hex-encoded.</param>
/// <param name="Path">The directory within the snapshot; null lists the root.</param>
public sealed record ListDirectoryCommand(string SnapshotId, string? Path) : ServiceCommand;

/// <summary>Plans a restore without performing it.</summary>
/// <param name="SnapshotId">The snapshot, hex-encoded.</param>
/// <param name="Path">The subtree to restore; null plans the whole snapshot.</param>
public sealed record PlanRestoreCommand(string SnapshotId, string? Path) : ServiceCommand;

/// <summary>
/// Performs a restore. The output directory is a path <b>on the machine running
/// the service</b>: a restore commanded remotely writes there and the client is
/// told what happened, never sent the files (ADR-0028 §6).
/// </summary>
/// <param name="SnapshotId">The snapshot, hex-encoded.</param>
/// <param name="Path">The subtree to restore; null restores the whole snapshot.</param>
/// <param name="OutputDirectory">Where to write, on the service's machine.</param>
public sealed record RunRestoreCommand(string SnapshotId, string? Path, string OutputDirectory) : ServiceCommand;

/// <summary>Verifies stored objects.</summary>
/// <param name="Level">One of <c>locator</c>, <c>digest</c>, <c>records</c>.</param>
public sealed record VerifyCommand(string Level) : ServiceCommand;

/// <summary>Checks repository health and reports findings.</summary>
/// <param name="Level">One of <c>locator</c>, <c>digest</c>, <c>records</c>.</param>
public sealed record CheckCommand(string Level) : ServiceCommand;

/// <summary>
/// Runs a retention pass over every configured set (architecture 07). The
/// dry-run report is always produced; with <paramref name="Apply"/> the
/// condemned are tombstoned and the grace-expired swept — the destructive
/// half, which is why it is not the default (FR-GC-005).
/// </summary>
/// <param name="Apply">False reports only; true tombstones and sweeps.</param>
public sealed record RetentionCommand(bool Apply) : ServiceCommand;

/// <summary>Reports the user-level protection status per set (architecture 10 §1).</summary>
public sealed record GetStatusCommand : ServiceCommand;

/// <summary>Exports the client configuration, which holds no secrets.</summary>
public sealed record ExportConfigurationCommand : ServiceCommand;

/// <summary>Reports what this service is and what it is doing.</summary>
public sealed record DescribeServiceCommand : ServiceCommand;
