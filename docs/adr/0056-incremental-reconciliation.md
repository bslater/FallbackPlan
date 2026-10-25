# ADR-0056 — A sync pass costs what changed, and reads through on a cadence

**Status:** Accepted
**Date:** 2026-09
**Requirements:** FR-REP-005, FR-DEST-003, FR-GC-009, NFR-PERF-016
**Related:** [ADR-0029](0029-pipeline-and-service-concurrency.md), [ADR-0034](0034-hub-and-spoke-destinations.md), [ADR-0046](0046-direct-to-destination-publication.md), [ADR-0047](0047-backup-pool-and-priorities.md), [ADR-0009](0009-garbage-collection-safety.md)

---

## Context

A replication pass has always been written as a diff: read what the
destination holds, read what the source holds, carry the difference. The diff
is the right idea and the reading was not. `Replication/StoreToStoreCopier`
ordered its work into eight dependency phases — blobs before the index, the
index before the snapshots that assert completeness — and implemented that by
listing the **entire source namespace once per phase** and discarding
seven-eighths of each listing. A ninth listing took the destination's whole
inventory into a hash set held for the length of the pass; convergence took a
tenth for its drop half.

So a pass cost the archive's object count multiplied by the number of
dependency classes, and it paid that whether or not anything had changed. The
comments excusing it were honest about the intent and wrong about the price:
*"the pass is cheap when there is nothing to carry — an inventory diff"*. An
inventory diff over a million objects is eight million listing entries and a
million-key set resident in memory, on every poll, on a store that charges by
the thousand keys.

The 2026-09 architecture review's R6 named all three parts: the per-phase full
listings, the four full key sets, and — the part that matters most — that
`synced_sequence` is *"a retention watermark the copier never reads"*. The
ledger has recorded, since the staging model, exactly the fact each pass was
re-deriving: everything published at or before this sequence is at this
destination. [ADR-0047](0047-backup-pool-and-priorities.md)'s decision 6 went
further and declared `baseline_snapshot_id` and `last_reconciled_at` for the
record that would fill them. No record did.

## Decision

### 1 Each phase lists under its own prefix

A dependency phase is a prefix, and a listing takes a prefix. Listing the
whole namespace and filtering is the same work with the discarded part left
in. Both sides are read per phase, and the destination's inventory for a phase
is released when the phase ends — so a pass's peak memory is the largest
phase rather than the archive, and the cross-phase duplicate set disappears
with it: prefixes that partition the namespace cannot yield the same key
twice.

### 2 The namespace's own prefixes are all named

