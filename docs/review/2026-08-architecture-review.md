# Architecture Review — August 2026

**Subject:** *FallbackPlan — Architecture and Implementation Plan* (initial proposal)
**Source under review:** [`2026-08-original-proposal.md`](2026-08-original-proposal.md)
**Outcome:** revised document set under [`docs/architecture/`](../architecture/), [`docs/requirements/`](../requirements/), [`docs/threat-model.md`](../threat-model.md), [`docs/adr/`](../adr/)
**Status:** complete — all findings resolved in the revised set or recorded in [`open-questions.md`](../open-questions.md)

---

## Purpose and method

The proposal is a strong document. Its principles are right, its survey of prior art is honest, and it correctly identifies that recoverability rather than feature count is the thing to build first. This review does not re-litigate any of that.

What it does is read the document as an *implementation contract* and look for places where two parts of it cannot both be true. That matters more than usual here, because a backup repository format is close to unchangeable once real data exists in it: every defect below is cheap to fix in prose today and expensive-to-impossible to fix once a user's only copy of their photographs is stored in the format.

Findings are graded by what happens if we build the document as written:

| Grade | Meaning |
|-------|---------|
| **Critical** | The document contradicts itself, or specifies something that leads to silent data loss or a cryptographic failure. Cannot be built as written. |
| **High** | Buildable, but a load-bearing detail is missing, unmeasurable, or a phase gate cannot be met. |
| **Medium** | Correctness is not at risk; clarity, completeness, or reviewability is. |

Every finding quotes the original wording so nothing is silently dropped.

**Summary: 6 critical, 7 high, 8 medium.**

---

## Critical findings

### C1 — Immutable manifests embed physical locations that compaction changes

**Where:** §7.8.1, FR-ARCH-010, FR-MAN-003, §11.2 step 6

> **§7.8.1:** "segment reference: plaintext hash or keyed content identity, logical file offset, logical length, **blob ID, physical record offset, stored length**, compression profile, encryption profile, and key generation"

> **FR-ARCH-010:** "File versions shall be reconstructed from ordered segment references containing logical offset, logical length, plaintext hash, **blob identifier, record offset, stored length**, compression profile, and encryption profile."

> **§11.2 step 6:** "Write replacement blobs before updating indexes."

These cannot both hold. Blob compaction exists to reclaim space from blobs that are mostly-unreachable: it reads the still-live records out of several old blobs and writes them into new ones. That necessarily changes the blob ID and the record offset of every record it moves.

But those two fields live inside the file-version manifest, which the document declares immutable in five separate places ("Manifests are never modified", §5.2; "immutable manifest", FR-MAN-003). So the first compaction pass either rewrites immutable objects — abandoning the property that the entire durability argument rests on — or leaves every affected manifest pointing at a blob that no longer exists.

This is not a corner case. It fires on the first run of a routine maintenance operation, and the symptom is unreadable historical snapshots.

The document already contains the correct answer without noticing it. FR-MAN-007 puts physical layout in the blob's own recovery footer, and §7.9 puts it in the index:

> **FR-MAN-007:** "Every sealed blob shall include an authenticated footer or sidecar recovery index listing each contained record, its keyed object identifier, physical offset, stored length, logical length, and encoding profiles."

**Resolution.** Physical location is index state, not manifest state. A manifest segment reference carries only logical facts — logical offset, logical length, and the segment's **logical object identifier**. Resolving that identifier to `(blob, record offset, stored length)` is the index's job, and the blob footer is the recovery path when the index is lost.

Compaction then becomes what it should be: republish index entries mapping the same object identifiers to new physical locations, and touch no manifest, no tree, and no snapshot. The immutability claim survives, and so does §11.2.

Applied in [`02-repository-format.md`](../architecture/02-repository-format.md), [`07-retention-and-gc.md`](../architecture/07-retention-and-gc.md), [ADR-0007](../adr/0007-logical-object-identifiers-in-manifests.md); requirements FR-ARCH-010, FR-MAN-003 rewritten.

---

### C2 — Nonce uniqueness is asserted but never constructed

**Where:** NFR-SEC-003, §7.6

> **NFR-SEC-003:** "Nonce uniqueness and key separation shall be guaranteed **by construction** and tested across concurrent writers and resumed operations."

> **§7.6:** "ensure nonce uniqueness by construction rather than probability alone"

The requirement is exactly right, and it is the only place in the document where a mistake is unrecoverable rather than merely expensive. It is also the only significant requirement with no corresponding design. §7.6 restates the goal and moves on.

The difficulty is real, which is presumably why it was deferred. Under AES-256-GCM with a 96-bit nonce, a single repeated `(key, nonce)` pair leaks the XOR of two plaintexts and — far worse — allows an attacker to recover the GHASH authentication subkey, after which they can forge arbitrary authenticated records. The document's own architecture creates two independent ways to hit that:

