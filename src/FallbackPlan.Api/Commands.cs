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
[JsonDerivedType(typeof(DeleteBackupSetCommand), "delete_backup_set")]
[JsonDerivedType(typeof(ListDestinationsCommand), "list_destinations")]
[JsonDerivedType(typeof(UpsertDestinationCommand), "upsert_destination")]
[JsonDerivedType(typeof(DeleteDestinationCommand), "delete_destination")]
[JsonDerivedType(typeof(ListPairingsCommand), "list_pairings")]
[JsonDerivedType(typeof(BrowseFoldersCommand), "browse_folders")]
[JsonDerivedType(typeof(ValidateSetDraftCommand), "validate_set_draft")]
[JsonDerivedType(typeof(PreviewSetChangesCommand), "preview_set_changes")]
[JsonDerivedType(typeof(ListNoticesCommand), "list_notices")]
[JsonDerivedType(typeof(AcknowledgeNoticeCommand), "acknowledge_notice")]
[JsonDerivedType(typeof(UnpairCommand), "unpair")]
[JsonDerivedType(typeof(CreatePairingInviteCommand), "create_pairing_invite")]
[JsonDerivedType(typeof(ListPairingInvitesCommand), "list_pairing_invites")]
[JsonDerivedType(typeof(RevokePairingInviteCommand), "revoke_pairing_invite")]
[JsonDerivedType(typeof(PairWithInviteCommand), "pair_with_invite")]
[JsonDerivedType(typeof(RunBackupCommand), "run_backup")]
[JsonDerivedType(typeof(CancelJobCommand), "cancel_job")]
[JsonDerivedType(typeof(ListJobsCommand), "list_jobs")]
[JsonDerivedType(typeof(ListSnapshotsCommand), "list_snapshots")]
[JsonDerivedType(typeof(ListDirectoryCommand), "list_directory")]
[JsonDerivedType(typeof(PlanRestoreCommand), "plan_restore")]
[JsonDerivedType(typeof(RunRestoreCommand), "run_restore")]
[JsonDerivedType(typeof(OpenRestoreSourceCommand), "open_restore_source")]
[JsonDerivedType(typeof(ProvisionWriteOnlySetCommand), "provision_write_only_set")]
[JsonDerivedType(typeof(CloseRestoreSourceCommand), "close_restore_source")]
[JsonDerivedType(typeof(VerifyCommand), "verify")]
[JsonDerivedType(typeof(CheckCommand), "check")]
[JsonDerivedType(typeof(RetentionCommand), "retention")]
[JsonDerivedType(typeof(SyncCommand), "sync")]
[JsonDerivedType(typeof(VerifyDestinationCommand), "verify_destination")]
[JsonDerivedType(typeof(GetStatusCommand), "get_status")]
[JsonDerivedType(typeof(ExportConfigurationCommand), "export_configuration")]
[JsonDerivedType(typeof(DescribeServiceCommand), "describe_service")]
public abstract record ServiceCommand;

/// <summary>Enumerates the configured backup sets.</summary>
public sealed record ListBackupSetsCommand : ServiceCommand;

/// <summary>Adds or replaces one backup set.</summary>
/// <param name="Set">The set to store.</param>
public sealed record UpsertBackupSetCommand(BackupSetDescriptor Set) : ServiceCommand;

/// <summary>
/// Removes one backup set from the configuration (ADR-0037 §4). Nothing else
/// is touched: the set's staging archive stays on disk and every destination
/// keeps what it holds — the result says so, because a removal that looked
/// like an erasure would be the more dangerous misreading.
/// </summary>
/// <param name="Name">The set to remove.</param>
public sealed record DeleteBackupSetCommand(string Name) : ServiceCommand;

/// <summary>
/// Enumerates every declared destination — referenced by a set or not — with
/// its declaration and any address defect (ADR-0037). The status matrix shows
/// only what sets reference; a configuration surface needs the rest.
/// </summary>
public sealed record ListDestinationsCommand : ServiceCommand;

/// <summary>Adds or replaces one declared destination (ADR-0037).</summary>
/// <param name="Destination">The declaration; a null id declares a new destination.</param>
public sealed record UpsertDestinationCommand(DestinationDescriptor Destination) : ServiceCommand;

/// <summary>
/// Removes one declared destination (FR-DEST-007). Refused while any set
/// references it, naming the sets; when it proceeds, the result names what
/// remains at the address, because removal stops the hub managing the data —
/// it deletes none of it.
/// </summary>
/// <param name="Name">The destination to remove.</param>
public sealed record DeleteDestinationCommand(string Name) : ServiceCommand;

