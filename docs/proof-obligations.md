# Proof obligations

**Status:** maintained · **Checked by:** [`eng/check-proofs.py`](../eng/check-proofs.py)

---

This page states the invariants the product actually rests on, and for each
one says what would falsify it and what would catch that happening. It exists
because the 2026-09 architecture review asked for it, and because the two
registers either side of it answer different questions: the
[traceability matrix](requirements/traceability.md) maps *requirements* to
tests, and [implementation status](implementation-status.md) maps *decisions*
to code. Neither asks the question this one does — **if this claim were false,
what would go red?**

The difference matters most where the answer is "nothing". A requirement with
a test is traced; an invariant with no falsifier is a belief. Both registers
above would show the second as healthy, because nothing in them is wrong: the
requirement is met, the decision is built, and the property nobody thought to
attack is simply not represented. So the gaps are written into the table
rather than left out of it, each naming the requirement that would carry the
work, which is how a prose worry becomes something countable.

**Legend**

| State | Means |
|-------|-------|
| **Proved** | A named test or drill goes red if the invariant breaks |
| **Partly proved** | Part of the falsifier is exercised; the row names the part that is not |
| **Unproved** | The invariant is claimed and nothing here would catch its breach |
| **Open by decision** | The invariant does *not* hold, deliberately; the row names the record that decided it |

A **Proof** cell names test classes as `Project/ClassName`, or a file under
`eng/` for a drill. `eng/check-proofs.py` resolves every one of them and
refuses a build that cites a class nobody wrote — the lesson the traceability
matrix taught expensively, applied here before the same thing can happen
again. Every **Unproved** row must cite at least one requirement id, so a gap
cannot be recorded as a shrug.

---

## The obligations

### Writing

| Invariant | What would falsify it | Proof | State |
|-----------|-----------------------|-------|-------|
| A committed snapshot restores in full — every object it depends on is present and readable | Kill the writer after each persistence boundary in turn, then restore every snapshot that reported committed | `InterruptionTests/PublicationInterruptionTests`, `InterruptionTests/TreeSnapshotInterruptionTests`, `InterruptionTests/StoreFaultTests` | **Proved** (NFR-REL-001) |
| Interrupted blob construction never becomes visible as a committed object | Kill mid-blob, at each checkpoint and between them; then resume, and separately abandon | `InterruptionTests/BlobSpoolResumeTests`, `InterruptionTests/SpoolHygieneTests`, `InterruptionTests/VoidObligationTests` | **Proved** (FR-ARCH-011) |
| A blob identifier is never reused, whatever happens to the sequence file | Roll the durable sequence state back and re-run the writer | `InterruptionTests/SequenceRollbackTests`, `InterruptionTests/SequenceAccountingTests` | **Proved** (FR-SNP-006) |
| No `(class key, nonce)` pair is ever assigned twice | Crash, resume, restart and concurrent writers, collecting every pair assigned across all of them | `Repository.Tests/NonceUniquenessTests`, `Repository.Tests/BlobKeySeparationTests` | **Proved** (NFR-SEC-003) |
| Memory is bounded by configuration, not by the size of the input | One synthetic file far larger than memory; measure peak RSS | `Repository.Tests/BoundedMemoryTests`, `PerformanceTests/MemoryBoundProof` | **Partly proved** — the scaling property is held at test scale for the capture pipeline, and spool resume is now held separately and directly by `Repository.Tests/SpoolCheckpointTests`, which refuses an allocation proportional to the spool's length. What is still unproved is the requirement as written: nothing gates peak RSS in CI, and no test processes anything near the 2 TiB the acceptance criterion names (FR-ARCH-002, NFR-PERF-001) |

### Reading back

| Invariant | What would falsify it | Proof | State |
|-----------|-----------------------|-------|-------|
| Format v1 bytes written today read identically in a year, by a reader nobody here wrote | Regenerate every vector and compare against the published fixtures | `Repository.ConformanceTests/VectorFileTests`, `Repository.ConformanceTests/FixtureRepositoryTests`, `Repository.ConformanceTests/RecordFramingConformanceTests` | **Proved** (NFR-COMP-004) |
| The recovery kit and the passphrase are sufficient — no catalogue, no local state, no this machine | Destroy the state directory, the catalogue and the source, then restore from the destination alone | `Repository.Tests/KitDrillTests`, `Hosts.Tests/RecoveryHostTests`, `eng/recovery-drill.sh` | **Proved** (FR-KIT-003, FR-KIT-006) |
| The catalogue is a cache and never an authority | Delete it; then delete the index plane as well and rebuild from blob footers | `Repository.Tests/CatalogueRebuildTests`, `Repository.Tests/ForensicRebuildTests`, `Repository.Tests/StaleCatalogueTests` | **Proved** (NFR-REL-002, NFR-REL-003) |
| Corruption stays local: one damaged record does not cost the blob, one damaged blob does not cost the archive | Corrupt a record, a footer, a whole blob; fuzz every parser | `Repository.Tests/ArchiveCorruptionTests`, `InterruptionTests/CorruptionHarnessTests`, `Repository.FuzzTests/ParserFuzzTests` | **Proved** (FR-ARCH-009, NFR-REL-004) |
| Restore never writes outside the restore root | Offer a repository path that escapes it, in every spelling the platform allows | `Repository.Tests/RecoveryContainmentTests`, `Storage.ContractTests/LocalFileSystemSecurityTests` | **Proved** (FR-RST-005) |

