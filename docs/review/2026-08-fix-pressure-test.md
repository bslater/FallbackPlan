# Pressure test — the six critical fixes

**Subject:** the fixes for [C1–C6](2026-08-architecture-review.md), as written in the revised architecture and ADRs 0005–0009 and 0011
**Purpose:** establish whether those fixes are sound enough to accept, before Phase 0 is built on them
**Outcome:** 3 critical, 7 high, 5 medium. **No fix is reversed.** Two are unsound as written and must not be accepted until amended; the rest need specification the current text does not supply.

---

## Why this pass exists

The first review read the original proposal as an implementation contract and found six places where it contradicted itself. Those contradictions were closed quickly, under time pressure, by one author, and three of the six fixes touch the same objects during maintenance. Fixes with that profile are exactly the kind of change that introduces new contradictions.

So this pass does not re-check that each fix closes its original finding — that was verified when they were written. It asks three different questions:

1. **What did this fix break?** What was cheap before and is expensive now; what invariant did it quietly weaken.
2. **What new failure mode did it introduce?** Especially silent ones, and ones that surface only under interruption, upgrade, or concurrency.
3. **Where does it collide with the other five?**

Grades:

| Grade | Meaning |
|-------|---------|
| **Critical** | The fix is unsound as written, or reintroduces a data-loss or cryptographic failure. Must not be accepted until amended. |
| **High** | Directionally right, but with an unacceptable regression or an unspecified dependency that blocks implementation. |
| **Medium** | Works, but the reasoning given for it does not. |

Every finding names a concrete scenario. A finding that cannot be written as a failing test is not in this document.

**The headline result is that all six fixes are directionally correct and none needs reversing.** That matters: it means the first review's diagnosis was right even where its prescription was incomplete. But two of them, as currently written, would ship a defect — and one of those defects is the exact catastrophe the fix was written to prevent.

---

## Critical

### PT-1 — C2's resume guarantee silently assumes bit-reproducible compression

