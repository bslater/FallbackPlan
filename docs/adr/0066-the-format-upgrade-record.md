# ADR-0066 — The format-upgrade record

**Status:** Accepted
**Date:** 2026-09
**Requirements:** FR-MAN-019, FR-WOR-001, FR-SVC-015, NFR-COMP-003, NFR-COMP-004, NFR-SEC-003
**Related:** [ADR-0052](0052-relocatable-records-format-v3.md), [ADR-0014](0014-format-versioning-and-stability.md), [ADR-0055](0055-reclaim-authority.md), [ADR-0046](0046-direct-to-destination-publication.md), [ADR-0061](0061-adopt-a-destinations-archives.md), [ADR-0065](0065-merkle-commitment-and-chunk-possession.md), [repository-format 11 §5](../../specifications/repository-format/11-lifecycle-objects.md#5-format-upgrade-record), [command-contract 1.36](../../specifications/command-contract/README.md)

**Built:** `Repository.Format/Lifecycle/FormatUpgradeRecord.cs` (the record, its canonical encoding, the bytes that are signed, and `EffectiveVersion` — the pure decision every reader shares), `Repository/RepositoryLifecycle` (`ReadEffectiveFormatAsync` on every open path, `WriteFormatUpgradeAsync`, and `OpenedRepository.EffectiveFormatVersion`, fixed at open), `Domain/FormatLimits` (the creation default at the latest version this build writes), `Agent/ServiceRuntime` (`ArchiveFormatVersion` as the service's creation version, `SetStorePath`, `UpgradeSetFormatAsync` — the write and the eviction in one method — and the `format-upgradable` notice raised at archive open), `Agent/ServiceCommandHandler.Configuration.cs` (the verb and its refusals), `Agent/ReplicationResponder` (the record on the never-deletable list), `Agent/BackupRunner`, `Cli/OperationGateway` and `Cli/CliApplication` (the publication paths reading the effective version), `Recovery/RecoverySession` and `Recovery/RecoveryHost` (the recovery tool's own listing, and `format 3 (created at 2)`), `Agent/AgentHost` (`upgrade-format`), `Api/Commands.cs` and `Api/ContractVersion.cs` (contract 1.36), the console's notice control; and, from Amendment 1, `Repository/NewerFormatException` and `Domain/FormatVersions` (`IsWritable`), checked by `Repository/PublicationOrchestrator`, `Repository/CompactionPass`, `Retention/RetentionRunner`, `Retention/SnapshotDeletion`, `Retention/DestinationConvergence` and `Agent/FanOut` (a destination's delta retirement); `Repository.Tests/Format/FormatUpgradeRecordTests`, `Repository.Tests/EndToEnd/EffectiveFormatTests`, `Repository.Tests/EndToEnd/WriteOnlyRepositoryTests`, `Hosts.Tests/FormatUpgradeTests`, `Hosts.Tests/AgentPairingVerbsTests`, `Hosts.Tests/PeerRetentionReplayTests`, `Hosts.Tests/DirectShipPeerTests`, `Api.Tests/ConfigurationContractTests`, `Web.Tests/ConsoleFormatUpgradeScriptTests`, `Repository.Tests/EndToEnd/NewerFormatTests`, `Retention.Tests/NewerFormatRetentionTests`, `Hosts.Tests/NewerFormatServiceTests`, `Cli.Tests/NewerFormatCommandTests`, `Hosts.Tests/DeltaRetirementServiceTests`.

---

## Context

[ADR-0052](0052-relocatable-records-format-v3.md) designed format 3 and two
slices built its record, blob and index planes — relocatable sealed records,
the Merkle commitment, the chunk possession challenge. None of it reached a
live installation. The only way to create a format-3 repository was
`init --format-version 3`, which the service never calls, so no repository
published a Merkle root and no peer could be challenged for one leaf of a
blob. Two large slices delivered nothing until something created a format-3
archive.

Making new sets format 3 is one line. The question this record answers is the
other half: what happens to a repository that already exists. The owner's
installation is entirely format 2, and the alternative to upgrading it is
re-seeding — backing up from scratch into a second archive beside the first,
which is exactly the outcome [ADR-0061](0061-adopt-a-destinations-archives.md)
exists to avoid.

## The finding that decided the mechanism

