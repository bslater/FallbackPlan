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
[JsonDerivedType(typeof(JobChangesCommand), "job_changes")]
[JsonDerivedType(typeof(JobFailuresCommand), "job_failures")]
[JsonDerivedType(typeof(ListSnapshotsCommand), "list_snapshots")]
[JsonDerivedType(typeof(ListDirectoryCommand), "list_directory")]
[JsonDerivedType(typeof(PlanRestoreCommand), "plan_restore")]
[JsonDerivedType(typeof(RunRestoreCommand), "run_restore")]
[JsonDerivedType(typeof(OpenRestoreSourceCommand), "open_restore_source")]
[JsonDerivedType(typeof(ProvisionWriteOnlySetCommand), "provision_write_only_set")]
[JsonDerivedType(typeof(ProvisionInstallationCommand), "provision_installation")]
[JsonDerivedType(typeof(ConfirmRecoveryKitCommand), "confirm_recovery_kit")]
[JsonDerivedType(typeof(GetDiagnosticsCommand), "get_diagnostics")]
[JsonDerivedType(typeof(SetLogLevelCommand), "set_log_level")]
[JsonDerivedType(typeof(ReadLogCommand), "read_log")]
[JsonDerivedType(typeof(CloseRestoreSourceCommand), "close_restore_source")]
[JsonDerivedType(typeof(VerifyCommand), "verify")]
[JsonDerivedType(typeof(CheckCommand), "check")]
[JsonDerivedType(typeof(RetentionCommand), "retention")]
[JsonDerivedType(typeof(SyncCommand), "sync")]
[JsonDerivedType(typeof(VerifyDestinationCommand), "verify_destination")]
[JsonDerivedType(typeof(RetireStagingCommand), "retire_staging")]
[JsonDerivedType(typeof(GetStatusCommand), "get_status")]
[JsonDerivedType(typeof(ExportConfigurationCommand), "export_configuration")]
[JsonDerivedType(typeof(DescribeServiceCommand), "describe_service")]
[JsonDerivedType(typeof(LoginCommand), "login")]
[JsonDerivedType(typeof(ResumeSessionCommand), "resume_session")]
[JsonDerivedType(typeof(LogoutCommand), "logout")]
[JsonDerivedType(typeof(ListUsersCommand), "list_users")]
[JsonDerivedType(typeof(CreateUserCommand), "create_user")]
[JsonDerivedType(typeof(DeleteUserCommand), "delete_user")]
[JsonDerivedType(typeof(ChangePasswordCommand), "change_password")]
[JsonDerivedType(typeof(RestartServiceCommand), "restart_service")]
public abstract record ServiceCommand;

/// <summary>
/// Restarts the service in place (contract 1.21; ADR-0049): the host tears
/// the runtime down — listeners, queue, archives, the writer role — and
/// starts it again in the same process, so the outcome is identical on
/// every platform whatever the service manager's restart policy says.
/// </summary>
/// <remarks>
/// Owner-only (the second such privilege after account management,
/// ADR-0045), local callers only (a paired console must not cut a machine
/// it cannot see, ADR-0028 §6), and refused before setup. The reply is
/// flushed before teardown begins; everything that lives in service memory
/// — sessions above all — dies with the old runtime, which is the
/// documented FR-USR-003 contract: a restart signs everybody out.
/// </remarks>
public sealed record RestartServiceCommand : ServiceCommand;

/// <summary>
/// Authenticates a person and mints a session (FR-USR-001, FR-USR-003;
/// ADR-0045 §5).
/// </summary>
/// <param name="User">The account name.</param>
/// <param name="Password">The password, as typed.</param>
/// <remarks>
/// One of the three verbs an unauthenticated connection may speak, alongside
/// <see cref="DescribeServiceCommand"/> — a login screen has to know whether
/// the installation is even set up — and <see cref="ResumeSessionCommand"/>.
/// </remarks>
public sealed record LoginCommand(string User, string Password) : ServiceCommand;

/// <summary>
/// Presents an existing session token on a new connection.
/// </summary>
/// <param name="Token">The token a previous <see cref="LoginCommand"/> returned.</param>
/// <remarks>
/// The session is minted once and lives in the service's memory; a connection
/// is what presents it. That separation is what lets the web console work at
/// all, because it opens a fresh connection for every request it relays — a
/// session welded to one transport connection would log the operator out
/// between clicking two buttons.
/// </remarks>
public sealed record ResumeSessionCommand(string Token) : ServiceCommand;

/// <summary>Ends the session this connection presented, everywhere (FR-USR-003).</summary>
/// <remarks>
/// Revokes the session itself, not just this connection's use of it, because
/// the operator pressing "sign out" means the person, not the socket.
/// </remarks>
public sealed record LogoutCommand : ServiceCommand;