/// <summary>
/// Lists this device's paired peers (ADR-0030's grants) — what a peer
/// destination declaration can point at.
/// </summary>
public sealed record ListPairingsCommand : ServiceCommand;

/// <summary>
/// Lists the directories under a path on the service's machine, for a client
/// building a folder picker (ADR-0037 §6). Names and sizes only, never
/// content; null lists the platform's roots.
/// </summary>
/// <param name="Path">The directory to list, or null for the roots.</param>
/// <param name="IncludeFiles">
/// Whether to list the files too — what a selection tree needs and a root
/// picker does not.
/// </param>
public sealed record BrowseFoldersCommand(string? Path, bool IncludeFiles = false) : ServiceCommand;

/// <summary>
/// Validates a set draft without saving anything: rule defects named
/// verbatim, and a parseable schedule answered with its next occurrences so
/// an editor can show what it means rather than what it says (ADR-0037 §2).
/// </summary>
/// <param name="Schedule">The schedule expression, or null/empty for manual-only.</param>
/// <param name="IncludeRules">rules-v1 include rules.</param>
/// <param name="ExcludeRules">rules-v1 exclude rules.</param>
public sealed record ValidateSetDraftCommand(
    string? Schedule,
    IReadOnlyList<string> IncludeRules,
    IReadOnlyList<string> ExcludeRules) : ServiceCommand;

/// <summary>
/// Walks a set's source now — under its saved root and rules, or a draft's —
/// and reports what changed against the set's latest snapshot: new, updated,
/// metadata-only, moved, deleted, and no-longer-included files, counts always
/// exact and paths sampled per bucket (ADR-0038, FR-SVC-009). A dry scan on
/// the reader lane; no content is opened and nothing is captured.
/// </summary>
/// <param name="SetName">
/// The set to compare; null compares the default (first) set. A name that
/// resolves to no set is still answered when draft roots are given — the
/// walk classifies against an empty baseline, which is what an editor
/// building a brand-new set needs (ADR-0040).
/// </param>
/// <param name="Root">A draft root to walk instead of the saved ones; null walks the saved roots.</param>
/// <param name="IncludeRules">Draft include rules; null compares under the saved rules.</param>
/// <param name="ExcludeRules">Draft exclude rules; null compares under the saved rules.</param>
/// <param name="SampleLimit">The most paths any bucket carries; null takes 20, capped at 200.</param>
/// <param name="Roots">Draft roots (ADR-0040); wins over <paramref name="Root"/> when present.</param>
public sealed record PreviewSetChangesCommand(
    string? SetName,
    string? Root = null,
    IReadOnlyList<string>? IncludeRules = null,
    IReadOnlyList<string>? ExcludeRules = null,
    int? SampleLimit = null,
    IReadOnlyList<BackupRootDescriptor>? Roots = null) : ServiceCommand;

/// <summary>
/// Issues a one-time pairing invite (ADR-0030 Amendment 3): the code this
/// returns, spoken to the other operator, is what authenticates their
/// device's pairing dial. Issuing it is this side's approval — role, label
/// and terms are committed here, not at connection time.
/// </summary>
/// <param name="Label">What to call the peer that redeems it.</param>
/// <param name="Role">The role recorded for that peer: <c>stores-here</c>, <c>stores-for-us</c>, or <c>both</c>.</param>
/// <param name="QuotaBytes">The storage ceiling offered when the peer stores here; null or 0 declares none.</param>
/// <param name="TimeToLiveMinutes">How long the invite stays redeemable; null takes the default day.</param>
public sealed record CreatePairingInviteCommand(
    string Label, string Role, ulong? QuotaBytes, int? TimeToLiveMinutes) : ServiceCommand;

/// <summary>Lists pending and consumed pairing invites — never their codes.</summary>
public sealed record ListPairingInvitesCommand : ServiceCommand;

/// <summary>Revokes one pending pairing invite.</summary>
/// <param name="InviteId">The invite's identifier, from the listing.</param>
public sealed record RevokePairingInviteCommand(string InviteId) : ServiceCommand;

/// <summary>
/// Pairs with a remote service using an invite its operator issued (ADR-0030
/// Amendment 3): this service dials the endpoint, proves possession of the
/// code, verifies the far side proves it too, and pins the grant. Entering
/// the code is this side's approval.
/// </summary>
/// <param name="Code">The invite code, as spoken by the other operator.</param>
/// <param name="Host">The remote service's host.</param>
/// <param name="Port">The remote service's port.</param>
/// <param name="Label">What to call the paired peer here.</param>
public sealed record PairWithInviteCommand(string Code, string Host, int Port, string Label) : ServiceCommand;