The obvious move is to rewrite `repository-format` in place: it already
carries `format_version` and the required-feature list, and a reader already
refuses a feature it does not implement, by name, at the door
([ADR-0014](0014-format-versioning-and-stability.md)).

Checking how a rewritten descriptor would reach the copies of the repository
showed that it cannot. The descriptor is the one object every copy path
refuses to replace:

- a local-path destination is seeded with it **if absent**, and never again
  (`Agent/DestinationShipSink`'s `SeedDescriptorAsync` copies if-absent and
  says so);
- a peer commits an object it lacks and keeps the one it has
  ([peer-protocol 03 §5](../../specifications/peer-protocol/03-replication.md)),
  which is the rule, not an implementation detail;
- `repository-format` may not be named by a retention instruction
  ([peer-protocol 06 §3](../../specifications/peer-protocol/06-retention.md)),
  so it cannot be replaced by delete-then-resend either.

So a rewritten descriptor moves the **source alone**. Every destination and
every peer replica would go on claiming format 2 while the source wrote
format 3 into them, and a recovery from one of those copies — which is the
whole point of having them — would read a format-2 descriptor over format-3
records.

An append-only record needs none of that. It is an ordinary immutable object,
which is the one thing all three paths already move, and an interrupted
upgrade leaves the original descriptor untouched.

## Decision

### 1 An append-only signed record, not a descriptor rewrite