/// <summary>Enumerates the installation's accounts, without any credential.</summary>
public sealed record ListUsersCommand : ServiceCommand;

/// <summary>Creates an account (FR-USR-004 — owner only, for now).</summary>
/// <param name="Name">The account name.</param>
/// <param name="Password">Its password.</param>
/// <param name="Role">The role, by name. Ignored for the first account, which is always the owner.</param>
public sealed record CreateUserCommand(string Name, string Password, string? Role = null) : ServiceCommand;

/// <summary>Removes an account (FR-USR-004 — owner only, and never the owner).</summary>
/// <param name="Name">The account to remove.</param>
public sealed record DeleteUserCommand(string Name) : ServiceCommand;

/// <summary>Changes the signed-in account's own password.</summary>
/// <param name="CurrentPassword">The password in force, proved before anything changes.</param>
/// <param name="NewPassword">Its replacement.</param>
/// <remarks>
/// Always the caller's own account, never a named one: an owner who could set
/// somebody else's password could become them, which would make every
/// attributable action attributable to the wrong person.
/// </remarks>
public sealed record ChangePasswordCommand(string CurrentPassword, string NewPassword) : ServiceCommand;

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
/// <param name="Roots">
/// The draft's source folders. Supplied together with
/// <paramref name="Destinations"/> so the answer can say where this set
/// would actually be durable (FR-SNP-007); omitted, the durability question
/// is simply not asked.
/// </param>
/// <param name="Destinations">The destination names the draft references, by name.</param>
public sealed record ValidateSetDraftCommand(
    string? Schedule,
    IReadOnlyList<string> IncludeRules,
    IReadOnlyList<string> ExcludeRules,
    IReadOnlyList<string>? Roots = null,
    IReadOnlyList<string>? Destinations = null) : ServiceCommand;

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
/// <param name="Limit">
/// Keep only the newest this-many rows (contract 1.22). Null keeps the
/// pre-1.22 meaning — everything — but the journal grows for the life of the
/// installation and <see cref="Transport.FrameCodec.MaximumFrameBytes"/> caps
/// a reply, so a history view should bound its ask.
/// </param>
public sealed record ListJobsCommand(bool ActiveOnly, int? Limit = null) : ServiceCommand;

/// <summary>
/// What a completed run changed, against its predecessor (contract 1.22,
/// ADR-0050): the committed snapshot diffed with the set's previous one in
/// the catalogue — exact counts, bounded samples. Refused for a run that
/// committed no snapshot: a failed or cancelled run has nothing to diff.
/// </summary>
/// <param name="JobId">The journal row whose run is asked about.</param>
/// <param name="SampleLimit">Paths per bucket; clamped by the service.</param>
public sealed record JobChangesCommand(string JobId, int? SampleLimit = null) : ServiceCommand;

/// <summary>
/// What a completed run could not capture (contract 1.22, ADR-0050): the
/// snapshot's error manifest read back — each failure's path, typed reason
/// and the scanner's own words. A clean run answers zero.
/// </summary>
/// <param name="JobId">The journal row whose run is asked about.</param>
/// <param name="SampleLimit">Failures listed; clamped by the service.</param>
public sealed record JobFailuresCommand(string JobId, int? SampleLimit = null) : ServiceCommand;

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
/// First-run setup: gives this installation the passphrase everything
/// derives from (ADR-0044 §2, FR-SVC-011).
/// </summary>
/// <remarks>
/// <para>
/// The same ceremony as <see cref="ProvisionWriteOnlySetCommand"/> and the
/// same sealed shape, addressed to the <b>installation</b> rather than to a
/// named set — because a service on its first run has no sets, which is
/// precisely the state this verb exists to leave behind. The client mints the
/// salt, derives in its own process, seals to this service's published
/// recipient key, and sends hex; the passphrase never crosses this contract.
/// </para>
/// <para>
/// Every set's staging archive is then created from the stored credential on
/// that set's first backup. There is no second setup: a v2 passphrase can
/// never change (ADR-0042 §11), so a repeat is refused rather than obeyed —
/// obeying it would mint a second, unrelated root and strand every archive
/// written under the first.
/// </para>
/// <para>
/// <b>Local callers only.</b> Choosing the one secret that can never be
/// changed sits inside the line ADR-0028 §6 already draws around remote
/// clients; a paired remote console is refused.
/// </para>
/// </remarks>
/// <param name="Envelope">
/// The provisioning envelope — write bundle plus KDF salt and parameters —
/// sealed to the service's recipient key and rendered as hex (NFR-SEC-009's
/// permitted shape, NFR-SEC-011).
/// </param>
public sealed record ProvisionInstallationCommand(string Envelope) : ServiceCommand;