1. **Concurrent writers** (§8.1 direct-store mode, §8.2) share a data-encryption key generation and write blobs simultaneously with no coordination channel. A counter needs partitioning; random 96-bit nonces need a birthday-bound budget nobody will track.
2. **Resumable spools** (FR-ARCH-011) explicitly restart interrupted blob construction from a checkpoint. If the nonce sequence is derived from anything that resets — a per-blob counter, a per-session counter, a timestamp — replaying a spool re-emits a nonce under the same key. This is the single most likely way to actually break it in practice, and FR-ARCH-011 makes it a designed-for path rather than an accident.

**Resolution.** Remove the need for coordination entirely by never sharing a key between blobs:

```
blob_salt        ← 256 bits from a CSPRNG, once per blob, stored in the blob's cleartext envelope
blob_key         ← HKDF-Expand(data_key[generation], "fbp/blob/v1" ‖ blob_salt, 32)
record nonce     ← 96-bit big-endian record ordinal within that blob (0, 1, 2, …)
```

Every blob has its own key, so nonce uniqueness only has to hold *within one blob*, where a single writer owns a strictly increasing ordinal. Concurrent writers cannot collide because they hold different keys. A resumed spool cannot collide because resumption replays the same `(blob_salt, ordinal)` pairs under the same derived key, producing byte-identical records — idempotent rather than catastrophic. A *restarted* spool draws a fresh salt and is therefore a different key.

Associated data binds each record to its context so records cannot be moved between blobs, repositories, or object types:

```
AAD = repository_id ‖ format_version ‖ object_type ‖ object_id ‖ record_ordinal
```

Applied in [`03-crypto.md`](../architecture/03-crypto.md), [ADR-0005](../adr/0005-aead-suite-and-nonce-construction.md); NFR-SEC-003 rewritten with the construction and its test obligations.

---

### C3 — Cross-device deduplication has no integrity guard

**Where:** §7.3, FR-MAN-006, §8.1, NFR-SEC-004

> **FR-MAN-006:** "The local catalogue shall support lookup of a segment plaintext hash and segmentation/encryption domain to determine whether a reusable segment already exists."

> **§8.1:** "Multiple trusted clients hold repository credentials and write immutable objects directly."

Deduplication across devices is a genuine feature — a household backing up four laptops that share an operating system and a music library benefits enormously. But the document enables it without asking what happens when one of those "trusted clients" is wrong.

The attack is straightforward. All writers in a repository share the content-ID key, so device B can discover that a segment with plaintext hash `H` already exists and reference it instead of uploading its own copy. Device B has no way to check that claim without downloading and decrypting the segment — which is precisely the work deduplication exists to avoid. A device A that is compromised, or merely running a build with a bug in its hashing path, can publish a record labelled `H` whose contents are something else. Every device that subsequently deduplicates against it silently backs up corrupt data.

The document does catch this, but too late to help:

> **FR-RST-002:** "Restore shall verify each segment after decryption and the complete reconstructed file before reporting success."

Verification at restore time detects the corruption at the exact moment the user needs the file and the source copy is gone. For a backup product that is barely better than not detecting it. The failure is also silent in the meantime: §17's "last verified restore point" would report healthy, because nothing verifies a segment the local device believes it already has.

Note that this is *not* the classic convergent-encryption confirmation-of-file attack, which §7.3's keyed object identifiers already handle correctly against the storage provider. This is an integrity attack by a repository *member*, and keyed identifiers do nothing about it because members hold the key.

**Resolution.** Make the trust boundary explicit rather than implicit, via **deduplication trust domains**:

| Mode | Behaviour | Default for |
|------|-----------|-------------|
| `device` | A device reuses only segments it wrote itself. No cross-device dedup. | All repositories, including single-user ones |
| `repository` | Any member's segments may be reused, after **verify-on-reuse**: fetch, decrypt, and confirm the plaintext hash before referencing. | Opt-in, single trust domain only |
| `repository-unverified` | Reuse without verification. Fastest, and only sound when every writer is equally trusted. | Opt-in, requires explicit acknowledgement |

`device` as the default costs some storage in the multi-device case and costs nothing in the single-device case, which is the overwhelmingly common one. `repository` keeps most of the bandwidth saving — verify-on-reuse downloads the segment but avoids re-uploading and re-storing it — while restoring the integrity guarantee.

A secondary consequence is worth stating plainly in the threat model: in any mode other than `device`, a member can detect whether another member has backed up a *known* file by observing whether deduplication hits. That is inherent to cross-device deduplication, not a flaw in this scheme, and `device` mode is the answer for anyone who cares.

