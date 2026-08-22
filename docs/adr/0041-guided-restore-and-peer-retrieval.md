# ADR-0041 — The guided restore: a passphrase gate, restore sources including a peer's replica, and honest run options

**Status:** Accepted
**Date:** 2026-08
**Requirements:** FR-RST-001, FR-RST-003, FR-RST-004, FR-RST-006, NFR-SEC-009
**Related:** [ADR-0040](0040-multi-root-backup-sets.md), [ADR-0036](0036-local-web-console.md), [ADR-0034](0034-hub-and-spoke-destinations.md), [ADR-0028](0028-service-boundary-and-deployment-topologies.md), [peer-protocol 07](../../specifications/peer-protocol/07-retrieval.md)

---

## Context

Restore was one dialog and one shape: a snapshot id, one path, an output
folder — always quarantined, always read from the hub's own staging archive.
Five real needs had nowhere to live. An operator wants a **guided** flow: a
passphrase before anything else, then *where from*, then *when*, then *what*,
then *where to*. Restores must work when the staging archive is damaged or
gone — from a local-path destination's replica, and from a **paired peer's
replica over the wire**, which had no read path at all (the alternate-site
drill read the replica off a shared disk with the recovery kit). A date, not
a snapshot id, is how people think about "when". A restore of several picked
files ran as N separate runs with N quarantine directories. And the
executor's overwrite policies — built, correct, defaulted safe — were not
reachable over the contract, while the receipt (FR-RST-004) was built and
then thrown away.

Two standing walls shaped everything: `KeyMaterialConfinementTests` forbids
anything passphrase-shaped on the command contract in either direction
(NFR-SEC-009), and `DependencyRuleTests` pins the web console to referencing
the client contract and nothing below it (ADR-0036's client posture).

## Decision

1. **The passphrase gate runs in the console process, against local key
   files — the contract carries nothing.** The wizard's first step posts to
   the console host's own `/api/restore-gate` (token-guarded like every
   endpoint); `ConsoleRestoreGate` reads the staging archive's descriptor
   and wrapped key objects off local disk, derives the KEK with the
   archive's own KDF parameters, and answers `verified`, `wrong`, or
   `unavailable`. The service's only contribution is *where the archives
   live* (`describe_service` gained `ArchivesRoot` — a path, not a secret);
   it is never handed the passphrase it already holds its own copy of. This
   is key export's posture (ADR-0028 §9) applied to verification: passphrase
   work runs where the person typed it. The console dependency rule gains
   its one named exception — `ConsoleRestoreGate` may reach
   `FallbackPlan.Repository` + `Storage.Local`; every other console type is
   still fenced to the contract, asserted per type. A console with nothing
   local to check (a remote console, a fresh machine) says `unavailable`
   honestly, and the wizard proceeds only past an explicit acknowledgement.
2. **Restore sources are server-side handles.** `open_restore_source
   {setName, destinationName?}` resolves a per-set repository — the staging
   archive (borrowing the runtime's open), a local-path destination's
   replica, or a peer's replica — and answers a handle plus the snapshots it
   holds. Non-staging sources get a **throwaway catalogue**: index-plane
   rebuild for locations, manifest projection for snapshots/paths/versions
   (FR-MAN-002's recipe), metadata-class blob footers only. Handles are
   gated one-command-at-a-time, touched on use, swept after 30 idle
   minutes, closed idempotently, and their caches purged at service start.
   The open carries **no passphrase**: the runtime unlocks replicas with the
   secret it holds — which is also the genuine authorisation, since a
   replica that is not this repository's will not unwrap.
3. **Replica resolution favours the disaster it exists for.** With staging
   alive, the replica is found by repository id directly. With staging
   gone, candidates are probed — a local destination's subdirectories, or
   the peer's **owner inventory** (07 §3.5: a zero-id `retrieve_open` lists
   the repository ids the dialling peer owns there) — and each candidate is
   opened, rebuilt, and kept only if it holds the named set's snapshots.
4. **Peer retrieval is new protocol surface**, specified normatively in
   [peer-protocol 07](../../specifications/peer-protocol/07-retrieval.md):
   feature `retrieval`; `retrieve_open/ready/list/list_page/read/data`;
   strictly request/response; the destination serves only replicas its
   attribution ledger assigns to the dialling pinned identity, refusing
   someone-else's and never-stored identically (no reconnaissance);
   ciphertext both ways. On the hub, `PeerRetrievalObjectStore` adapts the
   session to `IObjectStore`, so the repository open, the catalogue rebuild
   and the restore all run over the wire unchanged. The stated cost: the
   destination learns which objects the owner reads.
5. **The effective date resolves client-side.** The source's snapshot list
   is already in the wizard's hand; "newest `capturedAt` at or before the
   end of the chosen day" is one comparison, and a server-side `AsOf` would
   duplicate knowledge the client must render anyway. FR-RST-001's time
   selector is the wizard.
6. **One plan may carry several subtrees.** `RestorePlanner` takes a prefix
   list — sorted, deduplicated, descendants of another prefix dropped, the
   collision/length/degradation passes run over the union — so a
   multi-select restore is one run, one RunId, one quarantine, one receipt.
   The wire mirrors it (`paths`, winning over `path` exactly as roots win
   over root).
7. **The run options say what they do.** `target: original` maps the plan
   back onto the set's configured roots — label-sliced for a multi-root set
   (ADR-0040), refused whole naming the label when configuration has moved
   on. `existing: rename` is the new `WriteBeside` policy: the live file
   untouched, the restored copy beside it as `name (restored
   2026-08-18).ext`, deduped and capped, recorded per item as `written_as`
   (receipt schema 4). `overwrite` maps to `Replace` — destructive, never a
   default. `inPlace` makes FR-RST-006's explicit choice explicit on the
   wire; absent options reproduce the old behaviour byte for byte. The
   executor's symlink case now honours the policy too (it deleted
   unconditionally before).