/// <summary>
/// Records that the operator has saved the installation's recovery kit,
/// which is the last thing first-run setup waits for (FR-KIT-004,
/// ADR-0044's 2026-08 amendment).
/// </summary>
/// <remarks>
/// <para>
/// The <b>checksum, not the kit</b>. The service never holds a copy: a kit
/// stored on the machine being backed up is not a recovery kit, and a
/// service able to hand one out would have made itself a second factor.
/// What it keeps is enough to say <em>which</em> kit was saved, so a later
/// one can be told apart from the one in the drawer.
/// </para>
/// <para>
/// Local callers only, for the same reason setup is: the person confirming
/// has to be the person holding the kit.
/// </para>
/// </remarks>
/// <param name="KitChecksum">
/// The kit's SHA-256, lowercase hex — the last 32 bytes of its framed form.
/// </param>
public sealed record ConfirmRecoveryKitCommand(string KitChecksum) : ServiceCommand;

/// <summary>
/// Asks what this service is logging and where it is putting it (ADR-0043 §6,
/// FR-SVC-010).
/// </summary>
/// <remarks>
/// The answer says whether a durable sink exists and <b>never where it is</b>.
/// A client that learned the log's path would have learned a filesystem path
/// from a service that exposes no filesystem access to clients (threat T-16),
/// and a remote console would have learned one on a machine it cannot see.
/// Whoever may read the file already knows the state directory.
/// </remarks>
public sealed record GetDiagnosticsCommand : ServiceCommand;

/// <summary>
/// Changes a level while the service runs (ADR-0043 §6).
/// </summary>
/// <remarks>
/// <para>
/// The reason this is a verb rather than a configuration edit and a restart:
/// the level a machine needs is only ever known once it has already
/// misbehaved, and asking somebody to restart the service to find out why it
/// failed is asking them to destroy the evidence first.
/// </para>
/// <para>
/// The change is <b>in force, not persisted</b>. It lasts until the service
/// stops, at which point <c>config.json</c>'s level applies again — so an
/// afternoon of tracing cannot become a machine that has been logging at trace
/// for eight months because nobody remembered to put it back.
/// </para>
/// <para>
/// <b>Local callers only.</b> Turning the logging up is how an operator makes
/// this machine write more about itself to its own disk; a paired remote
/// console may read the log but not decide what goes into it (ADR-0028 §6).
/// </para>
/// </remarks>
/// <param name="Category">
/// The category prefix to set, matched by longest declared prefix — so
/// <c>FallbackPlan.Repository</c> covers its children. Null sets the default
/// level that applies where no prefix matches.
/// </param>
/// <param name="Level">
/// The level by name: trace, debug, information, warning, error, critical or
/// none. A name nobody recognises is refused naming the ones that exist.
/// </param>
public sealed record SetLogLevelCommand(string? Category, string Level) : ServiceCommand;

/// <summary>
/// Reads a page of the service's in-memory log ring (ADR-0043 §6).
/// </summary>
/// <remarks>
/// <para>
/// Paginated, and not as a courtesy: <c>FrameCodec</c> caps a frame at 8 MiB,
/// so "send me everything" is not a request this contract can answer. A client
/// walks the ring with the cursor the previous page returned.
/// </para>
/// <para>
/// The ring is bounded and drops its oldest records, so a reader that falls far
/// enough behind will find its cursor older than anything still held. That is
/// reported rather than papered over — <c>LogRecordsResult.Dropped</c> says
/// records were missed, which is a different fact from "nothing happened".
/// </para>
/// </remarks>
/// <param name="SinceSequence">
/// The cursor: records after this sequence. Zero starts at the oldest record
/// still held.
/// </param>
/// <param name="MaxRecords">
/// The most records to return in this page. Clamped to a ceiling the frame can
/// carry rather than refused, because a client asking for too many is asking
/// for a page, not committing an error.
/// </param>
/// <param name="MinimumLevel">
/// Records below this level are skipped. Null means everything the ring holds.
/// </param>
public sealed record ReadLogCommand(long SinceSequence, int MaxRecords, string? MinimumLevel = null)
    : ServiceCommand;

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

/// <summary>
/// Retires a direct-ship set's staging archive (ADR-0046, contract 1.18):
/// deletes the local copy a migrated set no longer publishes into. Refused
/// by name while the set is not direct-ship, or while staging holds any
/// object that has not reached a destination — retirement must never be the
/// act that loses the last copy of anything.
/// </summary>
/// <param name="SetName">The set whose staging archive to retire.</param>
public sealed record RetireStagingCommand(string SetName) : ServiceCommand;

/// <summary>Reports the user-level protection status per set (architecture 10 §1).</summary>
public sealed record GetStatusCommand : ServiceCommand;

/// <summary>Exports the client configuration, which holds no secrets.</summary>
public sealed record ExportConfigurationCommand : ServiceCommand;

/// <summary>Reports what this service is and what it is doing.</summary>
public sealed record DescribeServiceCommand : ServiceCommand;