Applied in [`03-crypto.md`](../architecture/03-crypto.md), [`threat-model.md`](../threat-model.md), [ADR-0006](../adr/0006-object-identifiers-and-dedup-trust-domains.md); new requirements FR-DED-001..004.

---

### C4 — Garbage collection can delete blobs belonging to an in-flight snapshot

**Where:** §11.2 steps 1–4, §5.1 (*Lease*), §7.10

> **§11.2 step 4:** "Account for active writer leases and grace periods."

> **§5.1:** "**Lease** — Time-limited coordination record for a writer or maintenance operation."

§7.10 establishes the publication order — blobs first, index deltas second, snapshot last — which is correct and matches restic's hard-won rule. But it also creates a window, potentially hours long for a large initial backup, in which a writer's blobs are durable in the store and referenced by nothing. To a mark-and-sweep collector, they are indistinguishable from garbage.

The document's answer is leases. Leases cannot carry that weight:

- A lease is a *timed* record, and §14 lists no trusted time source. Clock skew between the writer and the collector directly translates into blobs being swept while still in use.
- The store is explicitly allowed to be eventually consistent (§7.9: "no correctness dependency on object-listing freshness"). A collector may simply not see a lease that was written seconds ago.
- Renewal is a liveness property. A writer paused by a laptop lid, a suspended VM, or an OS scheduler hiccup loses its lease while its blobs remain perfectly legitimate.
- Nothing in the document ties a lease to *which* blobs it protects, so a collector cannot act on one except by refusing to collect at all.

The consequence is data loss inside a completed snapshot, discovered at restore.

**Resolution.** Invert the dependency: derive reachability from a durable, self-describing record rather than from a liveness signal.

Before uploading its first blob, a writer publishes a **write-intent record** to the journal naming its writer ID, the sequence, and the blob identifiers it intends to create (extended by further intent records as the job grows). The collector treats every blob covered by an unretired intent record as reachable, full stop. The writer retires the intent when its snapshot is published; an abandoned job's intent expires only after a grace period that exceeds the longest permitted job duration, and its blobs are then genuinely unreferenced.

Leases are demoted to what they are actually good for: an advisory optimisation that stops two collectors doing redundant work. GC safety rests on four things, none of which is a clock: the generation cut-off, unretired intent records, the tombstone grace period, and revalidation immediately before physical deletion.

Applied in [`04-concurrency-and-publication.md`](../architecture/04-concurrency-and-publication.md), [`07-retention-and-gc.md`](../architecture/07-retention-and-gc.md), [ADR-0009](../adr/0009-garbage-collection-safety.md); new requirements FR-GC-001..006.

---

### C5 — Snapshot commit is defined so that one offline destination stalls all protection

**Where:** FR-SNP-001, §7.10, §8.4

> **FR-SNP-001:** "The engine shall publish immutable point-in-time snapshots **only after all required blobs and index deltas are durable**."

Read against §8.4, "required" is ambiguous in a way that matters:

> **§8.4:** "Snapshot complete when: local repository: durable, **and** at least one trusted peer: durable"

If a snapshot may only be published once every destination named by the policy has the data, then a peer that is switched off for a fortnight's holiday means no snapshot is published for a fortnight. Local protection — which is working perfectly — is withheld because a remote destination is unavailable. Worse, the deduplication and version-comparison machinery has nothing to compare against, so the eventual catch-up is far more expensive than a series of incremental snapshots would have been.

The document is caught between two ideas that are both correct in isolation: §7.10's within-a-replica ordering rule (never reference an object that is not already durable *here*), and §8.4's cross-replica health policy. Collapsing them into one requirement makes protection hostage to the least available destination.

**Resolution.** Separate **commit** from **replication**.

*Commit* is per-replica and keeps §7.10 exactly as written: within a given replica, a snapshot becomes visible only after every object it references is durable *in that replica*. This is a local invariant, always satisfiable, and it is what makes a replica independently restorable.

*Replication* is separate state. Each `(snapshot, destination)` pair carries its own status — `pending`, `replicating`, `durable`, `verified` — and §8.4's policy is evaluated over that state to produce the user-facing health model in §17. A snapshot is committed locally and immediately protective; it becomes *policy-compliant* when its destinations catch up.

This also fixes a reporting problem the original could not express: it gives §17 a truthful way to say "protected locally, waiting on the offsite copy" instead of showing no recent backup at all.

Applied in [`04-concurrency-and-publication.md`](../architecture/04-concurrency-and-publication.md), [`09-replication-and-peers.md`](../architecture/09-replication-and-peers.md), [ADR-0011](../adr/0011-commit-versus-replication-semantics.md); FR-SNP-001 and FR-REP-001 rewritten.

---

### C6 — Checkpoint compaction requires a complete listing the design forbids relying on

**Where:** §7.9, FR-MAN-008

