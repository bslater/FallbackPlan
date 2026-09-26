# ADR-0051 — A local destination lives on its own drive

**Status:** Accepted
**Date:** 2026-08
**Requirements:** FR-DEST-017, FR-SNP-007, NFR-OPS-002
**Related:** [ADR-0018](0018-replica-failure-domains.md), [ADR-0035](0035-destination-fitness.md), [architecture 10 §1.1](../architecture/10-observability.md#11-states-must-be-distinguishable), [PT-8](../review/2026-08-fix-pressure-test.md#pt-8--protected-does-not-require-a-replica-outside-the-sources-failure-domain)

---

## Context

A locally stored backup is not wrong — so long as it does not live on the
drive whose files it protects. That separation is the primary check for a
local destination, and until this record nothing enforced it: a destination
on the source's own volume saved cleanly and earned a permanent warning,
while a destination on a second drive was lumped with it — capped at
`captured` under ADR-0018's machine-boundary reading of PT-8, reading
**Needs attention** at the glance forever, however correctly it was placed.

The owner's direction, recorded here, resolves both halves: make drive
separation the **condition of choosing** a local destination, and let a
destination that satisfies it **earn protection**.

## Decision

1. **The condition.** Binding a local-path destination to a backup set
   requires it to sit on a **different volume** than every one of the set's
   roots — and, where the platform can name physical drives, on a
   **different drive** (two partitions of one disk fail together). The
   command boundary refuses the choosing with both paths named
   (`LocalDestinationPlacement.Judge`; `Agent/ServiceCommandHandler`
   enforces it on new bindings, on root changes, and on a referenced
   destination's path edit).

2. **"Where possible", honestly.** Volume separation is the hard core of
   the condition — judged by volume identity via the nearest existing
   ancestor. The physical-drive refinement applies only where the platform
   can answer (`Filesystem.Local/PhysicalDisk`: Linux sysfs today — a
   partition resolves to its parent disk; anonymous and multi-device
   volumes answer null). An unknowable answer never refuses on a guess:
   the status derivation stays conservative for it instead.

3. **Only the choosing is gated.** A configuration written before this
   record keeps loading and keeps its standing bindings (ADR-0035's
   posture: report, never refuse to load); its same-volume placement keeps
   its status warning. An edit that touches neither the set's roots nor the
   binding is not re-judged.

4. **The protection boundary moves from machine to volume.** `protected`
   now asks: *if the drive the files live on is destroyed, does a copy
   survive?* A second drive answers yes and earns `protected` — and, when
   verified, `verified` — reading **Healthy** at the console's glance. Only
   `same-volume` still caps at `captured`. The residual risk of the best
   protecting copy is always named beside the badge ("survives drive
   failure, not fire, theft, or losing the machine"; same-site keeps its
   site-loss note), so the distinction PT-8 exists for — a copy that dies
   with its source reading healthy — remains impossible: the copy that
   earns Healthy provably does not die with the source's drive.

## Consequences

**Positive** — the false-confidence case (same disk) can no longer be
chosen at all, which is stronger than any badge; a correctly placed local
backup stops nagging; status and choosing judge drive-sharing through one
probe, so they cannot disagree.

**Negative** — a single-drive machine cannot choose a local destination
(an external drive, a peer, or a cloud kind is the answer — which is the
truth of that machine's options, stated at save time rather than
discovered at restore time); the machine-loss residue of a second internal
drive is a warning rather than a badge tier, resting on the named warning
staying in front of the owner.

**Neutral** — the failure-domain ladder (ADR-0018) is unchanged as data;
what changed is which rung earns protection. Declared domains still win
over derivation.

## Alternatives considered

- **Warn-and-confirm instead of refuse**: rejected — the whole class of
  placement exists to be caught before the first byte lands; a confirmed
  same-drive backup is still a backup that dies with its files.
- **A distinct badge tier for same-machine** (between captured and
  protected): rejected as vocabulary growth the glance layer would fold
  away again; the warning carries the residue.
- **Refusing unknowable topologies**: rejected — a network mount or
  layered volume would be refused on a guess; conservatism belongs to the
  status derivation, not the gate.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Accepted | The owner's direction: drive separation as the condition of choosing a local destination, and the protection boundary moved from machine to volume — `Application/LocalDestinationPlacement`, `Filesystem.Local/PhysicalDisk`, the upsert guards, and the deriver's gates, pinned by `Application.Tests/LocalDestinationPlacementTests`, `Hosts.Tests/LocalPlacementTests` and the flipped deriver suite |