**Under test:** [`03-crypto.md` §3.3](../architecture/03-crypto.md#33-why-resumption-is-safe), [ADR-0005](../adr/0005-aead-suite-and-nonce-construction.md), [`02-repository-format.md` §5.3](../architecture/02-repository-format.md#53-spooling-and-sealing)

> "A resumed spool replays the same `(blob_salt, ordinal)` pairs under the same derived key. Encrypting the same plaintext at the same ordinal under the same key yields **byte-identical output**. Replay is therefore idempotent, not catastrophic."

The whole safety argument for C2 rests on that sentence, and the sentence contains an unstated premise: that the *plaintext fed to the AEAD* is the same on replay. It is not the segment's plaintext. It is the segment's plaintext **after compression** — [`02-repository-format.md` §4](../architecture/02-repository-format.md#4-compression) puts compression before encryption.

So "replays the same plaintext" is true only if recompressing the same input produces the same bytes. Zstandard is deterministic for a given library version and parameter set. It offers **no guarantee across versions**, and compressors routinely change their output when internal heuristics are tuned.

**Failure scenario**

1. Agent v1.0 (zstd 1.5.5) is writing blob `B`. It compresses segment 7 to 812 KiB, encrypts it under `(blob_key, nonce=7)`, and checkpoints the spool.
2. The machine crashes mid-blob.
3. The agent is upgraded to v1.1, which links zstd 1.5.6.
4. On restart the agent resumes blob `B` from the checkpoint. It re-reads segment 7, recompresses it — to 811 KiB, with different bytes — and encrypts **that** under `(blob_key, nonce=7)`.

Two different plaintexts have now been encrypted under the same key and nonce. Under AES-256-GCM that yields the XOR of the two plaintexts to anyone holding both ciphertexts, and — far worse — permits recovery of the GHASH authentication subkey, after which an attacker can forge arbitrary authenticated records in that blob.

This is precisely the failure C2 was written to prevent, reintroduced through the back door of an optimisation the fix never considered. It needs no attacker and no unusual configuration: a crash, an unattended update, and a resume.

**Required change.** The spool checkpoint must persist the **sealed record bytes**, not a plaintext offset. Resume then re-emits stored bytes rather than recomputing them, and byte-identity is a property of the checkpoint rather than an assumption about the codec. As a secondary guard, the checkpoint records the exact codec identity and version; a mismatch forces **restart** (which draws a fresh salt and is therefore safe) rather than resume.

The same reasoning applies to any other non-determinism between crash and resume — segmentation profile parameters, encryption profile selection. All must be pinned in the checkpoint, not re-derived.

---

### PT-2 — C6's commutativity claim is false once C1 is in place

**Under test:** [`02-repository-format.md` §7.2](../architecture/02-repository-format.md#72-deltas-and-checkpoints-without-a-global-listing), [ADR-0008](../adr/0008-index-generations-and-checkpoints.md)

> "Two checkpoints at the same generation are **both retained and both applied**. This is safe because index deltas are immutable, idempotent, and commutative: the union of two overlapping checkpoints yields the same catalogue state as either alone plus the difference. No election, no lock, no tie-break."

That argument is correct for an index that only ever *adds* mappings for new object identifiers. C1 made the index the sole authority on physical location, and blob compaction ([`07-retention-and-gc.md` §3](../architecture/07-retention-and-gc.md#3-garbage-collection) step 6) therefore **remaps an object identifier that already has a mapping**.

Two deltas that map the same object identifier to different blobs are not commutative. Order decides which one a reader ends up with, and one of the two orders resolves to a blob that has been tombstoned and deleted.

**Failure scenario**

1. Object `O` lives in blob `B1`. Delta `D1` maps `O → (B1, offset 4096)`.
2. Compaction moves `O` into `B2`. Delta `D2` maps `O → (B2, offset 128)`. `B1` is tombstoned, then deleted after the grace period.
3. Checkpoint `CP-a` subsumes `{D1, D2}`. Checkpoint `CP-b`, published concurrently by another writer at the same generation, subsumes `{D1}` only.
4. A reader applies both, per the merge rule. Nothing in the format says `D2` wins.
5. If the reader lands on `D1`, every file version referencing `O` resolves to a deleted blob. The bytes still exist in `B2`; the repository no longer knows it.

The merge rule that makes multi-writer indexing work has no supersession semantics under it. This is not a corner case — it fires on the first compaction pass in any repository with more than one writer.

**Required change.** Index entries need an explicit precedence rule, and it cannot be inferred from application order. Each entry carries the generation at which it was published; for a given object identifier, the entry with the highest generation wins, with a documented deterministic tie-break below that. Relocation entries are typed as **supersessions** rather than being indistinguishable from first insertions, so a reader can tell the difference between "two writers independently recorded the same new object" (genuinely commutative) and "this object moved" (ordered).

The commutativity claim must be deleted from both documents and replaced with the precedence rule. It cannot be softened — as written it is simply false.

---

### PT-3 — Compaction output blobs are unprotected between creation and index publication

**Under test:** [`07-retention-and-gc.md` §3](../architecture/07-retention-and-gc.md#3-garbage-collection) steps 6–7, [ADR-0009](../adr/0009-garbage-collection-safety.md)

C4's whole insight is that a blob is unreachable between upload and index publication, and that a durable write-intent record is what protects it. Step 4 of the GC algorithm applies that protection to *writers*.

The collector then does exactly the same thing to itself in step 6 — "write replacement blobs for compaction, and publish index entries" — and publishes no intent for them.

**Failure scenario**

1. Collector 1 reaches step 6 and writes replacement blob `B2` containing the live records salvaged from `B1`.
2. Collector 2 — permitted, since [`04-concurrency-and-publication.md` §8](../architecture/04-concurrency-and-publication.md#8-concurrent-maintenance) states no routine operation requires a global exclusive lock — runs its mark phase. `B2` is referenced by no index entry yet and covered by no intent, so it is not marked.
3. Collector 2 deletes `B2`.
4. Collector 1 proceeds to step 7 and publishes index entries pointing into `B2`, then step 8 tombstones `B1`, and step 11 deletes it.

Both copies of every record in that compaction batch are now gone. This is permanent loss of live data reachable from protected snapshots — the exact outcome C4 exists to prevent, in the one code path that was supposed to have absorbed the lesson.

**Required change.** The collector is a writer. It publishes a write intent naming its replacement blobs before creating them, and retires it after the index entries are durable. The GC algorithm gains that step explicitly, and the rule is stated generally: **any component that creates a blob publishes an intent first, with no exceptions for maintenance.**

---

## High

### PT-4 — Blob identifier formation is unspecified, and C4 cannot be implemented without it

**Under test:** [`04-concurrency-and-publication.md` §4.2](../architecture/04-concurrency-and-publication.md#42-the-record), [`02-repository-format.md` §5.3](../architecture/02-repository-format.md#53-spooling-and-sealing)

The write-intent record names `intended_blob_ids` before the blobs are uploaded. §5.3 says a blob is uploaded "under its final immutable identifier" and never says how that identifier is formed.

Every *record* identifier in the format is content-derived and then keyed ([`03-crypto.md` §4](../architecture/03-crypto.md#4-object-identifiers)), so a reader will reasonably assume blob identifiers are too. If they are, they cannot be known before the blob is sealed, and the intent cannot name them — C4 is unimplementable as specified.

**Required change.** State it: blob identifiers are **writer-allocated** — random, or derived from `(writer_id, sequence)` — and are *not* content-derived. Blobs are containers, not content-addressed objects; their contents are individually addressed, which is what dedup and verification actually need. Writers pre-allocate identifiers, name them in the intent, and then create them. Recorded as [ADR-0016](../adr/0016-blob-identifier-formation.md).

Note this is a genuine asymmetry in the format worth calling out for independent implementers: record identifiers are content-derived and keyed; blob identifiers are opaque and writer-allocated.

---

### PT-5 — Intent expiry mixes generation and wall-clock, and couples slow writers to busy repositories

**Under test:** [`04-concurrency-and-publication.md` §4.2](../architecture/04-concurrency-and-publication.md#42-the-record), [ADR-0009](../adr/0009-garbage-collection-safety.md)

> "An abandoned job's intent expires only after a grace period exceeding the longest permitted job duration."

Two problems. First, "the longest permitted job duration" is undefined and unbounded: an initial multi-terabyte backup over a domestic uplink runs for weeks, so any grace period that is safe for it holds abandoned blobs for weeks.

Second, the record carries `expiry_generation` while the prose describes a time-based grace period, and the two are not interchangeable. Generations advance when *other* writers publish. A busy repository can advance many generations in an hour.

**Failure scenario**

A laptop begins a 4 TB initial backup over a slow link. Three other devices back up hourly to the same repository and advance generations rapidly. The laptop's intent reaches `expiry_generation` after two days, while its job has three weeks to run. A collector treats its blobs as expired and deletes them mid-job. The laptop then publishes a snapshot referencing objects that no longer exist.

That is one writer's liveness being destroyed by other writers' activity — a coupling C4 explicitly set out to eliminate.

**Required change.** Expiry requires **both** conditions: a generation delta *and* a skew-margined wall-clock delta. The writer declares `max_duration` in the intent and refreshes it by publishing an extension; the collector honours the declared duration rather than a global constant. An administrative force-expire exists for genuinely abandoned jobs, and is audited.

---

### PT-6 — A crashed writer can permanently block readers through a sequence gap

**Under test:** [`02-repository-format.md` §7.2](../architecture/02-repository-format.md#72-deltas-and-checkpoints-without-a-global-listing) rule 1

> "`sequence` strictly increasing per writer and no gaps. A reader … can *detect* a gap it has not seen rather than silently assuming completeness."

Detection was the point, and it is right. But nothing says what a reader does next.

**Failure scenario**

Writer `W` prepares delta 41, crashes before publishing it, and on restart continues at 42 — or publishes 42 for an unrelated later job. Every reader now sees `…40, 42` and correctly detects a gap at 41. Delta 41 will never exist. If readers block on the gap, `W`'s index contributions are unusable forever. If they ignore it, the gap-detection property is worthless and C6's defence against truncation goes with it.

**Required change.** A writer that discovers it has skipped a sequence publishes a **void delta** at that number, signed, declaring it intentionally empty. Readers treat a gap as unresolved until either the delta or a void record for it appears; after a bounded number of generations with neither, they surface it as a damage finding rather than blocking indefinitely. Silence is never interpreted as "empty".

---

### PT-7 — Delta retirement is ambiguous under concurrent checkpoints

**Under test:** [`02-repository-format.md` §7.2](../architecture/02-repository-format.md#72-deltas-and-checkpoints-without-a-global-listing) rules 2–3

A delta is retired "once a checkpoint that explicitly names it has been durable for the safety window". Rule 3 permits two checkpoints at the same generation subsuming different delta sets.

**Failure scenario**

Checkpoint `CP-a` names deltas `{D1, D2, D3}`; concurrent checkpoint `CP-b` names `{D1, D2}`. `D3` is retired on `CP-a`'s authority. A reader that fetched `CP-b` and, per rule 2, applies deltas above `CP-b`'s watermark now looks for `D3` — which has been deleted. Its index entries are lost until a forensic rebuild recovers them from blob footers.

**Required change.** A delta may be retired only when it is named by a checkpoint that **no live checkpoint at or above its generation contradicts** — in practice, when every retained checkpoint at that generation names it, or when a later checkpoint supersedes all of them. Retirement is a deletion and gets the same tombstone-and-grace treatment as any other, rather than being immediate.

---

### PT-8 — `protected` does not require a replica outside the source's failure domain

**Under test:** [`04-concurrency-and-publication.md` §6.3](../architecture/04-concurrency-and-publication.md#63-policy-evaluation), [`09-replication-and-peers.md` §4](../architecture/09-replication-and-peers.md#4-durability-policy)

> ```
> Snapshot protected when:
>   - local repository: durable
> ```

C5 was right to decouple commit from replication. But it made `protected` — the primary, reassuring, user-facing state — mean "committed to the local replica", with no requirement that the local replica be anywhere other than the disk holding the source data.

**Failure scenario**

A user follows the first-run flow, selects their home folder, and accepts a local repository on the same internal SSD. Their offsite peer has never come online. The status reads `protected`. The SSD fails. Everything is gone, and the product said it was protected right up until the moment it wasn't.

This is the "consumer UI hides degraded state → false confidence" risk from §23 of the original proposal, reintroduced by the fix for C5 — and it is worse than a UI defect, because it is what the status model *is defined to mean*.

**Required change.** Replicas declare a **failure domain** (same-volume, same-machine, same-site, independent). `protected` requires at least one replica whose failure domain is disjoint from the source's. A snapshot committed only to a same-volume replica gets its own honest state — `captured` — and the first-run flow warns when the only configured destination shares a failure domain with the source. Recorded as [ADR-0018](../adr/0018-replica-failure-domains.md).

---

### PT-9 — Local retention can silently erase history a destination never received

**Under test:** [`07-retention-and-gc.md` §1–2](../architecture/07-retention-and-gc.md#1-retention-selects-collection-deletes), [ADR-0011](../adr/0011-commit-versus-replication-semantics.md)

Commit is per-replica and retention operates per-replica. Neither consults the other.

**Failure scenario**

A backup set keeps hourly snapshots for 7 days locally and replicates to a peer that is offline for a fortnight. On day 8 local retention expires the day-1 snapshots. The peer returns on day 14 and replicates whatever remains. Days 1–7 exist in no replica and never will. Nothing reported a loss, because from each side's local view the policy was satisfied exactly as configured.

**Required change.** Retention must not expire a snapshot that has not reached the destinations its own policy requires, unless a configured bound on that deferral is exceeded — at which point the resulting history gap is reported as a warning requiring action, not applied silently. The local repository is allowed to grow beyond its retention window while a destination is behind; that is the cheaper failure.

---

### PT-10 — Emergency single-file restore regressed from one fetch to a full scan

**Under test:** [ADR-0007](../adr/0007-logical-object-identifiers-in-manifests.md), [`02-repository-format.md` §8.2](../architecture/02-repository-format.md#82-forensic-rebuild), FR-MAN-010

C1 is right, and this is its cost — which ADR-0007 acknowledged only as "one indexed local lookup".

That is true when the index is healthy. When it is not, the change is much larger than the ADR admits. Before C1, a manifest plus a blob was sufficient to recover a file: the manifest said where the bytes were. Now the manifest names object identifiers and nothing else, so recovering a single file with no index means scanning blob footers until its segments are located.

**Failure scenario**

A user's machine is destroyed. They have the recovery kit and the cloud repository. Index objects were lost to a misconfigured lifecycle rule. They want one file — a document, 4 MiB. Under NFR-PERF-012 (≥500 blobs/s) a scale-**M** repository takes about two hours of footer scanning before that document can be produced. FR-MAN-010 promises restore "as soon as the required snapshot, tree, file, and blob mappings are known", and the blob mapping is now precisely the expensive part.

**Required change.** Two things, neither of which reverses C1:

1. **Reconsider the physical hint.** ADR-0007 rejected hints as "a correctness question dressed up as an optimisation". That rejection does not survive scrutiny: record headers are independently authenticated and carry the object identifier ([`02-repository-format.md` §5.2](../architecture/02-repository-format.md#52-layout)), so a reader that follows a hint and finds the wrong object *detects it* and falls back to the index. A stale hint is detectably stale, not silently wrong. A non-authoritative `last_known_blob` on the segment reference restores O(1) first-byte latency in the index-lost case at a few bytes per reference — and, crucially, it is a hint the *format validator* can require readers to verify. The counter-argument that survives is that it partially re-couples manifests to physical layout and invites implementations that trust it; the mitigation is that conformance fixtures must include a stale-hint case that any correct reader passes.
2. **Prioritised footer scanning.** Forensic rebuild accepts a target snapshot or path and scans blobs in an order informed by the target's segment identifiers, so single-file recovery does not wait for a whole-repository rebuild.

Note the interaction: if the hint is adopted, compaction no longer has to touch manifests (the hint is allowed to go stale), so C1's core property is preserved intact. This is the one finding where the pressure test recommends revisiting a decision rather than completing it, and the decision belongs to the maintainer.

---

## Medium

### PT-11 — The stated rationale for the `device` dedup default does not distinguish it from `repository`

**Under test:** [ADR-0006](../adr/0006-object-identifiers-and-dedup-trust-domains.md)

> "`device` is the default because it costs **nothing at all** in the single-device case, which is the overwhelmingly common one"

The premise is true and the conclusion does not follow. `repository` mode with verify-on-reuse is **equally** free in the single-device case, because a repository with one writer contains no other writer's segments to verify — the mode degenerates to `device` behaviour at zero cost.

So the argument given selects between the two options on a criterion where they are identical, while the difference between them falls entirely on the multi-device household: four laptops sharing an operating system and a music library, which is the classic consumer use case and this project's stated reason for existing ([`00-overview.md` §1](../architecture/00-overview.md#1-what-fallbackplan-is)). Under `device` they store four copies.

**Required change.** Either re-argue the default on grounds that actually distinguish the options, or change it to `repository`. The evidence favours changing it: `repository` is free where `device` is free, and cheaper where they differ, while retaining the integrity guarantee that motivated C3 in the first place. The residual cost is one read per first reuse and the confirmation side channel already recorded as [T-12](../threat-model.md#t-12-dedup-confirmation-by-a-repository-member) — for which `device` remains available as the hardened setting.

This is graded Medium because the fix is sound either way; it is the reasoning that fails, and the consequence is storage cost rather than data loss.

---

### PT-12 — `device` attribution and verify-on-reuse state live only in a disposable cache

**Under test:** [`03-crypto.md` §5.2](../architecture/03-crypto.md#52-the-domains), FR-MAN-006, FR-DED-003

`device` mode requires knowing which segments *this device* wrote. Verify-on-reuse requires remembering which segments have already been verified. Both are catalogue state, and the catalogue is explicitly disposable and rebuildable ([ADR-0010](../adr/0010-local-store-separation.md)).

Writer attribution is in fact recoverable — index deltas carry `writer_id` — but nothing says so, and FR-MAN-006's dedup lookup key does not mention writer attribution at all. Verification state is *not* recoverable, so a catalogue rebuild silently re-imposes the full verify-on-reuse cost.

**Failure scenario.** A user deletes the catalogue on support advice. It rebuilds. The next backup in `repository` mode re-downloads and re-verifies every previously verified shared segment, turning a routine incremental into hours of egress.

**Required change.** State that writer attribution is recovered from delta `writer_id` during rebuild, and add it to the dedup lookup key in FR-MAN-006. Record verification outcomes durably enough to survive a rebuild — either as a repository object, or accept the re-verification cost explicitly and say so.

> **Resolved (2026-08).** Attribution is recovered from delta `writer_id` and is what `DedupTrustGate` reads first. Verification outcomes take the **second** option: they live in the catalogue's `verified_objects` and a rebuild re-imposes the read, once. A durable repository object was rejected for now as format surface frozen into v1 before anything consumes it — the cost it avoids exists only in a multi-writer repository, and adding the object later is a minor-version change. [ADR-0006](../adr/0006-object-identifiers-and-dedup-trust-domains.md#what-is-deliberately-not-solved) carries the reasoning; the catalogue schema carries the warning where an implementer will meet it.

---

### PT-13 — Blob salt uniqueness rests entirely on CSPRNG quality, and VM cloning defeats that

**Under test:** [`03-crypto.md` §3.1](../architecture/03-crypto.md#31-the-construction), [ADR-0005](../adr/0005-aead-suite-and-nonce-construction.md)

The construction draws a 256-bit CSPRNG salt per blob and relies on collision being negligible. That is sound when the CSPRNG is sound.

**Failure scenario.** A server template or laptop image is cloned after the agent is installed. Both clones resume from identical RNG state — a well-documented hazard for cloned VMs and early-boot embedded systems. Two writers draw the same salt, derive the same blob key, and encrypt different content under the same key and ordinals.

The original design had this exposure too, so it is not a regression, but C2 is the ADR that now owns nonce uniqueness and it should close it.

**Required change.** Bind writer identity into the derivation: include `writer_id` and a monotonic per-writer blob counter in the HKDF `info` alongside the random salt. Collision then requires identical salt *and* identical writer *and* identical counter. The counter comes from the journal sequence, which is already gapless, monotonic, and protected against cloning by the identity-conflict alert in [T-18](../threat-model.md#t-18-writer-identity-cloning). This costs nothing and removes a dependency on RNG quality in an environment where RNG quality is not guaranteed.

---

### PT-14 — Repository-side index growth is now strictly larger, with no requirement covering it

**Under test:** [ADR-0007](../adr/0007-logical-object-identifiers-in-manifests.md), NFR-PERF-005, NFR-PERF-011

C1 moved physical location out of manifests — which shard naturally through the tree and file-version graph — and into the index, which must now hold an entry for every distinct segment object in the repository, permanently.

NFR-PERF-011 targets *catalogue* bytes per file version. Nothing targets the size of the repository-side index, which is the thing C1 made larger and which every reader must materialise enough of to resolve a restore.

**Required change.** Add an NFR for repository-side index bytes per distinct segment object, and confirm the shard scheme in [`02-repository-format.md` §7.1](../architecture/02-repository-format.md#71-structure) bounds what a reader must fetch to resolve one file. ADR-0007's consequences section should state the growth explicitly rather than only mentioning the lookup.

---

### PT-15 — AES-GCM is not key-committing

**Under test:** [`03-crypto.md` §3](../architecture/03-crypto.md#3-nonce-and-key-construction)

AES-GCM provides no key commitment: a ciphertext can be constructed that authenticates under two different keys. Here, keys derive from the repository master key and an attacker without it cannot choose them, so exploitability is low.

It is worth recording because `repository-unverified` deduplication accepts records from other writers without verification, which is the closest this design comes to a setting where an adversary influences what gets decrypted under a key the victim holds.

**Required change.** Note the property in the crypto document as an input to the external cryptographic review, and confirm the AAD binding in §3.4 is sufficient. Not a v1 blocker.

---

## What survived unchanged

A pressure test that condemns everything is not a pressure test. These held:

| Fix | Verdict |
|-----|---------|
| **C1** — manifests carry logical object identifiers only | **Sound.** The contradiction it closed is real, and no alternative preserves both immutability and compaction. PT-10 and PT-14 are costs to manage and disclose; PT-2 is a consequence in the *index*, not a defect in the manifest decision. |
| **C2** — per-blob key derivation, record ordinal as nonce | **Sound.** The construction itself withstands concurrent writers and restart exactly as claimed. PT-1 is a defect in the *spool checkpoint*, not in the key schedule; PT-13 hardens an input. Nothing about the derivation needs to change. |
| **C3** — deduplication trust domains | **Sound.** The threat is real, the three-domain model is the right shape, and verify-on-reuse is the right middle setting. Only the choice of default is challenged (PT-11). |
| **C4** — write-intent records; leases advisory | **Sound.** The reasoning for demoting leases — clock skew, eventual consistency, suspension, no binding to blobs — is correct and holds under scrutiny. PT-3, PT-4 and PT-5 are gaps in specifying the mechanism, not arguments against it. |
| **C5** — commit per replica, replication as separate state | **Sound.** Decoupling was necessary and the per-`(snapshot, destination)` model is right. PT-8 and PT-9 concern what we then *call* the resulting states and how retention interacts — both downstream of a correct decision. |
| **C6** — per-writer delta chains, checkpoints enumerate what they subsume | **Sound.** Removing the listing dependency was the correct diagnosis and the chain mechanism delivers it. PT-2, PT-6 and PT-7 all concern supersession and retirement — semantics the fix needed and did not supply. |

Also re-confirmed from the first review and unchanged here: publication ordering, the catalogue as a pure cache, keyed object identifiers, encryption with no plaintext mode, retention-selects/GC-deletes, and importer isolation.

---

## Dispositions

| ADR | Fix | Findings | Disposition |
|-----|-----|----------|-------------|
| [0005](../adr/0005-aead-suite-and-nonce-construction.md) | C2 | PT-1 (critical), PT-13, PT-15 | **Accepted with amendment** — construction unchanged; spool checkpoint and derivation input amended |
| [0006](../adr/0006-object-identifiers-and-dedup-trust-domains.md) | C3 | PT-11, PT-12 | **Accepted with amendment** — default changed to `repository`, attribution recovery stated |
| [0007](../adr/0007-logical-object-identifiers-in-manifests.md) | C1 | PT-10, PT-14 | **Proposed** — the hint question is a maintainer decision ([Q11](../open-questions.md#q11--physical-hints-in-segment-references)) |
| [0008](../adr/0008-index-generations-and-checkpoints.md) | C6 | PT-2 (critical), PT-6, PT-7 | **Accepted with amendment** — commutativity claim replaced by precedence; gap closure and retirement specified |
| [0009](../adr/0009-garbage-collection-safety.md) | C4 | PT-3 (critical), PT-4, PT-5 | **Accepted with amendment** — collector intents, blob identifier formation, two-condition expiry |
| [0011](../adr/0011-commit-versus-replication-semantics.md) | C5 | PT-8, PT-9 | **Accepted with amendment** — failure domains, retention deferral |

New records: [ADR-0016](../adr/0016-blob-identifier-formation.md) (blob identifier formation), [ADR-0017](../adr/0017-index-entry-supersession.md) (index supersession and precedence), [ADR-0018](../adr/0018-replica-failure-domains.md) (replica failure domains).

"Accepted with amendment" means the decision stands and the amendment is already applied in the linked documents — not that it is pending. ADR-0007 alone stays `Proposed`, because PT-10 asks a question only the maintainer can answer.