> **§7.9:** "periodic authenticated checkpoint generations that compact prior deltas … **no correctness dependency on object-listing freshness or eventual-consistency timing**"

> **FR-MAN-008:** "Writers shall publish immutable index deltas after blobs are durable. Periodic checkpoint indexes shall compact deltas by shard without invalidating prior generations."

Compacting "prior deltas" into a checkpoint means knowing what the prior deltas *are*. The only mechanism the document offers for discovering them is enumerating the `/index/delta/` prefix — a listing. On an eventually consistent store, that listing may omit a delta that was written moments ago. The compactor then produces a checkpoint that silently drops it, and every reader that trusts the checkpoint and discards the superseded deltas loses the index entries for a set of blobs. Those blobs are still present and perfectly readable; the repository just no longer knows where anything in them lives, and the next GC pass sees them as unreachable.

The document is also silent on the concurrent case. §8.1 permits multiple direct-store writers, so two of them can decide to compact at the same generation. Nothing says which checkpoint wins, or what a reader does when it finds two.

**Resolution.** Make deltas discoverable without a global listing, and make checkpoints explicit about what they claim to cover.

- Each delta carries `(writer_id, sequence, predecessor_delta_id)`, with `sequence` strictly increasing per writer and no gaps. A reader that has any delta from a writer can walk backwards, and can *detect* a gap it has not seen rather than silently assuming completeness.
- Each checkpoint enumerates the **exact delta IDs it subsumes** and the per-writer high-water sequence it covers. A reader keeps applying any delta whose sequence exceeds the checkpoint's watermark for that writer, whether or not the listing showed it.
- A delta is only retired once a checkpoint that explicitly names it has been durable for the safety window in §7.9.
- Two checkpoints at the same generation are **both retained and both applied**. This is safe precisely because index deltas are immutable, idempotent, and commutative: applying the union of two overlapping checkpoints yields the same catalogue state as either alone plus the difference. No election, no lock, no tie-break.

Listing remains a useful accelerator for finding a recent checkpoint quickly. It is no longer load-bearing for correctness, which is what §7.9 asked for in the first place.

Applied in [`02-repository-format.md`](../architecture/02-repository-format.md), [`04-concurrency-and-publication.md`](../architecture/04-concurrency-and-publication.md), [ADR-0008](../adr/0008-index-generations-and-checkpoints.md); FR-MAN-008 rewritten, FR-MAN-013 added.

---

## High findings

### H1 — Fixed-size segmentation is under-argued and its review is scheduled after the point of no return

**Where:** §7.4, FR-ARCH-014, §25 P0 item 20

> **§7.4:** "It is less tolerant than content-defined chunking when bytes are inserted near the beginning of a file, because subsequent fixed segment boundaries shift."

> **§25 P0 item 20:** "Benchmark fixed segmentation and record/blob profiles; **defer content-defined segmentation to a later comparative design spike**."

Choosing fixed-size segmentation for v1 is defensible, and the document's instinct is sound: it is simpler, it makes positional version comparison trivial, it gives deterministic test fixtures, and it is genuinely *better* than content-defined chunking (CDC) for the in-place-rewrite workloads that dominate large-file churn — VM disks, database files, mailbox stores, disk images. Those are also the files where the saving matters most in absolute bytes.

The problem is the schedule, not the choice. One sentence of acknowledgement is not proportionate to the risk, and deferring the comparison to "a later spike" places it *after* the format has been frozen and real repositories exist. The affected workloads are not exotic:

- any file that grows at the front (prepended logs, some container and archive formats);
- formats that rewrite wholesale on save — `.docx`, `.xlsx`, `.zip`, and anything else that is a recompressed container, where a one-character edit changes essentially every byte;
- SQLite and similar files after a `VACUUM`, which shifts page contents;
- any editor that writes a new file and renames over the original with different leading bytes.

For a product whose headline promise is long version history over continuous backup, incremental efficiency *is* the product. Getting this wrong shows up as a storage bill and a bandwidth bill, both of which arrive after users have committed data.

**Resolution.** Keep fixed-size as the v1 default — the reasoning above holds — but stop treating CDC as future work:

1. **Specify the CDC profile now.** Segment records already carry a segmentation-profile field (FR-ARCH-014), so a second profile costs nothing structurally. Writing it into the format specification today guarantees the field is actually sufficient to describe one, which is the part that is expensive to discover later.
2. **Move the benchmark before the freeze.** A corpus comparison of fixed versus CDC on representative real data becomes a gate on format v1 freeze, not a spike after it. If CDC wins decisively on that corpus, the default changes while changing it is still free.
3. **Make the profile per-backup-set, not per-repository.** A backup set holding VM images and one holding a document folder want different answers, and there is no reason to force a single choice on a repository that contains both.

