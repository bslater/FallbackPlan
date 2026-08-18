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
[JsonDerivedType(typeof(RestoreSourceOpenedResult), "restore_source")]
[JsonDerivedType(typeof(VerificationResult), "verification")]
[JsonDerivedType(typeof(CheckResult), "check")]
[JsonDerivedType(typeof(RetentionResult), "retention")]
[JsonDerivedType(typeof(SyncResult), "sync")]
[JsonDerivedType(typeof(VerifyDestinationResult), "verify_destination")]
[JsonDerivedType(typeof(StatusResult), "status")]
[JsonDerivedType(typeof(ConfigurationResult), "configuration")]
[JsonDerivedType(typeof(ServiceDescriptionResult), "service_description")]
[JsonDerivedType(typeof(ConfigurationChangeResult), "configuration_change")]
[JsonDerivedType(typeof(DestinationsResult), "destinations")]
[JsonDerivedType(typeof(PairingsResult), "pairings")]
[JsonDerivedType(typeof(FolderListingResult), "folder_listing")]
[JsonDerivedType(typeof(SetDraftValidationResult), "set_draft_validation")]
[JsonDerivedType(typeof(SetChangePreviewResult), "set_change_preview")]
[JsonDerivedType(typeof(NoticesResult), "notices")]
[JsonDerivedType(typeof(PairingInviteResult), "pairing_invite")]
[JsonDerivedType(typeof(PairingInvitesResult), "pairing_invites")]
[JsonDerivedType(typeof(PairingCompletedResult), "pairing_completed")]
public abstract record ServiceResult;

/// <summary>A command that did not succeed, with a reason a client can branch on.</summary>
/// <param name="Reason">The closed-set reason.</param>
/// <param name="Message">What to tell the user.</param>
public sealed record ServiceError(ServiceErrorReason Reason, string Message) : ServiceResult;

/// <summary>A command that succeeded and has nothing to report.</summary>
public sealed record AcknowledgedResult : ServiceResult;

/// <summary>
/// One retention policy, as the contract carries it (ADR-0037). Every field
/// optional; a declared value must be positive — zero is refused as a typo,
/// absent is how "no rule" is said (FR-GC-001's never-destructive default).
/// </summary>
/// <param name="KeepDaily">Keep one snapshot per calendar day, this many days.</param>
/// <param name="KeepWeekly">Keep one per ISO week, this many weeks.</param>
/// <param name="KeepMonthly">Keep one per calendar month, this many months.</param>
/// <param name="MinGenerations">The floor the other rules cannot override.</param>
/// <param name="DeferralDays">How long retention may wait on a lagging destination before warning (FR-GC-009).</param>
public sealed record RetentionPolicyDescriptor(
    int? KeepDaily = null,
    int? KeepWeekly = null,
    int? KeepMonthly = null,
    int? MinGenerations = null,
    int? DeferralDays = null)
{
    /// <summary>Whether every field is absent — the "no policy" spelling.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        KeepDaily is null && KeepWeekly is null && KeepMonthly is null
        && MinGenerations is null && DeferralDays is null;
}

/// <summary>One capture root of a backup set (ADR-0040).</summary>
/// <param name="Path">The folder to capture.</param>
/// <param name="Label">
/// The root's name inside multi-root snapshots and the first component of
/// its rule subjects; the service materialises one at save time when the
/// caller does not choose.
/// </param>
public sealed record BackupRootDescriptor(string Path, string? Label = null);

