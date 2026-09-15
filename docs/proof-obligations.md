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
| A service that may publish for ever cannot author a deletion | Take the service's entire state; write new objects with it — which must succeed — then author a tombstone the collector acts on, or command a destination to delete | `Repository.Tests/ReclaimAuthorityTests`, `Retention.Tests/ReclaimAuthoritySweepTests`, `Hosts.Tests/WriteOnlySetTests`, `Protocol.Tests/ReplicationMessageTests`, `Retention.Tests/PeerRetentionTests` | **Partly proved** — for the **write-only** shape it holds and is exercised end to end: destruction signs under a reclaim key on its own derivation domain, a write credential cannot derive it, and a collection run without a grant is refused by name rather than falling back ([ADR-0055](adr/0055-reclaim-authority.md)). Two parts are not proved because they are not true. An ordinary v1 service holds the master key and derives both keys from it, so the falsifier succeeds against it by construction and FR-GC-007's floor is the safeguard that holds there. The peer half is now exercised against both ways of getting round it, by `Hosts.Tests/PeerRetentionReplayTests` ([ADR-0059](adr/0059-session-bound-deletion-authority.md)): a page recorded from one session and re-sent in another deletes nothing, because the signature covers the session identifier; and a commander that simply declined to offer the signing feature is refused rather than excused, because the spoke enforces on the reclaim key it recorded and not on the hello. That second one was open until this suite looked for it, and it was forgery rather than replay. What remains unproved is the audit half of the requirement: a destination keeps no signed record of what it deleted (FR-GC-008, FR-GC-007) |
| Another writer's segment is confirmed against its plaintext before it is referenced | Publish a record whose claimed content identifier does not match its bytes, then back up against it | `Repository.Tests/DedupTrustDomainTests` | **Partly proved** — the gate itself holds in all three domains. FR-DED-004's acknowledgement, the thing that stops `repository-unverified` being chosen by someone who does not know what it means, does not exist (FR-DED-003, FR-DED-004, NFR-SEC-007) |
| A writer never re-issues a sequence number its own history already holds | Delete the sequence file, or restore an older copy of it, then back up again | `InterruptionTests/SequenceRollbackTests`, `Hosts.Tests/ObservedHeadAdoptionTests`, `InterruptionTests/CorruptionHarnessTests` | **Proved** — the repository attests the head in signed checkpoints, signed deltas and journal keys, all of which outlive the state directory; `Repository.Index/ObservedHead` reads it at archive open and the sequence moves past it, loudly. The colliding-put refusal still stands behind that (NFR-SEC-005) |
| A sealed record can be relocated between blobs without its key | Move a record into another blob and read it back with the same key material | `Repository.Tests/RecordCipherTests` — which proves the invariant does *not* hold | **Open by decision** — it cannot, on three counts at once: the key derives from the blob, the nonce is the position, and the AAD binds the position. [ADR-0025](adr/0025-compaction-reseals-records.md) states and accepts that; [ADR-0052](adr/0052-relocatable-records-format-v3.md) reverses all three for format v3, and nothing compacts yet, so the cost is a format revision rather than a data migration |

### Destinations and durability