Applied in [`02-repository-format.md`](../architecture/02-repository-format.md), [ADR-0002](../adr/0002-segmentation-strategy.md), [`roadmap.md`](../roadmap.md); FR-ARCH-001 and FR-ARCH-014 rewritten.

---

### H2 — Two Phase 0 exit criteria cannot be met at Phase 0

**Where:** §21 Phase 0 exit criteria

> "an **independently written reader** can parse public fixtures; and one representative **CrashPlan file version** can be streamed into this same archive pipeline."

Both criteria are excellent ideas attached to the wrong milestone, and as written Phase 0 cannot exit.

The CrashPlan criterion requires a working read-only CrashPlan archive reader. That reader is Phase 5, and §13.8 makes it contingent on a research spike, a legal review, and a corpus of user-supplied archives that the project does not yet have. Making Phase 0 — the foundational engine slice, the thing everything else waits on — depend on the most legally and technically uncertain component in the plan inverts the entire dependency order.

The independent-reader criterion requires a second implementation of the format to exist, written by someone else, at the moment the format is first drafted. There is nothing to write it from and no one to write it.

**Resolution.**

- Replace the CrashPlan criterion with what it was actually trying to prove — that the ingest path is not secretly coupled to the filesystem scanner. A **synthetic legacy source adapter** feeding the pipeline an arbitrary byte stream plus a provenance record demonstrates exactly that, needs no CrashPlan archive, and doubles as the test double the real importer will be developed against.
- Move the independent-reader criterion to the **format v1 freeze gate**, where it belongs and where it is genuinely valuable: a reader written from the published specification alone, by someone who did not write the format, in a different language. That is the check that proves NFR-COMP-004, and it should block the freeze rather than the first prototype.

Applied in [`roadmap.md`](../roadmap.md).

---

### H3 — "Disposable" conflates three stores with incompatible durability requirements

**Where:** §15.2, NFR-REL-002, FR-MAN-002

> **§15.2:** "SQLite for **disposable local cache, job state, and UI configuration** — not repository authority"

> **NFR-REL-002:** "The local catalogue shall be treated as a cache. **Deleting or corrupting it shall not cause repository data loss.**"

The catalogue genuinely is disposable, and the document is right to insist on it. But §15.2 puts three different things in the same sentence and the same database, and they do not share that property:

- The **device private key** (§8.2) is not rebuildable from the repository. Losing it means the device loses its identity, and every pairing in §16.2 has to be re-approved by hand at the other end.
- **Pairing grants and destination authorisations** are not derivable from repository contents either.
- **Job history and schedules** are not required for recovery, but silently losing them means backups stop happening — and nothing alerts, because the thing that would have alerted is also gone.

NFR-REL-002 is then true of the catalogue and false of the store it shares. Anyone acting on it — a support article saying "delete the database and let it rebuild", or the rebuild tooling in §7.8.3 — destroys the device identity while following the documented advice.

**Resolution.** Three stores, three lifecycles, stated separately:

| Store | Contents | Lifecycle | In recovery kit |
|-------|----------|-----------|-----------------|
| Catalogue | Path/version/segment/blob indexes, generation watermarks | Disposable, rebuildable from the repository | No |
| Durable local state | Device keypair, pairing grants, destination authorisations, job history | Backed up, restored, or re-established by re-pairing | Device identity: yes |
| Configuration | Backup sets, schedules, policies, provider settings | Version-controlled files, exportable without secrets | Yes |

Applied in [`11-solution-structure.md`](../architecture/11-solution-structure.md), [`08-restore-and-recovery.md`](../architecture/08-restore-and-recovery.md), [ADR-0010](../adr/0010-local-store-separation.md); NFR-REL-002 scoped to the catalogue, NFR-REL-007 added.

---

### H4 — The recovery kit is load-bearing but never specified

**Where:** §5.1, §7.6, §12.4, §16.1, §20, §24

The recovery kit appears throughout the document as the thing that makes recovery possible. §24 makes it a release gate:

> "a repository can be restored from a clean machine using only repository access and a recovery kit"

§20 versions its format. §16.1 makes confirming it a mandatory first-run step. §12.4 has the emergency tool consume it. §5.1 defines it as:

> "Export containing repository identity, key material or wrapped keys, format details, and recovery instructions."

That one sentence is the entire specification. "Key material **or** wrapped keys" is the crux and it is left open — the two choices have completely different security properties, and the answer determines whether a stolen kit is game over or merely one factor of two. Nothing else is pinned down either: what identifies the repository, how a user gets from a printed kit back to a working restore, whether it can be split, or what happens when the kit's format version predates the repository's.

A release gate that depends on an unspecified artefact is not a gate.

**Resolution.** Specify contents, encoding, both representations, and the restore procedure, in the restore document and [ADR-0013](../adr/0013-recovery-kit.md):

