# ADR-0018 — Replica failure domains

**Status:** Accepted
**Date:** 2026-08
**Requirements:** FR-SNP-005, FR-SNP-006, NFR-OPS-002
**Pressure-test finding:** [PT-8](../review/2026-08-fix-pressure-test.md#pt-8--protected-does-not-require-a-replica-outside-the-sources-failure-domain)

---

## Context

[ADR-0011](0011-commit-versus-replication-semantics.md) decoupled commit from replication so that an offline destination could not stall protection. That was necessary and correct.

It also made `protected` — the primary, reassuring, user-facing state — mean "committed to the local replica", with no requirement that the local replica live anywhere other than the disk holding the source data.

The default first-run flow puts the local repository on the source machine. A user who accepts that default and never brings their offsite peer online sees `protected` from the first backup onwards. When the disk fails, both copies go together, and the product will have said `protected` right up until the moment there was nothing left.

The original proposal named "consumer UI hides degraded state → false confidence" as a major risk. This is that risk, reintroduced by the fix for something else — and worse than a display defect, because it is what the status model is *defined* to mean.

## Decision

### Replicas declare a failure domain

| Domain | Example | Independent of the source? |
|--------|---------|---------------------------|
| `same-volume` | Repository directory on the source volume | No |
| `same-machine` | Second internal disk | Partially — survives disk failure, not theft, fire, flood, or ransomware with local write access |
| `same-site` | NAS or peer on the same LAN | Survives machine loss, not site loss |
| `independent` | Offsite peer, cloud store | Yes |

### `protected` requires a disjoint domain

```text
Snapshot captured when:
  - any replica: durable

Snapshot protected when:
  - at least one replica outside the source's failure domain: durable
```

A snapshot held only on the source volume is `captured`. That is accurate, and it is deliberately not reassuring.

### First run says so

The setup flow warns when the only configured destination shares a failure domain with the source, and states plainly what that does and does not survive. It does not refuse — a same-machine copy is genuinely better than nothing, and protects against the most common real event, which is the user deleting a file. It simply must not be described as protection against losing the machine.

## Rationale

`captured` and `protected` are different claims and users read them as different claims. Collapsing them makes the reassuring word cover a case where it is false, and a backup product's status display is the one place where an optimistic default is least defensible.

Domain granularity is deliberately coarse. Finer distinctions — separate power circuits, separate cloud availability zones — are meaningful to some deployments and meaningless to a household. Four levels are enough to answer the question that matters: *if this machine is destroyed, does a copy survive?*

The domain is declared rather than inferred. A cloud store is `independent`; a mounted network share might be `same-site` or `independent` depending on where the server actually is, and only the user knows. Inference would be wrong in exactly the cases where being wrong matters most, so the destination declares its domain at configuration time with a sensible default and a plain-language explanation.

## Consequences

**Positive**

- The most common consumer misconfiguration is visible instead of disguised as success.
- The status vocabulary gains a truthful state for local-only capture, rather than overloading `protected`.
- Durability policy can express "at least one independent replica" directly.

**Negative**

- Users who genuinely only want a local copy will see a permanent non-`protected` state. Correct, but it will generate support questions, and the wording needs to be careful rather than nagging.
- A declared domain can be wrong — a user may label a NAS `independent` when it sits under the same desk. Declaration is a floor on honesty, not a guarantee of it.

**Neutral**

- Existing policy expressions continue to work; `protected` becomes stricter and `captured` is added beneath it.

## Alternatives considered

**Infer the domain from the destination type.** Rejected — a network path gives no reliable signal about physical location, and a wrong inference is worst precisely where the stakes are highest.

**Keep `protected` as-is and fix it in the UI copy.** Rejected. The defect is in the model, and every surface that consumes the model would have to remember to compensate. One of them eventually will not.

**Require an independent replica before any backup runs.** Rejected as hostile: a local-only backup is worth having, and refusing to start until an offsite destination exists would push users to no backup at all.

## Amendment 1 (2026-08) — the domain is declared per configured destination

[ADR-0034](0034-hub-and-spoke-destinations.md) makes destinations named
configuration entries that backup sets reference, so the failure domain now has
an unambiguous home: **each destination declares its domain in its
configuration entry**, defaulting sensibly by kind (a directory on the source
volume is `same-volume` by volume-identity comparison rather than by trust; a
peer or cloud destination defaults to `independent` unless the user says
otherwise) and always overridable, because §Rationale's point stands — only the
user knows where the NAS actually sits.

Two consequences of the hub-and-spoke shape. The evaluation becomes a matrix —
`protected` is judged per set over that set's destinations, not over one global
replica list. And the set's staging archive **never counts**: it shares the
source's domain by construction and ADR-0034 makes it a cache rather than a
replica, so the first-run warning of §First-run-says-so now triggers when a
set's *destinations* all share the source's domain, which is the same honesty
with the new vocabulary.

## Amendment 2 (2026-08) — the vocabulary is built, and a peer's default narrows

The four-value domain is now code, not prose: destinations carry an optional
`failure_domain` field (`same-volume` / `same-machine` / `same-site` /
`independent`, refused outside that vocabulary), the status derivation
consumes the domain instead of the boolean same/other comparison that stood
in for it, and the status matrix renders each destination's domain beside its
sync state.

Two defaults changed from Amendment 1's sketch, both toward honesty:

- **A peer defaults to `same-site`, not `independent`.** The common peer is a
  friend's machine on the same LAN or a NAS in the same house; a LAN friend
  does not survive the house fire. Calling a peer `independent` is now a
  declaration the user makes, never an assumption the product does. Cloud
  kinds keep the `independent` default — offsite is their nature.
- **A local path never infers past `same-machine`.** Device-identity
  comparison distinguishes `same-volume` from `same-machine` (conservatively
  `same-volume` when the platform cannot say), and stops there: a second disk
  or attached USB drive still dies with the machine, so anything further is
  the user's declaration.

The threshold follows §Rationale's question — *if this machine is destroyed,
does a copy survive?* `protected` (and therefore `verified`) requires an
in-sync destination at `same-site` or `independent`; `same-volume` and
`same-machine` cap at `captured` with a warning naming the domain. When
protection rests only on `same-site` destinations, the status says so — a
copy at the same site survives losing the machine, not losing the site.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Accepted | Forced into the open by PT-8 |
| 2026-08 | Accepted (amended) | Amendment 1: domains are declared per configured destination and evaluated per set; staging never counts ([ADR-0034](0034-hub-and-spoke-destinations.md)). |
| 2026-08 | Accepted (amended) | Amendment 2: the vocabulary is built — declared `failure_domain` per destination, peer default narrowed to `same-site`, threshold fixed at surviving the machine. |
| 2026-08 | Accepted (amended) | The protection threshold moved from surviving the machine to surviving the source's volume, and drive separation became the condition of choosing a local destination — the owner's direction, recorded in [ADR-0051](0051-local-destination-placement.md); the ladder itself is unchanged. |