/// <summary>One configured backup set, as a client sees it.</summary>
/// <param name="Id">The set's 32-hex identity.</param>
/// <param name="Name">The set's name.</param>
/// <param name="Root">
/// The first captured directory — the pre-1.10 single root, kept for older
/// clients. On an upsert it is read only when <paramref name="Roots"/> is
/// absent.
/// </param>
/// <param name="Schedule">Its schedule expression, or null for manual-only.</param>
/// <param name="IncludeRules">Include rules, in rules-v1 dialect.</param>
/// <param name="ExcludeRules">Exclude rules, in rules-v1 dialect.</param>
/// <param name="Destinations">The declared destination names the set replicates to (FR-DEST-001).</param>
/// <param name="Retention">
/// The set's retention policy. On an upsert, null preserves whatever the set
/// already has (what a 1.6 client always sends), and a policy with every
/// field absent clears it — the difference between not speaking and saying
/// "none" (ADR-0037).
/// </param>
/// <param name="DestinationRetention">
/// Per-destination overrides by destination name. An entry fully replaces the
/// set policy for that destination (FR-GC-010) — there is no field merge. On
/// an upsert, null preserves existing overrides; an empty-policy entry clears
/// that destination's override.
/// </param>
/// <param name="Roots">
/// The capture roots (ADR-0040). Listings always carry them; on an upsert
/// they win over <paramref name="Root"/> when present, and at least one of
/// the two forms must be spoken.
/// </param>
public sealed record BackupSetDescriptor(
    string Id,
    string Name,
    string Root,
    string? Schedule,
    IReadOnlyList<string> IncludeRules,
    IReadOnlyList<string> ExcludeRules,
    IReadOnlyList<string> Destinations,
    RetentionPolicyDescriptor? Retention = null,
    IReadOnlyDictionary<string, RetentionPolicyDescriptor>? DestinationRetention = null,
    IReadOnlyList<BackupRootDescriptor>? Roots = null);

/// <summary>
/// One declared destination, as the configuration surface sees it
/// (ADR-0037). Which fields apply depends on <paramref name="Kind"/>:
/// <c>local-path</c> takes a path; <c>peer</c> takes a fingerprint and an
/// endpoint; the schema-reserved cloud kinds take neither yet.
/// </summary>
/// <param name="Id">The destination's 32-hex identity; null on an upsert declares a new one.</param>
/// <param name="Name">Its unique name — what sets reference.</param>
/// <param name="Kind">Its kind, in the configuration's spelling.</param>
/// <param name="Path">The directory, for <c>local-path</c>.</param>
/// <param name="Fingerprint">The paired peer's fingerprint, for <c>peer</c>.</param>
/// <param name="Endpoint">The peer's <c>host:port</c>, for <c>peer</c>.</param>
/// <param name="FailureDomain">The declared failure domain, or null to derive by kind (ADR-0018).</param>
/// <param name="DeepVerifyIntervalDays">How often the deep sweep re-reads it; null takes the default.</param>
/// <param name="AddressDefect">
/// What is syntactically wrong with the declared address, when anything is —
/// reported, never a refusal to load (ADR-0035 §1). Ignored on an upsert.
/// </param>
public sealed record DestinationDescriptor(
    string? Id,
    string Name,
    string Kind,
    string? Path,
    string? Fingerprint,
    string? Endpoint,
    string? FailureDomain = null,
    int? DeepVerifyIntervalDays = null,
    string? AddressDefect = null);

/// <summary>Every declared destination, referenced by a set or not.</summary>
/// <param name="Destinations">The declarations, in configuration order.</param>
public sealed record DestinationsResult(IReadOnlyList<DestinationDescriptor> Destinations) : ServiceResult;

/// <summary>A configuration change that succeeded and owes the operator facts.</summary>
/// <param name="Lines">
/// What the change did <em>not</em> do, mostly: the copies and archives that
/// remain after a removal (FR-DEST-007), stated rather than inferred.
/// </param>
public sealed record ConfigurationChangeResult(IReadOnlyList<string> Lines) : ServiceResult;

/// <summary>One paired peer, as the grant store records it.</summary>
/// <param name="Fingerprint">The peer's fingerprint.</param>
/// <param name="Label">What this device calls it.</param>
/// <param name="Role">The role recorded for it: <c>stores-here</c>, <c>stores-for-us</c>, or <c>both</c>.</param>
/// <param name="PairedAt">When it was pinned, Unix milliseconds.</param>
public sealed record PairingDescriptor(string Fingerprint, string Label, string Role, ulong PairedAt);

/// <summary>This device's paired peers.</summary>
/// <param name="Pairings">The grants, oldest first.</param>
public sealed record PairingsResult(IReadOnlyList<PairingDescriptor> Pairings) : ServiceResult;