8. **Runs read only what the plan needs, when the source may be remote.**
   The plan probe already computes each item's manifest and segment blobs;
   the run feeds exactly that set to a targeted `RepositoryReader` load
   instead of opening every footer in the store — the difference between a
   restore-sized transfer and a repository-sized one over a peer session.
9. **The receipt is persisted, every run** — `<state>/receipts/<run>.json`
   (FR-RST-004's machine-readable record, finally durable) — and the wire
   result carries the per-outcome counts, a bounded failure sample, and the
   receipt's path.

## Consequences

**Positive** — restore survives the loss of the staging archive with
nothing but the running pairing; the wizard walks an operator from
passphrase to receipt without a single free-typed snapshot id; multi-select
is one honest run; the overwrite semantics users expect exist without
weakening the quarantine default; and the passphrase gate arrived without
touching NFR-SEC-009's wall.

**Negative** — the gate is real only where the archives are readable: a
remote console gets `unavailable` plus an acknowledgement, which is honesty,
not verification. Opening a non-staging source costs a catalogue rebuild
(metadata reads over the wire for a peer). The retrieval feature hands the
destination read-pattern knowledge. And the console now links the
repository and local-store assemblies for one class — a boundary bend,
fenced by name in the architecture tests.

**Neutral** — source handles are state the service must expire (the sweep);
the beside-name convention is a policy others may want configurable someday;
and the CLI keeps its existing single-path restore verb untouched this
round.

## Alternatives considered

- **A passphrase field on the contract, allow-listed.** One verb instead of
  an endpoint — and the first breach of a wall whose test remarks predict
  exactly this request. The service also gains nothing: it holds the
  passphrase already; only the wire would learn it.
- **In-browser verification (WASM Argon2).** The secret never leaves the
  tab — but the repository's memory-hard KDF parameters can exceed what a
  tab will allocate, and the page would need the wrapped key objects served
  to it. Heavier, weirder, no safer than the console process.
- **A server-side `as_of` selector.** Symmetric with the wizard — and a
  second implementation of a one-line comparison whose inputs the client
  must hold anyway to render the choice.
- **Restoring from a peer via `RecoverySession` semantics.** Already built —
  and index-free means reading every blob footer, which over a wire is the
  whole repository. The rebuild-plus-projection path reads metadata only.
- **Per-file executor mapping for original-location multi-root restores.**
  One executor run instead of per-root slices — and a path-rewriting layer
  inside the executor's containment logic, which is exactly where surprise
  is most expensive. The handler slices; the executor stays literal.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | Written with the wizard's six steps agreed and the two walls (key confinement, console dependency) identified as the design's fixed points |
| 2026-08 | Accepted | Built end to end: engine policies and multi-prefix plans, contract 1.11, source handles over staging/replica/peer, peer-protocol 07 implemented both sides, the console gate and wizard — proven by service-level drills including a total-staging-loss restore over the wire, and a live Playwright walk of all six steps |
