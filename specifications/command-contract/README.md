# Command contract — the client↔service surface

**Status:** register · **Authority:** the code — see below · **Current version:** 1.60

---

## Authority

This document is the human-readable register of the command contract: the
verbs a client may send a FallbackPlan service, the results it can be
answered with, and the version history of both. It mirrors the
[repository-format authority rule](../repository-format/README.md) with the
direction reversed: the repository format's specification is normative and
the code follows it, whereas the command contract is **defined by the code**
— pre-1.0, the wire truth is `FallbackPlan.Api` (`Commands.cs`'s
discriminator register, `Results.cs`, `ContractVersion.cs`) — and this
document follows it. Where they disagree, the code wins and this document is
wrong. Each version's entry in `ContractVersion.cs`'s remarks is the
authoritative changelog; the history below transcribes it.

The contract is versioned independently of the repository format and of the
peer protocol ([ADR-0003](../../docs/adr/0003-canonical-metadata-encoding.md)
anticipates exactly this). Nothing in it is durable — a contract change never
touches a byte already written.

## Shape and compatibility

- Commands and results are JSON objects discriminated by a `command` /
  `result` property (System.Text.Json polymorphism over the registers in
  `Commands.cs` and `Results.cs`), carried over the local socket or named
  pipe — and, when the remote binding is enabled, to paired clients
  ([ADR-0028](../../docs/adr/0028-service-boundary-and-deployment-topologies.md)).
- **Compatibility is by major version.** A client and service that disagree
  on the major must refuse to proceed with **both versions named**
  (FR-SVC-007); minor versions are additive, and an older peer simply does
  not see fields it predates. 1.42 is the stated exception: it refuses, by
  name, the one-call adoption an older client sends, because that call is
  what FR-DR-009 ends. A console managing several services degrades
  per service rather than refusing to start.
- Refusals are a typed `error` result with a reason code; the message is for
  people and explicitly not for parsing.
- Who may call what: the local binding is authenticated by the operating
  system; the remote binding by pinned pairing; person-identity rides inside
  either as a session ([ADR-0045](../../docs/adr/0045-client-authentication.md)).
  Some verbs are local-only (`set_log_level`, `provision_installation`,
  `restart_service`, `list_replica_attributions`, `reattribute_replica`,
  `acknowledge_replica_claim`) and say so when refused.

## Verbs, by area

The register as of 1.60 — 65 commands. One line each; parameters, results
and refusal semantics live with the records in `Commands.cs`/`Results.cs`.