/// <summary>One directory on the service's machine, for a folder picker.</summary>
/// <param name="Name">The directory's name.</param>
/// <param name="Path">Its full path, as a later command would use it.</param>
/// <param name="Hidden">Whether the platform marks it hidden.</param>
/// <param name="Inaccessible">Whether listing inside it failed; shown, never thrown.</param>
public sealed record FolderDescriptor(string Name, string Path, bool Hidden, bool Inaccessible);

/// <summary>One file on the service's machine, for a selection tree.</summary>
/// <param name="Name">The file's name.</param>
/// <param name="Length">Its length in bytes.</param>
/// <param name="Hidden">Whether the platform marks it hidden.</param>
public sealed record FileEntryDescriptor(string Name, long Length, bool Hidden);

/// <summary>One directory listing on the service's machine (ADR-0037 §6).</summary>
/// <param name="Path">The directory listed, or null when the roots were.</param>
/// <param name="Parent">The directory above it, or null at a root.</param>
/// <param name="Folders">The child directories, sorted by name.</param>
/// <param name="Files">The files, when they were asked for; null otherwise.</param>
public sealed record FolderListingResult(
    string? Path,
    string? Parent,
    IReadOnlyList<FolderDescriptor> Folders,
    IReadOnlyList<FileEntryDescriptor>? Files = null) : ServiceResult;

/// <summary>What a set draft means, before anything is saved.</summary>
/// <param name="Defects">Everything wrong, rules and schedule alike, named verbatim; empty when the draft is sound.</param>
/// <param name="NextRuns">The schedule's next occurrences (ISO-8601, the service's clock frame); empty for manual-only or a defective schedule.</param>
public sealed record SetDraftValidationResult(
    IReadOnlyList<string> Defects, IReadOnlyList<string> NextRuns) : ServiceResult;

/// <summary>One bucket of a set-change preview: the exact count, and at most the sample cap of paths.</summary>
/// <param name="Count">Every file the bucket matched — exact, never truncated.</param>
/// <param name="Sample">The first paths encountered, capped by <see cref="SetChangePreviewResult.SampleLimit"/>.</param>
public sealed record ChangeBucketDescriptor(long Count, IReadOnlyList<string> Sample);

/// <summary>
/// What the source holds now versus the set's latest snapshot (ADR-0038,
/// FR-SVC-009). Deletion keeps its two honest faces apart: <paramref name="Deleted"/>
/// is a file the disk lost; <paramref name="NoLongerIncluded"/> is a file the
/// rules stopped capturing.
/// </summary>
/// <param name="SetName">The set compared.</param>
/// <param name="BaselineSnapshotId">The snapshot compared against, hex; null when the set has never backed up — everything reads as new.</param>
/// <param name="BaselineCapturedAt">When that snapshot was captured, Unix milliseconds; null with no baseline.</param>
/// <param name="Unchanged">Files identical in content and metadata.</param>
/// <param name="New">Files the last snapshot does not hold.</param>
/// <param name="Updated">Files whose content changed.</param>
/// <param name="MetadataOnly">Files whose bytes are unchanged but whose metadata moved — a chmod, an owner change.</param>
/// <param name="Moved">Files found at a different path by stable identity.</param>
/// <param name="Deleted">Files the last snapshot holds that are gone from disk and still captured by the rules.</param>
/// <param name="NoLongerIncluded">Files the last snapshot holds that the rules no longer capture — they leave future snapshots because of the rules, not the disk.</param>
/// <param name="Failures">Paths the walk could not read.</param>
/// <param name="SampleLimit">The per-bucket sample cap that was applied.</param>
public sealed record SetChangePreviewResult(
    string SetName,
    string? BaselineSnapshotId,
    ulong? BaselineCapturedAt,
    long Unchanged,
    ChangeBucketDescriptor New,
    ChangeBucketDescriptor Updated,
    ChangeBucketDescriptor MetadataOnly,
    ChangeBucketDescriptor Moved,
    ChangeBucketDescriptor Deleted,
    ChangeBucketDescriptor NoLongerIncluded,
    long Failures,
    int SampleLimit) : ServiceResult;

