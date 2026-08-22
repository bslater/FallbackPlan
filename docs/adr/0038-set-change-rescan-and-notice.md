# ADR-0038 — Set changes rescanned: the preview verb, the after-edit rescan, and the notice a backup resolves

**Status:** Accepted
**Date:** 2026-08
**Requirements:** FR-SVC-009, FR-SNP-002, FR-SVC-001, FR-SVC-006
**Related:** [ADR-0037](0037-configuration-over-the-command-contract.md), [ADR-0028](0028-service-boundary-and-deployment-topologies.md), [ADR-0029](0029-pipeline-and-service-concurrency.md), [ADR-0024](0024-include-exclude-rule-dialect.md), [ADR-0027](0027-services-scheduling-status-telemetry.md), [architecture 06 §4](../architecture/06-filesystem-capture.md#4-change-detection)

---

## Context

ADR-0037 gave the contract a configuration lifecycle, and left an honest gap
behind it: editing a set's root or rules was a validated atomic file write and
nothing more. The change *did* take effect — configuration is re-read every
pass, and every backup fully re-walks the source, so a rule change cannot be
missed — but silently. No rescan ran, nothing anywhere compared two snapshots
or a snapshot against the disk (deletion is implicit by absence, FR-SNP-002,
and nothing computed the absence), and the operator who just excluded a
subtree, or moved a root, learned what that meant only by reading the next
snapshot's listing by hand.

Two smaller facts sharpened the decision. The capture pipeline already
classifies every file each backup — reused, renamed, restated, captured,
failed — and throws the classification away (only a telemetry counter sees
it). And `run_backup`'s `full` flag was **silently dropped by the service**:
the handler never read it, so `--full` over the service or the web console
behaved as an ordinary incremental, while direct CLI mode honoured it — two
answers to one flag.

## Decision

1. **Contract 1.7 → 1.8, additive.** One new verb, `preview_set_changes`:
   walk the set's source now — under its saved root and rules or a draft's —
   and diff it against the set's latest snapshot. Counts are always exact;
   each bucket carries at most a capped sample of paths (default 20, cap
   200), because the result crosses the contract. Paths on the contract have
   precedent (`list_directory`, `browse_folders`); FR-SVC-005 bars content,
   not names.
2. **The classification is the publication's own.** The comparer
   (`Repository/SourceComparer.cs`) reuses the tree publisher's unchanged
   predicates verbatim (extracted, not copied), walks with the same scanner
   under the same compiled rules, and applies includes at the same NFC
   spelling — so what the preview reports and what the next backup decides
   are one judgement, not two implementations drifting apart.
3. **Deletion keeps its two honest faces apart.** A baseline file absent from
   the walk is `deleted` only while the rules would still capture it; a file
   the new rules stopped capturing is `no_longer_included`, even when it also
   left the disk — under the new configuration its absence is not a loss the
   next backup would see. A filter edit flagged as deletion would be a false
   alarm; a deletion flagged as a filter edit would be a missed one.
4. **A material edit rescans on its own.** When `upsert_backup_set` changes
   an existing set's root or rules, the answer is a `configuration_change`
   naming what changed, and — when the set has a last backup to compare
   with — a fire-and-forget rescan is queued on the reader lane. Its finding
   lands as **one durable notice per set** (`set-changed:{id}`, counts only),
   refreshed by later edits rather than piled, and **resolved by the next
   backup that completes for the set** — the moment the new settings are
   actually captured. Schedule and retention edits change when and how long,
   not what, and stay a plain acknowledgement.
5. **`full` means full everywhere.** The flag is plumbed through the
   scheduler to the runner, which empties the parent list and the incremental
   baseline exactly as direct mode always did.
6. **Progress stays counts-only.** The per-job progress channel's privacy
   posture (no path ever rides it) is untouched: paths cross only as a
   command result the caller asked for; the automatic notice carries counts.
7. **The console never applies a material edit on one click.** Saving a
   root or rules change is two steps: the editor first runs the comparison
   and shows what the edit means — with an explicit warning, and a
   danger-styled Apply, when files the last backup holds would stop being
   included — and only the Apply in that step performs the upsert; Back
   returns to the editor with the draft intact. A comparison that fails
   (an unmounted new root, say) says so and still allows Apply, because
   refusing would make the honest case impossible. The gate is the
   client's: the contract stays one verb, so a script that knows what it
   is doing is not made to say so twice.

## Consequences

**Positive** — a configuration edit now answers with its meaning; the
flagged consequences of a filter change are computed by the engine that will
enforce it; the web editor previews a draft's rules against the real disk
before saving; `fallbackplan changes` answers "what changed since the last
backup" even when nothing was reconfigured; and the `full` discrepancy is
gone.

**Negative** — the preview is a full source walk on the single-worker reader
lane, so it queues behind a long restore and is priced like one; the baseline
loads one snapshot's leaf rows into memory (the same order the catalogue
itself holds); and three costs are recorded rather than fixed: a file
excluded in one snapshot and re-included later has no prior row to inherit
from and is captured as new with severed ancestry (manifests are immutable);
a root change reads as everything-deleted plus everything-new, which is the
honest answer by path; and a hand-edit of `config.json` bypasses the upsert
hook entirely — the accepted hand-edit posture, since the next backup applies
it regardless.

**Neutral** — the notice raised is prose in the existing store, surfaced by
`status`, the `notices` verb and the web console with no changes to any of
them; and a 1.7 client that upserts a material edit receives a
`configuration_change` it already knows how to decode.

## Alternatives considered

- **Diff inside the backup itself and report afterwards.** No new walk — but
  the answer arrives only after the next backup has already committed under
  the new rules, which is exactly too late for "is this edit right?".
- **Debounced live preview in the editor.** The 350 ms `validate_set_draft`
  pattern extended to the rescan. Rejected: a full tree walk per keystroke;
  the preview is a deliberate button instead.
- **Structured notices (typed payloads with per-file lists).** The notice
  record deliberately has no type or payload; growing one for this would
  re-litigate the store's shape for a finding the preview verb already
  answers better on demand.
- **Comparing policy-manifest hashes at pass time to catch hand-edits.** The
  snapshot already records its policy manifest, so drift detection is
  possible — but the pass applying the current configuration is the
  behaviour hand-editors rely on, and a notice about every hand-edit is
  noise nobody asked for. Recorded as the accepted gap.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | Written with the include-rule enforcement fresh (ADR-0037) and the full-flag discrepancy verified against both gateways |
| 2026-08 | Accepted | Built: contract 1.8, the comparer beside the publisher's own predicates, the after-edit rescan and its notice, the CLI `changes` verb and the web editor's preview |