/// <summary>
/// Lists the durable notices — the events awaiting a person (FR-DEST-008),
/// structured so a client can show age and acknowledge by identity rather
/// than parsing the status strings.
/// </summary>
/// <param name="IncludeAcknowledged">
/// Whether to include notices a person has already acknowledged — they stay
/// on record; the default answers only what still awaits one.
/// </param>
public sealed record ListNoticesCommand(bool IncludeAcknowledged = false) : ServiceCommand;

/// <summary>
/// Acknowledges one notice (FR-DEST-008): a person has seen it. The notice
/// stays on record; it merely stops demanding attention.
/// </summary>
/// <param name="Id">The notice's identifier, from the listing.</param>
public sealed record AcknowledgeNoticeCommand(string Id) : ServiceCommand;

/// <summary>
/// Ends a pairing (ADR-0030 Amendment 2): announces the termination to the
/// peer when asked and reachable, then revokes the grant locally, leaving a
/// tombstone so the peer's next dial is told <c>revoked</c> rather than
/// <c>never paired</c>. Refused while a configured destination references
/// the fingerprint — a revocation must not silently break what sets sync to.
/// </summary>
/// <param name="Fingerprint">The pairing's fingerprint, or an unambiguous prefix of it.</param>
/// <param name="Notify">Whether to announce the ending to the peer before revoking.</param>
/// <param name="Endpoint">
/// Where to dial the announcement, as <c>host:port</c>; null consults the
/// configured destinations — which the honest order (destination deleted
/// first) has usually just emptied, so a caller that knows the address says
/// it here, exactly as the agent verb's <c>--to</c> does.
/// </param>
public sealed record UnpairCommand(string Fingerprint, bool Notify = true, string? Endpoint = null) : ServiceCommand;

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
/// <param name="Source">An open restore source to read from (ADR-0041); null reads the staging archives.</param>
public sealed record ListDirectoryCommand(
    string SnapshotId, string? Path, string? Source = null) : ServiceCommand;

/// <summary>Plans a restore without performing it.</summary>
/// <param name="SnapshotId">The snapshot, hex-encoded.</param>
/// <param name="Path">The subtree to restore; null plans the whole snapshot.</param>
/// <param name="Source">An open restore source to read from (ADR-0041); null reads the staging archives.</param>
/// <param name="Paths">
/// Several subtrees in one plan — one run, one receipt (ADR-0041). Wins over
/// <paramref name="Path"/> when present, exactly as the set descriptor's
/// roots win over its root.
/// </param>
public sealed record PlanRestoreCommand(
    string SnapshotId,
    string? Path,
    string? Source = null,
    IReadOnlyList<string>? Paths = null) : ServiceCommand;

/// <summary>
/// Performs a restore. The output directory is a path <b>on the machine running
/// the service</b>: a restore commanded remotely writes there and the client is
/// told what happened, never sent the files (ADR-0028 §6).
/// </summary>
/// <param name="SnapshotId">The snapshot, hex-encoded.</param>
/// <param name="Path">The subtree to restore; null restores the whole snapshot.</param>
/// <param name="OutputDirectory">Where to write, on the service's machine.</param>
/// <param name="Source">An open restore source to read from (ADR-0041); null reads the staging archives.</param>
/// <param name="Paths">Several subtrees in one run; wins over <paramref name="Path"/> when present.</param>
/// <param name="Target">
/// <c>folder</c> (default) writes under <paramref name="OutputDirectory"/>;
/// <c>original</c> writes each subtree back where it was captured — the set's
/// configured root folders, label-mapped for a multi-root set (ADR-0040) —
/// and ignores <paramref name="OutputDirectory"/>. Original is in-place by
/// definition.
/// </param>
/// <param name="Existing">
/// What to do about a file already at a destination: null preserves it into
/// the displaced store (today's default), <c>rename</c> keeps it untouched
/// and writes the restored copy beside it with a dated suffix, and
/// <c>overwrite</c> replaces it — destructive, and never a default
/// (FR-RST-006's explicit choice).
/// </param>
/// <param name="InPlace">
/// Whether restored content lands directly where pointed rather than under
/// the quarantine directory. False keeps today's quarantine default; the
/// wizard's confirmed flow sends true.
/// </param>
public sealed record RunRestoreCommand(
    string SnapshotId,
    string? Path,
    string OutputDirectory,
    string? Source = null,
    IReadOnlyList<string>? Paths = null,
    string? Target = null,
    string? Existing = null,
    bool InPlace = false) : ServiceCommand;