- repository identity and format profile;
- the wrapped repository master key, plus the KDF parameters needed to unwrap it — **wrapped**, never bare, so a kit alone is insufficient without the passphrase;
- destination descriptors sufficient to *locate* the repository, with credentials explicitly excluded;
- the device identity of the issuing device;
- kit format version and the minimum recovery-tool version;
- printable representation (QR plus checksummed text, transcribable by hand) and machine-readable representation;
- step-by-step recovery instructions embedded in the kit itself, on the assumption that no other project documentation is reachable;
- an explicit statement that the kit is one factor and the passphrase the other.

Applied in [`08-restore-and-recovery.md`](../architecture/08-restore-and-recovery.md), [ADR-0013](../adr/0013-recovery-kit.md); new requirements FR-KIT-001..006.

---

### H5 — There are no quantitative performance targets anywhere

**Where:** §19, NFR-PERF-001..007

> **§19:** "Initial engineering targets, to be validated through benchmarks: bounded memory configurable independently of repository size; streaming operation for all file sizes; initial local backup capable of **saturating typical consumer storage without excessive CPU**…"

> **NFR-PERF-007:** "The implementation shall be benchmarked with millions of files, high version counts, multi-terabyte files, and repositories containing at least tens of millions of segment references."

Every statement in §19 is directional. "Excessive", "typical", "large enough", "proportional primarily to" — none of these can fail a build or block a release. NFR-PERF-007 names the right *scales* but no *outcomes*, so a benchmark run at ten million files can be declared a pass no matter what it measures.

This matters beyond tidiness. §23 lists "object-store request amplification" as a major risk with "configured-size blob objects" as the mitigation — but with no target for requests per GB, there is no way to detect that the mitigation has stopped working. The same applies to the catalogue: NFR-PERF-004 asks for "logarithmic or constant-time" lookups, which is a complexity class, not a latency, and says nothing about the catalogue's *size* — the thing most likely to make it unusable on a consumer laptop at ten million files.

**Resolution.** Replace §19 with numbered, measurable targets, each tied to a fixture and a threshold. Initial proposals, to be revised once Phase 0 benchmarks report real numbers, are in [`non-functional.md`](../requirements/non-functional.md): single-stream capture throughput on a stated reference machine; p99 catalogue path-resolution latency at a stated repository size; catalogue bytes per file version; object-store requests and PUTs per GB written; forensic rebuild rate in blobs per second; peak RSS bounds independent of repository size.

Values chosen now will be wrong. Named, measured, and revised targets are still categorically more useful than adjectives, because a wrong number gets corrected by a benchmark and an adjective never does.

Applied in [`non-functional.md`](../requirements/non-functional.md) (NFR-PERF-001..012), [`10-observability.md`](../architecture/10-observability.md).

---

### H6 — "Independently verified" trusts the destination to report on itself

**Where:** §8.4, §21 Phase 2

> **§8.4:** "Snapshot healthy when: local repository: verified within 7 days, and **trusted peer: verified within 30 days**"

> **§21 Phase 2 features:** "independent destination verification"

This is the status the entire product promise rests on — §2.2's "When was the last independently verified recoverable snapshot?" — and it is the one status a destination can fabricate for free.

A peer that has lost the data to a failed disk, quietly deleted it to reclaim space, or is simply running buggy software can return "verified" without holding anything. The word "independent" in Phase 2 signals the right intent, but no mechanism is specified, and the obvious implementations do not deliver it: asking the peer to hash a blob lets it cache the answer from the first challenge; asking for a whole blob back defeats the point of not downloading it.

The consumer-facing risk is the worst kind, because §17 will confidently display a green status derived from an unverifiable claim. §23 already warns that "consumer UI hides degraded state" leads to "false confidence"; this is that risk with a specific mechanism.

**Resolution.** A **keyed random-range challenge**. The verifier picks a blob, a random byte range within it, and a fresh nonce, then asks for `MAC(challenge_key, nonce ‖ blob_id ‖ range ‖ bytes_at_range)`. Because the nonce is fresh and the range is unpredictable, the response cannot be precomputed or cached, and answering it requires actually holding those bytes. The verifier recomputes the expected value from its own copy or from another replica.

Sampling policy is a coverage-versus-cost trade: a small random sample per interval, weighted towards blobs that have gone longest without a challenge, plus full verification on demand. §17 then reports verification *coverage* and challenge age rather than a bare boolean, so "verified" means something a user can act on.

This does not defend against a destination that holds the data but will refuse to return it later; nothing short of restoring does. That limitation belongs in the threat model, and it is now there.

Applied in [`09-replication-and-peers.md`](../architecture/09-replication-and-peers.md), [`threat-model.md`](../threat-model.md); new requirements FR-VER-001..005.

---

