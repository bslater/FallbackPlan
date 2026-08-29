# ADR-0046 — Direct-to-destination publication: the staging archive gives way to the ship sink

**Status:** Accepted
**Date:** 2026-08
**Requirements:** FR-DEST-002, FR-DEST-003, FR-DEST-013, FR-DEST-015, FR-DEST-016, NFR-PERF-001
**Related:** [ADR-0034](0034-hub-and-spoke-destinations.md), [ADR-0047](0047-backup-pool-and-priorities.md), [ADR-0029](0029-pipeline-and-service-concurrency.md), [ADR-0011](0011-commit-versus-replication-semantics.md), [ADR-0042](0042-write-only-repositories.md), [architecture 04 §5](../architecture/04-concurrency-and-publication.md)

---

## Context

ADR-0034 §1 gave every set a staging archive: publication lands locally, and
destinations receive whole-archive replicas by fan-out afterwards. The owner
has rejected that shape: a backup's content is to be written **to its
destinations directly**, with nothing cached or temporarily stored on the
agent's machine — the staging copy's disk cost buys a durability property
(a capture never blocks on destination availability) the owner is
deliberately trading away, with the trade stated rather than implied.

The 2026-08 exploration established what the pipeline actually needs from
its store: `IObjectStore` is five members; every publication component
already holds `IObjectStore`, not a concrete type; `PutAsync` takes a
re-openable content factory (a sealed blob's spool file re-opens per call);
the writer sequence, catalogue and spool are local files, not store objects;
and the capture path's store reads are few and specific — the open-time
descriptor/keys reads, the dedupe presence probe (`GetMetadataAsync` on a
blob key), the rename-manifest optimisation, and collision read-backs.

## Decision

1. **The ship sink.** A direct-ship set's `ArchiveHandle.Store` is a
   `DestinationShipSink` — an `IObjectStore` the publication pipeline writes
   exactly as it wrote the staging store. Routing by key: `blobs/` objects
   go to the set's in-scope destinations and never to local disk; every
   other object (descriptor, keys, journal, index, snapshots, hints) goes to
   the **local metadata store** (`<state>/sets/<setId>/`) *and* the in-scope
   destinations. Each destination therefore holds a whole, independently
   restorable repository at `<destination>/<repositoryId>/` — ADR-0034 §2's
   invariant (one snapshot history, N lawful copies) survives the staging
   archive's removal. The `ArchiveHandle.Store` type widened from
   `LocalFileSystemObjectStore` to `IObjectStore` to admit it.
2. **Reads route to whoever holds the bytes.** Metadata reads answer
   locally. A `blobs/` read — the dedupe presence probe, a copier's fetch, a
   verifier's range — is answered by the first destination holding the key,
   in priority order; a `blobs/` listing is the **union** across
   destinations. The invariant that makes the union sufficient: a capture
   refuses to run with no reachable destination, so every committed
   snapshot's closure exists at at least one destination — and a sibling
   can therefore always seed a destination that missed a run. This is also
   what keeps **dedupe working** with no local content: the presence probe
   (the guard against a stale catalogue row) asks the destinations.
3. **The run scope** (with ADR-0047's ledger): a run writes to the set's
   defect-free, reachable, local-path destinations that hold a baseline —
   or all reachable ones when the set has never captured, because that
   first capture ships everything and is every destination's full backup. A
   baseline-less destination on a set with history is *skipped* by the run
   (an incremental would hand it a snapshot without its closure) and
   **seeded by catch-up instead**: the existing fan-out, unchanged, copying
   "from the archive" through the sink — which reads from whichever sibling
   holds each object. A destination that fails mid-run is dropped, named in
   the ledger and the log (event 3758), and its replica is lagging-but-valid
   — a journal intent nothing retired, exactly an interrupted copy's state,
   healed by the next catch-up. When the last destination fails, the run
   fails through the pipeline's ordinary interruption safety.
4. **No reachable destination refuses the capture.** The owner's accepted
   consequence of holding no local copy: with every destination away there
   is nowhere to write, and the run says so (a recoverable failure the next
   pass retries) instead of pretending. ADR-0034 §1's counter-argument — a
   capture must never block on destination availability — is hereby
   consciously given up for direct-ship sets.
5. **The spool stays; it is not staging.** Blobs still assemble in the
   per-set spool (`spool/<repoId>/`), because in-memory assembly of 64–256
   MiB blobs breaks NFR-PERF-001's memory bound and deletes crash-resume —
   and because a sealed spool file re-opening per destination is precisely
   what a fan-write needs. A spool file lives from first record to last
   destination acknowledgement of its blob, then deletes; bounded by
   in-flight blobs, it is a working buffer, not a copy of the backup.
6. **Device trust everywhere for direct-ship** (as write-only sets already
   run): reuse decisions come from the catalogue, guarded by the
   destination presence probe. Verify-on-reuse re-reading ranges through
   the sink would pay a destination round trip per reuse to re-check bytes
   the catalogue vouches for.
7. **Dual mode, deliberately gated.** A set whose staging archive exists
   keeps it — migration is its own record. A set flagged `direct_ship`
   (configuration schema 5) publishes through the sink from birth. The flag
   defaults **false** and the console does not yet offer it: restore,
   retention and verification still read the local archive, and a set whose
   restore path does not work yet must not be creatable by accident. The
   flag flips to the default — and retires — with the destination-side read
   paths and the migration record.

   > **Amended (2026-08): the gate is discharged for local-path sets.** The
   > read paths, the migration record and the retention-with-trimming drill
   > have all landed, so the flag reached the contract (`direct_ship` on the
   > set descriptor, 1.23 — null preserves, an explicit value sets) and the
   > console's set editor. A **new set referencing a local-path destination
   > is born direct-ship**; staging is the explicit opt-out, and a peer-only
   > set stays staging until the peer write adapter lands (the one remaining
   > tail). A shape change is refused while a run is live, takes effect
   > in-process (the archive handle is evicted, so the next open migrates —
   > no service restart), and queues the seeding catch-up immediately, since
   > a flipped set's next capture refuses until a destination holds its
   > history. The drill also caught and fixed the deleting half: the sink
   > used to stop sweep deletes at the metadata store, so destinations never
   > shrank under retention and the union listing resurrected every swept
   > object into the next survey. The sink now fans a delete to the
   > destinations first and the metadata store last — policy-safe because
   > the replication gate already holds expiry until every entitled
   > destination's keep-set has dropped the snapshot (FR-GC-010) — with the
   > staging fallback never touched (retire_staging is its one deleter).

## Consequences

**Positive** — a saved set's content lands at its destinations directly;
the agent's disk holds metadata only (the report that started this — an
archive growing beside the logs — becomes structurally impossible for
direct-ship sets); destinations are whole repositories from their first
byte; catch-up needs no new machinery.

**Negative** — a capture now depends on destination availability (decision
4); dedupe pays a destination presence probe per candidate (local-disk
cheap for local paths, and memoized per blob per publication); verification
and retention convergence for direct-ship sets read through the sink and
land properly destination-side in the next record; peer destinations are
not yet served by the sink (a stated `NotSupported` in the ledger, never a
silent skip) until the peer write adapter lands. Two narrower consequences
are accepted and named: each object is put **local metadata store first,
then the targets**, so a put that fails at every target can leave the
metadata store one object ahead of every destination — the run fails
through the pipeline's ordinary interruption safety and the next catch-up
carries the object out, exactly as it heals a dropped destination. And the
unchanged-file short-circuit stays catalogue-trusting (NFR-PERF-003: the
content is never opened), so a blob deleted from every replica behind the
catalogue's back is re-shipped only when the dedupe probe next asks about
its content; loss discovery belongs to verification and the sweep, not to
capture.

**Neutral** — the publication pipeline is untouched above the store
interface; interruption safety is the same intent/ordering machinery,
per destination; staging sets behave exactly as before.

## Alternatives considered

- **A transient whole-archive spool** (package locally, stream out, delete
  when every destination has it): keeps captures destination-independent
  but re-creates the local copy the owner rejected, growing unbounded while
  any destination is away.
- **In-memory blob assembly** (no spool at all): breaks the stated memory
  bound at pool concurrency and deletes spool-resume; rejected in favour of
  calling the spool what it is — a bounded working buffer.
- **Per-destination publication pipelines** (N independent captures): reads
  the source N times and packages N times — the owner's requirement is
  package once, ship N ways.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Accepted | The owner's direction, recorded with the exploration of every store interaction the pipeline makes |
| 2026-08 | Built (first slice) | The ship sink, the metadata store, run scoping with ADR-0047's ledger, sibling catch-up through the existing fan-out, the no-destination refusal, and the `direct_ship` flag — default off until restore/retention/verification read destination-side |
| 2026-08 | Built (read paths) | Restore, destination verification and the retention traversal proven THROUGH the sink, unchanged: a restore of a direct-ship set comes back byte-identical (blobs read from whichever destination holds them), verify-destination re-reads each replica against its seals with zero damage, and the retention report walks closures out of destination-held metadata blobs. The staging trim's blob deletes are ignored by the sink by design — per-destination convergence is the deleting half. Outstanding before the flag flips: the peer write adapter, the migration record, and a full retention-with-trimming drill on aged direct-ship snapshots |
| 2026-08 | Built (migration) | A staging set flagged direct_ship migrates at first open: metadata copies into the metadata store, the staging archive stays as a read-only seed source the sink consults last (so reuse and restores of unseeded history keep working), a standing notice says retirement awaits, and the pass keeps syncing while staging remains — the catch-up through the sink is what carries history outward. Contract 1.18's retire_staging deletes staging only when every non-lifecycle object it holds is present in the union of the destinations, refusing by count otherwise; retirement resolves the notice, and history then restores from the destinations alone. FanOut and the staging machinery stay in the codebase for unflagged sets until the default flips |
| 2026-08 | Built (console retirement) | The staging-retirable notice carries the act it announces: a Retire staging button on the notice opens a typed confirmation and invokes retire_staging, refusals surfacing verbatim — and the threat model records what leaving the staging copy means (the source device no longer holds a whole-archive replica; capture now depends on a reachable destination) |
| 2026-08 | Built (hardened) | The scenario sweep's correctness round: a behind destination is excluded from run scope like a baseline-less one (metadata without closure must not mint an in-sync row; migrating sets excepted while staging remains the union's promise), run scope is released on completion so reads resolve freshly, `CompleteRun` records outcomes on failure too, seeding is per-destination under the drop rule, the capacity floor applies to sink writes (FR-DEST-010), a pair owed its seed is recorded behind — never a counted failure that starves its own catch-up — and the 04 §5.1 kill matrix runs through a two-destination sink in `Hosts.Tests/DirectShipFaultSweepTests`, every put-death healing to sibling convergence. The direct-ship edges stopped assuming staging exists: the console's restore gate scans the metadata stores, write-only provisioning routes by the set's shape, and delete-set names what actually remains |
| 2026-08 | Built (operable, default for local paths) | The gate discharged (Decision 7's amendment): `direct_ship` on the contract (1.23) with null-preserve semantics, offered by the console's set editor with the trade stated; a direct-ship set must reference a local-path destination (a peer-only one used to save cleanly and refuse every capture); a shape change is refused mid-run, evicts the cached archive handle so migration happens at the next open in the same process, and queues the seeding catch-up at once. The retention-with-trimming drill (`Hosts.Tests/DirectShipRetentionTests`) ran the full tombstone/grace/sweep cycle against a destination-resident archive and caught the deleting half short — sweep deletes stopped at the metadata store, destinations never shrank, and the union listing resurrected every swept object — fixed by fanning sink deletes destinations-first, metadata-last, policy-safe under the replication gate. New local-path sets are born direct-ship; the peer write adapter remains the stated tail |
