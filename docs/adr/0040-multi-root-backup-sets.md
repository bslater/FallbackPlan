# ADR-0040 — Multi-root backup sets: several folders, one snapshot, a machine-wide selection tree

**Status:** Accepted
**Date:** 2026-08
**Requirements:** FR-SNP-008, FR-SVC-009, FR-SNP-007, FR-RST-001
**Related:** [ADR-0038](0038-set-change-rescan-and-notice.md), [ADR-0037](0037-configuration-over-the-command-contract.md), [ADR-0026](0026-phase-1-capture-shapes.md), [ADR-0022](0022-standalone-metadata-records-and-index-identifiers.md)

---

## Context

A backup set captured exactly one folder. The limit was threaded everywhere —
`root` in the configuration file, `Root` on the contract descriptor,
`RootPath` on the publication job, one scan producing one tree — yet the
**repository format never once records the root path**: trees name their
children relative to wherever the walk started, readers recurse generically,
and restore lays out whatever the tree says under the chosen output. The
single root was an orchestration habit, not a format invariant.

The user-facing cost was real: documents on one drive and photos on another
meant two sets, two schedules, two retention policies, and a status page
implying two backups where the operator meant one. And the editor's tree
started *at* the root, so choosing what to capture and choosing where to
start were the same act — there was no way to point at the machine and tick.

The sharp constraints found in exploration, which shaped every decision
below: `source_filesystem` is one signed map per snapshot (one
case-sensitivity bit, one set of limits); rules-v1 subjects are
root-relative with **exclude-wins and no negation** — "exclude the parent,
include the child" is inexpressible; tree entries MUST sort by raw UTF-8
name bytes; restore re-roots `Path.Combine` at any component that looks like
a drive letter, so top-level names must be plain components; prior-version
lookup falls back to path identity, so a coordinate change is a one-time
re-capture of metadata, not of content.

## Decision

1. **A single-root set keeps today's snapshot shape, bit for bit.** No
   synthetic root, no prefix, no migration of existing archives. The new
   shape engages only past one root: a synthetic `/` root entry
   (empty metadata, name-normalisation *unknown* — the existing synthetic
   precedent) whose children are **one directory per root, named by its
   label**, each carrying the real root folder's stat metadata. Rule
   subjects for a multi-root set are `<label>/<relative>`. The format
   already permits every byte of this; [06 §5](../../specifications/repository-format/06-manifests.md#5-tree-manifest)
   now says so in place.
2. **Labels are persisted and materialised once, at edit time.** The upsert
   derives a missing label from the folder's leaf name (sanitised to a
   plain component, `root` when nothing survives, numeric-suffix dedupe)
   and saves it; nothing ever derives labels on read, because a later
   sibling deriving differently would silently move an existing root's
   snapshot coordinates. Validation: plain NFC components, no
   `/ \ : * ?`, not `.`/`..`, ≤255 UTF-8 bytes, unique case-insensitively —
   a case-insensitive restore target would fold two labels into one folder.
3. **Roots iterate in ascending raw-UTF-8 label bytes** — the tree codec's
   entry-order MUST, applied at the outer level. Not `string.CompareOrdinal`:
   UTF-16 code-unit order diverges from byte order above the BMP.
4. **`source_filesystem` is the conservative intersection** of the per-root
   probes: case-insensitive if any root is, sparse-capable only if all are,
   minimum of each limit, names joined with `+`. One signed map stays one
   signed map; a reader planning a restore gets the weakest promise any
   root made.
5. **Every root must exist or the run refuses** — `FailedRecoverable`,
   naming each missing root. Capturing with a root silently absent would
   publish a snapshot in which that whole labelled subtree reads "deleted".
   The preview verb refuses the same way, with the same words.
6. **Configuration schema 3.** `roots: [{path, label}]` replaces `root`; a
   v2 file is migrated in memory on load (single `root` → one-entry
   `roots`) and written back as schema 3 on its next save; a set speaking
   both forms is refused by name, never guessed at.
7. **Contract 1.9 → 1.10, additive.** `BackupRootDescriptor(path, label?)`;
   the set descriptor gains `roots` with `root` back-filled from the first
   root for older clients; upsert accepts either, `roots` winning, neither
   refused. `preview_set_changes` gains draft `roots` and a **draft mode**:
   a set name that resolves nowhere is still answered when draft roots are
   given, classified against an empty baseline — what an editor building a
   brand-new set needs.
