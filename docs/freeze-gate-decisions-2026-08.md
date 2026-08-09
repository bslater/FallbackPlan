# Two decisions the freeze gate forced

**Status:** decided · **Amends:** [ADR-0006](adr/0006-object-identifiers-and-dedup-trust-domains.md), [FR-DED-003](requirements/functional.md) · **Resolves:** [PT-12](review/2026-08-fix-pressure-test.md#pt-12--device-attribution-and-verify-on-reuse-state-live-only-in-a-disposable-cache)'s open half · **Settles a convention:** [06 §4.3](../specifications/repository-format/06-manifests.md#43-what-name-must-contain)

---

Two pieces of outstanding work turned out to be blocked on choices rather than on effort, and the [format freeze gate](roadmap.md#format-v1-freeze-gate) is why both had to be made now rather than when the code got there.

They are unrelated in subject and identical in shape. One would have added a new object to the repository format; the other would have had implementers inventing a convention independently, in four places, and disagreeing. In both cases the expensive mistake is not choosing wrong — it is choosing *late*, after something has been built that assumes an answer.

| | Decision | Why it could not wait |
|---|---|---|
| 1 | Verification outcomes are **catalogue state**; a rebuild re-imposes the read | The alternative is a repository object, and v1 freezes |
| 2 | A name with no valid decoding renders **percent-encoded** | Four call sites would each have guessed |

---

## Decision 1 — verification outcomes live in the catalogue, not the repository

### The question

The default dedup trust domain fetches, decrypts, and confirms another writer's object before referencing it ([ADR-0006](adr/0006-object-identifiers-and-dedup-trust-domains.md)). That confirmation has to be remembered, or every backup re-reads every shared object forever. Remembering it is easy; remembering it *durably* is a format question.

[PT-12](review/2026-08-fix-pressure-test.md#pt-12--device-attribution-and-verify-on-reuse-state-live-only-in-a-disposable-cache) named the failure precisely: a user deletes the catalogue on support advice, it rebuilds, and the next backup re-downloads and re-verifies every previously confirmed shared segment — a routine incremental becomes hours of egress. It offered two ways out and deliberately chose neither.

### The options

**A durable repository object recording verification outcomes.** Survives a rebuild, so the read is paid once ever. It is also a new object type, a new key layout, a new lifecycle question for the collector, and — most of all — **frozen into format v1** on the strength of a cost nobody has yet paid.

**Accept re-verification after a rebuild, and say so.** No format surface. The cost is that losing the catalogue becomes expensive rather than merely slow, in exactly the situation where the user has already been told the catalogue is disposable.

### Chosen: accept re-verification

Three reasons, in order of weight.

**The cost it avoids does not exist yet.** Verification only runs against *another writer's* objects. There is no second writer, because replication is not built. A format object designed to optimise a workload that has never run is the definition of speculative surface, and [NFR-COMP-004](requirements/non-functional.md)'s whole posture is that an independent implementer should have less to reimplement, not more.

**Nothing is foreclosed.** Adding an optional object later is a minor-version change under [ADR-0014](adr/0014-format-versioning-and-stability.md). If a real multi-writer deployment measures the re-verification cost and finds it unacceptable, the answer is available and the evidence for it will exist by then. Deciding now would spend the format budget before the evidence.

**The failure is loud, bounded, and already understood.** A rebuild is a rare, user-initiated event that the system already warns costs something. It re-imposes the read once, not per backup, and only for objects this device did not write.

### What it costs, measured

The gate reads **writer attribution first, domain second**, and that ordering is what keeps the default affordable — a device never verifies bytes it wrote itself, in any domain.

| Situation | Store reads |
|---|---:|
| Second backup of an unchanged tree, single writer | **0** |
| Second writer's first publication (confirming the first writer's objects) | **9** |
| Its next publication of the same tree | **0** |
| After deleting that device's catalogue | back to **9** |

The 9 and the 0 are the same fixture, and the test that holds them corrupts the blob *between* the two publications: a reuse that still succeeds can only mean the bytes were not read again. The single-writer zero is not "few reads" but none — attribution is answered from the catalogue, so an unchanged tree touches the store not at all, which is the literal text of [FR-DED-002](requirements/functional.md)'s acceptance criterion.

NFR-PERF-003's fast path is untouched. An unchanged file short-circuits on identity, size, and modification time before any segment is considered, so no verification read is reachable from it.

### Where the decision is written down

In four places, because each has a different reader: [ADR-0006 §What is deliberately not solved](adr/0006-object-identifiers-and-dedup-trust-domains.md#what-is-deliberately-not-solved) for the reasoning; [PT-12](review/2026-08-fix-pressure-test.md#pt-12--device-attribution-and-verify-on-reuse-state-live-only-in-a-disposable-cache) so the pressure-test finding is closed rather than left open; [FR-DED-003](requirements/functional.md), whose acceptance criterion previously required durability and would otherwise have been silently unmet; and the catalogue schema's own DDL comment, which is where an implementer will actually meet it.

---

## Decision 2 — a name with no valid decoding renders percent-encoded

### The question

A POSIX filename is a byte sequence with no encoding guarantee. The format already stores it correctly — `name` is raw bytes, and [06 §4.3](../specifications/repository-format/06-manifests.md#43-what-name-must-contain) says so normatively. The problem is everywhere a **host string** is unavoidable: terminal output, the restore receipt's JSON, and the catalogue's path key. Something has to be shown, and whatever is shown becomes a convention the moment two call sites do it differently.

### The candidates, each rejected on one property

| Convention | Lossless | Valid UTF-8 | Typeable back in |
|---|:---:|:---:|:---:|
| **Percent-encoding** (`%XX`) | ✅ | ✅ | ✅ |
| Surrogate-escape (`U+DC80`–`U+DCFF`, PEP 383) | ✅ | ❌ | ❌ |
| `U+FFFD` for display, bytes authoritative | ✅ | ✅ | ❌ |

**Surrogate-escaping** is the seductive one: it round-trips inside a .NET string with nothing visibly wrong. That is exactly its defect — the resulting string is not valid UTF-16, so the receipt's JSON writer and every UTF-8 stream either throw or substitute. It does not remove the loss; it moves it to the edge, where it is harder to see and happens later.

**`U+FFFD` for display only** is honest and keeps the bytes authoritative, but produces a name the user cannot paste back as an argument — and the first thing anyone does with a filename they were shown is try to use it.

### Chosen: percent-encoding

`%` followed by two uppercase hexadecimal digits for each byte that is not part of a valid UTF-8 sequence, with a literal `%` in an otherwise-decodable name rendered `%25`. It is the only candidate that is simultaneously lossless, valid UTF-8, and typeable — and it is an existing convention rather than an invention, so a user meeting one has seen the shape before.

**This is a rendering rule and nothing else.** `name` in the format stays raw bytes. No percent-encoded form is ever stored in a manifest, and an implementation that encodes *into* the field rather than *out of* it has stored a name the file does not have — which is the precise failure [06 §4.3](../specifications/repository-format/06-manifests.md#43-what-name-must-contain) exists to prevent.

### The implementation stays deferred, and that is a separate call

Capturing such a name — rather than refusing it with error-manifest reason 8, which is what happens today — needs a byte-native relative path end to end. That is: a catalogue schema bump, a receipt schema bump, byte-native rule matching, and native `openat`/`mkdirat` writes in a restore path that does not reference `Filesystem.Local` at all today.

Deferred past the freeze on three grounds. **The format needs nothing** — it already stores the bytes. **Today's behaviour is a clean refusal**, not silent loss: the entry appears in the error manifest and the user is told. And **the freeze gate has no claim on it**, so doing it now would spend the gate's schedule on work the gate does not need.

What the deferral used to cost was the risk that someone would build the display path against a guess before the rule existed. That risk is what this decision removes, and it is why the convention was written down while the work was being put off rather than after.

---

## Two things decided along the way that nobody asked about

Both are recorded here because they are the kind of small call that is invisible until it is wrong.

**A record that fails verification is reported as a damage finding, not a security finding.** FR-DED-003's acceptance criterion said "security finding". The repository reserves that kind for signature failures, where the evidence supports attributing intent; an AEAD failure on another writer's record is equally explicable as bit rot, and nothing available can tell the two apart. Reporting it as a security finding would assert an attribution the evidence does not carry. The requirement was amended to match the evidence rather than the code amended to match the requirement — the reverse of the usual direction, and deliberately so.

**FR-DED-004 stays unmet, and its matrix cell says why.** `repository-unverified` works and is tested, but nothing requires the acknowledgement that enabling it means accepting another repository member can corrupt your backup. That gate belongs in the client that offers the choice, not in the engine that obeys it. Recording it as unmet is more useful than implementing an acknowledgement in the wrong layer to turn a cell green.

---

## What this does not settle

**The durable verification object is deferred, not rejected.** If a multi-writer deployment measures the rebuild cost and finds it unacceptable, the object is the answer and this document is the record of why it was not built first.

**FR-DED-004's acknowledgement gate is unbuilt**, so the one domain that leaves [T-10](threat-model.md#t-10-malicious-repository-member-poisons-deduplication) open can still be selected without anyone being told what it means.

**The byte-native path is unbuilt**, so a POSIX name that is not valid UTF-8 is still refused rather than captured. The convention above is what that work will render with when it happens; it is not evidence that it has.

---

**See also:** [implementation status](implementation-status.md#0006--the-integrity-guard-is-built-and-one-thing-is-deliberately-not) · [threat model T-10](threat-model.md#t-10-malicious-repository-member-poisons-deduplication) · [metadata encoding benchmark](metadata-encoding-benchmark.md) · [roadmap — format v1 freeze gate](roadmap.md#format-v1-freeze-gate)