/// <summary>A freshly issued pairing invite — the one time the code exists in the clear.</summary>
/// <param name="Code">The code to speak to the other operator. Never persisted, never shown again.</param>
/// <param name="InviteId">The invite's identifier, for the listing and revocation.</param>
/// <param name="ExpiresAt">When it stops being redeemable, Unix milliseconds.</param>
/// <param name="ListeningEndpoint">
/// Where the peer should dial — the remote binding's endpoint, or null when
/// the binding is off, in which case <paramref name="Warning"/> says what to do.
/// </param>
/// <param name="Warning">What stands between this invite and a successful dial, when anything does.</param>
public sealed record PairingInviteResult(
    string Code, string InviteId, ulong ExpiresAt, string? ListeningEndpoint, string? Warning) : ServiceResult;

/// <summary>One pairing invite, as the listing shows it — never its code.</summary>
/// <param name="InviteId">The invite's identifier.</param>
/// <param name="Label">The label the redeemer will be recorded under.</param>
/// <param name="Role">The role committed at issue time.</param>
/// <param name="ExpiresAt">When it stops being redeemable, Unix milliseconds.</param>
/// <param name="ConsumedBy">The fingerprint that redeemed it, when one has.</param>
public sealed record PairingInviteDescriptor(
    string InviteId, string Label, string Role, ulong ExpiresAt, string? ConsumedBy);

/// <summary>The pending and consumed pairing invites.</summary>
/// <param name="Invites">The invites, oldest first.</param>
public sealed record PairingInvitesResult(IReadOnlyList<PairingInviteDescriptor> Invites) : ServiceResult;

/// <summary>A pairing that completed over an invite (ADR-0030 Amendment 3).</summary>
/// <param name="Fingerprint">The paired service's fingerprint, now pinned.</param>
/// <param name="Label">What this device calls it.</param>
/// <param name="TheirRole">The role the far side recorded for this device.</param>
/// <param name="QuotaBytes">The storage ceiling the far side granted, when it stores for us; null or 0 is no ceiling.</param>
public sealed record PairingCompletedResult(
    string Fingerprint, string Label, string TheirRole, ulong? QuotaBytes) : ServiceResult;

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
/// <param name="Destinations">
/// Where this snapshot stands at each of its set's destinations, one
/// <c>name: state</c> line per destination with the FR-SNP-003 vocabulary
/// (<c>pending</c>, <c>replicating</c>, <c>durable</c>, <c>verified</c>,
/// <c>degraded</c>); null from a service that could not derive it.
/// </param>
public sealed record SnapshotDescriptor(
    string SnapshotId,
    string BackupSetId,
    ulong CapturedAt,
    byte CaptureStatus,
    long Files,
    IReadOnlyList<string>? Destinations = null);

/// <summary>The committed snapshots.</summary>
/// <param name="Snapshots">The snapshots, oldest first.</param>
public sealed record SnapshotsResult(IReadOnlyList<SnapshotDescriptor> Snapshots) : ServiceResult;

/// <summary>One durable notice, as the ledger holds it (FR-DEST-008).</summary>
/// <param name="Id">The identifier acknowledgement acts on.</param>
/// <param name="Key">The stable machine key — one notice per (kind, subject); a re-raise refreshes the message under the same key.</param>
/// <param name="Message">The prose a person reads.</param>
/// <param name="RaisedAt">When the condition was first seen, Unix milliseconds.</param>
/// <param name="AcknowledgedAt">When a person acknowledged it, Unix milliseconds; null while it still awaits one.</param>
public sealed record NoticeDescriptor(
    string Id, string Key, string Message, ulong RaisedAt, ulong? AcknowledgedAt);

/// <summary>The notices, oldest first.</summary>
/// <param name="Notices">The listed notices.</param>
public sealed record NoticesResult(IReadOnlyList<NoticeDescriptor> Notices) : ServiceResult;