| Invariant | What would falsify it | Proof | State |
|-----------|-----------------------|-------|-------|
| `verified` means bytes were read at the destination and checked against evidence it does not hold | Corrupt a blob at a set's only destination, and separately let a converged set sit with nothing to copy | `Hosts.Tests/DestinationVerificationTests`, `Hosts.Tests/DirectShipVerificationTests`, `Replication.Tests/ReplicaVerifierTests` | **Proved** — a peer answers a keyed range challenge; a readable destination has its sampled blobs *opened* at the replica, so the AEAD tag the writer computed is the evidence and no second copy is needed. Challenges are due on the proof's age, not on a copy being due. A set with **no** evidence independent of the destination — a direct-ship set shipping only to a peer, where this installation holds no content at all — is proved by **reading the replica back** through the retrieval session and authenticating a record inside a sampled blob, which needs no second copy because the peer never held the key that computed the tag ([ADR-0058](adr/0058-peer-write-adapter.md) §8). `Hosts.Tests/PeerReadBackVerificationTests` rots the peer's blobs and the pass refuses to call it in sync; `Hosts.Tests/DirectShipPeerTests` holds that a peer which will not serve the read-back is said to be unchecked rather than quietly assumed good (FR-VER-001, FR-VER-002) |
| A protected historical piece stays provable after the source copy is discarded | Delete the source copy, then corrupt the destination's | `Hosts.Tests/DirectShipVerificationTests` | **Partly proved** — a blob is proved by opening it at the replica, which needs no source copy at all. What is not proved is the write-only data plane: a v2 repository's service holds the structure key and not the content key, so its data records cannot be opened service-side and only the container is proved. The signed per-blob digest list (`Repository.Index/IndexPublisher`) is published and would close that, and still does not reach the challenge path (FR-VER-001, FR-WOR-003) |
| A peer never trims below the retention floor it agreed to | Ask it to trim past the floor, from the hub that planned the trim | `Retention.Tests/PeerRetentionTests` | **Proved** (FR-GC-007, FR-GC-010) |
| Retiring a staging archive never strands live history | Retire while a destination still lacks a blob a live snapshot reaches | `Hosts.Tests/DirectShipMigrationTests` | **Proved** (FR-DEST-016) |
| A direct-ship set keeps no copy of the backup on the machine it is protecting, whatever kind its destinations are | Capture a set whose only destination is a paired peer, then look for content under the agent's roots and try to restore from the peer's replica alone | `Hosts.Tests/DirectShipPeerTests`, `Hosts.Tests/DirectShipTests` | **Proved** for both served kinds ([ADR-0058](adr/0058-peer-write-adapter.md)): the agent's state gains metadata only, the peer ends holding a replica byte-identical to that metadata plus the content, and the standalone recovery tool restores the captured file out of it with the kit and the passphrase alone. A second capture adds to the replica without disturbing the first, and an unreachable peer is dropped while a sibling carries the run (FR-DEST-013, FR-DEST-015) |
| An interrupted transfer exposes no partial object, and a re-run resumes rather than restarting | Interrupt mid-transfer — between objects, and inside one — then run again | `InterruptionTests/StoreCopyOrderTests`, `Hosts.Tests/PeerReplicationTests`, `Hosts.Tests/PeerResumeTests` | **Proved** for the peer wire, at both granularities ([ADR-0057](adr/0057-resumable-object-transfer.md)): no partial object is ever visible, a re-run resumes from the destination's inventory, and a transfer cut **inside** an object sends only the remainder — verified by the source against a digest of the bytes the destination claims, so a damaged prefix restarts the object rather than being built on. The cut itself is now exercised rather than assumed. A local-path copy still restarts the object it was cut inside, deliberately: a re-copy there reads local disk (FR-REP-003) |
| Sync work is proportional to what changed, not to the size of the repository | A repository at scale **M** with a 0.01% delta; count listings, requests and peak memory | `Replication.Tests/CopierListingCostTests`, `Application.Tests/ReconciliationGateTests`, `Hosts.Tests/IncrementalSyncTests` | **Partly proved** — the shape is held and the scale is not. A pass over a pair the last one left level issues no listing at all; a pass with work lists each dependency phase under its own prefix and holds no whole-namespace key set, where before it walked the source once per phase ([ADR-0056](adr/0056-incremental-reconciliation.md)). The listings are **counted**, by prefix as well as by number, because a scoped listing and a full one are indistinguishable in a fixture of nine objects. What is not proved is the row's own falsifier: nothing runs at scale **M**, so the claim that the remaining per-pass work stays proportional there is reasoning rather than measurement (NFR-PERF-016, NFR-PERF-005) |
| A destination that quietly loses an object does not stay lost | Delete an object out of a replica, then let passes run | `Hosts.Tests/IncrementalSyncTests`, `Hosts.Tests/DestinationVerificationTests`, `Hosts.Tests/RecoveryDrillTests` | **Proved** for a local-path destination, by three mechanisms that do not share a failure mode (FR-REP-005, FR-VER-002, FR-KIT-007): the pass that reads both inventories through comes due on its own cadence whatever the watermark says; verification opens sampled bytes at the destination; and the drill restores a file from it. A pair that fails either of the last two stops claiming to be level, and a pair that is not level is never skipped — which is what caught the deleted blob first when this was written. The window where only the watermark is watching is bounded by [ADR-0056](adr/0056-incremental-reconciliation.md)'s reading-through interval |
| A whole state directory rolled back together is detected | Restore state directory *and* metadata store from one older copy, leaving the destinations ahead | — | **Unproved** (NFR-SEC-005) — the observed head is read from the repository the writer publishes into, and for a direct-ship set that plane is local, so a consistent rollback of the whole directory rolls back the witness with it. The destinations hold the newer index and nothing consults them |
| A machine that lost everything can claim its peer replica back | Delete the state directory and the archive, then install fresh and try to recover | — | **Unproved** (FR-REP-001, FR-KIT-006) — the peer drills preserve the state directory, and a replica is attributed to a pinned peer identity a fresh install cannot present. [ADR-0053](adr/0053-peer-claim-and-configuration-recovery.md) decides the ceremony that would let it; nothing implements it, and the record says which obstacle each half is behind. The set's own shape still lives only in the local configuration |
| Recovery is drilled rather than merely possible | Let a drill go stale, or never run one | `eng/recovery-drill.sh`, `Hosts.Tests/RecoveryHostTests`, `Hosts.Tests/RecoveryDrillTests` | **Proved** for a local-path destination (FR-KIT-007, [ADR-0054](adr/0054-scheduled-restore-drills.md)) — the service restores a sampled file out of each destination's own replica on a cadence, records when and what, raises a notice when it cannot, and keeps never-drilled distinguishable from drilled-and-failed. Ruining the staging archive leaves the drill passing, which is what pins the replica as the thing being read |
| A drill's answer is not mistaken for more than it proves | Read a green drill row as "recovery works" | `eng/recovery-drill.sh`, `ArchitectureTests/DependencyRuleTests` | **Partly proved** — the scheduled drill runs inside the service, so it does not parse the kit file, does not exercise the recovery tool's dependency closure, and cannot delete the state directory it runs out of. The script proves all three and the closure is held by a test; nothing schedules the script, and nothing can ([ADR-0054](adr/0054-scheduled-restore-drills.md) §5) |
| A peer replica is drilled, not only challenged | Let a peer-held set's read path rot unexercised | — | **Unproved** (FR-KIT-007) — only local-path destinations drill. A peer's replica is behind the wire, and restoring from one on a cadence the peer never agreed to is peer-protocol work rather than a schedule ([ADR-0054](adr/0054-scheduled-restore-drills.md) §6). The possession challenge still proves the bytes are there |

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