`/format-upgrade/<to-version>`, specified at
[11 §5](../../specifications/repository-format/11-lifecycle-objects.md#5-format-upgrade-record):
`from_version`, `to_version`, an informational `upgraded_at`, the `writer_id`
that decided, and an Ed25519 signature over the canonical encoding of the
rest. Written once, if-not-exists, never rewritten, never removed. Keyed by a
prefix rather than as a bare key, so a repository that goes 2 → 3 now and
3 → 4 later carries one object per step and a listing answers what it has
been through.

It is cleartext CBOR, not a sealed standalone record like every other object
in that section, because a reader must learn what format a repository is in
**before** deciding how to open it — sealing the answer would put it behind
the decision it exists to inform — and because what it reveals, a format
version, the descriptor beside it already reveals in the clear.

**Propagation costs nothing, and this was checked rather than assumed.** The
first draft of this work budgeted for "the prefix added to the three accept
lists". There are no accept lists: `Replication/StoreToStoreCopier` keeps a
**deny** list (`tombstones/`, `leases/`) and a catch-all phase that claims
anything no named phase does; `Agent/ReplicationResponder`'s commit validates
that the key parses and nothing else; `Agent/DestinationShipSink` branches on
`blobs/` and forwards the rest. An unfamiliar immutable key is carried by
default on every path, which is what makes an append-only record the cheap
mechanism and not merely the correct one.

> **Amended 2026-10 ([ADR-0056 Amendment 1](0056-incremental-reconciliation.md#amendment-1-2026-10--every-prefix-this-build-writes-is-named-and-a-pass-is-asked-only-about-what-it-carries)).**
> Carried by default, but not promptly. The catch-all phase runs only on a
> reconciling pass, and the sync between reconciliations, which verified
> against the whole listing, marked a destination Failed for lacking the
> record. `format-upgrade/` is now a named phase, so the record crosses on the
> sync after the set's next backup.

### 2 Signed under the signing key, not the reclaim key

An upgrade changes what the writer will emit and destroys nothing, so it
belongs to the authority that signs publications rather than to the reclaim
authority [ADR-0055](0055-reclaim-authority.md) split out for destruction.
The consequence is the useful one: a set-up installation can upgrade without
the passphrase, exactly as it publishes without one. ADR-0055's split is
untouched, and nothing here lets a compromised write-only service delete
anything it could not delete before.

### 3 The effective version is a read, and it is fixed at open

A reader's **effective** format version is the highest `to_version` among
records whose signature verifies and which name a version at or above the
descriptor's; with none, it is the descriptor's own. There is no stored
"current version" field to disagree with the records. The descriptor keeps
its original meaning — what the repository was **created** at — and the
recovery tool prints both when they differ (`format 3 (created at 2)`),
because a tool a person reaches for when things have gone wrong must not
report the wrong number.

The decision is one pure method shared by the engine and the recovery tool,
which each do their own listing — the shape `ObservedHead.JournalHeadOf` and
`WriteOnlyDerivation.TryDeriveVerified` already set, and the only shape that
respects the recovery tool's deliberately narrow dependency closure.

It is fixed when an archive **opens**, and the service caches one open
archive per set. So the verb that writes the record must also drop that
handle, and the two live in one method (`ServiceRuntime.UpgradeSetFormatAsync`)
so that a caller cannot reach the write without the eviction. An upgrade that
quietly did nothing until the next restart, having reported success, is the
worst failure shape available here.

> **Amended 2026-10 ([Amendment 1](#amendment-1-2026-10--a-build-writes-only-a-format-it-implements)).**
> The effective version is whatever the newest verified record names, and a
> build that does not implement it reads the repository and writes nothing
> into it.

### 4 An unverifiable record is ignored, never damage

A record that does not decode, or whose signature does not verify, is
ignored. It is not a damage finding and not a reason to refuse the
repository: an unverifiable claim about the format is a claim nobody made,
and refusing to open a repository over a stranger's file would hand anyone
who can write into an archive a denial of service — a worse outcome than
ignoring a file that says nothing.

### 5 The record is undeletable by instruction

`format-upgrade/` joins `repository-format`, `tombstones/` and `leases/` on
the responder's never-deletable list. This is the **one** real work item
propagation cost, and it is worth more than the list edits the plan expected
to spend: without it a commander — including the compromised write-only
service [ADR-0055](0055-reclaim-authority.md) exists for — could instruct a
spoke to delete the record and silently revert that replica's format claim,
while the source went on writing the newer format into it. The replica would
then fail closed on every new blob, diagnosed as damage.

### 6 The way in: contract 1.36, and no gate beyond a typed confirmation

`upgrade_set_format {set_name}` answers the existing `configuration_change`,
so no result shape moves, and takes **no** version parameter: the service
upgrades to the one version it writes, so a client cannot ask for a format
this build could not read back. Refused by name for a set already at that
version, a set with no archive yet (one created here is born at the latest
format), and a set with a run in progress — the storage-shape flip's rule,
applied to the one other edit that swaps an archive handle out beneath
whoever holds it.

It carries **no role gate and no caller-scope refusal**, on the precedent of
`retire_staging`, which deletes a whole archive and carries neither; it is
guarded instead by a typed confirmation in the console and by being reachable
from a notice the service raises. An upgrade is irreversible but destroys
nothing, so anything stricter would say it is graver than deleting the
staging archive, which is not true.

The record is written to the set's **own store**, never through the ship
sink. Outside a run the sink resolves its targets fresh, and a destination
that is present and then refuses the write leaves it with nothing in scope,
so it throws — after the local copy has already landed, reporting a completed
upgrade as a failure. An upgrade must not turn on whether a drive happens to
be writable.

### 7 Both formats are supported, and nothing pushes

A set stays at format 2 until someone chooses otherwise. The
`format-upgradable` notice the service raises at archive open is
informational: acknowledging it silences it for good, because the console's
control renders only for an unacknowledged notice. Nothing schedules an
upgrade, and no surface nags.

One asymmetry is worth stating rather than leaving to be discovered. A
format-2 set publishes no Merkle root, so a peer holding its blobs is proved
by reading the whole blob back over the link, while a format-3 set's peer is
asked for one leaf and its authentication path
([ADR-0065](0065-merkle-commitment-and-chunk-possession.md)). Both are real
proofs; one is much cheaper.

> **Amended 2026-10 ([ADR-0096](0096-format-4-an-index-generation-per-delta.md)).**
> The same record moves a set to format 4, which changes the index delta
> rather than the container. The upgraded set's next delta carries an index
> generation, and nothing already published changes. A build older than
> format 4 opens such a repository, because its descriptor names what it was
> created at, and reports each newer delta as an index object it cannot read
> — this record's price, carried from blobs to deltas. A repository created
> at 4 lists `index-generations`, and an older build refuses it by name.

> **Amended 2026-10 ([ADR-0097](0097-format-5-a-checkpoint-carries-its-deltas.md)).**
> The same record moves a set to format 5, the latest, which changes the
> checkpoint. The upgraded set checkpoints from its next backup, and nothing
> already published changes. A build older than format 5 opens such a
> repository and reports each checkpoint as an index object it cannot read,
> while still reading every delta at its key; it loses the speed and nothing
> else. A repository created at 5 lists `delta-checkpoints`, and an older
> build refuses it by name.

## Consequences

**The stated price: an older build misdiagnoses.** A build predating format 3
that opens an upgraded repository is **not** refused at the door, because the
descriptor's required-feature list is not rewritten. It fails at the first
format-3 blob instead — `BlobEnvelope.Parse` asks its predicate by version
and class, demands the sealed shape an 88-byte format-3 data envelope does
not have, and throws. [ADR-0014](0014-format-versioning-and-stability.md)'s
*refuse, never misread* holds in substance: it refuses, and no byte is read
wrongly. What it loses is the diagnosis — "this repository is newer than me"
becomes "this blob is damaged". That is the cost of append-only propagation,
and it is acceptable here for the same reason format 1 could be withdrawn:
the product is pre-release, there is no installed base beyond the owner's
own, and the recovery tool ships from the same build as the service.

**A destination holds newer blobs before it holds the record.** A capture
ships the blobs it wrote; the upgrade record is an ordinary immutable object
no capture produces, so it rides the next *reconciling* pass. Until then a
copy holds format-3 blobs and a record that does not yet explain them. This
costs nothing — every blob declares its own container in its envelope, and
the reader dispatches on what it finds — and it is pinned on both sides by
`Hosts.Tests/FormatUpgradeTests` rather than hidden behind a sync.

**An upgrade cannot be undone.** Nothing removes the record and nothing
rewrites a blob that is already sealed. The console says so before it acts.

**`discover_archives` still reports the created version.** It reads
descriptors credential-free, by design ([ADR-0061](0061-adopt-a-destinations-archives.md)),
so it cannot verify a signature and cannot honestly report an effective
version. The contract says so on that verb rather than letting a console draw
the wrong conclusion from a row.

## Alternatives considered

**Rewrite the descriptor.** The obvious answer, and it cannot reach the
copies — the finding above. Not rejected on taste: rejected on three
independent mechanisms that each refuse to replace it.

**A per-blob marker saying "this repository now writes format N".** A
per-object statement is a per-object choice, and the attacker chooses —
[ADR-0055](0055-reclaim-authority.md) made exactly this argument against a
per-object schema version and chose a repository-level statement instead. The
same argument applies here, and a per-blob marker would also be redundant:
every blob already declares its own container version.

**Stamp the upgrade into the journal.** The journal is a plane a destination
may legitimately lack, and the record has to reach every copy including ones
that hold only what a retention instruction left them.

**Re-seed instead of upgrading.** Create a new format-3 set beside the old
one and let the old history age out. It is the honest zero-mechanism option
and it costs a full second copy of everything, on the destination and over
the link — the outcome ADR-0061 was written to avoid for a rebuilt machine,
and no more palatable for an upgrade.

## What this record does not do

It does not downgrade: there is no verb and no record shape for moving a
repository back, and nothing rewrites sealed blobs in either direction.

It does not compact, and it is what makes compaction reachable: the compactor
is written over `AppendSealedRecordAsync`
([ADR-0067](0067-the-keyless-compactor.md)), which exists only at format 3, so
an upgraded set gains a rewrite for its partly dead blobs that a format-2 set
has no way to perform at all. Nothing here rewrites a sealed blob; the upgrade
only changes what the *next* one is sealed as.

It does not withdraw format 2. `init --format-version 2` remains a supported
choice, the format-2 fixture stays frozen, and NFR-COMP-004 reads against
both.

## Amendment 1 (2026-10) — a build writes only a format it implements

§3 makes the effective version whatever the newest verified record names, and
nothing bounded that by the build reading it. A repository a newer build had
upgraded past format 5 read as format 6 here, and this build wrote into it as
though 6 were 5: `FormatVersions.ContainerVersion` answered 3 for every
version from 3 up, so a backup stamped format-3 containers, format-4 deltas
and format-5 checkpoints into a repository whose rules it had never seen.
[ADR-0096](0096-format-4-an-index-generation-per-delta.md) and
[ADR-0097](0097-format-5-a-checkpoint-carries-its-deltas.md) each listed the
refusal as owed.

A build now writes only a repository whose effective version it implements
([specification 11 §5.3](../../specifications/repository-format/11-lifecycle-objects.md#53-a-writer-writes-only-a-format-it-implements)).
Above that:

- **Nothing is authored.** A publication, a compaction pass, a retention
  apply and a person's deletion request each refuse before they write a key.
  All four throw `NewerFormatException`, whose message names both versions. `FormatVersions.ContainerVersion` refuses a version it does not
  know instead of naming a container for it, so a writing path that missed the
  check fails at its first blob rather than guessing. An upgrade record naming
  a version above this build's is refused too, because the record is the
  claim that the repository writes that format.
- **Nothing is reclaimed on this build's reading.** Which objects are live is
  read off an index in the newer format. So a destination's keep-set is
  refused, as an unwalkable closure already was, and the destination receives
  a whole copy. It keeps history its policy would drop until a build that
  writes the format converges it: more is kept, never less.
- **Everything that reads goes on.** The archive opens on every path, so the
  set's snapshots list, restore and verify read, and copies carry what is
  there, the newer build's record included. A retention dry run, which writes
  nothing, still reports.

The service raises `format-newer` at archive open and resolves it at an open
this build can write. The set's backup fails permanently, because only another
build can make it run, and a command that would write answers `refused` with
the message. The upgrade verb has nothing to move such a set to. The CLI
prints the refusal as an operator's error with exit code 1.

The refusal is enforced in the shared layers where each act begins, not at a
host's door. The service opens one handle per set for its reads and its
writes, so refusing the open would have taken restore and copy down with the
backup. The CLI takes its writer role for the catalogue rebuild too, which
writes nothing into the repository. Placed in the shared layers, the check
reaches both hosts and every verb that reaches the act.

What this does not do: reach a build older than this one. A build that
predates the amendment still writes into a repository a newer build upgraded.
That is why the refusal had to ship before the next format rather than with
it.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-10 | Accepted (amended) | The record is a named copy phase, so it reaches a destination with the sync after the set's next backup rather than at the daily reconciliation, and that sync's verification no longer fails for lacking it ([ADR-0056 Amendment 1](0056-incremental-reconciliation.md#amendment-1-2026-10--every-prefix-this-build-writes-is-named-and-a-pass-is-asked-only-about-what-it-carries); `Hosts.Tests/FormatUpgradeTests`) |
| 2026-10 | Accepted (amended) | A destination's delta retirement ([ADR-0098](0098-a-store-retires-what-its-own-checkpoints-carry.md)) is one more act Amendment 1 refuses above the implemented format: the sync still copies the set, and the destination retires nothing (`Agent/FanOut`; `Hosts.Tests/DeltaRetirementServiceTests`) |
| 2026-10 | Accepted (amended) | [Amendment 1](#amendment-1-2026-10--a-build-writes-only-a-format-it-implements): a build writes only a repository whose effective version it implements. Above it the repository is opened, read and copied, nothing is authored in it, and no destination is converged on this build's reading of it (`Repository/NewerFormatException`, `Domain/FormatVersions`, `Retention/DestinationConvergence`; `Repository.Tests/EndToEnd/NewerFormatTests`, `Retention.Tests/NewerFormatRetentionTests`, `Hosts.Tests/NewerFormatServiceTests`, `Cli.Tests/NewerFormatCommandTests`) |
| 2026-10 | Accepted (amended) | The upgrade verb targets the latest format, 5 ([ADR-0097](0097-format-5-a-checkpoint-carries-its-deltas.md)): an upgraded set checkpoints its deltas from its next backup, and an older build reads its checkpoints as damage and its deltas as before (`Hosts.Tests/FormatUpgradeTests`) |
| 2026-10 | Accepted (amended) | The upgrade verb targets format 4 ([ADR-0096](0096-format-4-an-index-generation-per-delta.md)): an upgraded set stamps its next delta with an index generation, and its older deltas keep none (`Hosts.Tests/FormatUpgradeTests`). The service's notice and the verb's lines say the newer format begins with what the set writes next, rather than at its next blob |
| 2026-09 | Accepted | Built over five commits: the creation default moved to the latest format the product writes; the record, its codec and the never-deletable refusal; the writer reading the effective version, with the recovery tool sharing the one decision; the verb, the agent verb and the console control at contract 1.36; and this record. The owner's installation stays format 2 by choice — both formats are supported and nothing pushes |
