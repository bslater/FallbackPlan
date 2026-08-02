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

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Accepted | Forced into the open by PT-8 |
