# ADR-0076 — Damage is traced to the snapshots and files that need it

**Status:** Accepted
**Date:** 2026-09
**Requirements:** FR-VER-005, FR-SNP-003, FR-VER-007, FR-VER-008, FR-RST-007, FR-MAN-002
**Related:** [ADR-0011](0011-commit-versus-replication-semantics.md) (the `degraded` state), [ADR-0035](0035-destination-fitness.md) Amendments 1 and 2, [ADR-0075](0075-a-restore-reads-around-damage.md), [ADR-0010](0010-local-store-separation.md) Amendment 4, [specification 04 §7](../../specifications/repository-format/04-record.md#7-corruption-is-local), [architecture 09 §5.6](../architecture/09-replication-and-peers.md#56-what-damage-reaches)

**Built:**
- The index: catalogue schema v8's `version_contents` and `snapshot_structure`
  (`Repository.Catalogue/CatalogueSchema`, `Repository.Catalogue/Catalogue`),
  written by the capture (`Repository/SnapshotPublication`), the projection
  (`Repository/CatalogueProjector`) and the forensic rebuild
  (`Repository.Catalogue/Forensic/ForensicRebuilder`).
- The trace: `Catalogue.ReachOf` and `Repository/DamageScope`.
- The status: `Application/SnapshotReplication` (`touchesDamage`), the
  ledger's `damage_only` (`Application/DestinationSyncStore`, schema 7), and
  `Agent/ServiceCommandHandler` (`list_snapshots`).
- The words: `Agent/DamageReachText`, `Agent/ReplicaSweepJob` (the sweep's
  findings, and a peer's copies checked for a sound one),
  `Agent/ServiceCommandHandler.ReadAround` (a restore's findings),
  `Agent/ServiceCommandHandler` (`verify_destination`) and `Agent/FanOut`
  (the sync's re-check).
- The tests: `Repository.Tests/CatalogueReachTests`,
  `Repository.Tests/DamageReachTests`, `Application.Tests/SnapshotReplicationTests`,
  `Application.Tests/DestinationSyncStoreTests`, `Hosts.Tests/DamageScopeTests`,
  `Hosts.Tests/PeerDeepSweepTests` and `Hosts.Tests/RestoreReadAroundTests`.

---

## Context

The deep sweep finds damage at a destination and repairs what it can
([ADR-0035](0035-destination-fitness.md) Amendments 1 and 2), and a restore
finds it too, reading around it meanwhile
([ADR-0075](0075-a-restore-reads-around-damage.md)). What stands after that —
a peer's damage, which nothing here can replace, or a local path's with no
sound copy anywhere — was not what the records promised a person would hear.

- **Every snapshot at the destination read degraded.** FR-VER-005 asks for the
  *affected* `(snapshot, destination)`. `SnapshotReplication.Derive` degraded
  every snapshot at a failed pair, so one rotted blob of last year's content
  marked this morning's snapshot degraded there too. That is a scare with no
  remedy in it, and it hides the answer a person needs: which of their backups
  that destination can still give back.
- **The words named blob keys.** Specification 04 §7 asks a reader to "report
  the affected file versions by name, so the damage has a scope a user can act
  on". The sweep's notice, the restore's and `verify-destination`'s line named
  `blobs/data/…` keys, which nobody can act on.

The gap was in the catalogue. It can go from a blob to the objects in it, and
from a snapshot to its file versions. It could not go from a segment back to
the versions that use it: that list lives in each version's manifest, and the
manifests are sealed. Naming what a damaged segment belongs to meant opening
every manifest of every snapshot.

## Decisions

### 1. The reverse of the manifests, kept beside them

Catalogue schema v8 adds two tables of derived data.

- **`version_contents`** maps each content object to the file versions that
  need it: their segments and their alternate streams' content.
- **`snapshot_structure`** maps each record a snapshot is made of, besides its
  files' own, to the snapshot. That covers its manifest, its policy and error
  manifests, and every tree manifest of its structure, continuations included,
  which no parent entry names.

Every route that fills a catalogue writes them:

- the capture, for each version it records, including the content a renamed
  file inherited;
- the projection that rebuilds a catalogue from a repository;
- the forensic rebuild from blobs alone.

A test over each route holds that every blob a capture wrote is traced to
what needs it, so no route can leave the index out without being noticed.

They are a table rather than a walk at finding time for the reason in §3.

### 2. The trace

The trace runs from the damaged blobs outwards:

- to the objects whose winning location is in them (specification 07 §3) — a
  copy that compaction superseded is nothing a read would take;
- to the file versions that need those objects, or are them;
- to the snapshots and paths that hold those versions;
- and to the snapshots built of those objects.

**Anything nothing traces is counted, never dropped.** A caller that cannot
place damage must count every snapshot as reached, because not knowing is not
evidence that nothing needs it.

The sweep and the restore name blobs by store key, which is a keyed rendering
of the blob's identity. A rebuilt catalogue records no store key for most
blobs, so each located blob's key is derived and compared. A key that matches
no located blob is untraced too: it could be garbage, or what another writer's
snapshot needs.

### 3. Read when asked, not kept from the finding

The reach is traced whenever it is needed: each time the status is read
(`list_snapshots`), and whenever a notice or a line is written. It is never
stored with the finding.

- **Why not stored.** A capture that finds content already held reuses those
  objects rather than writing them again (NFR-PERF-010), damaged or not. A
  snapshot taken after the finding that needs the same objects must be
  counted too, and a stored reach would miss it.
- **What it costs.** Each trace makes two passes for each destination
  holding outstanding damage. One is over the located blobs, deriving each
  one's store key to find the damaged ones (§2), and the other over the
  snapshots' listings. That is the same order as the file counts
  `list_snapshots` already takes, and nothing at all while no damage stands.
- **What it never waits on.** The trace writes only a scratch table of its
  own connection, so it takes no write lock on the catalogue, and a status
  read does not wait behind a capture's writes.

### 4. Which failure degrades what

- **Damage a pair still holds degrades the snapshots that need it, whatever
  the pair's state.** An unplugged drive holds what it holds, and that includes
  what it holds damaged.
- **A pair failed for its damage alone degrades those snapshots and no
  others.** The ledger records which kind of failure is standing
  (`damage_only`, schema 7):
  - a sweep's or a restore's finding sets it;
  - a failure of another kind clears it;
  - a damage finding recorded over a failure of another kind does not narrow
    it, because only a success shows the rest of the copy answers;
  - a success over outstanding damage leaves the pair failed for that damage
    alone, and so does a sync that copies everything and finds the damage
    still standing when it re-checks.
- **A failure of another kind degrades every snapshot, as before.** A refusal
  or a failed proof says something about the whole copy.
- **A caller that has not traced the damage counts every snapshot as needing
  it.** `touchesDamage` defaults to true.
- **A finding whose every object was replaced degrades nothing.** The pair
  stays failed, because the device altered a backup once, but the snapshots it
  holds are whole again.
- **A schema-6 ledger reads every failure as the whole copy's.** That is the
  reading that cannot understate damage.

### 5. What is said

Each place that reports damage names its reach:

- the sweep's findings at a local path and at a peer;
- a restore's findings at a destination and in the staging archive;
- `verify-destination`'s line, which covers everything still standing there,
  not only what that read found.

Each gives the number of snapshots, the number of files and the first five
paths in order, the directory structure of any snapshot whose own records are
reached, and what could not be traced.

A finding at a peer also says whether a copy this installation reads without
the peer holds the objects sound: the staging archive, or the set's local
paths. Nothing here can replace an object at a peer, so what the person
reading needs to know is whether they still have it. Each object is proved at
those copies without writing anything. Another peer is never dialled for it:
a finding at one peer is no reason to put a session on another's link.

### 6. The schema change needed the rebuild at open

Before [ADR-0010 Amendment 4](0010-local-store-separation.md#amendment-4-2026-09--a-catalogue-discarded-is-rebuilt-before-it-is-read),
a catalogue schema change emptied the history of every set it met. Bumping the
catalogue to v8 would have hidden every existing snapshot from the service on
upgrade. So the rebuild at open landed first, in the same change, and the
first open after an upgrade rebuilds the catalogue with the index in it.

## Consequences

**Positive**

- The per-snapshot status says which of a person's backups a damaged
  destination cannot give back, and no longer calls the others degraded.
- The words name files and snapshots, and a peer's finding says whether a copy
  close at hand is still sound.

**Negative**

- Each capture writes one more row per content object of each version it
  records, and one per tree manifest per snapshot.
- `list_snapshots` pays, for each destination with outstanding damage, a pass
  over the located blobs and one over the snapshots' listings.
- The first open after the upgrade rebuilds each set's catalogue.

**Neutral**

- The ledger's own words for a failed pair still name the blob keys. The reach
  is in the notices, the verify line and the per-snapshot status.

**Limits, stated**

- **A reach can over-count snapshots.** The catalogue keeps a pruned
  snapshot's rows, so a reach can count a snapshot the set no longer lists.
  The error is always towards too many, never too few.
- **The sample is not a priority.** It is the first paths in ordinal order,
  not the most important ones.
- **A directory is counted, not named.** Damage to a directory's own listing
  is counted per snapshot and does not name the directory's path.

## Alternatives considered

**Walk every manifest when damage is found.** Correct, and what a scanning
reader of specification 04 §7 would do. Rejected because the status read would
pay for the walk every time, and because a reach stored from the walk goes
stale at the next capture (§3).

**Store the reach on the ledger with the finding.** Cheap to read. Wrong after
the next capture that reuses the damaged objects.

**A new sync state instead of the flag.** A `damaged` state would change the
wire and the status vocabulary for a distinction the ledger can hold as a fact
beside `Failed`. The pair *is* failing, and the destination status goes on
saying so.

**Index `tree_entries` by object.** Faster traces, at the cost of every capture
writing one more index entry per path of every snapshot. Rejected while a trace
runs only when damage stands.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-09 | Accepted | Built tests first (the catalogue's trace, the three routes that fill the index, the derivation, the ledger, and the service's status and words), with each rule's removal confirmed to turn its tests red: a capture that records no version contents or no trees, a projection that records no contents, a forensic rebuild that records no structure, a superseded location taken for reached, an untraced object or key dropped, the derivation or the list ignoring the scope, a damage finding narrowing another failure, a sync's re-check not narrowing one, and a trace that waits behind a capture's write lock |