### Keys and trust

| Invariant | What would falsify it | Proof | State |
|-----------|-----------------------|-------|-------|
| A write-only repository's own service cannot read what it wrote | Take the service's entire state and try to decrypt content with it | `Repository.ConformanceTests/WriteOnlyConformanceTests`, `Repository.Tests/WriteOnlyDerivationTests` | **Proved** (FR-WOR-001, NFR-SEC-010) |
| Another writer's segment is confirmed against its plaintext before it is referenced | Publish a record whose claimed content identifier does not match its bytes, then back up against it | `Repository.Tests/DedupTrustDomainTests` | **Partly proved** — the gate itself holds in all three domains. FR-DED-004's acknowledgement, the thing that stops `repository-unverified` being chosen by someone who does not know what it means, does not exist (FR-DED-003, FR-DED-004, NFR-SEC-007) |
| A repository never accepts a state older than one it has already seen | Restore an older copy of the local state directory and continue | `InterruptionTests/CorruptionHarnessTests` | **Partly proved** — the anchor is durable local state, so it answers the rollback of a *file*. Nothing survives the loss of the state directory itself: there is no witness held anywhere else, and a rebuilt machine cannot tell an empty history from a truncated one (NFR-SEC-005) |
| A sealed record can be relocated between blobs without its key | Move a record into another blob and read it back with the same key material | `Repository.Tests/RecordCipherTests` — which proves the invariant does *not* hold | **Open by decision** — it cannot, on three counts at once: the key derives from the blob, the nonce is the position, and the AAD binds the position. [ADR-0025](adr/0025-compaction-reseals-records.md) states and accepts that; [ADR-0052](adr/0052-relocatable-records-format-v3.md) reverses all three for format v3, and nothing compacts yet, so the cost is a format revision rather than a data migration |

### Destinations and durability

| Invariant | What would falsify it | Proof | State |
|-----------|-----------------------|-------|-------|
| `verified` means bytes were read at the destination and checked, never the destination's word | Answer a challenge from a cached digest rather than from the stored bytes | `Hosts.Tests/DestinationVerificationTests`, `Replication.Tests/ReplicaVerifierTests` | **Partly proved** — the challenge is keyed and unprecomputable where it runs. It does not run where it matters most: see the next row |
| A protected historical piece stays independently provable after the source copy is discarded | Delete the source copy, then corrupt the destination's | — | **Unproved** (FR-VER-001, FR-VER-005) — `Replication/ReplicaVerifier` skips any sample it cannot recompute from bytes it holds, so a destination holding the only copy is the one never challenged. The signed per-blob digest list is published but never reaches the challenge path, and a whole-object digest cannot localise damage anyway |
| A peer never trims below the retention floor it agreed to | Ask it to trim past the floor, from the hub that planned the trim | `Retention.Tests/PeerRetentionTests` | **Proved** (FR-GC-007, FR-GC-010) |
| Retiring a staging archive never strands live history | Retire while a destination still lacks a blob a live snapshot reaches | `Hosts.Tests/DirectShipMigrationTests` | **Proved** (FR-DEST-016) |
| An interrupted transfer exposes no partial object, and a re-run resumes rather than restarting | Interrupt mid-transfer, then run again | `InterruptionTests/StoreCopyOrderTests`, `Hosts.Tests/PeerReplicationTests` | **Partly proved** — no partial object is ever visible, and a re-run resumes from the destination's inventory. The granularity is the whole object: one interrupted part-way is re-sent entire (FR-REP-003) |
| Sync work is proportional to what changed, not to the size of the repository | A repository at scale **M** with a 0.01% delta; count listings, requests and peak memory | — | **Unproved** (NFR-PERF-005, NFR-PERF-008) — `Replication/StoreToStoreCopier` lists both stores in full on every pass, and the per-destination synced sequence it could diff against is a retention watermark the copier never reads |
| A machine that lost everything can claim its peer replica back | Delete the state directory and the archive, then install fresh and try to recover | — | **Unproved** (FR-REP-001, FR-KIT-006) — the peer drills preserve the state directory, and a replica is attributed to a pinned peer identity a fresh install cannot present. The set's own shape (name, roots, schedule, retention, destinations) lives only in the local configuration and is not recoverable at all |
| Recovery is drilled rather than merely possible | Let a drill go stale, or never run one | `eng/recovery-drill.sh`, `Hosts.Tests/RecoveryHostTests` | **Partly proved** — a real end-to-end drill exists and runs on demand. Nothing schedules one, records its result, or surfaces its age, so "we could recover" is a claim about the last time somebody chose to check (FR-KIT-006, NFR-OPS-005) |

---

## How to use this

**When adding an invariant**, add the row before the code. The falsifier
column is the useful half: an invariant whose falsifier cannot be written down
is not yet stated precisely enough to be worth testing.

**When a row moves to Proved**, it moves because a named class goes red
without the code, not because the code looks right. A test that passes both
with and without the invariant is not a proof of it, and the checker cannot
tell the difference — that judgement is the reviewer's.

**When a row stays Unproved**, that is information, not a failure. Three of
the rows above are phase-4 work correctly not started. What the register
refuses is the fourth possibility: an invariant everyone assumes and nobody
has looked at.
