# Command contract — the client↔service surface

**Status:** register · **Authority:** the code — see below · **Current version:** 1.22

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
  not see fields it predates. A console managing several services degrades
  per service rather than refusing to start.
- Refusals are a typed `error` result with a reason code; the message is for
  people and explicitly not for parsing.
- Who may call what: the local binding is authenticated by the operating
  system; the remote binding by pinned pairing; person-identity rides inside
  either as a session ([ADR-0045](../../docs/adr/0045-client-authentication.md)).
  Some verbs are local-only (`set_log_level`, `provision_installation`) and
  say so when refused.

## Verbs, by area

The register as of 1.22 — 51 commands. One line each; parameters, results
and refusal semantics live with the records in `Commands.cs`/`Results.cs`.

**Service, setup and sessions** — `describe_service` (version, machine,
setup/kit/sign-in state), `provision_installation` + `confirm_recovery_kit`
(the first-run ceremony, ADR-0044), `provision_write_only_set` (ADR-0042),
`login` / `resume_session` / `logout`, `list_users` / `create_user` /
`delete_user` / `change_password` (ADR-0045).

**Configuration** — `list_backup_sets` / `upsert_backup_set` /
`delete_backup_set`, `list_destinations` / `upsert_destination` /
`delete_destination`, `browse_folders`, `validate_set_draft`,
`preview_set_changes`, `export_configuration` (ADR-0037/0038/0040). Since
1.17 a new set's upsert answers with its queued first backup.

**Backups and jobs** — `run_backup`, `cancel_job`, `list_jobs` (since
1.22 with the run's terminal numbers on each row and an optional newest-N
bound), `job_changes` / `job_failures` (since 1.22 — one run's diff against
its predecessor and its capture failures, read from the repository on
demand), `get_status` (the per-set, per-destination matrix — since 1.19
with each destination's baseline facts, since 1.22 with each demotion's
machine cause and the set's `last_completed_at`).

**Snapshots and restore** — `list_snapshots`, `list_directory`,
`plan_restore` / `run_restore`, `open_restore_source` /
`close_restore_source` (ADR-0041).

**Destinations at work** — `sync`, `verify_destination`, `verify`, `check`,
`retention`, `retire_staging` (1.18, ADR-0046).

**Pairing and peers** — `list_pairings`, `create_pairing_invite` /
`list_pairing_invites` / `revoke_pairing_invite` / `pair_with_invite`,
`unpair` (ADR-0030/0039).

**Notices and diagnostics** — `list_notices` / `acknowledge_notice`
(ADR-0039), `get_diagnostics` / `read_log` / `set_log_level` (ADR-0043).

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
| 1.17 | The backup pool's ordering: optional priority on set and destination descriptors, null-preserving on upsert; first-backup-on-save and gained-destination seeding beside it ([ADR-0047](../../docs/adr/0047-backup-pool-and-priorities.md)) |
| 1.18 | `retire_staging`: a migrated direct-ship set's staging archive deleted only by this explicit verb, refused while anything it holds has not reached a destination ([ADR-0046](../../docs/adr/0046-direct-to-destination-publication.md)) |
| 1.19 | The full-backup facts on the status matrix: each destination row says when its baseline completed and whether the pair is owed its seed — additive with defaults, invisible to a pre-1.19 client ([ADR-0047](../../docs/adr/0047-backup-pool-and-priorities.md) §§5–6) |
| 1.20 | The counted plan on the progress stream: a backup counts its work before archiving and every progress report then carries `total_files` and `total_bytes` — null until the count completes and from producers that never count, so additive with defaults; a pre-1.20 client keeps its indeterminate meter. The watch frame also carries the client's session token, so a signed-in console's event stream is authenticated — before this, every watch on an installation with accounts was answered with an empty stream ([ADR-0048](../../docs/adr/0048-determinate-backup-progress.md)) |
| 1.21 | `restart_service`: an in-process recycle of the running service — Owner-only, local callers only, refused before setup and under `--once`; the acknowledgement is flushed before teardown and the restart signs every session out ([ADR-0049](../../docs/adr/0049-service-lifecycle-hygiene.md)) |
| 1.22 | The completed-run record and drill-down: the job row carries the run's terminal numbers (nullable, additive — a pre-1.22 row reads "not recorded", never zero) and `list_jobs` takes an optional newest-N bound; `job_changes` and `job_failures` answer one run's diff and failure listing from the repository with exact counts and bounded samples; the progress stream names the `current_file` being processed; and the status matrix carries each demotion's `reason` plus the set's `last_completed_at` — all additive with null defaults ([ADR-0050](../../docs/adr/0050-completed-run-record-and-drill-down.md)) |