### H7 — The sample interfaces contradict the requirements they illustrate

**Where:** §9.1, §22, NFR-PORT-004

> **NFR-PORT-004:** "Public APIs shall use asynchronous streaming, cancellation, bounded concurrency, and **explicit result/error types**."

The document is clear that these are "conceptual starting points", so this is not a complaint about detail. But three of the problems are semantic rather than cosmetic, and each encodes a decision that would be expensive to reverse once providers are implemented against it:

```csharp
ValueTask PutAsync(ObjectKey key, Stream content, PutConditions conditions, CancellationToken cancellationToken);
```

**A single non-rewindable `Stream` cannot be retried.** Every requirement around this method assumes retry: NFR-REL-005 ("resumable, cancellable, idempotent where practical"), §9.2's throttling handling, FR-REP-003's resume at verified boundaries. But after a failed upload the stream has been partially consumed, and the caller — who by then has streamed encrypted segments through it — usually cannot reproduce it. Providers will each invent their own buffering workaround, and they will differ. The contract must state up front either "the stream is seekable and will be rewound" or "supply a factory that can produce it again".

```csharp
IAsyncEnumerable<ObjectEntry> ListAsync(ObjectPrefix prefix, ContinuationToken? continuationToken, CancellationToken cancellationToken);
```

**Continuation is expressed twice.** `IAsyncEnumerable` already models resumable iteration; a continuation-token parameter alongside it means every provider must decide which wins, and callers cannot tell whether re-enumerating resumes or restarts. Pick one — the enumerator — and surface a resume token on the entry type if callers need to persist a position across process restarts.

**Errors are exceptions, not results.** Nothing in either interface returns a status, so "conditional create" — the primitive C6 and §7.10 both depend on — reports "already exists" by throwing. That makes the single most common expected outcome an exception path, and it directly contradicts NFR-PORT-004.

**Resolution.** Corrected shapes in [`05-storage-providers.md`](../architecture/05-storage-providers.md) and [ADR-0012](../adr/0012-storage-provider-contract.md): content supplied as a re-openable factory, explicit result types distinguishing expected outcomes (`Created`, `AlreadyExists`, `PreconditionFailed`) from genuine faults, continuation owned by the enumerator, and capability probing kept out of the data path as §9.1 already intends.

---

## Medium findings

### M1 — Terminology drifts between synonyms

§5.1 defines **segment** and **blob** as the normative terms and explicitly notes that a blob is "equivalent to a pack in some backup systems". The rest of the document then uses the alternatives freely: the §6 architecture diagram has `Chunk` in the domain core; §16.3's job state machine has a `Chunking` state; §13.4's import diagram says "chunk / encrypt / pack"; §25 P0 uses both. §15's project list has `Repository.Packing`, and §5.1's own *Blob* row uses "pack" in its definition.

For a document whose stated goal is to be implementable by a third party (NFR-COMP-004), two names per concept is a defect. **Resolution:** [`01-domain-model.md`](../architecture/01-domain-model.md) is the single normative glossary; *segment* and *blob* are used throughout the revised set, with a short table of prior-art synonyms retained in one place so readers coming from restic or Kopia can map the vocabulary.

### M2 — The threat model omits metadata side channels

§14.1 covers a compromised store reading content, tampering, and rollback. It does not cover what an honest-but-curious store learns from data it is *supposed* to see. Stored record lengths reveal compressed sizes, which fingerprint file types and sometimes specific files; blob arrival timing reveals when a device is active and roughly how much changed; per-record boundaries within a blob leak the segment-size distribution. §7.5's compress-then-encrypt ordering is the right choice for efficiency, and it is also what makes the length channel exist — a trade worth stating rather than leaving implicit.

**Resolution:** added to [`threat-model.md`](../threat-model.md) with an optional record-padding policy (padding to size buckets, costing storage) for high-sensitivity backup sets, and an explicit statement of the residual leak that padding does not close.

### M3 — Cross-platform metadata semantics are named but never resolved

§10.1 requires preserving "filesystem-specific metadata through capability records" and protecting against "Unicode and case-folding collisions during cross-platform restore". §13.2 lists ACLs, alternate streams, resource forks, hard links, and sparse files. Neither says what actually happens when a macOS resource fork is restored to ext4, or when `README.md` and `readme.md` from a Linux source land on a case-insensitive APFS volume.

**Resolution:** a concrete matrix in [`06-filesystem-capture.md`](../architecture/06-filesystem-capture.md) covering each metadata class against each target platform with one of *preserve*, *degrade-and-report*, or *refuse*; plus a normative path rule — store the original bytes, record the normalisation form observed, index by a casefold key, and detect collisions at restore-plan time (§12.2) rather than mid-restore.

### M4 — Whole requirement categories are missing