The catch-all phase exists so an object under a prefix no phase names is
copied rather than silently skipped. It was also carrying `hints/` and
`audit/` — parts of the namespace [specification 01 §2](../../specifications/repository-format/01-object-layout.md#2-namespace)
defines, reaching their destinations only because the net caught them. Both
are named phases now. The catch-all is for a repository written by a version
this one has never heard of, which is the only thing it should ever find.

This matters more than tidiness: decision 3 leaves the catch-all out of the
ordinary pass, and that is only correct if everything this build writes is
named. The first full test run after the change failed a verification on a
hint object that had stopped crossing, which is the check that has to exist
for this rule to be safe.

> **Amended 2026-10 ([Amendment 1](#amendment-1-2026-10--every-prefix-this-build-writes-is-named-and-a-pass-is-asked-only-about-what-it-carries)).**
> The rule was broken by the next prefix the product added: `format-upgrade/`
> ([ADR-0066](0066-the-format-upgrade-record.md)) was never named, and the
> verification that should have caught it sampled the whole listing. It is
> named now, and a pass's verification asks only about what that pass carries.

### 3 A pass decides what to do before it reads anything

`Application/ReconciliationGate` answers from the ledger row, the source's
publication sequence and the clock:

- **Skip** — nothing published since this pair was last read through, its
  keep-set has not moved, and the reading-through is inside its interval. The
  pass costs the sequence read that established it.
- **Incremental** — something published, or a keep-set that moved with the
  clock. The named phases carry it; the catch-all sweep waits.
- **Reconcile** — everything else: no row, a pair owed its baseline, a pair
  not claiming to be level, a reading-through that has come due or never
  happened, a source sequence *behind* the destination's, a clock that went
  backwards, or a caller that knows the source holds more than the watermark
  can speak for.

Every arm errs towards reading. A skip is a claim about a destination made
without looking at it, so it is made only from facts a pass that did look
wrote down.

### 4 A reading-through expires

Only a reconciling pass stamps `last_reconciled_at`, and the stamp is good for
a day. This is the decision's safety rail rather than a tuning knob: a
watermark describes what was **published** and can say nothing about the
destination's own side. A file deleted out of the replica, a half-finished
copy from a build with a bug in it, a stray under an unknown prefix — none of
them move a publication sequence, and all of them are found by reading the
inventories through.

A day is where the cost of a full pass on a large archive and the time a
destination can be quietly wrong meet. It is not the product's only net:
verification reads bytes at the destination on its own cadence and the restore
drill ([ADR-0054](0054-scheduled-restore-drills.md)) restores a file from it,
and both are stronger tests of a replica than any listing.

### 5 The ship sink records the sequence it published

A direct-ship set's run is its own fan-out, so nothing else is in a position to
say what the destinations now hold — and `DestinationShipSink` recorded its
success without a sequence at all. The ledger's watermark therefore sat at
whatever the last copy pass had written, which on a set that never needs a copy
pass is its first value for ever.

The sink reads the publication sequence out of the snapshot record's own
cleartext prefix as it ships it ([specification 08 §2](../../specifications/repository-format/08-journal.md)),
which needs no keys and no catalogue, and records it with the run's success.
This is a correctness fix in its own right: the replication gate of
[FR-GC-009](../requirements/functional.md) compares snapshot publication
sequences against that watermark, and a watermark frozen at 1 under-claims
what the destination holds.

### 6 A keep-set and a spare set are fingerprinted

Two of a pass's inputs move for reasons no publication sequence records. A
retention window expires with the clock, so a destination can be owed a
deletion while nothing at all is published. And a **spare** — a copy this
destination holds only because a sibling has not received it yet
(FR-GC-009's direct-ship shape) — is released when that sibling catches up.

Both sets are therefore rendered as a stable fingerprint the ledger carries
and the gate compares: order-independent, sixteen bytes of SHA-256 per key
combined by exclusive-or, with the count. A change detector and not a security
control — the keys digested are the product's own, and a collision costs a
convergence deferred to the next reading-through, never a deletion that should
not have happened.

### 7 A migrating set reads through every pass, unchanged

A direct-ship set that migrated keeps its staging archive until retirement.
Its runs record success for the objects they ship, which says nothing about
the older history only staging holds ([ADR-0046](0046-direct-to-destination-publication.md) §3),
so the gate is told to read through until the archive is gone. That is exactly
today's behaviour and the cost is bounded by the migration, which ends when
`retire_staging` succeeds.

## Consequences

**Positive**

- A converged pair's pass costs one sequence read rather than nine listings of
  the whole archive, so the poll cadence stops scaling with the archive.
- Peak memory during a pass is the largest phase, not the object count.
- `synced_sequence` becomes true on direct-ship sets, which is what the
  replication gate needs to reclaim a source copy at all.
- `last_reconciled_at` and `baseline_snapshot_id` are written, three records
  after they were declared.

**Negative**

- A destination that loses an object can stay wrong for up to a day, where
  before every pass would have found it. Verification and the drill still
  read that destination on their own cadences, and a pair that fails either
  stops claiming to be level — which takes the next pass off the skip path
  regardless of any interval.
- A stray under a prefix no phase names waits for a reading-through rather
  than crossing on the next pass.
- One more durable field per destination, and two more inputs the gate can be
  wrong about. Both are fingerprints of sets the pass already computes.
- The reconciliation age is not on the status contract, so an operator cannot
  yet see when a destination was last read through. It is recorded; surfacing
  it is a contract addition and has not been made.

## Alternatives considered

**Resume listings after the last synced key.** Ordinal listing order is
chronological for `index/` and `journal/`, whose keys are zero-padded
sequences — and is meaningless for `blobs/` and `snapshots/`, whose keys are
keyed renderings ([02 §4.3](../../specifications/repository-format/02-identifiers.md#43-not-leaking-writer-identity)).
The prefixes where most of the objects are are exactly the ones this cannot
help, which is a consequence of the naming rule that hides writer identity and
is not worth reversing for a listing optimisation.

**Keep a durable per-destination inventory.** The exact answer, and a
placement catalogue by another name — which [ADR-0052](0052-relocatable-records-format-v3.md) §5
already proposes for format v3, on a much larger scope than replication. A
second one built here would have to be reconciled with that one later.

**Enumerate new objects from the index plane.** The index knows what each
generation added, so a pass could carry exactly that. It couples the copier to
the index plane's decoding — the copier today needs no keys at all, which is
what lets it run against a store it cannot read — and the coupling buys
nothing on the pass that matters, which is the one with nothing to carry.

**Skip on the watermark alone, with no expiry.** Cheapest, and it makes the
ledger authoritative about a destination it never looks at. The ledger is
metadata about the destination; the destination is the ground truth. An
unexpiring skip inverts that quietly, and the inversion is invisible until
somebody needs the data.

## Amendment 1 (2026-10) — every prefix this build writes is named, and a pass is asked only about what it carries

§2's rule held for the namespace as it stood in 2026-09. The format-upgrade
record ([ADR-0066](0066-the-format-upgrade-record.md)) arrived later under a
prefix of its own, and nothing named it. It fell to the catch-all phase, and an
incremental pass left it behind.

§2 said the check that made the rule safe was a verification that fails when
an object stops crossing. That check was looking at the wrong population. The
verification sample was drawn from the whole staging listing, catch-all keys
included, while an incremental pass carried only the named phases. So the
missing name produced no clean failure pointing at the copier. It produced a
false failure at the destination instead. The first sync after an upgraded
set's next backup asked the destination for the record, found it absent, and
marked a healthy destination Failed until the reconciling pass up to a day
later.

Two things change:

- **`format-upgrade/` is a named phase.** It sits beside the descriptor it
  qualifies, so a destination learns of a move before the objects written
  under it arrive. The record crosses on the sync after the set's next
  backup.
- **A pass is asked only about what it carries.** The sampler takes the pass's
  scope and leaves out what that scope does not carry, through the copier's
  own phase list (`Replication/StoreToStoreCopier`, `Replication/VerificationSampler`).
  A pass that skips samples as an incremental one does: a stray written since
  the last reconciliation is not at the destination either. This holds for
  any prefix, not only the ones this build names. A store a newer build has
  written to is still synced by this one, which reads it and never writes it,
  and may hold objects under prefixes nothing here knows.

§2's check is narrower for the second change, and still exists in the form it
needs. `Replication.Tests/CopierScopeTests` copies one object under every
prefix of specification 01 §2 this build writes, on an incremental pass, and
fails if any is left behind. A new prefix that is not named now fails that
test, not a customer's destination.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted (amended) | [Amendment 1](#amendment-1-2026-10--every-prefix-this-build-writes-is-named-and-a-pass-is-asked-only-about-what-it-carries): `format-upgrade/` is a named phase, and a pass's verification asks only about what the pass carries (`Replication/StoreToStoreCopier`, `Replication/VerificationSampler`, `Agent/FanOut`; `Replication.Tests/CopierScopeTests`, `Replication.Tests/VerificationSamplerTests`, `Hosts.Tests/FormatUpgradeTests`) |
| 2026-09 | Accepted | In response to the 2026-09 architecture review's R6. Built: `Replication/StoreToStoreCopier` lists per phase and takes a `CopyScope`, `Application/ReconciliationGate` decides, `Application/DestinationSyncStore` carries `keep_fingerprint` and stamps `last_reconciled_at`, `Agent/DestinationShipSink` records the sequence it published, and `Retention/DestinationConvergence` renders the keep and spare sets. Takes up the `baseline_snapshot_id` and `last_reconciled_at` that [ADR-0047](0047-backup-pool-and-priorities.md) decision 6 left to a later record |
