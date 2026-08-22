# ADR-0039 — The console completes the operator loop: notices acknowledged, pairings ended, snapshots explored

**Status:** Accepted
**Date:** 2026-08
**Requirements:** FR-DEST-008, FR-SVC-009, FR-SVC-005, FR-SNP-002, FR-SVC-001
**Related:** [ADR-0036](0036-local-web-console.md), [ADR-0037](0037-configuration-over-the-command-contract.md), [ADR-0038](0038-set-change-rescan-and-notice.md), [ADR-0030 Amendment 2](0030-peer-identity-and-pairing.md#amendment-2-2026-08--the-pairing-lifecycle-completes-roles-on-the-wire-endings-announced-terms-enforced), [ADR-0028](0028-service-boundary-and-deployment-topologies.md)

---

## Context

The console spoke every verb the contract had, and three loops still ended
at a terminal. A notice — the durable channel FR-DEST-008 built so a peering
that ended at 3 a.m. is still known at breakfast — could be *read* in the
console but acknowledged only by `fallbackplan-agent notices --ack`, and the
page said so; worse, that verb wrote `notices.json` directly beside a
running service's own live store, a second writer on one file. A pairing
could be *made* from the console (ADR-0030 Amendment 4's invites) but never
*ended* — no contract verb existed, so `delete_destination`'s peer refusal
pointed operators at a CLI they might never have opened. And the snapshot
browser showed bare names and sizes: no times, no sense of what changed
between two snapshots, even though the catalogue holds both listings and
deletion is precisely absence between them (FR-SNP-002) — computed nowhere
a person could see. (Its `kind` field also leaked the enum's internal
`directoryplaceholder` naming, which no client's `directory` check matched.)

## Decision

1. **Contract 1.8 → 1.9, additive.** `list_notices` (structured:
   id, key, message, raised-at, acknowledged-at; oldest first; acknowledged
   history behind a flag), `acknowledge_notice`, and `unpair`; and
   `list_directory` enriched in place — per-entry modification time and a
   change marker against the set's previous snapshot, plus the names the
   previous snapshot held in that directory and this one does not, and the
   predecessor's id so the markers are self-describing.
2. **Acknowledged is a stamp, not an eraser.** The store's semantics are
   kept verbatim: acknowledging records who-has-seen and stops the notice
   demanding attention; the record stays, and the history flag lists it.
   `StatusResult.Notices` — the flattened strings — stays untouched for
   every existing reader.
3. **Unpair is honest about order and reach.** It is refused while a
   configured destination references the fingerprint, naming the
   destination (ADR-0037 §4's no-cascade posture pointed both ways — the
   destination's refusal names unpair, unpair's names the destination). The
   announcement is best-effort under a 15-second bound and never gates the
   revocation; because the honest order usually deletes the destination —
   and with it the only known address — first, the command carries an
   optional endpoint, exactly as the agent verb's `--to` does. Revocation
   leaves the tombstone that turns the peer's next dial into `revoked`
   rather than `never paired`, and deletes data nowhere. The mechanics are
   extracted (`Agent/PeerUnpairing.cs`) and shared with the agent verb, so
   the two surfaces cannot drift.
4. **"Changed" is object identity; folders make no claim.** A file's marker
   compares recorded object ids — exact, because an unchanged file re-emits
   its prior manifest's id verbatim, so equal ids are the same statement
   and unequal ids mean content, metadata, or a restated manifest. A
   directory's id is its tree-chain head, whose recorded metadata mixes in
   access times the scan itself perturbs — a folder-level marker would read
   "changed" as noise, so directories answer null and their own listing
   answers instead. Deleted names ride a separate list rather than
   synthesized entries, which would lie about kind and length and imply
   restorability from a snapshot that does not hold them. The wire `kind`
   now says `directory`, matching the documented vocabulary.
5. **The CLI acknowledgement routes through the service when one listens**
   (ADR-0028 §3: liveness decides), falling back to the direct file only
   when nothing holds the state directory — ending the second-writer race
   without costing the no-service breakfast read.

The console builds on all of it: a Notices view with ages, per-notice
acknowledgement and history; a Pairings table with a typed-word Unpair
dialog; a snapshot browser with modification times, new/changed badges,
greyed deleted rows that jump to the previous snapshot, and older/newer
navigation at the same path — plus the round of affordances the contract
already carried but the page never offered: folder pickers for the restore
output and local destination paths, a confirmed full re-capture, and "what
changed?" from the overview card.

## Consequences

**Positive** — the three loops close where the operator lives; the notices
file has one writer whenever a service runs; a pairing's whole lifecycle is
now a console affair; and the browser is a small time machine over data the
catalogue always had.

**Negative** — the contract grew three verbs and four fields in one minor;
`list_directory` now costs two listings and a snapshot enumeration instead
of one listing; unpair's announcement can silently spend its 15-second
bound against a black-holed address before revoking; and folders' null
markers will read as a gap to someone expecting transitive change flags —
the alternative was flags that were wrong.

**Neutral** — acknowledged history accumulates in `notices.json` unbounded,
as it already did; and the older/newer rail is client-side, so a console
with a stale snapshot list may offer a jump the service then refuses.

## Alternatives considered

- **Structured notices inside `StatusResult`.** One poll instead of two —
  but every status consumer pays for detail only the notices view wants,
  and the flattened strings would still be needed for old clients.
- **Deleting acknowledged notices.** A smaller file — and a lost record of
  what was seen and when, which is the half FR-DEST-008 exists for.
- **Transitive directory markers via tree-head comparison.** Free in
  theory; in practice access times ride the head manifest and the scan
  itself moves them, so every folder would read "changed". Rejected as
  noise wearing a feature's clothes.
- **Synthesizing deleted entries into the listing.** One list instead of
  two — with fabricated kinds and lengths, and a Restore button that could
  not work.
- **A second HTTP surface for notices in the web host.** The relay is
  verb-agnostic; a bespoke endpoint would be the first special case and
  earn nothing.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | Written with the console speaking all of 1.8 and the three dead ends freshly inventoried |
| 2026-08 | Accepted | Built: contract 1.9, the shared unpair mechanics, the enriched listing, the CLI reroute, and the console's notices, pairings and explorer surfaces |
