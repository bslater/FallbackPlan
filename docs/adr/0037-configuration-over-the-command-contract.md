# ADR-0037 — Configuration over the command contract: sets, destinations, selection and the folder browser

**Status:** Accepted
**Date:** 2026-08
**Requirements:** FR-SVC-001, FR-DEST-001, FR-DEST-007, FR-GC-001, FR-GC-010, NFR-OPS-003
**Related:** [ADR-0028](0028-service-boundary-and-deployment-topologies.md), [ADR-0034](0034-hub-and-spoke-destinations.md), [ADR-0035](0035-destination-fitness.md), [ADR-0036](0036-local-web-console.md), [ADR-0030 Amendment 4](0030-peer-identity-and-pairing.md#amendment-4-2026-08--the-invite-authenticated-ceremony-pairing-without-two-humans-present-at-once), [ADR-0024](0024-include-exclude-rule-dialect.md), [spec 06 §7.1](../../specifications/repository-format/06-manifests.md#71-rule-dialect-rules-v1)

---

## Context

ADR-0028 §7 put "enumerate and modify backup sets" inside the command
contract's scope, and one verb existed: an upsert that could not carry
retention, appended edits to the end of the list, and accepted a schedule it
never parsed — which then failed permanently at the next pass, at two in the
morning, once per pass forever. There was no delete for anything.
FR-DEST-007's removal warnings were a named open item because no removal
surface existed to warn from. A client building a folder picker had nothing to
list directories with, and a client offering granular selection would have
written include rules that **the engine did not enforce**: `IsCaptured` was
validated, stored in the signed policy manifest, and called by nothing in
production — only the test fake honoured it.

The web console (ADR-0036) made all of this acute: a browser-shaped operator
expects to add a set, choose folders, exclude subtrees, set a schedule and a
retention policy, and map destinations — without editing JSON over SSH.

## Decision

1. **Contract 1.6 → 1.7, additive.** Set CRUD (`delete_backup_set`, a widened
   descriptor carrying retention and per-destination overrides), destination
   CRUD (`list/upsert/delete_destination`), `list_pairings`, `browse_folders`,
   `validate_set_draft`, and ADR-0030 Amendment 4's invite verbs
   (`create/list/revoke_pairing_invite`, `pair_with_invite`). Older clients
   ignore what they do not know; their upserts leave the new fields absent,
   which preserves — a policy spoken with every field empty is the explicit
   "none", so silence and "none" stay distinguishable.
2. **The schedule is validated at the command boundary**, refused with the
   parser's own defect — not at configuration load, where a throw would stop
   every set over one typo (ADR-0035 §1's blast-radius rule). The interval
   overflow the parser let escape is caught and named. `validate_set_draft`
   gives editors the same answer live, plus the schedule's next occurrences,
   because "what it means" beats "what it says".
3. **Include rules are enforced at capture.** The capture decision lives in
   the publication orchestrator — a source describes what exists and never
   decides what happens to it (architecture 11 §2) — so every source gains it
   at once: non-captured files are not published, and a directory that
   captured nothing and is not itself captured leaves no manifest. The tree
   records what the snapshot holds, not a skeleton of what the scanner walked
   past.
4. **Deletes are honest and never cascade.** Removing a set refuses while its
   job runs, then names what remains: the staging archive's path and every
   destination's copies. Removing a destination refuses while any set
   references it — naming the sets, because a cascade would quietly change
   what several sets protect — and, unreferenced, names what stays at the
   address (FR-DEST-007, met). A removal is a configuration edit; deleting
   data is retention's job, behind its own confirmations.
5. **Edits preserve position.** The first set is the default `run_backup`
   target and status renders declaration order; an upsert replaces in place.
   A destination rename follows through to every set that references it.
6. **`browse_folders` lists names, never content** — directories always,
   files on request for the selection tree, inaccessible entries flagged
   rather than thrown — and it is reachable by paired remote consoles, which
   is recorded as accepted: a paired console already commands restores to
   arbitrary service-machine paths and reads the configuration, which names
   paths. Q18/Q19 are untouched; no file content crosses any binding.

## Consequences

**Positive** — the whole configuration lifecycle works from any client of the
contract; the CLI, the web console and the future desktop shell share one
validated path; the include-rule gap is closed at the layer that owns it; and
the two config-file failure modes that hurt most — the 2 a.m. schedule and the
silent include — are now refusals at edit time.

**Negative** — the contract surface grew by eleven verbs in one minor, which
is a lot to hold stable; the descriptor's preserve-vs-clear convention (null
preserves, empty clears) is subtle enough to need its own tests; a destination
rename leaves the sync ledger's old-name rows behind until the next pass
rewrites them; and `browse_folders` widens what a compromised paired console
can enumerate from names alone.

**Neutral** — configuration remains a file a human may still edit by hand;
the command path validates through the same `Save` the file loader uses, so
neither route can write what the other refuses.

## Alternatives considered

- **A separate configuration service/endpoint.** A second surface with its own
  auth and versioning for what is, semantically, six more commands — rejected
  as ceremony; ADR-0028 §7 already scoped configuration into the contract.
- **Schedule validation inside `ClientConfiguration.Validate`.** One place for
  everything — but `Validate` runs on load, several times per scheduler pass,
  and a throw there stops every set backing up over one typo. The boundary
  validates; the load path stays tolerant (ADR-0035 §1).
- **UI-side rules compilation only, engine untouched.** Ship the selection
  tree compiling to excludes and leave includes unenforced. Rejected as
  dishonest: the rules ride the signed policy manifest, and a snapshot that
  claims "photos/** only" while holding everything is a wrong artefact, not a
  UI limitation.
- **Guessing kind-specific address fields into one string.** A single
  `address` field parsed per kind would have made the descriptor smaller and
  every defect message worse; the configuration's own field-per-kind shape is
  kept verbatim.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | Written with the web console shipped read-only and the include-rule gap freshly verified against the scanner |
| 2026-08 | Accepted | Built: contract 1.7, handlers, include enforcement in the orchestrator, and the web console's Configuration surface over it |