**Service, setup and sessions** — `describe_service` (version, machine,
setup/sign-in state, the installation's public derivation parameters),
`provision_installation` (the first-run ceremony, ADR-0044; the passphrase
is the whole of it, ADR-0060), `provision_write_only_set` (ADR-0042),
`discover_archives` / `adopt_archive` (since 1.32 — what a declared
destination holds, by descriptor alone, and taking one archive back under
its original ids with the passphrase; ADR-0061; discovery holds no
credential, so the `format_version` it reports is the version each archive
was **created** at — an upgrade record is signed, and verifying a signature
needs a key discovery does not have; since 1.40 the adoption answer carries
the set's own retention as the archive recorded it, FR-DR-006),
`preview_adoption` (1.42, FR-DR-009 — what adopting would declare, each root
flagged where it does not resolve here, answered with the confirmation
`adopt_archive` now requires),
`login` / `resume_session` / `logout`, `list_users` / `create_user` /
`delete_user` / `change_password` (ADR-0045).

**Configuration** — `list_backup_sets` / `upsert_backup_set` /
`delete_backup_set`, `list_destinations` / `upsert_destination` /
`delete_destination`, `browse_folders`, `validate_set_draft`,
`preview_set_changes`, `export_configuration` (ADR-0037/0038/0040). Since
1.19 a new set's upsert answers with its queued first backup, and a material
edit of an existing set now queues a backup under the new settings too, or
one to follow the run still capturing under the earlier settings, its answer
naming the job either way (ADR-0038 Amendment 2, no wire change); since 1.25
the set descriptor carries `direct_ship` (null preserves — a pre-1.25
client cannot convert a set; an explicit value sets the storage shape,
refused mid-run and without a local-path destination, and a new local-path
set defaults to direct-ship). Since 1.44, `get_service_settings` /
`update_service_settings` read and set the installation's own settings — the
background window, the background read limit and `max_concurrent_backups`
(ADR-0037 Amendment 1, FR-SVC-021) — and the destination descriptor carries
`transfer_limit` and `drill_interval_days`. On every one of them null keeps
the stored value, and an empty text or a zero clears it. Since 1.49,
`validate_set_draft` takes `set_id`, the set the draft edits or would create,
and with it names each placement refusal the save would give (ADR-0037
Amendment 2, ADR-0051, FR-DEST-017). A draft that names no set is not judged
for placement. Since 1.60 the destination descriptor addresses an `s3`
destination — `bucket`, `region`, `prefix`, `addressing`, with `endpoint` the
store's base URL — and says whether the service holds its access key
(`access_key_stored`); `set_destination_credentials` hands the service that
key, its `access_key_id` in clear and the secret only as an `envelope` sealed
to the service's recipient key for that destination and key id
([ADR-0091](../../docs/adr/0091-an-s3-compatible-destination.md),
NFR-SEC-009). Nothing answers the key back.

**Backups and jobs** — `run_backup`, `cancel_job`, `list_jobs` (since
1.24 with the run's terminal numbers on each row and an optional newest-N
bound), `job_changes` / `job_failures` (since 1.24 — one run's diff against
its predecessor and its capture failures, read from the repository on
demand; since 1.56 only through a `source` of the run's set unlocked with
the passphrase), `get_status` (the per-set, per-destination matrix — since 1.21
with each destination's baseline facts, since 1.24 with each demotion's
machine cause and the set's `last_completed_at`, since 1.39 with the
background window's state, since 1.43 with the byte-rate limits background
work is held to).

**Snapshots and restore** — `list_snapshots`, `list_directory`,
`plan_restore` / `run_restore`, `open_restore_source` /
`close_restore_source` (ADR-0041). Since 1.56 a person's `open_restore_source`
carries a restore grant or is refused, and `list_directory`, `plan_restore`
and `run_restore` read only through a source so opened, held by the caller's
session ([ADR-0089](../../docs/adr/0089-a-backups-file-names-need-the-passphrase.md));
`list_snapshots` names no file and needs none. Since 1.53, `plan_restore` takes the run's
shape (`output_directory`, `target`, `existing`, `in_place`) and answers the
room the run needs on each volume it writes to (`space`) and what its files
write (`write_bytes`); `run_restore` refuses a run that will not fit before
writing anything unless told `ignore_free_space`, and `restore` says which
captured metadata was not applied (`not_applied`) ([ADR-0083](../../docs/adr/0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md)).
Since 1.45, a `run_restore` of a set's
own archive that read some files from another copy, because a copy passed
over was damaged or would not read, says how many (`read_around`) and which
(`read_around_sample`) ([ADR-0075](../../docs/adr/0075-a-restore-reads-around-damage.md)).
Since 1.47, each snapshot of `list_snapshots` and `open_restore_source` says
how far the capturing machine's clock stood from a peer's
(`observed_clock_skew_ms`, [ADR-0077](../../docs/adr/0077-observed-clock-skew.md)).
Since 1.48, each snapshot of `list_snapshots` says whether its capture time
fits the order its writer published it in (`implausible_capture_time`,
`behind` or `ahead`, [ADR-0078](../../docs/adr/0078-implausible-capture-times.md)).
Retention keeps such a snapshot, and `open_restore_source` does not judge.
The two answer in opposite orders. `list_snapshots` gives each set's snapshots
newest first, the sets in configuration order. `open_restore_source` gives a
source's oldest first, because the guided restore reads it as a timeline.
Since 1.51, `delete_snapshots` deletes named snapshots of one set from staging
and every copy, under that set's reclaim grant, and answers
`snapshots_deleted` with each one's state; a dry run needs no grant
([ADR-0080](../../docs/adr/0080-a-person-deletes-a-snapshot.md)). Each
snapshot of `list_snapshots` a person asked to delete carries
`deletion_pending`, the destinations the deletion still waits on.

**Destinations at work** — `sync`, `verify_destination`, `verify`, `check`,
`retention`, `retire_staging` (1.20, ADR-0046), `upgrade_set_format`
(1.38, [ADR-0066](../../docs/adr/0066-the-format-upgrade-record.md) — one set moved to the latest repository format this
build writes, by an appended signed record rather than a rewritten
descriptor), `run_drill` (1.58, [ADR-0054](../../docs/adr/0054-scheduled-restore-drills.md)
Amendment 6 — a destination's restore drill now, outside its cadence,
recorded and announced as the schedule's is).

**Pairing and peers** — `list_pairings`, `create_pairing_invite` /
`list_pairing_invites` / `revoke_pairing_invite` / `pair_with_invite`,
`unpair` (ADR-0030/0039); `list_replica_attributions` /
`reattribute_replica` (1.33, ADR-0053 §3 — the operator's view of the
replicas stored here, and the override for one the passphrase cannot claim);
`acknowledge_replica_claim` (1.41, FR-DR-005 — the operator accepting a
claim that moved a replica here, which until then may read it and may not
delete from it).

**Notices and diagnostics** — `list_notices` / `acknowledge_notice`
(ADR-0039), `notice_names` (1.59, [ADR-0089](../../docs/adr/0089-a-backups-file-names-need-the-passphrase.md)
Amendment 1 — the files a notice's words leave out, only through a source
its set's passphrase unlocked), `get_diagnostics` / `read_log` / `set_log_level` (ADR-0043);
`list_receipts` (1.35, ADR-0063/0064 — every deletion and replication
receipt filed here, both roles, as facts with the service's verdict on each
signature; any signed-in role, any caller scope); `export_diagnostics`
(1.52, [ADR-0081](../../docs/adr/0081-diagnostic-bundle.md) — one diagnostic
bundle, as base64 bytes the client saves, rendered as a record leaving the
machine is; plaintext paths only by `include_paths`, which a paired console
may not set).

## Version history

Transcribed from `ContractVersion.cs`; the code's remarks are authoritative.
Versions before 1.7 built the initial surface (jobs, snapshots, restore,
verification, status) and predate the per-version changelog convention.

| Version | Carries |
|---------|---------|
| 1.7 | The configuration surface: set and destination CRUD, the folder browser, draft validation, pairing-invite verbs ([ADR-0037](../../docs/adr/0037-configuration-over-the-command-contract.md)) |
| 1.8 | `preview_set_changes`; a material set edit answers `configuration_change`; `run_backup`'s full flag honoured over the service ([ADR-0038](../../docs/adr/0038-set-change-rescan-and-notice.md)) |
| 1.9 | The operator loop: `list_notices` / `acknowledge_notice`, `unpair`; `list_directory` enriched with times, change markers and deletions ([ADR-0039](../../docs/adr/0039-console-operator-loop.md)) |
| 1.10 | Multi-root sets: roots on the set descriptor and the preview ([ADR-0040](../../docs/adr/0040-multi-root-backup-sets.md)) |
| 1.11 | The guided restore: restore sources over staging, replica and peer; plan conflicts; the receipt summary ([ADR-0041](../../docs/adr/0041-guided-restore-and-peer-retrieval.md)) |
| 1.12 | Write-only repositories: `provision_write_only_set`, the sealed restore-grant envelope, the grant-recipient key ([ADR-0042](../../docs/adr/0042-write-only-repositories.md)) |
| 1.13 | First-run setup: `provision_installation`, setup state on `describe_service`; local callers only ([ADR-0044](../../docs/adr/0044-first-run-setup.md)) |
| 1.14 | The ceremony finished: `confirm_recovery_kit`, the `kit_required` state, draft failure-domain warnings |
| 1.15 | Diagnostics opened: `get_diagnostics` / `read_log` / `set_log_level`, redaction at the rendering boundary; kit status on `describe_service` ([ADR-0043](../../docs/adr/0043-structured-logging-and-diagnostics.md)) |
| 1.16 | Who is acting: `login` / `resume_session` / `logout` and the user-management verbs; sessions in service memory only ([ADR-0045](../../docs/adr/0045-client-authentication.md)) |
| 1.17 | `claim_replicas` ([ADR-0070](../../docs/adr/0070-replica-claim-after-total-loss.md)): a machine rebuilt from bare metal claims its replicas by the passphrase rather than by the device identity that died with its state directory. **Withdrawn** by the merge that brought it in: no service answers it, the property it served is built under [ADR-0053](../../docs/adr/0053-peer-claim-and-configuration-recovery.md) Amendment 2 through the reclaim path, and the number is not reused |
| 1.18 | `consistency_method` on `snapshot_descriptor`: 1 live, 2 VSS, 3 filesystem snapshot, 4 application-quiesced. The snapshot manifest has carried it since the format was written; every snapshot this build takes reports 1, and null from an older service is not the same fact as 1 |
| 1.19 | The backup pool's ordering: optional priority on set and destination descriptors, null-preserving on upsert; first-backup-on-save and gained-destination seeding beside it ([ADR-0047](../../docs/adr/0047-backup-pool-and-priorities.md)) |
| 1.20 | `retire_staging`: a migrated direct-ship set's staging archive deleted only by this explicit verb, refused while anything it holds has not reached a destination ([ADR-0046](../../docs/adr/0046-direct-to-destination-publication.md)) |
| 1.21 | The full-backup facts on the status matrix: each destination row says when its baseline completed and whether the pair is owed its seed — additive with defaults, invisible to a pre-1.21 client ([ADR-0047](../../docs/adr/0047-backup-pool-and-priorities.md) §§5–6) |
| 1.22 | The counted plan on the progress stream: a backup counts its work before archiving and every progress report then carries `total_files` and `total_bytes` — null until the count completes and from producers that never count, so additive with defaults; a pre-1.22 client keeps its indeterminate meter. The watch frame also carries the client's session token, so a signed-in console's event stream is authenticated — before this, every watch on an installation with accounts was answered with an empty stream ([ADR-0048](../../docs/adr/0048-determinate-backup-progress.md)) |
| 1.23 | `restart_service`: an in-process recycle of the running service — Owner-only, local callers only, refused before setup and under `--once`; the acknowledgement is flushed before teardown and the restart signs every session out ([ADR-0049](../../docs/adr/0049-service-lifecycle-hygiene.md)) |
| 1.24 | The completed-run record and drill-down: the job row carries the run's terminal numbers (nullable, additive — a pre-1.24 row reads "not recorded", never zero) and `list_jobs` takes an optional newest-N bound; `job_changes` and `job_failures` answer one run's diff and failure listing from the repository with exact counts and bounded samples; the progress stream names the `current_file` being processed; and the status matrix carries each demotion's `reason` plus the set's `last_completed_at` — all additive with null defaults ([ADR-0050](../../docs/adr/0050-completed-run-record-and-drill-down.md)) |
| 1.25 | The storage shape surfaced (ADR-0046): `direct_ship` on the set descriptor with null-preserve semantics; a direct-ship set must reference a local-path destination, a shape change is refused while a run is live and takes effect in-process with its seeding catch-up queued at once, and a new local-path set defaults to direct-ship |
| 1.26 | A completion figure on each destination row: `held_bytes`, `owed_bytes` and `measured_at`, counted by the sync pass rather than by the status poll. Owed is by the destination's own retention policy, so a narrow override reads complete when it holds its own keep-set. Additive with defaults; `measured_at` is what separates "holds none of it" from "nobody has counted", and a client without it must not draw an empty gauge |
| 1.27 | The restore drill's answer on each destination row: `drilled_at`, `drill_files` and `drill_failure` — when a drill last brought a sampled file back out of that destination's own replica, how many it restored, and why it could not when it could not. **Three states a client must keep apart:** never drilled (`drilled_at` absent), drilled and passed (a stamp, no failure), drilled and failed (a stamp **and** a failure). The first and the third both mean the destination has not been shown to restore, and only the third means something is wrong. The failure is deliberately not the destination's sync state: a destination may hold every byte it was sent and prove possession of them and still fail to restore. Additive with defaults |
| 1.28 | `reclaim_grant` on `retention`: a collection run's authority to author deletions on a **write-only** set — the derived reclaim sub-root, sealed end-to-end to the service's recipient key and rendered as hex, the same permitted shape under NFR-SEC-009 as the restore grant. Null is correct for every v1 set, which derives the key it already holds, and for a dry run, which authors nothing. A write-only set applying without one is **refused by name**, never fallen back to the publication key. The service proves the grant against a tombstone the repository already holds before it authors anything, so a grant from another passphrase — or a restore grant sent in its place — is caught before it writes ([ADR-0055](../../docs/adr/0055-reclaim-authority.md)) |
| 1.29 | `drill_limit` on each destination row: what a passing restore drill could not prove, in the drill's own words ([ADR-0054 Amendment 2](../../docs/adr/0054-scheduled-restore-drills.md)). A **write-only** set's replica seals its content to a key the service does not hold, so the scheduled drill proves the road back as far as the sealed content — the replica opens, its index and catalogue rebuild, every sampled file's manifest and segment records are found — and states that limit rather than reporting the passphrase's absence as damage. A limit rides beside a null `drill_failure`: it is a **pass with a stated limit**, and a client must not render it as a failure; `drill_files` counts the files proved that far. A content drill is the recovery tool with the passphrase. Additive with a default; a pre-1.29 client reads such a row as a plain pass |
| 1.30 | The installation's public derivation parameters on `describe_service`: `kdf_salt`, `kdf_memory_kib`, `kdf_iterations`, `kdf_parallelism` and `sealing_public_key` — every one public by construction (each archive's descriptor records the same facts), null until first-run setup has run. What lets a client holding the passphrase derive the restore grant a set-up installation's restore needs ([ADR-0042 §5](../../docs/adr/0042-write-only-repositories.md)) without holding the archive: the CLI's `restore`, locally and over `--connect`, derives it from `--passphrase-env`, proves it against `sealing_public_key` before sending anything, opens a restore source under it and restores through that source. Additive with defaults |
| 1.31 | The recovery kit withdrawn (ADR-0060): `confirm_recovery_kit` is gone, `describe_service` no longer carries `kit_status` or `kit_confirmed_at`, and `setup_state` is two-valued again — `setup_required` or `ready`. A minor with removals, admitted under the pre-release rule: the only clients are this repository's, a client reads a missing `kit_status` exactly as it read one from a pre-1.15 service, and a client that still knows `kit_required` treats it as an unfinished ceremony |
| 1.32 | Adopting a destination's archives (ADR-0061): `discover_archives` lists what a declared destination holds by descriptor alone — `repository_id`, `format_version`, `created_at`, `created_by`, the public `kdf_salt` / `kdf_memory_kib` / `kdf_iterations` / `kdf_parallelism` and `sealing_public_key`, `snapshot_objects`, `highest_publication_sequence`, `owned_by_set` and `same_installation` — with no credential involved; `adopt_archive` takes one back under its original repository id and set id with the same sealed provisioning envelope `provision_write_only_set` carries, derived against the **discovered** archive's salt, and answers `archive_adopted`: the set as re-declared from the shape the archive records (`roots`, `set_name`, `schedule`, rules), each overridable on the command, `missing_roots` reported rather than refused, `writer_identity_resumed`, `already_adopted`. The set descriptor gains the archive's own `kdf_salt`, costs and `sealing_public_key`, so a client derives a restore grant per set — an adopted set keeps the salt its archive was born under. Additive with defaults |
| 1.33 | The operator's re-attribution ([ADR-0053 §3](../../docs/adr/0053-peer-claim-and-configuration-recovery.md)): `list_replica_attributions` answers every replica stored here as `replica_attributions` — `repository_id`, `owner_fingerprint`, `owner_label` and `claimable`, which says whether a claim key is on record and never carries the key — and `reattribute_replica {repository_id, fingerprint}` points one at a different paired device, answering `configuration_change`. Owner-only and local callers only, like `restart_service`; a fingerprint prefix resolves as `unpair`'s does; refused by name for a device paired only as a destination we store at, and for a replica its owner can claim with the passphrase — the override exists only for a replica recorded before the claim key was published. Additive |
| 1.34 | The verification tiers on each destination row: `verified_sealed` and `verified_digest` say how many of the objects the last passed verification proved were proved by opening a record's AEAD tag at the destination and how many by hashing the whole sealed blob there against the digest the writer signed into the index ([07 §2.2](../repository-format/07-index.md)) — the latter being the only proof a **write-only** set's data plane has, its records being sealed to a key the service does not hold (FR-WOR-003). The digest tier reads whole blobs and is budgeted per pass; a blob above the budget is never provable by digest. Additive with zero defaults; a pre-1.34 client reads the coverage it always had |
| 1.35 | `list_receipts` ([ADR-0063](../../docs/adr/0063-deletion-receipts.md), [ADR-0064](../../docs/adr/0064-replication-receipts.md)): every receipt filed under the state directory — deletion and replication, the ones this device signed as a destination and the ones it verified as a commander — answered as `receipts_listed` rows of facts newest first: `kind`, `role`, `filed_at`, `status` (`verified` / `signature-invalid` / `unreadable`), `verified`, `problem`, `signer_fingerprint`, `set`, `destination`, `repository_id`, `issued_at`, `session_prefix`, and the kind's counts (`deleted_count` / `not_held`, `committed_count` / `held_objects` / `held_bytes`). No path and no signed or key bytes cross. Narrowed by `kind`, `set`, `repository` and `limit`. Any signed-in role, any caller scope: an audit listing of what a peer already said under its own signature. Additive |
| 1.36 | `verified_chunk` on each destination row ([07 §3.6](../peer-protocol/07-retrieval.md#36-merkle_challenge-278--merkle_proof-279)): of the objects the last passed verification proved, how many were proved by asking the destination for one leaf of the blob's Merkle commitment and its authentication path, checked against the root the writer signed into the index ([07 §2.3](../repository-format/07-index.md#23-covered-blob-merkle-roots)). A **sampled** proof of the blob, counted apart from `verified_digest` — which reads every byte — so the cheaper tier cannot be rendered as the stronger one. Additive with a zero default |
| 1.37 | `total` on `receipts_listed` ([ADR-0063](../../docs/adr/0063-deletion-receipts.md), [ADR-0064](../../docs/adr/0064-replication-receipts.md)): how many receipts are on file for the `kind` and `repository` asked for, counted from file names rather than from what was read — so `limit` now bounds the **reading** as well as the answer, and a client can still say what share of the pile it is showing. Peer receipts are swept under a stated retention rule (NFR-OPS-008), and a count beside the rows is what makes a bound that is working visible. The count precedes the `set` filter, which can only be answered by reading a receipt, so a listing narrowed by set may return fewer rows than its limit while the total stands above both. Additive with a zero default |
| 1.38 | `upgrade_set_format {set_name}` ([ADR-0066](../../docs/adr/0066-the-format-upgrade-record.md)): one set's repository moved to the latest format this build writes, answering `configuration_change` so no result shape moves. The move is an **appended signed record**, not a rewritten descriptor: a destination seeds a descriptor only if absent and a peer keeps the copy it has, so a rewrite would carry the source alone and leave every copy claiming the older format over newer blobs. It takes no version — the service upgrades to the one version it writes, so a client cannot ask for a format this build could not read back. Refused by name for a set already at that version, for a set with no archive yet (one created here is born at the latest format), and while a run holds the set. What it changes is what the set **seals next**: everything already sealed stays exactly as it is, and the record reaches each destination on the next reconciling pass. Additive |
| 1.39 | `background_window` on `status` ([ADR-0069](../../docs/adr/0069-the-background-window.md)): the configured window, whether background work may start right now, and when that next changes. The window is the first of NFR-PERF-013's four named limits to exist and it can hold every backup on an installation for hours; before this the only way to find out was the service's log, which is not where "why did nothing run last night" gets asked. One nullable descriptor rather than three loose fields, so a client tests "is there a window" once. Null from a service with no window configured **and** from one older than 1.39 — deliberately the same answer, because a client does nothing different in the two cases and an absent window has always meant always open. Reporting only: the window is edited in the configuration file, as `max_concurrent_backups` is, and a console control for it is owed. The state is evaluated at the instant `observed_at` names, from the same parsed window the scheduler's pass uses, so a client cannot catch the two disagreeing across a boundary. Additive |
| 1.40 | `retention` on `archive_adopted` ([ADR-0061](../../docs/adr/0061-adopt-a-destinations-archives.md) Amendment 1, FR-DR-006): the set's own retention policy as the adopted set is now configured, taken from the archive's newest policy manifest, which records it from this version on. The same descriptor the set listing carries, null when the set defers retention; a destination's override is never in it, because it names the destination (FR-DEST-006). Additive with a null default: a pre-1.40 service never sends it, which a client reads as "the archive recorded none" |
| 1.41 | `acknowledge_replica_claim {repository_id}` and `claim_awaiting_acknowledgement` on each `replica_attributions` row (FR-DR-005, [peer-protocol 06 §3](../peer-protocol/06-retention.md#3-what-the-spoke-validates)): a claim that moves a replica stored here is held — the claimant reads it at once, and its retention instructions are refused, deleting nothing, until this machine's owner acknowledges the claim. Owner-only and local callers only, like `reattribute_replica`; a replica with nothing held answers `configuration_change` saying nothing changed. The flag is additive with a false default, which a pre-1.41 service, never having held a claim, would have sent had it known the field. |
| 1.42 | `preview_adoption {destination_name, repository_id, envelope}` and its answer `adoption_preview` ([ADR-0061](../../docs/adr/0061-adopt-a-destinations-archives.md) Amendment 2, FR-DR-009): a recovered configuration takes effect only as a person was shown it. The preview takes the envelope adoption takes, proves it the same way and writes nothing. It answers with the recorded `set_id` and `set_name`; each root as `recorded_path`, `label` and `resolves` on this machine; `schedule`, the rules and `retention`, the set's own policy it would delete by; `snapshot_count` and the newest snapshot; `already_adopted`; the service's `lines`; and a `confirmation`, a digest of all of it. `adopt_archive` gains `confirmation` and requires it. Without one it is refused before any envelope is opened, naming the preview. With one the archive no longer matches, because it gained a snapshot or a different recorded shape, it is refused as changed and leaves nothing behind. **Not additive**, deliberately: a pre-1.42 client that adopts in one call is refused by name rather than adopting a set nobody was shown, and the console and the CLI ship with the service and preview first |
| 1.43 | `background_limits` on `status` ([ADR-0074](../../docs/adr/0074-background-byte-rate-limits.md), NFR-PERF-013): the byte rates background work is held to, beside the window — `read_limit`, the rate background captures read their sources at, and `transfer_limits`, each limited destination by `destination_name`, each as the configured `text` and as `bytes_per_second`. Reporting only: both are edited in the configuration file, and a person's work is never held to either. Additive with a null default: a pre-1.43 service never sends it, which a client reads as "nothing limited" — what a 1.43 service with no limit configured says too |
| 1.44 | `get_service_settings` / `update_service_settings` and `transfer_limit` / `drill_interval_days` on the destination descriptor ([ADR-0037](../../docs/adr/0037-configuration-over-the-command-contract.md) Amendment 1, FR-SVC-021): the settings 1.39 and 1.43 report, settable through the service as ADR-0069 §8 and ADR-0074 §7 named as owed. Null keeps a setting, an empty text or a zero clears it, a refused value refuses the request whole and names the setting, never the configuration file's path. `service_settings` carries `effective_max_concurrent_backups`, the width the running pool has — the pool is sized when the service starts, so a width change applies at the next restart. Additive: two verbs, one result and two optional descriptor fields, and a pre-1.44 client's upsert carries neither field and so keeps both |
| 1.45 | `read_around` and `read_around_sample` on `restore` ([ADR-0075](../../docs/adr/0075-a-restore-reads-around-damage.md), FR-RST-007): a restore of a set's own archive reads a record its own store will not serve from the set's other copies, and the answer counts the files that came from another copy because a copy passed over was damaged or would not read, with up to twenty lines naming the copy each came from and what was wrong with those passed over. A file read from a destination only because staging no longer holds it is not counted. Additive with defaults: a pre-1.45 service sends neither, which reads as nothing read around |
| 1.46 | `deep_sweep` on each destination row of `status` ([ADR-0035 Amendment 3](../../docs/adr/0035-destination-fitness.md#amendment-3-2026-09--a-circuit-is-reported-where-the-status-is), FR-VER-003): the destination's deep sweep as the ledger holds it. `circuit_closed_at` is when a circuit last closed — every stored blob read back and matched to its seal, the one fact that supports that claim; `read_this_circuit` and `last_read_at` describe the circuit under way, a count of blobs and never a share of the replica; `stalls` and `stalled_on` say a circuit stopped at a blob that will not read, or at a replica that could not be read; `interval_days` is the cadence the scheduler keeps, **null** for a peer read in full only when a person asks. The object is null where no sweep exists — a reserved kind nothing reads back, a destination no longer declared — which is not a sweep that has not run. Additive with a null default: a pre-1.46 service sends none, which reads as no sweep reported |
| 1.47 | `observed_clock_skew_ms` on each snapshot of `list_snapshots` and `open_restore_source` ([ADR-0077](../../docs/adr/0077-observed-clock-skew.md), NFR-TIME-002): how far the capturing machine's clock stood from a peer's when the snapshot was taken, as its manifest records it — the peer's clock minus the capturing one's, in milliseconds, so positive is a clock that was behind. Null where the capture had no reading, which is not a clock in step. Additive with a null default: a pre-1.47 service sends none, which reads as nothing to report |
| 1.48 | `implausible_capture_time` on each snapshot of `list_snapshots` ([ADR-0078](../../docs/adr/0078-implausible-capture-times.md), FR-GC-012): whether the snapshot's capture time is out of step with the order its writer published it in, by more than the configured clock skew margin, and which way — `behind` is dated before snapshots published ahead of it, `ahead` after snapshots published after it. Retention keeps such a snapshot and never expires it, and its keep line in the `retention` report says why. Additive with a null default: a capture that fits, a restore source (which does not judge) and a pre-1.48 service send none, which reads as nothing to report |
| 1.49 | `set_id` on `validate_set_draft` ([ADR-0037 Amendment 2](../../docs/adr/0037-configuration-over-the-command-contract.md#amendment-2-2026-10--a-new-set-is-made-in-steps-and-its-draft-is-judged-for-placement), FR-DEST-017, FR-SVC-022): the set the draft edits, or the id a new one will be created under. With it, the answer names each placement refusal ([ADR-0051](../../docs/adr/0051-local-destination-placement.md)) the save would give, in the save's words, judged as the save judges it: every local destination of a set the configuration does not hold, and for one it does, the destinations newly referenced, or all of them when its roots change. Additive with a null default: a pre-1.49 client names no set, and its draft is not judged for placement, because a standing binding the save would leave alone cannot be told from a new one |
| 1.50 | `reclaim_grants` on `retention` ([ADR-0055 Amendment 3](../../docs/adr/0055-reclaim-authority.md#amendment-3-2026-10--a-grant-per-set-from-every-client-proved-against-the-archives-key), FR-GC-008): a reclaim grant per set, keyed by set id, each in `reclaim_grant`'s sealed shape, because a set adopted from a destination keeps the salt it was born under and the installation's grant is not its authority. A set the map names is collected under its own entry, and one it leaves out falls back to `reclaim_grant`. With neither, the set is reported and not applied, and the report says so. A command with no map is refused for want of a grant exactly as before. Every grant is now proved against the reclaim public key its archive's credential carries, before any set runs, so a wrong one is refused by name even on an archive with no tombstone yet. Additive with a null default: a pre-1.50 client sends one grant or none, and is answered as before |
| 1.51 | `delete_snapshots`, answered by `snapshots_deleted` ([ADR-0080](../../docs/adr/0080-a-person-deletes-a-snapshot.md), FR-GC-013): a person deletes named snapshots of one set from staging and every copy. Without `apply` it is a dry run that needs no grant and says where each snapshot is held. With it, under the set's `reclaim_grant`, the service requests the deletion, converges every copy it can reach and takes the snapshots, and what only they held, out of staging, answering each snapshot `deleted` or `pending` with the destinations it awaits. An id the set does not hold, and a request that would leave the set nothing to restore from, are refused before anything is written. Who asked is not on the wire: the connection's gate supplies it from the session. `list_snapshots` gains `deletion_pending` on each snapshot a person asked to delete. Additive: a pre-1.51 client never sends the command, and reads the listing as before |
| 1.52 | `export_diagnostics`, answered by `diagnostic_bundle` ([ADR-0081](../../docs/adr/0081-diagnostic-bundle.md), NFR-PRIV-003): the service builds one diagnostic bundle — its log, versions and environment, configuration, status, open notices and recent jobs, as a zip — and answers with `content_base64`, a suggested `file_name`, `includes_paths`, the `entries` and how many log records it carries and left out (`log_records`, `log_records_left_out`; the bundle is kept under 5 MiB so its base64 clears the frame). Every field is rendered as a record leaving the machine is: credentials, keys and recovery material never, identifiers shortened, paths and text no type classifies withheld. `include_paths` is the per-bundle opt-in to plaintext paths; a paired console asking for it is refused. The service writes no file. `read_log` now withholds from a paired caller an exception's message and every value no type declares safe, as `(withheld)`, where it had passed them as written. Additive: a pre-1.52 client never sends the command |
| 1.53 | The restore says whether it fits and what it will not write back ([ADR-0083](../../docs/adr/0083-a-restore-says-whether-it-fits-and-what-it-will-not-write-back.md), FR-RST-003, FR-RST-004). `plan_restore` takes the run's shape — `output_directory`, `target`, `existing`, `in_place`, as `run_restore` takes them — and, with a folder or the original location named, `restore_plan` answers `space`: each volume the run would write to, with `directory`, `needed_bytes`, `available_bytes` (null where the platform will not say, never short) and `working` for the engine's working directory standing alone. A volume short of room is also a conflict line. `restore_plan` gains `write_bytes`, the logical bytes less a sparse file's holes, and its `degradations` now count each captured attribute the target will not get back, naming root or CAP_CHOWN for ownership. `run_restore` refuses a run that will not fit before writing anything, naming the space needed, the space free and where, unless `ignore_free_space` is true; and `restore` gains `not_applied`, one line an attribute with how many items it was left off. Additive: a pre-1.53 client names no folder and is planned as before; it never sends `ignore_free_space`, so its run that will not fit is refused |
| 1.54 | A backup's percentage is what it has backed up ([ADR-0088](../../docs/adr/0088-a-backups-percentage-is-what-it-has-backed-up.md), FR-SVC-006). Each progress report carries `bytes_backed_up`: the plan's bytes the store has acknowledged at every destination the run writes to, and those it already held — what a client divides by `total_bytes` in place of files read. A run backs up its whole plan before its snapshot is published, so a client shows 99% until the job settles. While the run finishes, `hints_total` and `hints_written` count the source-identity hints it writes after its last content byte. The job row gains `bytes_backed_up`, how far a failed or cancelled run got. Additive: a pre-1.54 service sends none of them, and a client that sees null divides by files as before |
| 1.55 | Two figures, kept apart ([ADR-0088](../../docs/adr/0088-a-backups-percentage-is-what-it-has-backed-up.md) Amendment 1, FR-SVC-006, FR-DEST-004). A job's progress is three equal stages over its counted plan's files — scanned, processed, and backed up, which `JobProgress` and the job row now count as `files_backed_up`. How much of a backup a destination holds is a status row's: `files_held` and `files_total`, of the set's newest backup's files, how many have all their content there; `holds_newest`, whether it holds that backup whole, snapshot record included, the only thing that lets a client draw 100%; `in_run`, whether the set's live run writes to it; and `syncing`, whether a sync to it is under way, its count then being that sync's own. Additive: a pre-1.55 service sends none of them, and a client reads null `files_held` as not counted, never as none |
| 1.56 | A backup's file names need the passphrase ([ADR-0089](../../docs/adr/0089-a-backups-file-names-need-the-passphrase.md), FR-WOR-007). The proof is a restore source opened under a verified restore grant, serving only the session that opened it. `job_changes`, `job_failures` and `preview_set_changes` gain `source` to name one; `preview_set_changes` answers without one, counting deleted and no-longer-included files and leaving their names out, and says so in `names_withheld`. Not additive, as 1.42 was not: `open_restore_source` without an `envelope`, and `list_directory`, `plan_restore`, `run_restore`, `job_changes` and `job_failures` without such a source — or with another session's, or another set's — are refused by name. The console and the CLI ship with the service and unlock first. The session a command came from is the connection's gate's to say, never on the wire |
| 1.57 | A set with no snapshot yet gives its destinations nothing to hold ([ADR-0050](../../docs/adr/0050-completed-run-record-and-drill-down.md) Amendment 2, FR-DEST-004). A destination row's `reason` may be `awaiting-first-backup`, on a row that reads `behind`, whatever the ledger row says, unless the ledger reported a fault in its own words. Additive: a pre-1.57 client that does not know the value shows the row's `detail`, which says the same in words |
| 1.58 | `run_drill` ([ADR-0054](../../docs/adr/0054-scheduled-restore-drills.md) Amendment 6, FR-DRL-003): a person runs a destination's restore drill now, outside its cadence. It names its pair as `sync` does, `backup_set_name` and `destination_name`, and either left out means every one. The answer, `drill`, carries a line per pair and two counts, `failed` and `not_drilled`, so an exit code is never read from the prose. The drill is recorded on the pair's row and raises or clears its notice as a scheduled one does; a pair already being drilled is joined, not drilled twice; a caller who stops waiting is answered cancelled and the drill finishes. A pair with nothing there to restore is said and not drilled. Additive: a pre-1.58 client never sends the command |
| 1.59 | A notice counts, and its names need the passphrase ([ADR-0089](../../docs/adr/0089-a-backups-file-names-need-the-passphrase.md) Amendment 1, FR-WOR-007). The notices the service raises about a drill or about damage, a drill's failure on the status matrix's `drill_failure` and in a `drill` answer, and verify-destination's lines count the backup's files they concern and name none. A listed notice gains `names_withheld`, how many files its message left out, and `set_id`, the set whose passphrase names them. `notice_names`, answered by `notice_names`, gives those files through `source`, a restore source of that set the caller's session opened under a verified grant, and is refused without one in the same words as the other looks; a notice that left nothing out answers none. Additive: a pre-1.59 service sends neither field and has no names to give, and its notices keep the names in their words |
| 1.60 | An S3-compatible destination ([ADR-0091](../../docs/adr/0091-an-s3-compatible-destination.md), FR-DEST-005). `DestinationDescriptor` gains `bucket`, `region`, `prefix` and `addressing`, its `endpoint` is the store's base URL for an `s3` destination, and `access_key_stored` says whether the service holds that destination's access key. New verb `set_destination_credentials` (destination name, `access_key_id`, `envelope`): the secret crosses only sealed to the service's recipient key, bound to the destination and key id, joining the envelope verbs by decision (NFR-SEC-009); the service holds it in its state directory and never answers it back. Additive: a pre-1.60 client sends none of the fields, and its upsert of another kind is read as before. |
