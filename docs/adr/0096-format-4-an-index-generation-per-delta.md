# ADR-0096 — Format 4: an index generation per delta

**Status:** Accepted
**Date:** 2026-10
**Requirements:** FR-MAN-015 (met by rank at format 4: a relocation outranks what it relocates, whichever writer published either); FR-MAN-016 (amended: a gap ages per gap, in index generations, with a production patience of 8); NFR-COMP-004 (amended: `fixture-repository-v4`)
**Related:** [ADR-0017](0017-index-entry-supersession.md) Amendments 1–3 (the rank rule; Amendment 3 records this), [ADR-0008](0008-index-generations-and-checkpoints.md) Amendment 1 (the gap patience and the checkpoint constraints), [ADR-0022](0022-standalone-metadata-records-and-index-identifiers.md) §Decisions 5 and 7 (the current generation; sequence accounting), [ADR-0052](0052-relocatable-records-format-v3.md) (format 3, whose records format 4 keeps), [ADR-0066](0066-the-format-upgrade-record.md) (the upgrade record), [ADR-0014](0014-format-versioning-and-stability.md) (refuse by name), [ADR-0006](0006-object-identifiers-and-dedup-trust-domains.md) (the reuse shortcut, fixed in format 3), [specification 07 §2.4](../../specifications/repository-format/07-index.md#24-index-generation), [§3](../../specifications/repository-format/07-index.md#3-precedence), [§4](../../specifications/repository-format/07-index.md#4-sequence-gaps-and-void-deltas)

**Built:**
- The constants: `Domain/FormatVersions` (`IndexGenerations` = 4) and `Domain/FormatLimits`, which create and upgrade at 4.
- The wire: `Repository.Index/IndexDeltaCodec` reads and writes key 12, signed, and refuses a present 0. `Repository.Format/Descriptor/RepositoryDescriptorCodec` adds feature `0x0004`, and `Repository/RepositoryLifecycle` lists it at format 4.
- The writer: `Repository.Index/WriterSequence` keeps the writer's last index generation beside its sequence state. `Repository.Index/IndexPublisher` stamps every delta, voids included, one above the higher of what the publication has seen and the writer's own last. `Repository/SnapshotPublication`, `Repository/PublicationOrchestrator` and `Repository/CompactionPass` pass the catalogue's highest at format 4 and nothing below it.
- The readers: `Repository.Index/IndexLoader` ranks an entry by its delta's index generation and ages each gap from the first later delta of its writer, with `IndexLoader.GapPatienceGenerations` (8), which the product's two rebuilds now pass. `Repository.Catalogue/Catalogue` (schema 10, `Repository.Catalogue/CatalogueSchema`) records each delta's index generation, ranks locations by it and answers `MaxIndexGeneration`.
- The tests:
  - `Repository.Tests/Index/IndexGenerationTests`: key 12 is signed, absent reads as none and a present 0 is refused both ways; a writer publishes one above the highest it has seen, every delta advancing it, and its own last outlives the process; a writer below format 4 publishes none; the later index generation wins whichever writer published it, and without one the writer id still decides; a gap ages from the delta after it and is damage at the patience, whether its writer is still publishing or stopped and another writer is; and a gap in format-3 history is not aged by the format-4 deltas after it
  - `Repository.Tests/EndToEnd/IndexGenerationPublicationTests`: a backup publishes above the highest its catalogue holds, or with no catalogue above its own last; it voids a leftover at an index generation too; and a format-3 backup publishes none
  - `Repository.Tests/Catalogue/CatalogueTests`: the catalogue ranks by index generation, agreeing with `IndexPrecedence` on random entry sets, and answers the highest any applied delta carries
  - `Repository.Tests/Format/RepositoryDescriptorCodecTests`, `Repository.Tests/EndToEnd/RepositoryLifecycleTests`, `Domain.Tests/FormatVersionsTests`: a format-4 descriptor lists both features, either mismatch is a violation, format 5 is refused naming 2 to 4, and a repository is born at 4
  - `Retention.Tests/CompactionCollectionTests`, `Hosts.Tests/CompactionRetentionTests`: at format 4 a compaction under the lower writer id stands, the dry run offers it, and the next pass does not undo it; at format 3 the stopgap still declines it
  - `Hosts.Tests/FormatUpgradeTests`, `Hosts.Tests/CreatedFormatTests`: a set upgraded from 3 stamps its next delta and its older ones keep none, and a set created today is format 4
  - `Repository.ConformanceTests/FixtureRepositoryV4Tests`: the committed `fixture-repository-v4` declares format 4 and both features, its blobs are stamped 3, its delta carries index generation 1, and it restores byte for byte

---

## Context

An index entry ranks by `(generation, writer_id, sequence)` ([07 §3](../../specifications/repository-format/07-index.md#3-precedence)). Up to format 3, the generation is the delta's key generation. Nothing advances it, so every entry for an object ties on it, and between two writers the writer id's bytes decide ([ADR-0017 Amendment 1](0017-index-entry-supersession.md#amendment-1-2026-10--precedence-as-built-and-the-writer-it-favours)).

That matters when a machine adopts an archive and keeps its own identity, so the archive has two writers. A compaction published under the lower of the two ids loses to the insertions it relocates, and the next retention pass undoes it. [Amendment 2](0017-index-entry-supersession.md#amendment-2-2026-10--the-compactor-declines-a-relocation-that-would-not-stand) stopped the waste: the compactor declines such a blob and writes nothing. That leaves the lower id unable to compact the other writer's blobs at all.

Amendment 1 set three conditions on the fix:
1. A compaction's supersessions must outrank every entry they relocate, whichever writer published either.
2. Two insertions must stay order-independent.
3. Advancing a generation must not turn a stopped writer's journal records and blobs into missing deltas. The gap patience is counted in generations, and once they move, those numbers would age.

The third was met ahead of the fix. The product's index loads pass sequence accounting ([ADR-0022](0022-standalone-metadata-records-and-index-identifiers.md) §Decision 7, amended), so a number spent on a journal record or a named blob is not a gap. The reuse shortcut's dependence on the winner was fixed in format 3 too ([ADR-0006](0006-object-identifiers-and-dedup-trust-domains.md#amendment-2026-10--a-devices-own-copy-is-one-it-holds-not-only-one-that-wins)). What remained is the rank, and a rank is in the format.

## Decision

### 1 Every delta a format-4 writer publishes carries an index generation

Key 12, `index_generation`, a u64 of at least 1 ([07 §2.4](../../specifications/repository-format/07-index.md#24-index-generation)).
- **On every delta, voids included,** so a reader can age a gap from whichever delta follows it (§4).
- **Signed.** The signature covers every key but itself, so a store that raised an entry's rank would break its delta's signature.
- **Never on a delta below format 4.** A reader that predates the key refuses a delta carrying it as an unknown key ([07 §2.5](../../specifications/repository-format/07-index.md#25-unknown-keys)), so a repository such a reader is entitled to read never holds one. This is §2.3's rule for the Merkle roots, applied again.
- **Never 0.** Absence means 0, and a canonical form has one encoding of each fact. A writer refuses to encode it, and a reader refuses to decode it.

A reader reads the key on any delta that carries it, whatever the repository's descriptor or upgrade records say. A copy can hold a delta before it holds the upgrade record that explains it, as a copy can already hold a format-3 blob before its record arrives.

### 2 One more than the highest the writer has seen

A writer stamps a delta with one more than the higher of two numbers:
- **The highest index generation in the catalogue the publication applies its delta to.** That catalogue holds every delta the writer has loaded, so a relocation, which a pass plans from it, is published above every entry it relocates.
- **The writer's own last.** `WriterSequence` keeps it beside the sequence state, durable before it is used, as a sequence number is. A publication with no catalogue, or one whose catalogue is waiting to be rebuilt, still publishes above every delta its writer published before. Without it, such a writer could publish below its own earlier entries, which would then outrank its newer ones.

Each delta advances the writer's own last, so a publication's deltas rise one at a time, and a writer's index generations strictly increase across everything it publishes.

The number is a Lamport clock over the index: it orders what a writer had seen before what it published next. It does not order two writers that saw nothing of each other. Two insertions published that way may carry one index generation, and the writer id decides between them as it always did. Either location serves, so condition 2 holds.

### 3 An entry ranks by its delta's index generation

Precedence keeps its shape: `(index generation, writer_id, sequence)`. A delta without key 12, as every delta below format 4 is, ranks at 0. So format-3 history ranks exactly as it did, and anything a format-4 writer publishes ranks above all of it.

The key generation keeps one meaning: which key the delta is signed and sealed under. It ranks nothing. [ADR-0022](0022-standalone-metadata-records-and-index-identifiers.md) §Decision 5's current generation is still the key generation, and the gap patience's key-generation term stays, though nothing advances it yet.

A checkpoint's entries rank at the checkpoint's generation, as before. Nothing in the product publishes a checkpoint, and how a checkpoint keeps each entry's rank is the first of the constraints [ADR-0008 Amendment 1](0008-index-generations-and-checkpoints.md#amendment-1-2026-10--what-is-built-and-what-a-checkpoint-must-satisfy-first) sets. The checkpoint work owes it.

### 4 A gap ages per gap, in index generations, and is judged at 8

Once index generations advance, the patience counted in generations runs out for the first time. So the age is counted where the evidence is. A gap's age is the highest index generation any loaded delta carries, less that of the first delta its own writer published above the gap. That delta was published after the missing number was allocated, so the gap is at least that old. A gap is judged when that age, or the key-generation age as before, reaches the patience.

- **Per gap, not per writer.** A rule that aged a gap from the writer's newest delta would never judge a writer that is still publishing. Its newest delta is always new. Counting from the delta after the gap judges it however busy its writer is.
- **Against every writer's deltas.** A writer that has stopped still has its gap aged as the repository's other writers publish.
- **Not across the upgrade.** A gap whose next delta carries no index generation, as one in format-3 history does, has nothing to count from. It stays as it was before the upgrade, unresolved, however many format-4 deltas follow.
- **Eight.** A number its writer left pending is discharged by that writer's next publication ([07 §4](../../specifications/repository-format/07-index.md#4-sequence-gaps-and-void-deltas)). Eight is room for the writer to miss several runs while other writers publish, after which the gap is reported as the damage it then most likely is. It is `IndexLoader.GapPatienceGenerations`, and the product's two rebuilds now pass it rather than the 2 they passed while nothing aged.

### 5 The catalogue ranks by it: schema 10

`index_deltas` gains `index_generation`, and `MaxIndexGeneration` answers the highest any applied delta carries. `object_locations.generation` keeps its name and now holds what an entry ranks by, its delta's index generation or 0, so the queries that order by it rank as the loader does. A catalogue at schema 9 ranked by key generation, so it is dropped and rebuilt from the repository at open, as every schema change is. A rebuilt catalogue holds the index generations, because the deltas do.

### 6 Format 4 names itself, and the product creates at it

A format-4 descriptor lists `0x0004` (`index-generations`) beside `0x0003`. A format-2 or format-3 descriptor lists neither or only the first, and either mismatch is a format violation ([01 §3.2](../../specifications/repository-format/01-object-layout.md#32-body)). So a reader that predates format 4 refuses a repository created at it by name, at open, rather than at each delta.

Format 4 changes the index delta and nothing else. Its blobs and records are format 3's and are stamped 3 ([00 §5](../../specifications/repository-format/00-conventions.md#5-versioning-and-feature-negotiation)). `FormatLimits.FormatVersion` is 4, so a repository is born at 4, and formats 2 and 3 remain creatable on request and readable for ever.

### 7 The stopgap retires at format 4, by the same rule

The compactor still asks whether a relocation would stand, and now asks it at the index generation it will publish at: one above the highest its catalogue holds. Every incumbent it would relocate is in that catalogue, so every relocation stands, under either writer id. Below format 4 the pass publishes no index generation, asks at 0, and declines exactly what Amendment 2 declined. Nothing branches on the format in the compactor. The rule is the same, and the number it is asked at moves.

### 8 A set below format 4 is upgraded on request

[ADR-0066](0066-the-format-upgrade-record.md)'s record does it, to `FormatLimits.FormatVersion`. The upgraded set stamps its next delta. Its older deltas keep none: they are history, which ranks at 0. Nothing is pushed: the service raises its notice at open, and the person decides.

## Consequences

**Positive**
- **A relocation outranks what it relocates, whichever writer published either.** That is FR-MAN-015's acceptance met by the rank rather than by declining. The lower writer id compacts too.
- **The patience runs out.** A gap is reported as damage after eight index generations rather than held unresolved for ever, whether its writer is still publishing or has stopped.
- **Format-3 history is read exactly as before.** Every entry in it ranks at 0, and every gap in it ages as it did.

**Negative**
- **An older reader of an upgraded repository reads the newer deltas as damage.** The descriptor of a repository upgraded to 4 still says what it was created at, so an older build opens it, refuses each delta carrying key 12 as an unknown key, and reports it as an index object it cannot read. That is ADR-0066's stated price, carried from blobs to deltas. A repository created at 4 is refused at the door.
- **An older writer of an upgraded repository publishes below everyone.** It writes deltas without key 12 into it, which rank at 0, so its relocations would lose. Nothing in this record can reach a build that predates it.
- **One more write per delta.** The writer's last index generation is made durable before it is stamped, as a sequence number is. A backup publishes one delta and a void for each leftover, so the cost is one or two small writes a backup.
- **Up to nine bytes a delta.** The key and its value.

## Alternatives considered

- **A rank on every entry.** A Lamport pair inside each entry, rather than one per delta. Every entry would pay for it, and a delta's entries are published together, so one number per delta says the same.
- **Advance the key generation.** A rotation would move rule 1, but a key generation names a key, and rotating a key to reorder an index ties two unrelated decisions together. A rotation is a key-management act with costs of its own ([ADR-0009](0009-garbage-collection-safety.md) Amendment 5).
- **Count the patience in the writer's own later publications.** Simpler, but a writer that stopped publishing would never have its gap judged.
- **Count the age from the writer's newest delta.** That is the rule as built. It never judges a writer that is still publishing, which is the gap `Repository.Tests/Index/IndexGenerationTests` reports and the old rule held unresolved.
- **Take the highest from the catalogue alone.** A writer whose catalogue was absent or awaiting a rebuild would publish at 1, below its own earlier entries.

## What this record does not do

- **Rank a checkpoint's entries.** Its entries still rank at the checkpoint's generation. Nothing publishes a checkpoint, and the checkpoint work owes the rank ([ADR-0008 Amendment 1](0008-index-generations-and-checkpoints.md#amendment-1-2026-10--what-is-built-and-what-a-checkpoint-must-satisfy-first), constraint 1).

  > **Amended 2026-10 ([ADR-0097](0097-format-5-a-checkpoint-carries-its-deltas.md)).** Met another way. A format-5
  > checkpoint carries deltas rather than entries, and each carried entry
  > ranks at its own delta's index generation, writer and sequence. A
  > checkpoint's own entries, the earlier form, are ranked nowhere.

- **Refuse to write above the newest format this build knows.** An upgrade record naming a version above 4 sets the effective version above it, and this build would still write format 4's objects. A build should refuse to write a repository whose effective version it does not know. That is owed.
- **Reach older builds.** What an older build does with an upgraded repository is decided by that build.

> **Amended 2026-10 ([ADR-0066 Amendment 1](0066-the-format-upgrade-record.md#amendment-1-2026-10--a-build-writes-only-a-format-it-implements)).** The refusal is built. A build writes
> only a repository whose effective version it implements; one upgraded past
> it is read and copied, and nothing is authored in it or converged away on
> this build's reading of it.

## Status history

| Date | Status | Note |
|---|---|---|
| 2026-10 | Accepted; built (amended) | The rank owed to a checkpoint's entries is met by [ADR-0097](0097-format-5-a-checkpoint-carries-its-deltas.md): a format-5 checkpoint carries deltas, each entry ranked as its own delta's, and a checkpoint's own entries are ranked nowhere. |
| 2026-10 | Accepted; built (amended) | The refusal to write above the newest format this build knows, owed here, is built by [ADR-0066 Amendment 1](0066-the-format-upgrade-record.md#amendment-1-2026-10--a-build-writes-only-a-format-it-implements): at a newer effective version a publication, compaction, retention apply, deletion request and upgrade record are refused naming both versions, and a destination is given a whole copy (`Repository/NewerFormatException`, `Hosts.Tests/NewerFormatServiceTests`) |
| 2026-10 | Accepted; built | Key 12 is written by every format-4 writer and read by every reader; entries rank by it; a gap ages per gap in index generations with a production patience of 8; the catalogue is schema 10; a repository is born at 4 and lists `0x0004`; the compactor's stopgap declines nothing at 4. Recorded in [ADR-0017 Amendment 3](0017-index-entry-supersession.md#amendment-3-2026-10--format-4-ranks-an-entry-by-its-index-generation) |