/// <summary>One entry inside a snapshot directory.</summary>
/// <param name="Name">The entry's name.</param>
/// <param name="Kind">One of <c>file</c>, <c>directory</c>, <c>symlink</c>, <c>special</c>.</param>
/// <param name="Length">The logical length, for files.</param>
/// <param name="ModifiedAt">The recorded modification time, Unix milliseconds; null for directories and rebuilt catalogues.</param>
/// <param name="Change">
/// How this entry compares with the set's previous snapshot: <c>new</c>,
/// <c>changed</c> (its recorded object differs — content, metadata, or a
/// restated manifest), or <c>same</c>. Null when the snapshot has no
/// predecessor — and always null for directories, whose recorded head mixes
/// in access times the scan itself perturbs: a folder makes no claim, and
/// its own listing answers instead.
/// </param>
public sealed record DirectoryEntryDescriptor(
    string Name, string Kind, long Length, ulong? ModifiedAt = null, string? Change = null);

/// <summary>One directory's contents.</summary>
/// <param name="Path">The directory listed.</param>
/// <param name="Entries">Its entries.</param>
/// <param name="Deleted">
/// Names present in the previous snapshot's same directory and absent here —
/// deletion is absence between snapshots (FR-SNP-002), and this is where the
/// absence is computed. Null when the snapshot has no predecessor.
/// </param>
/// <param name="PreviousSnapshotId">The snapshot the markers compare against, hex; null when there is none.</param>
public sealed record DirectoryResult(
    string Path,
    IReadOnlyList<DirectoryEntryDescriptor> Entries,
    IReadOnlyList<string>? Deleted = null,
    string? PreviousSnapshotId = null) : ServiceResult;

/// <summary>What a restore would do.</summary>
/// <param name="Files">How many files the plan covers.</param>
/// <param name="Bytes">How many logical bytes it would write.</param>
/// <param name="MissingObjects">Objects the plan needs and cannot find.</param>
/// <param name="Conflicts">
/// How many plan-time conflicts need an operator decision — case collisions
/// on a case-folding target, path-length overruns, prefixes the snapshot
/// does not hold (FR-RST-003; ADR-0041).
/// </param>
/// <param name="ConflictSample">At most twenty conflicts, as <c>path — reason</c> lines.</param>
/// <param name="Degradations">Declared metadata shortfalls on this target, one line each.</param>
public sealed record RestorePlanResult(
    long Files,
    long Bytes,
    IReadOnlyList<string> MissingObjects,
    long Conflicts = 0,
    IReadOnlyList<string>? ConflictSample = null,
    IReadOnlyList<string>? Degradations = null) : ServiceResult;

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
/// <param name="Skipped">Items recorded but not materialised on this target (declared in the plan).</param>
/// <param name="Degraded">Files restored short of what was captured — main stream only.</param>
/// <param name="Displaced">Existing files moved into the displaced store to make room.</param>
/// <param name="WrittenBeside">Restored copies written beside a kept existing file under the rename policy (ADR-0041).</param>
/// <param name="ReceiptPath">The persisted machine-readable receipt (FR-RST-004), on the service's machine.</param>
/// <param name="FailedSample">At most twenty failures, as <c>path — detail</c> lines.</param>
public sealed record RestoreResult(
    long Restored,
    long Failed,
    string OutputDirectory,
    string Outcome,
    long Skipped = 0,
    long Degraded = 0,
    long Displaced = 0,
    long WrittenBeside = 0,
    string? ReceiptPath = null,
    IReadOnlyList<string>? FailedSample = null) : ServiceResult;

/// <summary>
/// An opened restore source (ADR-0041): the handle the source-aware verbs
/// take, and the snapshots the source holds — which is everything the guided
/// flow's effective-date step needs, resolved client-side against
/// <see cref="SnapshotDescriptor.CapturedAt"/>.
/// </summary>
/// <param name="SourceId">The handle; expires after idle disuse, closed by <c>close_restore_source</c>.</param>
/// <param name="SetName">The set whose repository this is.</param>
/// <param name="Location"><c>staging</c>, or the destination's declared name.</param>
/// <param name="Snapshots">The snapshots this source holds, oldest first.</param>
/// <param name="Warnings">
/// What opening had to note — catalogue-rebuild findings, blobs that would
/// not open. A warned source still answers; the operator decides with the
/// facts in view.
/// </param>
public sealed record RestoreSourceOpenedResult(
    string SourceId,
    string SetName,
    string Location,
    IReadOnlyList<SnapshotDescriptor> Snapshots,
    IReadOnlyList<string> Warnings) : ServiceResult;

