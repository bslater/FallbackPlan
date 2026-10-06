using System.Globalization;

namespace FallbackPlan.Api;

/// <summary>
/// The client↔service contract version (ADR-0028 §7).
/// </summary>
/// <remarks>
/// <para>
/// Versioned independently of the repository format and of the peer protocol.
/// ADR-0003 anticipates exactly this: repository encoding is canonical CBOR,
/// while "wire protocols are versioned independently and may use a different
/// encoding". Nothing here is durable — a contract change never touches a byte
/// already written.
/// </para>
/// <para>
/// Compatibility is by <b>major</b>. A client and service that disagree on the
/// major version must refuse to proceed with both versions named (FR-SVC-007),
/// because the failure users of a legacy backup service met was an
/// unexplained blank window. A
/// console in topology 3 routinely meets services at several versions at once,
/// so the refusal is per service and never stops the console starting.
/// </para>
/// </remarks>
/// <param name="Major">Incompatible changes.</param>
/// <param name="Minor">Additive changes an older peer can ignore.</param>
public readonly record struct ContractVersion(int Major, int Minor)
{
    /// <summary>The version this build speaks.</summary>
    /// <remarks>
    /// 1.7 added the configuration surface: set and destination CRUD, the
    /// folder browser, draft validation, and the pairing-invite verbs
    /// (ADR-0037). 1.8 added preview_set_changes / set_change_preview, made
    /// upsert_backup_set answer a material root-or-rules edit with
    /// configuration_change, and honours run_backup's full flag over the
    /// service (ADR-0038). 1.9 added the operator-loop verbs —
    /// list_notices / acknowledge_notice over the notices ledger and unpair
    /// for ending a pairing from a console — and enriched list_directory
    /// with modification times, change markers against the set's previous
    /// snapshot, and the names deleted since it (ADR-0039). 1.10 added
    /// multi-root sets (ADR-0040): roots on the set descriptor and on
    /// preview_set_changes — upsert accepts roots or root, roots winning —
    /// and the preview answers a draft with no saved set against an empty
    /// baseline. 1.11 added the guided restore (ADR-0041):
    /// open_restore_source / close_restore_source over per-set staging,
    /// replica and peer sources; source, several paths, target, existing
    /// and in-place options on the restore verbs; plan conflicts and the
    /// persisted-receipt summary on their results; and the archives root on
    /// describe_service. 1.12 added write-only repositories (ADR-0042):
    /// provision_write_only_set carrying the sealed write bundle for both the
    /// create and adopt ceremonies, the optional sealed restore-grant
    /// envelope on open_restore_source, the grant-recipient public key on
    /// describe_service, and the sealed-record count on verification results
    /// so a records-level sweep of a write-only set reads as neither damage
    /// nor a clean content check.
    /// 1.13 added first-run setup (ADR-0044): provision_installation
    /// carrying the sealed write bundle for the installation rather than for
    /// a named set — a service on its first run has no sets — and the setup
    /// state on describe_service, so a client learns it must capture the
    /// passphrase before anything else. Local callers only; a paired remote
    /// console is refused.
    /// 1.14 finished that ceremony (ADR-0044's amendment, ADR-0013's):
    /// confirm_recovery_kit records that the installation's kit was saved,
    /// describe_service gains a kit_required state between setup_required
    /// and ready plus this device's public identity for the kit to record,
    /// and validate_set_draft answers a draft's roots and destinations with
    /// a failure-domain warning (FR-SNP-007).
    /// 1.15 opened the diagnostics the engine had been writing to nobody
    /// (ADR-0043 §6, FR-SVC-010): get_diagnostics reports the levels in
    /// force and whether a durable sink exists — never where it is (T-16);
    /// read_log serves the in-memory ring by cursor, paginated because
    /// FrameCodec caps a frame at 8 MiB and "send me everything" is not a
    /// thing a log reader may ask; and set_log_level changes a level
    /// without a restart, which matters because the level a machine needs
    /// is only known once it has already misbehaved. Records cross
    /// rendered rather than as their name/value state, because rendering
    /// is where redaction happens: a local caller is served in full, a
    /// paired remote console redacted, and set_log_level is refused to a
    /// remote caller outright. describe_service carries the effective
    /// level so a console can show it without a second round trip.
    /// 1.15 also carries the recovery kit's status on describe_service
    /// (FR-KIT-005): never_saved or saved, with when it was confirmed. Two
    /// values rather than the three the requirement's wording implies —
    /// an installation kit carries no destinations, so the stated staleness
    /// trigger cannot fire, and its salt, parameters and sealing key are
    /// fixed for the installation's life, so nothing else can make it
    /// stale either (ADR-0013 as amended). Surfaced continuously rather
    /// than only during the ceremony, which is what "continuously" means.
    /// 1.16 gave the product a way to say who is acting (ADR-0045,
    /// FR-USR-001..006): login mints a session, resume_session presents an
    /// existing one on a new connection — which the web console needs,
    /// because it opens a fresh connection for every request it relays —
    /// logout revokes it, and list_users / create_user / delete_user /
    /// change_password manage the accounts. describe_service carries who is
    /// signed in and their role, and reports users_required when an
    /// installation has finished setup but has no accounts yet. Sessions are
    /// held in the service's memory alone, so a restart signs everyone out;
    /// there is no session file, which is the stale-credential failure
    /// ADR-0028 §5 rejected. A session token crosses on the two verbs that
    /// mint and present it and nowhere else, and no password or hash reaches
    /// any result or any log record.
    /// 1.17 added claim_replicas (ADR-0070), the verb a
    /// machine rebuilt from bare metal uses to get its data back. A
    /// destination serves a replica only to the identity its attribution
    /// ledger names, and that identity died with the state directory, so the
    /// passphrase proves the household instead. The command carries the
    /// Argon2id root sealed to the service's recipient key — derived on the
    /// client from the passphrase and the recovery kit's salt and parameters,
    /// because a rebuilt machine has the kit and no repository to read the
    /// salt from — and names a pairing rather than a destination, since
    /// configuration is one of the things a claim exists to recover. Null
    /// claims from every pairing. claimed_replicas answers per peer, an
    /// unreachable one reported beside the rest rather than aborting them.
    /// claim_replicas is withdrawn as of this merge and no service answers
    /// it: the property it served — the passphrase claims the replica and the
    /// device identity does not — is built instead under ADR-0053 Amendment 2,
    /// reached by the reclaim path. The number is not reused; a client that
    /// sends the verb is answered as it would be for any verb this service
    /// does not implement.
    /// 1.18 added consistency_method to snapshot_descriptor: 1 live, 2 VSS,
    /// 3 filesystem snapshot, 4 application-quiesced. The snapshot manifest
    /// has carried it since the format was written and nothing read it, while
    /// specification 06 §6 and architecture 06 §5 both said it was surfaced.
    /// Every snapshot this build takes reports 1, honestly; the value exists
    /// on the contract now so that the day a snapshot provider lands, what it
    /// records is already visible. Null from an older service, which is not
    /// the same fact as 1 — "captured live" and "would not say" are different
    /// answers to a person deciding whether to trust a restored database.
    /// 1.19 carries the backup pool's ordering (ADR-0047): backup sets and
    /// destinations gain an optional priority on their descriptors — higher
    /// runs or ships first among waiting work of the same initiation, and a
    /// person still outranks any priority. Null on an upsert preserves, so a
    /// pre-1.19 client edits nothing it cannot see. Alongside (no wire
    /// change): saving a new set queues its first backup at once, and a set
    /// gaining a destination queues that destination's seed.
    /// 1.20 carries retire_staging (ADR-0046): a migrated direct-ship set's
    /// staging archive is deleted only by this explicit verb, and only when
    /// deletion would lose nothing the live history needs (the refusal's
    /// condition as ADR-0046 Amendment 2 states it; no wire change).
    /// 1.21 puts the full-backup facts on the status matrix (ADR-0047 §§5–6):
    /// each destination row says when its baseline completed and whether the
    /// pair is still owed its seed, so a console can render "awaiting full
    /// backup" instead of a bare "behind". Additive with defaults — a
    /// pre-1.21 client simply does not see the fields.
    /// 1.22 puts the counted plan on the progress stream (FR-SVC-006's
    /// determinate half): a backup first counts what it will process, and
    /// every progress report then carries total_files and total_bytes — the
    /// denominator a client divides by for a percentage and a time estimate.
    /// Null until the count completes and from producers that never count
    /// (the single-stream path, verification sweeps, pre-1.22 services), so
    /// additive with defaults: a client seeing null falls back to the
    /// indeterminate meter it always had. The watch frame also gains the
    /// client's session token: a watch takes its own connection with its own
    /// authentication gate, and without the session every watch on an
    /// installation with accounts was anonymous — answered with an empty
    /// stream, so no progress ever reached a signed-in console. Additive the
    /// same way: a pre-1.22 service ignores the field, a pre-1.22 client
    /// keeps its anonymous watch.
    /// 1.23 adds restart_service (ADR-0049): an in-process recycle of the
    /// running service — Owner-only, local callers only, refused before
    /// setup and under --once. The acknowledgement is flushed before the
    /// teardown, and the restart signs every session out (the FR-USR-003
    /// contract, unchanged).
    /// 1.24 is the completed-run record and its drill-down (ADR-0050,
    /// FR-SVC-018), plus the carried behind-reason (ADR-0027 §4). The job
    /// row gains the run's terminal numbers — files_seen/done/reused/failed,
    /// bytes_seen/stored, total_files/total_bytes — all nullable with null
    /// defaults, so an old service's rows and a pre-1.24 journal's rows read
    /// as "not recorded", never as zeroes. list_jobs gains an optional limit
    /// (null keeps the ask-for-everything meaning; the journal outgrows a
    /// frame eventually). Two new read verbs serve the details on demand:
    /// job_changes (the run's snapshot against its predecessor — exact
    /// counts, bounded samples) and job_failures (the error manifest's
    /// paths and typed reasons). The live feed gains current_file — the one
    /// path being processed, to exactly the authenticated audience
    /// list_directory already shows every path to; null from older
    /// services. And the status matrix stops summarising the
    /// most common degradation away: destination rows gain a machine cause
    /// (reason) beside the prose, and the set row gains last_completed_at —
    /// the operand the behind-demotion compares against — both additive with
    /// null defaults.
    /// 1.25 surfaces the storage shape (ADR-0046): the set descriptor
    /// gains direct_ship — null preserves, the semantics every additive
    /// field on this surface shares, so a pre-1.25 client's upsert cannot
    /// silently convert a set between staging and direct-ship. An explicit
    /// value sets it: a direct-ship set must reference at least one
    /// local-path destination (the sink does not serve peers yet), the
    /// change is refused while the set has a live run, and a staging set
    /// flipped on migrates at its next open in the same process — no
    /// service restart — with staging retained as a read-only seed source
    /// until retire_staging.
    /// 1.26 puts a completion figure on each destination row: held bytes,
    /// owed bytes, and when they were counted. Counted by the sync pass,
    /// which lists both sides anyway, rather than by the status poll, which
    /// would have to list a whole replica to answer. Owed is by the
    /// destination's OWN retention policy, so a narrow override reads
    /// complete when it holds its own keep-set. Additive with defaults, and
    /// the timestamp is what separates "holds none of it" from "nobody has
    /// counted" — a client without it must not draw an empty gauge.
    /// 1.27 adds the restore drill's answer to each destination row: when a
    /// drill last brought a sampled file back out of that destination's own
    /// replica, how many files it restored, and why it could not when it
    /// could not (ADR-0054). Three states a client must keep apart — never
    /// drilled (drilled_at null), drilled and passed (a stamp, no failure),
    /// and drilled and failed (a stamp AND a failure) — because the first
    /// and the third both mean "this has not been shown to work" while only
    /// the third means something is wrong. The failure is separate from the
    /// sync state on purpose: a destination can hold every byte it was sent,
    /// prove possession of them, and still not restore. Additive with
    /// defaults.
    /// 1.28 adds `reclaim_grant` to the retention command (ADR-0055 §6): a
    /// collection run's authority to author deletions on a write-only set —
    /// the derived reclaim sub-root, sealed end-to-end to this service's
    /// recipient key and rendered as hex, the same permitted shape under
    /// NFR-SEC-009 as 1.x's restore grant. Null is correct for a dry run,
    /// which authors nothing (and was, until format 1 went, for a set whose
    /// service derived the key itself). A write-only set applying without one
    /// is refused by name rather than falling back to the key it publishes
    /// with. Additive:
    /// a pre-1.28 client's retention command still parses, and still reports.
    /// 1.29 adds `drill_limit` to each destination row (ADR-0054 Amendment
    /// 2): what a passing drill could not prove. A write-only set's replica
    /// seals its content to a key the service does not hold, so its drill
    /// proves the road back as far as the sealed content — the replica
    /// opens, its index and catalogue rebuild, every sampled file's manifest
    /// and segment records are found — and states that limit instead of
    /// reporting the passphrase's absence as damage. A limit rides beside a
    /// null failure: it is a pass, and a client must not render it as a
    /// failure. Additive with a default: a pre-1.29 client reads such a row
    /// as a plain pass, which overstates by exactly the limit it cannot see.
    /// 1.30 puts the installation's public derivation parameters on
    /// describe_service — the Argon2id salt and parameters, and the sealing
    /// public key — so a client holding the passphrase can derive the
    /// restore grant a set-up installation's restore needs (ADR-0042 §5)
    /// without holding the archive: the paired console's ceremony, now
    /// possible from the CLI, locally and over the remote binding. Every
    /// value is public by construction (each archive's descriptor records the
    /// same three facts) and null until setup has run. Additive with
    /// defaults.
    /// 1.31 withdraws the recovery kit (ADR-0060): `confirm_recovery_kit`
    /// is gone, `describe_service` no longer carries `kit_status` or
    /// `kit_confirmed_at`, and `setup_state` is two-valued again —
    /// `setup_required` or `ready`. A minor with removals, admitted under
    /// the pre-release rule stated at the top of these remarks: the only
    /// clients are this repository's, a client reads a missing `kit_status`
    /// exactly as it read one from a pre-1.15 service, and a client that
    /// still knows `kit_required` treats it as an unfinished ceremony. A
    /// 2.0 would protect a client nobody has.
    /// 1.32 adds adoption of a destination's archives (ADR-0061):
    /// `discover_archives` lists what a declared destination holds by
    /// descriptor alone — repository id, format, creation facts, the public
    /// derivation parameters and sealing key, snapshot count and highest
    /// publication counter, which configured set already owns it, whether
    /// this installation wrote it — and `adopt_archive` takes one back under
    /// its original repository and set ids with the same sealed provisioning
    /// envelope `provision_write_only_set` carries, derived against the
    /// discovered archive's salt; the set is re-declared from the shape the
    /// archive records, overridable field by field. The set descriptor gains
    /// the archive's own public derivation parameters and sealing key, so a
    /// client derives a restore grant per set: an adopted set keeps the salt
    /// its archive was born under, which is not the installation's. Additive.
    /// 1.33 adds the operator's re-attribution (ADR-0053 §3):
    /// `list_replica_attributions` answers every replica stored here with
    /// its owner's fingerprint and label and whether a claim key is on
    /// record — never the key — and `reattribute_replica` points one at a
    /// different paired device, for the replica attributed before the claim
    /// key existed by a machine that died before publishing one. Owner-only
    /// and local callers only, like `restart_service`; refused by name for a
    /// replica its owner can claim with the passphrase. Additive.
    /// 1.34 adds the verification tiers to each destination row:
    /// `verified_sealed` and `verified_digest` say how many of the proved
    /// objects a record's AEAD tag proved and how many the signed
    /// whole-blob digest proved — the latter being the proof a write-only
    /// set's data plane has, its records being sealed to a key the service
    /// does not hold. Additive with zero defaults; a pre-1.34 client reads
    /// the row as before, with the coverage it always had.
    /// 1.35 adds `list_receipts` (ADR-0063, ADR-0064): every receipt filed
    /// under the state directory — deletion and replication, the ones this
    /// device signed as a destination and the ones it verified as a
    /// commander — answered as `receipts_listed` rows of facts newest
    /// first: kind, role, the service's verdict on the signature over the
    /// bytes on disk now, the signer's fingerprint, the set and destination
    /// the commander filed under, the repository, issue time and session
    /// prefix, and the kind's counts. No path and no signed or key bytes
    /// cross. Narrowed by kind, set, repository and count. Any signed-in
    /// role, any caller scope: it is an audit listing of what a peer already
    /// said under its own signature. Additive.
    /// <para>
    /// 1.36 adds `verified_chunk` to each destination row (ADR-0065): of the
    /// objects the last passed verification proved, how many were proved by
    /// asking the destination for one leaf of the blob's Merkle commitment
    /// and its authentication path, checked against the root the writer
    /// signed into the index. It is a <b>sampled</b> proof of the blob and
    /// is counted apart from `verified_digest`, which reads every byte, so
    /// that the cheaper tier cannot be rendered as the stronger one.
    /// Additive with a zero default; a pre-1.36 client reads the row as it
    /// did and a pre-1.36 service answers zero, which is true of it.
    /// </para>
    /// <para>
    /// 1.37 adds `total` to `receipts_listed`: how many receipts are on file
    /// for the kind and repository asked for, counted from names rather than
    /// from what was read, so `limit` can bound the reading and a client can
    /// still say what share of the pile it is showing. Peer receipts are now
    /// swept under a stated retention rule (NFR-OPS-008), and a count beside
    /// the rows is what makes a bound that is working visible. The count
    /// precedes the set filter, which can only be answered by reading a
    /// receipt, so a listing narrowed by set may return fewer rows than its
    /// limit while the total is larger than both. Additive with a zero
    /// default; a pre-1.37 client ignores it and a pre-1.37 service answers
    /// zero, which reads as "this service does not count", not as "nothing
    /// is on file".
    /// </para>
    /// <para>
    /// 1.38 adds `upgrade_set_format` (ADR-0066): one set's repository is
    /// moved to the latest format this build writes, by appending a signed
    /// format-upgrade record rather than by rewriting the descriptor — which
    /// no destination would ever accept, since each seeds a descriptor only
    /// if absent and commits an object it lacks while keeping the one it
    /// has. It answers `configuration_change`, so no result shape moves. It
    /// takes no version: the service upgrades to the one version it writes,
    /// so a client cannot ask for a format this build could not read back.
    /// Refused by name for a set already at that version and for a set with
    /// no archive yet, which is born at the latest format anyway. Note that
    /// `discover_archives` reads descriptors without a credential, so it
    /// cannot verify an upgrade record and goes on reporting the version
    /// each archive was **created** at.
    /// </para>
    /// <para>
    /// 1.39 puts the background window's state on `get_status` (ADR-0069):
    /// the configured text, whether background work may start right now, and
    /// when that next changes. The window is the first of NFR-PERF-013's four
    /// named limits to exist, and it can hold every backup on an installation
    /// for hours; before this a person could only find out by reading the
    /// service's log, which is not where "why did nothing run last night" gets
    /// asked. One nullable descriptor rather than three loose fields, so a
    /// client tests "is there a window" once. Null from a service with no
    /// window configured AND from one older than 1.39 — deliberately the same
    /// answer, because a client does nothing different in the two cases and an
    /// absent window has always meant always open. Reporting only: the window
    /// is edited in the configuration file, as `max_concurrent_backups` is,
    /// and a console control for it is owed rather than smuggled in behind a
    /// status field. The state is evaluated at the instant `observed_at` names,
    /// from the same parsed window the scheduler's pass uses, so a client
    /// cannot catch the two disagreeing across a boundary.
    /// </para>
    /// <para>
    /// 1.40 adds `retention` to `archive_adopted` (FR-DR-006): the set's own
    /// retention policy as the adopted set is now configured, taken from the
    /// archive's newest policy manifest, which records it since this version.
    /// Adoption re-declares a set that will delete by that policy, so the
    /// answer says what it is before anything runs under it. The same
    /// descriptor the set listing carries, null when the set defers retention.
    /// A destination's override is not in it and cannot be: it names the
    /// destination, and the repository carries no destination identity
    /// (FR-DEST-006). Additive with a null default. A pre-1.40 client ignores
    /// the field, and a pre-1.40 service never sends it, which a client reads
    /// as "the archive recorded none", as it could not have.
    /// </para>
    /// <para>
    /// 1.41 adds `acknowledge_replica_claim` and `claim_awaiting_acknowledgement`
    /// on each `replica_attributions` row (FR-DR-005). A claim that moves a
    /// replica stored here is held: the claimant reads it at once, and its
    /// retention instructions are refused, deleting nothing, until this
    /// machine's owner acknowledges the claim with the new command. The
    /// command is owner-only and local, like `reattribute_replica` beside it.
    /// The flag is additive with a false default. A pre-1.41 client ignores
    /// it, and a pre-1.41 service never sends it, which is true of a service
    /// that never held a claim.
    /// </para>
    /// <para>
    /// 1.42 adds `preview_adoption` and its answer, `adoption_preview`
    /// (FR-DR-009). A recovered configuration takes effect only as a person was
    /// shown it. The preview takes the same sealed envelope adoption takes,
    /// proves it the same way, and writes nothing. It answers with:
    /// - the shape the archive recorded, and each root's recorded path as a
    ///   hint flagged `resolves` or not on this machine;
    /// - the set's own retention, which the set would delete by;
    /// - a `confirmation`, a digest of all of it.
    ///
    /// `adopt_archive` gains `confirmation` and requires it. Without one it
    /// is refused before any envelope is opened. With one the archive no
    /// longer matches, because it gained a snapshot or a different recorded
    /// shape since the preview, it is refused as changed and leaves nothing
    /// behind.
    ///
    /// The refusal is the one part that is not additive. A pre-1.42 client
    /// that adopts in one call is refused, by name, rather than adopting a
    /// set nobody was shown: that one-call adoption is the behaviour FR-DR-009
    /// exists to end. The console and the CLI ship with the service and preview
    /// first.
    /// </para>
    /// <para>
    /// 1.43 adds `background_limits` to `status` (NFR-PERF-013, ADR-0074): the
    /// byte rates background work is held to, beside the window 1.39 reports.
    /// `read_limit` is the rate background captures read their sources at, and
    /// `transfer_limits` lists each limited destination with its rate, as the
    /// configured `text` and as `bytes_per_second`. Reporting only: both are
    /// edited in the configuration file, and a person's work is never held to
    /// either. Additive with a null default. A pre-1.43 service never sends it,
    /// which a client reads as "nothing limited" — what a 1.43 service with no
    /// limit configured says too.
    /// </para>
    /// <para>
    /// 1.44 lets the settings 1.39 and 1.43 report be set through the service
    /// (ADR-0037 Amendment 1), which ADR-0069 §8 and ADR-0074 §7 named as
    /// owed. `get_service_settings` answers `service_settings`: the background
    /// window, the background read limit and `max_concurrent_backups` as the
    /// configuration file states them, with `effective_max_concurrent_backups`
    /// — the width the running pool has, since the pool is sized when the
    /// service starts. `update_service_settings` changes them: null keeps a
    /// setting, an empty text or a zero width clears it, a value the parser
    /// refuses refuses the whole request, and the answer is a
    /// `configuration_change` saying when each change applies. The destination
    /// descriptor gains `transfer_limit` and `drill_interval_days`, both ways,
    /// under the same rule — null keeps, empty or zero clears. Additive: a
    /// pre-1.44 client's upsert carries neither, and keeps both.
    /// </para>
    /// <para>
    /// 1.45 adds `read_around` and `read_around_sample` to `restore`
    /// (FR-RST-007). A restore of a set's own archive that meets a copy it
    /// cannot use reads the record from the set's next copy, and the answer
    /// counts the files that came from another copy because a copy passed
    /// over was damaged or would not read, with up to twenty lines naming the
    /// copy each came from and what was wrong with those passed over. A file
    /// read from a destination only because staging no longer holds it is not
    /// counted. Additive with defaults: a pre-1.45 service sends neither,
    /// which a client reads as nothing read around, and that is what a
    /// pre-1.45 service did.
    /// </para>
    /// <para>
    /// 1.46 adds `deep_sweep` to each destination row of `status` (ADR-0035
    /// Amendment 3, FR-VER-003). It holds when a circuit of the deep sweep
    /// last closed (`circuit_closed_at`), the one fact that supports "every
    /// stored object was read back and matched its seal". Beside it are
    /// `read_this_circuit` and `last_read_at` for the circuit under way, and
    /// `stalls` and `stalled_on` for one stopped at a blob that will not read.
    /// `interval_days` is the cadence the scheduler keeps, null for a peer
    /// read in full only when a person asks. The object is null where no
    /// sweep exists — a kind nothing reads back in full, a destination no
    /// longer declared. Additive with a null default: a pre-1.46 service sends
    /// none, which a client reads as no sweep reported and draws nothing for,
    /// never as a sweep that has not run.
    /// </para>
    /// <para>
    /// 1.47 adds `observed_clock_skew_ms` to each snapshot descriptor of
    /// `list_snapshots` and `open_restore_source` (ADR-0077, NFR-TIME-002):
    /// how far the capturing machine's clock stood from a peer's when the
    /// snapshot was taken, as its manifest records it — the peer's clock minus
    /// the capturing one's, in milliseconds, so positive is a clock that was
    /// behind. Additive with a null default: a capture with no reading and a
    /// pre-1.47 service both send none, which a client reads as nothing to
    /// report, never as a clock in step.
    /// </para>
    /// <para>
    /// 1.48 adds `implausible_capture_time` to each snapshot descriptor of
    /// `list_snapshots` (ADR-0078, FR-GC-012). It says whether the snapshot's
    /// capture time is out of step with the order its writer published it in,
    /// by more than the configured clock skew margin, and which way: `behind`
    /// is dated before snapshots published ahead of it, and `ahead` is dated
    /// after snapshots published after it. Retention keeps such a snapshot and
    /// never expires it, and the retention report says so on its keep line.
    /// Additive with a null default: a capture that fits, a restore source
    /// (which does not judge) and a pre-1.48 service all send none, which a
    /// client reads as nothing to report.
    /// </para>
    /// <para>
    /// 1.49 adds `set_id` to `validate_set_draft` (ADR-0037 Amendment 2,
    /// FR-DEST-017): the set the draft edits, or the one it would create. With
    /// it, the answer names the placement refusal (ADR-0051) the save would
    /// meet, judged as the save judges it: every local destination of a set
    /// the configuration does not hold, and for one it does, the destinations
    /// the draft newly references, or all of them when its roots change.
    /// Additive with a null default: a pre-1.49 client names no set, and its
    /// draft is not judged for placement, because a standing binding the save
    /// would leave alone cannot be told from a new one.
    /// </para>
    /// <para>
    /// 1.50 adds `reclaim_grants` to `retention` (ADR-0055 Amendment 3,
    /// FR-GC-008): a reclaim grant per set, keyed by set id, because a set
    /// adopted from a destination keeps the salt it was born under and the
    /// installation's grant is not its authority. A set the map leaves out
    /// falls back to `reclaim_grant`; with neither, it is reported and not
    /// applied, and the report says why. A command with no map is refused for
    /// want of a grant exactly as before. Every grant is proved against the
    /// reclaim public key its archive's credential carries before the run
    /// authors anything, so a wrong one is refused even on an archive with no
    /// tombstone yet. Additive with a null default: a pre-1.50 client sends
    /// one grant or none, and is answered as before.
    /// </para>
    /// <para>
    /// 1.51 adds `delete_snapshots` (FR-GC-013, ADR-0080): a person deletes
    /// snapshots of one set from staging and every copy. A dry run says what
    /// would go and where it is held; an apply, under the set's reclaim grant,
    /// requests the deletion, converges every copy it can reach, and takes the
    /// snapshots and what only they held out of staging, answering
    /// `snapshots_deleted` with each snapshot's state. It refuses an id the set
    /// does not hold and a request that would leave the set no complete
    /// snapshot. Who asked is not on the wire: the connection's gate supplies
    /// it from the session. `list_snapshots` gains `deletion_pending`, the
    /// copies a requested snapshot still waits on. Additive: a pre-1.51 client
    /// never sends the command and reads the listing as before.
    /// </para>
    /// <para>
    /// 1.52 adds `export_diagnostics` (NFR-PRIV-003, ADR-0081): the service
    /// builds one diagnostic bundle — its log, versions, configuration,
    /// status, open notices and recent jobs, as a zip — and answers
    /// `diagnostic_bundle` with the bytes, a suggested file name, whether
    /// paths are in it, and how many log records it carries and left out.
    /// Paths are a per-bundle opt-in, `include_paths`, which a paired console
    /// may not set. `read_log` withholds from a paired caller an exception's
    /// message and every value no type declares safe, where it had passed
    /// them as written. Additive: a pre-1.52 client never sends the command,
    /// and a paired one reads the withheld text as `(withheld)`.
    /// </para>
    /// <para>
    /// 1.53 makes a restore say whether it fits and what it will not write
    /// back (FR-RST-003, FR-RST-004, ADR-0083). `plan_restore` takes the
    /// run's shape, `output_directory`, `target`, `existing` and `in_place`,
    /// and with a folder or the original location named, `restore_plan`
    /// answers `space`: each volume the run would write to, with the bytes it
    /// needs there, what is free, and whether it is only the engine's working
    /// directory. A volume short of room is also a conflict. `restore_plan`
    /// gains `write_bytes`, which is the logical bytes less a sparse file's
    /// holes, and its degradations now count each captured attribute the
    /// target will not get back. `run_restore` refuses a run that will not
    /// fit before writing anything, unless told `ignore_free_space`, and
    /// `restore` gains `not_applied`, a line an attribute. Additive: a
    /// pre-1.53 client names no folder and is planned as before. It never
    /// sends `ignore_free_space`, so its restore that will not fit is
    /// refused, as it should be.
    /// </para>
    /// <para>
    /// 1.54 makes a backup's percentage what it has backed up (FR-SVC-006,
    /// ADR-0088). `JobProgress` gains `bytes_backed_up`: the plan's bytes the
    /// store has acknowledged at every destination the run writes to, and
    /// those it already held, which a client divides by `total_bytes` in
    /// place of files read. A run backs up its whole plan before its snapshot
    /// is published, so a client shows 99% until the job settles. While the
    /// run finishes, `hints_total` and `hints_written` count the
    /// source-identity hints it writes after its last content byte. The job
    /// row gains `bytes_backed_up` too: how far a failed or cancelled run
    /// got. Additive: a pre-1.54 service sends none of them, and a client
    /// that sees null divides by files as before.
    /// </para>
    /// </remarks>
    public static ContractVersion Current { get; } = new(1, 54);

    /// <summary>Whether a peer at <paramref name="other"/> can be spoken to.</summary>
    /// <param name="other">The peer's version.</param>
    /// <returns><see langword="true"/> when the major versions match.</returns>
    public bool IsCompatibleWith(ContractVersion other) => Major == other.Major;

    /// <summary>Renders as <c>major.minor</c>.</summary>
    /// <returns>The rendered version.</returns>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}");

    /// <summary>Parses a <c>major.minor</c> rendering.</summary>
    /// <param name="text">The text to parse.</param>
    /// <param name="version">The parsed version.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> was well formed.</returns>
    public static bool TryParse(string? text, out ContractVersion version)
    {
        version = default;
        if (text is null)
        {
            return false;
        }

        var separator = text.IndexOf('.', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        if (!int.TryParse(text[..separator], CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(text[(separator + 1)..], CultureInfo.InvariantCulture, out var minor))
        {
            return false;
        }

        version = new ContractVersion(major, minor);
        return true;
    }

    /// <summary>
    /// The refusal message for an incompatible peer — it names both versions,
    /// because "cannot connect" is what makes this failure infamous.
    /// </summary>
    /// <param name="clientVersion">The client's version.</param>
    /// <param name="serviceVersion">The service's version.</param>
    /// <returns>The message to show.</returns>
    public static string DescribeMismatch(ContractVersion clientVersion, ContractVersion serviceVersion) =>
        $"The client speaks contract {clientVersion} and the service speaks {serviceVersion}. "
        + "These are incompatible; upgrade whichever is older rather than retrying.";
}