8. **The 1↔N transitions re-anchor saved rules, per rule.** Growing past
   one root prefixes the old root's anchored rules (those containing `/`,
   not `re:`) with its new label; shrinking to one strips the survivor's
   prefix — and a stripped rule left with no `/` becomes an exact-path
   regex (escaped by hand: the rules-v1 subset refuses the
   backslash-alphanumeric escapes `Regex.Escape` emits), because a bare
   name is the any-depth shorthand and would silently widen. Glob-carrying
   single components stay and widen honestly. Only rules the set already
   carried are rewritten — a rule the client just sent is trusted to speak
   the new coordinates — and the change is narrated in the material-edit
   answer. Identity-fallback keeps ancestry and content reuse across the
   coordinate move.
9. **The editor is a machine-wide checkbox tree compiling to roots +
   excludes, never includes.** Checkbox marks override what they inherit;
   the deepest fully-ticked folder IS the root. Include rules are global
   per set, so one root emitting includes would force blanket includes on
   every sibling root — the compiler never emits one. Re-ticking under an
   unticked parent meets exclude-wins head on: the compiler enumerates the
   parent's *other* children as excludes and warns that new arrivals there
   will be captured. Reopening an editor consumes literal anchored
   excludes back into unticked boxes; every other rule stays a hand chip.
   Feedback is a summary recomputed per click plus a debounced dry
   `preview_set_changes` walk with the draft roots.
10. **Restore needs no code change.** Labels are plain components, so the
    layout is `<output>/<label>/…` through the existing planner and
    executor. An empty non-captured labelled root folds away, as any empty
    directory always has.
11. **The failure domain answers for the weakest root.** The scalar
    derivation stays; a local-path destination earns `SameMachine` only
    when *every* root is known to sit on a different volume from it — any
    shared or unknowable volume keeps `SameVolume` (FR-SNP-007's
    conservatism, extended pointwise).

## Consequences

**Positive** — one set, one schedule, one retention policy, one snapshot
for folders on any mix of drives; existing archives and single-root sets
are untouched down to the byte; the editor finally points at the machine;
and the whole surface is additive on the contract.

**Negative** — the re-check wall's sibling enumeration excludes by name,
so something *new* appearing beside a kept child is captured until the
operator revisits — the warning says exactly this, and it is the honest
price of a dialect with no negation. Rule re-anchoring rewrites saved
rules, which surprises anyone diffing the config file — hence the
narration. The preview walks every draft root on each debounce, which is
real IO on large sources. And a 1↔N transition re-coordinates paths, so
the next backup re-captures metadata for everything moved (content is
reused through identity fallback).

**Neutral** — the conservative `source_filesystem` intersection can
under-promise (a case-sensitive root inside a mostly-insensitive set reads
insensitive); labels are a new name a restore surfaces that the user may
never have typed; and the synthetic root's empty metadata means the
snapshot's top level restores with default directory permissions, exactly
as the single-root top level always has.

## Alternatives considered

- **One snapshot per root under one set.** No format question at all — and
  n snapshots per run, n× status rows, no single point in time, and
  retention counting generations per root. The set's whole meaning is "one
  moment, together".
- **Deriving labels on read.** No schema change — and adding a root can
  rename a sibling's snapshot subtree, which is silent coordinate
  corruption. Persisting the label is what makes decision 8's re-anchoring
  a *narrated, one-time* event.
- **Recording per-root `source_filesystem` maps.** More faithful — and a
  format change to a signed manifest for data no current reader consumes.
  The conservative intersection costs nothing and can be revisited when a
  reader exists.
- **Compiling re-checked children as includes.** It reads naturally — and
  rules-v1's include list is set-global: one root's re-check would force
  include rules that blanket every other root. The sibling-enumeration
  wall is uglier and correct.
- **A `roots` array on the snapshot manifest.** The format does not record
  the single root today; starting to record several would be new normative
  surface for something the tree already expresses better.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | Written after the exploration that found the single root absent from the format and the five sharp constraints above |
| 2026-08 | Accepted | Built end to end: engine adapter and intersection, schema 3, contract 1.10 with draft preview, upsert labels and re-anchoring, refusing runner, conservative domain, and the console's machine-wide tree with its compiler and live feedback |