/// <summary>What a verification run found.</summary>
/// <param name="ObjectsChecked">How many objects were examined.</param>
/// <param name="Failures">How many failed.</param>
/// <param name="Level">The level that was run.</param>
public sealed record VerificationResult(long ObjectsChecked, long Failures, string Level) : ServiceResult;

/// <summary>What a health check found.</summary>
/// <param name="Findings">The findings, in the order they matter.</param>
public sealed record CheckResult(IReadOnlyList<string> Findings) : ServiceResult;

/// <summary>A retention pass's report, per set in configuration order (FR-GC-005).</summary>
/// <param name="Lines">The report — what is protected and why, what is held for a laggard, what went.</param>
public sealed record RetentionResult(IReadOnlyList<string> Lines) : ServiceResult;

/// <summary>An on-demand sync's report, one line per (set, destination) pair (FR-DEST-002/004).</summary>
/// <param name="Lines">Where each pair stands after its sync ran — read from the refreshed ledger.</param>
public sealed record SyncResult(IReadOnlyList<string> Lines) : ServiceResult;

/// <summary>
/// What a deep verification found, one line per (set, destination) pair.
/// </summary>
/// <param name="Lines">The per-pair report.</param>
/// <param name="Damaged">
/// Objects that no longer match what was sealed, across every pair. Separate
/// from the lines because an exit code must not be recovered by parsing prose.
/// </param>
public sealed record VerifyDestinationResult(IReadOnlyList<string> Lines, long Damaged) : ServiceResult;

/// <summary>One destination's row in a set's status matrix (FR-DEST-004).</summary>
/// <param name="Name">The destination's declared name.</param>
/// <param name="Kind">Its declared kind, in the configuration's spelling.</param>
/// <param name="State">Where the pair stands: <c>in-sync</c>, <c>behind</c>, <c>unavailable</c>, <c>failed</c>, or <c>not-supported</c>.</param>
/// <param name="LastSuccessAt">When it last synced, Unix milliseconds; null when never.</param>
/// <param name="Detail">What the last failure said, or null.</param>
/// <param name="FailureDomain">Where it sits relative to the source: <c>same-volume</c>, <c>same-machine</c>, <c>same-site</c>, or <c>independent</c> (FR-SNP-007).</param>
/// <param name="Verification">
/// Where it stands on proving possession (FR-VER-006): <c>proven</c>,
/// <c>stale</c> (proven once, but not for what it was last sent),
/// <c>unproven</c> (no challenge has ever succeeded), or
/// <c>unprovable (accepted)</c> (knowingly kept without proof).
/// </param>
public sealed record DestinationStatusDescriptor(
    string Name,
    string Kind,
    string State,
    ulong? LastSuccessAt,
    string? Detail,
    string FailureDomain,
    string Verification);

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
/// <param name="Notices">Durable events awaiting a human — surfaced here until acknowledged (10 §3.1).</param>
public sealed record StatusResult(
    string MachineName,
    IReadOnlyList<BackupSetStatusDescriptor> Sets,
    ulong ObservedAt,
    IReadOnlyList<string> Notices) : ServiceResult;

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
/// <param name="ArchivesRoot">
/// The root holding the per-set staging archives (ADR-0034), on the
/// service's machine. A path, not a secret — a console on the same machine
/// uses it to verify a restore passphrase locally, without the passphrase
/// ever crossing this contract (ADR-0041, NFR-SEC-009).
/// </param>
public sealed record ServiceDescriptionResult(
    string ContractVersion,
    string ServiceVersion,
    string MachineName,
    string StateDirectory,
    bool RemoteBindingEnabled,
    int ActiveJobs,
    string? ArchivesRoot = null) : ServiceResult;