/// <summary>
/// Opens a restore source (ADR-0041): the place a guided restore reads from —
/// the set's own staging archive, a local-path destination's replica, or a
/// paired peer's replica over the retrieval session. The service opens the
/// repository, prepares a catalogue for it, and answers a handle plus the
/// snapshots it holds; the handle feeds the source-aware verbs until closed
/// or idle-expired.
/// </summary>
/// <param name="SetName">The backup set whose repository to open — sources are per-set, one repository each (ADR-0034).</param>
/// <param name="DestinationName">The destination whose replica to read; null opens the set's staging archive.</param>
/// <param name="Envelope">
/// A restore grant for a write-only set (ADR-0042 §5): the derived sealing
/// scalar, sealed end-to-end to this service's published recipient key and
/// rendered as hex. The one shape of key material NFR-SEC-009 permits on the
/// contract — opaque to every relay, opened only inside the service, held
/// only for the source handle's life. Null opens structure-plane only on a
/// write-only set; v1 sets ignore it.
/// </param>
public sealed record OpenRestoreSourceCommand(
    string SetName, string? DestinationName = null, string? Envelope = null) : ServiceCommand;

/// <summary>
/// Provisions a backup set as a write-only repository (ADR-0042 §4, §10) —
/// both ceremonies in one verb. The admin client derives the write bundle
/// from the passphrase, seals it to this service's published recipient key,
/// and sends the envelope; the service opens it and either <b>creates</b> the
/// v2 repository (no descriptor at the staging path yet) or <b>adopts</b> an
/// existing one (descriptor present — a moved archive or a lost state
/// directory — after proving the derived sealing public key matches the
/// descriptor's; a mismatch is a wrong passphrase and is refused). The
/// passphrase itself never crosses this contract.
/// </summary>
/// <param name="SetName">The backup set to provision.</param>
/// <param name="Envelope">
/// The provisioning envelope — write bundle plus KDF salt and parameters —
/// sealed to the service's recipient key and rendered as hex (NFR-SEC-009's
/// permitted shape).
/// </param>
public sealed record ProvisionWriteOnlySetCommand(string SetName, string Envelope) : ServiceCommand;

/// <summary>
/// Closes a restore source. Idempotent — closing an unknown or already-closed
/// handle acknowledges rather than errors, because a page unloading fires
/// this without awaiting anything.
/// </summary>
/// <param name="SourceId">The handle to release.</param>
public sealed record CloseRestoreSourceCommand(string SourceId) : ServiceCommand;

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

/// <summary>
/// Converges destinations now, outside the schedule (ADR-0034 §3,
/// FR-DEST-002): one sync per matching <c>(set, destination)</c> pair,
/// answered when they have run so the result reflects the refreshed ledger.
/// </summary>
/// <param name="BackupSetName">The set to sync; null syncs every configured set.</param>
/// <param name="DestinationName">The destination to sync; null syncs each set's every destination.</param>
public sealed record SyncCommand(string? BackupSetName, string? DestinationName) : ServiceCommand;

/// <summary>
/// Asks what a destination can still be trusted for, at one of three depths
/// (FR-DEST-001, FR-VER-002, FR-VER-004).
/// </summary>
/// <remarks>
/// One verb with a depth, rather than several that drift apart: probe asks
/// whether the destination could take a backup at all, the default reads one
/// bounded segment of its stored bytes, and full reads every one of them.
/// Aimed at a <i>destination</i>, where <see cref="VerifyCommand"/> sweeps the
/// hub's own staging archives at a chosen level.
/// </remarks>
/// <param name="BackupSetName">The set to verify; null takes every configured set.</param>
/// <param name="DestinationName">The destination to verify; null takes each set's every destination.</param>
/// <param name="Full">
/// False reads one bounded segment, as the scheduled sweep does; true keeps
/// going until the circuit closes, which is what a recovery drill wants
/// (FR-VER-004). Ignored when <paramref name="Probe"/> is set.
/// </param>
/// <param name="Probe">
/// Reads nothing: confirms only that the destination could take a backup —
/// the address is usable, the directory exists and accepts writes, or the peer
/// is reachable and still honours the grant. The one depth that answers before
/// the first sync has ever run, which is when a destination is least proven
/// and most trusted.
/// </param>
public sealed record VerifyDestinationCommand(
    string? BackupSetName, string? DestinationName, bool Full, bool Probe = false) : ServiceCommand;

/// <summary>Reports the user-level protection status per set (architecture 10 §1).</summary>
public sealed record GetStatusCommand : ServiceCommand;

/// <summary>Exports the client configuration, which holds no secrets.</summary>
public sealed record ExportConfigurationCommand : ServiceCommand;

/// <summary>Reports what this service is and what it is doing.</summary>
public sealed record DescribeServiceCommand : ServiceCommand;