The FR/NFR set covers the engine thoroughly and omits several areas the document itself raises elsewhere: licence and contribution governance (§25 P0 item 1 requires it; nothing specifies it); SBOM and supply-chain requirements (§14.3 mentions dependency pinning and signed releases, with no requirement behind them); telemetry and diagnostics privacy (§17 has a rule, §3.4/§3.5 have no requirement); time source and clock skew (§5.2 records "source clock observations" but nothing says what to do when they are wrong — and C4 shows this has correctness consequences); destination disk-full and quota exhaustion (§16.2 sets quotas, nothing says what happens on reaching one); agent version skew between paired peers (§20 versions the protocol, no requirement governs a mismatch mid-transfer); accessibility and internationalisation (§21 Phase 6 lists them as features with no requirement).

**Resolution:** added as FR-GOV-*, NFR-SUP-*, NFR-PRIV-*, NFR-TIME-*, FR-QUOTA-*, NFR-COMP-006, NFR-UX-* in the requirements set.

### M5 — Most requirements are not testable as written, and the promised traceability does not exist

§3.4 opens by saying requirement IDs exist "so they can be traced into architecture decisions, implementation work items, and tests". No such mapping is in the document. Many requirements also resist testing: FR-ARCH-005 compresses "where beneficial"; FR-ARCH-007 says sizes "shall be configuration driven"; NFR-PERF-003 says reuse happens when metadata is "trustworthy". Each describes an intention without a threshold, so no test can fail.

**Resolution:** requirements rewritten with observable acceptance criteria (for example, FR-ARCH-005 now states the compression-ratio threshold below which a segment is stored uncompressed, and the requirement to record which was chosen), and [`traceability.md`](../requirements/traceability.md) maps every ID to its architecture section, ADR where one exists, and planned test class.

### M6 — No stability posture for pre-1.0 repositories

§20 sets out good rules for format evolution — feature advertisement, safe refusal, append-only where possible, resumable migration — but never says when v1 freezes or what guarantee, if any, applies before it. Early adopters will point real backups at pre-1.0 builds; some of them will be the *only* copy. Without a stated posture, the project will find itself either shipping a breaking change that destroys those repositories or frozen on a format it wanted to revise.

**Resolution:** [ADR-0014](../adr/0014-format-versioning-and-stability.md) states the posture explicitly: pre-1.0 repository formats carry no forward-compatibility guarantee, builds must warn on creation, the format version is recorded so a build can refuse a repository it cannot read rather than misread it, and each pre-1.0 breaking change ships either a migration tool or an explicit statement that re-seeding is required. H1's CDC benchmark and H2's independent-reader gate both attach to the v1 freeze.

### M7 — Naming proximity to CrashPlan carries trademark risk

§13.3 covers interoperability and reverse-engineering carefully, and instructs that CrashPlan trademarks must not be used "in a way that implies affiliation". It does not consider the product name itself. "FallbackPlan" shares a structure, a domain, and a rhyme with "CrashPlan", and the project's principal advertised feature is reading CrashPlan archives — precisely the combination that makes a confusion argument easy to state.

This is a flag, not a legal opinion. **Resolution:** folded into the §13.3 legal review scope in [`open-questions.md`](../open-questions.md) so the name is assessed at the same time as the interoperability question, while renaming is still cheap.

### M8 — Malformed glossary table

The §5.1 term table has mismatched delimiter widths and renders inconsistently across markdown implementations. **Resolution:** rebuilt in [`01-domain-model.md`](../architecture/01-domain-model.md).

---

## What was not changed

For the avoidance of doubt, the following were examined and deliberately kept:

- **Snapshot-based backup over folder synchronisation** (§27). The reasoning is correct and the ordering of the two vertical slices is the right one.
- **Publication ordering — blobs, then indexes, then snapshot** (§7.10). This is the single most important durability rule in the document and it is right. C4, C5, and C6 all reinforce it rather than replace it.
- **The local catalogue as a pure cache** (FR-MAN-001, NFR-REL-002). Correct, and the direct answer to the Duplicati failure mode §4.5 identifies. H3 narrows its scope; it does not weaken it.
- **Keyed object identifiers** (§7.3). Correctly defends against confirmation-of-file by the storage provider. C3 addresses a different adversary.
- **Encryption with no plaintext mode** (§7.1). Keep, including the discipline of not offering it as a compatibility switch.
- **Retention selects; GC deletes** (§11.1). The separation is right and is what makes C4's fix possible.
- **CrashPlan import as a separate, optional, read-only package** (§13.3, §15.1). Correct on both engineering and licensing grounds.
- **Compress before encrypt** (§7.5). Correct for efficiency; M2 records the side channel it creates rather than reversing the decision.
- **Fixed-size segmentation as the v1 default** (§7.4). Kept — H1 changes when it is reviewed, not what it is.
