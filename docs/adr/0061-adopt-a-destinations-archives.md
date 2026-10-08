# ADR-0061 — Adopt a destination's archives: the rebuilt machine resumes its sets

**Status:** Accepted
**Date:** 2026-09
**Requirements:** FR-WOR-006, FR-MAN-018, FR-WOR-002, FR-SVC-015, FR-DEST-006, NFR-OPS-005
**Related:** [ADR-0060](0060-the-passphrase-is-the-recovery-credential.md), [ADR-0053](0053-peer-claim-and-configuration-recovery.md), [ADR-0042](0042-write-only-repositories.md), [ADR-0040](0040-multi-root-backup-sets.md), [ADR-0046](0046-direct-to-destination-publication.md), [ADR-0006](0006-object-identifiers-and-dedup-trust-domains.md), [ADR-0022](0022-standalone-metadata-records-and-index-identifiers.md), [06 §7](../../specifications/repository-format/06-manifests.md#7-policy-manifest), [command contract](../../specifications/command-contract/README.md)

---

## Context

[ADR-0060](0060-the-passphrase-is-the-recovery-credential.md) withdrew the
recovery kit and named what it left undone. A person holds two things after a
machine dies: the passphrase, and knowing where the backups are. With those
two the standalone tool restores every file (§1 of that record), and that is
recovery of **data**. Recovery of **operation** — the machine going on
backing up, incrementally, into the same archives — needed the set's shape,
which lived only in the lost configuration file, and a way to bind a new
installation to an archive it did not write. Neither existed.
`ProvisionWriteOnlySetCommand` adopted only from `<archives>/<setId>`, the
console minted a fresh set id for every new set, and the only road back was
to declare a new set and pay for a full first backup into a second archive
beside the first.

Three decisions were taken by the owner in planning and shaped the whole
slice. Local-path destinations first and peers next, as a later commit of the
same slice, with a design that would not have to be reopened for them. The
console, the CLI and the web console all get the ceremony, because the
recovery drill needs a headless path. And **the archive must hold the root
path details as part of the metadata**: [ADR-0040](0040-multi-root-backup-sets.md)
found the repository format never records a root path — the tree names its
children relative to wherever the walk started — and the owner's decision is
that it now does, so adoption restores the set rather than asking the person
to re-type it. Pre-freeze and additive under
[ADR-0014 Amendment 1](0014-format-versioning-and-stability.md#amendment-1-2026-09--format-1-withdrawn-before-freeze)'s
rule; the format version stays 2.

One finding shaped the design more than any other, and it was found by
reading rather than by failing: **dedup is by writer id, and a rebuilt
machine mints a new one.** `DedupTrustGate` never reuses another writer's
segment under the device domain ([ADR-0006](0006-object-identifiers-and-dedup-trust-domains.md)),
which is the domain every write-only set runs under. A rebuilt machine
writing under a fresh identity would re-ship every re-read file once —
"the next backup is incremental" would have been false for exactly the files
a person touches after a rebuild.

## Decision

### 1. The archive records the set's shape

The policy manifest ([06 §7](../../specifications/repository-format/06-manifests.md#7-policy-manifest))
gains three optional keys: `10 roots`, an array of `{1 label?, 2 path}` maps;
`11 set_name`; `12 schedule`. The policy manifest is the object that already
answers "what settings produced this?", and rules (keys 8 and 9) and the set
id (snapshot key 3) were already there; these three are exactly what a
configuration says about a set that the tree does not. A manifest that
records none of them encodes to the nine-key map every existing archive
holds, byte for byte — the encoder counts only the keys it writes — and the
decoder tolerates their absence while still refusing an unknown key, at the
manifest and inside a recorded root.

**Not recorded, on purpose:** retention, priority and destinations.
Retention is overridable per destination by destination *name*, and
FR-DEST-006 keeps destination identities out of the repository, so a
recorded retention would be half a policy; priority is a scheduling
preference re-declared for free. A single-file archive and the CLI's direct
`backup --repo` have no set and record roots only.

> **Amendment 1 (2026-09).** The set's own retention is now recorded, as
> key 13. What stays out is a destination's override, which names the
> destination — see
> [Amendment 1](#amendment-1-2026-09--the-sets-own-retention-is-recorded).

Root paths sit in the metadata plane, encrypted under the metadata key, so a
destination or peer sees ciphertext and T-11 is unchanged. The structure
plane a write-credential holder can read (FR-WOR-003) now names *where on
the source disk* a root sat as well as what its tree is called; the threat
model says so in one sentence.

### 2. Discovery is credential-free

`discover_archives {destination_name}` lists what a declared destination
holds by descriptor alone: repository id, format, creation facts, the public
salt, costs and sealing key, the snapshot count and highest publication
counter read from cleartext object names, which configured set already owns
it, and whether this installation wrote it. Every fact is read from the
unencrypted descriptor or counted from object names, so the verb reveals
nothing a directory listing of the destination would not; the sealing public
key is the verifier a client compares its own derivation against before
sending anything. A damaged descriptor is a warning line, never a failure —
one broken directory must not hide the archives beside it from the person
looking for them.

### 3. Adoption is set-id-preserving, ledger-seeding and first-backup-free

`adopt_archive {destination_name, repository_id, envelope, set_name?, roots?,
schedule?, priority?}` takes one archive back under its original repository
id **and** set id. The envelope is the provisioning envelope
`provision_write_only_set` already carries, derived against the
**discovered** archive's salt and parameters rather than the installation's:
the rebuilt machine's own salt is new, and only a credential derived under
the archive's opens it. The service proves the derived sealing public key
against the descriptor, and nothing is written before that proof.

The set is re-declared from the newest snapshot's policy manifest — name,
roots, schedule, rules — with each field overridable on the command and
required exactly where the archive records nothing: an archive written for
no configured set, or before the shape was recorded, needs a name given.
Then, in an order a half-finished adoption can be re-run over: the catalogue
is rebuilt at the runtime's real path for that repository, over the replica;
the metadata is copied into the set's metadata store with if-absent puts;
the writer identity is decided (§4); the per-set credential is stored; the
set is appended to the configuration as direct-ship — the destination holds
the whole repository ([ADR-0046](0046-direct-to-destination-publication.md)
decision 1) — without queuing a first backup or marking the destination
owed a full copy; and the destination's ledger row is seeded as a completed
baseline at the replica's own publication head, so the sink admits the
destination and the first fan-out pass reconciles by listing rather than by
bytes. The same placement and circular-capture guards an upsert applies
apply here.

> **Amended 2026-10 ([ADR-0091](0091-an-s3-compatible-destination.md) Amendment 1).**
> A set adopted from an S3-compatible store is appended as a staging set,
> not direct-ship, because a run never writes through a store. In place of
> the metadata store, its staging archive is seeded with what a trimmed one
> keeps: the metadata, and the data the newest snapshot's files are read
> from.

A recorded root missing on this machine is **reported, never refused**: the
person edits the set, and nothing silently rewrites a path. A set already
configured against this very archive is acknowledged rather than repeated —
its destination reference, ledger row and credential are made sure of — and
one configured against a different archive is refused by name.

> **Amended 2026-09 ([Amendment 2](#amendment-2-2026-09--a-recovered-configuration-is-confirmed-before-it-takes-effect)).**
> The report now comes first. `preview_adoption` shows the recorded shape,
> each root flagged where it does not resolve on this machine, and
> `adopt_archive` takes effect only with the confirmation that preview
> answered. A missing root is still not refused at adoption. It can be
> re-pointed at confirmation, to a path the person typed, and a backup of a
> set whose root is absent fails naming it. Still nothing silently rewrites
> a path.

### 4. The writer identity is resumed when that is safe

Adoption takes over the archive's writer identity when this state directory
has published nothing — no sequence file for any repository — and the archive
has exactly one writer. Otherwise the new identity stays and the answer says
so: the next run re-sends unchanged content once, because the device domain
never reuses another writer's segment. Two writers in one repository is a
supported shape ([ADR-0008](0008-index-generations-and-checkpoints.md)), so the
fallback is slow, not wrong. `ServiceRuntime.AdoptObservedHeadAsync` then
moves the fresh sequence file past the head the index and journal attest at
first open, exactly as it already did for a restored state directory.

### 5. Contract 1.30, and the envelope fence

Two verbs and two results, additive. The set descriptor also gains the
archive's own public derivation parameters and sealing key, so a client
derives a restore grant **per set**: an adopted set keeps the salt its
archive was born under, which the installation's parameters on
`describe_service` cannot tell it. The console already read each set's
descriptor from disk; the CLI's routed restore and the test harness now
derive per set, one derivation per distinct salt.

`KeyMaterialConfinementTests`' envelope allowlist grows by decision to name
`adopt_archive`, with its message extended, rather than by widening the
pattern — the fence is only a fence while it is a list.

### 6. Every surface, and peers by the same steps

The web console offers "Find backups…" on every local-path destination row
and an "Adopt…" dialog per unowned archive; its endpoint asks the service for
the discovery afresh rather than trusting facts the page sends, derives
against that row in the console process, proves the derivation against the
row's sealing key, and only then sends. The CLI's `discover` and `adopt`
speak to the running service, locally or over `--connect`, and never touch a
repository in-process. Everything below the resolve step reads through the
object-store abstraction, which is what let the peer half add an enumerator
and a store and nothing else: at a peer the owner inventory the retrieval
session already serves ([07 §3.5](../../specifications/peer-protocol/07-retrieval.md))
names what this device owns there — after a claim
([ADR-0053](0053-peer-claim-and-configuration-recovery.md)) — and each
candidate is read over the retrieval object store. Before any claim the
inventory names nothing and discovery answers an empty list rather than a
refusal; the remedy is the claim, not a different verb.

> **Amended 2026-10 ([ADR-0091](0091-an-s3-compatible-destination.md) Amendment 1).**
> An S3-compatible store takes the same steps through the same abstraction:
> discovery lists the folders under its prefix that hold a descriptor, with
> the key this service holds for the store, and the console offers "Find
> backups…" on a store's row as on a local path's.

### 7. What is given up, and the bounds

**A recorded path on a machine laid out differently** is reported as
missing and left for the person to edit; a different drive letter or
operating system is not guessed at.

**A replica that is behind the archive's true head** — the destination the
person still has was not the last one written — resumes from *that*
replica's head. History the other destination holds is not lost, but the new
snapshot's parent is the newest this replica has, and the synced watermark
is seeded from this replica only. A later adoption of the second destination
for the same set takes the idempotent path and seeds its own ledger row from
its own counters. The sequence-collision bound `ObservedHead` states remains
the backstop, and the store's colliding-put refusal stands behind it.

**Two writers in one archive** after an adoption that could not resume the
identity costs one re-send of touched content, said in the answer.

## Consequences

**Positive**

- The owner's recovery model is complete: the passphrase and knowing where
  the backups are recover the data, and now also recover the operation —
  original ids, name, roots, schedule and rules, and an incremental next
  backup into the same archive. `eng/recovery-drill.sh` step 8 runs it on the
  Release binaries: the resumed backups of two sets shipped fourteen
  kilobytes, not the history.
- An archive now says what set it was, which is worth having on its own —
  a snapshot years later answers "what folders, on what schedule" without the
  configuration file that wrote it.
- Nothing pinned moved: the nine-key policy manifest is byte-identical, the
  fixture's read expectations stand, and the format version is unchanged.

**Negative**

- The structure plane the service can read gained source-machine paths.
  Stated in the threat model rather than hidden; the content stays sealed.
- Adoption from a peer reads the replica over the wire — descriptors,
  snapshot objects, metadata blobs for the catalogue rebuild — once per
  adoption. It is the cost of the machine holding nothing.

**Neutral**

- A provisioning envelope too short to be one at all crashed both the
  adoption and the provisioning verbs with an argument exception from the
  content-key length check; both now refuse it as an envelope this service
  cannot open. Found by the peer drill's not-found case.
- ADR-0040's rejected alternative — a `roots` array on the snapshot manifest
  — stays rejected: the roots are recorded on the policy manifest, which is
  the "what settings produced this?" object, and the tree shape of a
  single-root set is unchanged.

## Alternatives considered

**Extend `provision_write_only_set`.** It needs a configured set name and
routes by set id to a *local* path; adoption starts from a destination and an
archive nobody has configured yet. Bending it would have put a destination
name and a repository id on a verb whose contract is "the set you already
declared".

**Extend `upsert_backup_set` with an envelope.** It queues a first backup and
marks every destination owed a full copy, which is precisely what an adopted
set must not do; and it would have put a sealed envelope on the configuration
verb, widening the fence NFR-SEC-009 draws.

**Put the shape in the recovery kit.** [ADR-0053 §4](0053-peer-claim-and-configuration-recovery.md),
closed as will-not-do with the kit. The archive is the artefact that
survives; a printed page holding what the archive could hold is a note.

**Take the shape from the caller only.** Simpler, and the person re-types
what the machine already knew. The owner's decision was the opposite, and
the cost — three optional keys — is small.

**Always mint a new writer identity.** Simplest, and every touched file
ships again after every rebuild. The device dedup domain exists to make
another writer's bytes untrusted; a rebuilt machine is the same writer with
a new state directory, and taking the identity back when nothing has been
published under the new one is the honest reading of that.

## Amendment 1 (2026-09) — the set's own retention is recorded

§1 left retention out because "a recorded retention would be half a
policy": retention is overridable per destination by destination name, and
FR-DEST-006 keeps destination identities out of the repository. The premise
holds, but the conclusion does not follow from it. A set's retention is two
things. One is the set's own policy, which is the whole of what the set says
about deletion: it governs the set's own archive where there is one, and
every destination that declares no override. The other is the overrides,
which belong to a pairing of set and destination, and only they name a
destination. Leaving both out meant a set re-declared from its archive came
back with retention deferred. A machine rebuilt from nothing recovered
everything about a set except what it was allowed to delete, which the merge
that brought this record in wrote down as owed (FR-DR-006).

So the policy manifest gains key 13, `retention`: the set's own policy, one
u32 per declared rule, absent when the set defers retention
([06 §7](../../specifications/repository-format/06-manifests.md#7-policy-manifest)).
A manifest without it is byte-identical to one written before, as with
keys 10–12. Overrides stay out for the reason §1 gave. They come back where
destinations come back: declared by hand, with the destination they name.

Adoption re-declares the recorded policy with the rest of the shape, and
says so. The answer carries it (contract 1.40), and the report names the
rules, so a person sees what the adopted set will delete by before anything
runs under it. That is not a confirmation step: FR-DR-009 stays unbuilt,
and this amendment does not claim it. A recorded zero, which this writer
never produces, is refused by the configuration's own rule. The set is then
adopted with retention deferred and the report says why, rather than the
adoption failing on a field the command gives no way to correct.

Priority and destinations remain unrecorded, for §1's reasons.

> **Amended 2026-10 ([ADR-0094](0094-a-deleted-file-stays-restorable.md)).** The retention map gains rule 6, `keep_deleted_days`: how many days a file deleted from the sources stays restorable. It is the set's own, recorded and re-declared with the rest of the policy; a destination's override of it is still not recorded. A reader that predates the rule refuses a map carrying it, as it refuses any key it does not know.

## Amendment 2 (2026-09) — a recovered configuration is confirmed before it takes effect

§3 re-declared the set from its archive and acted on it in one call. A
missing root, and since Amendment 1 the retention, were reported only in the
answer, after the set was configured. FR-DR-009 asks for the other order:
the recovered configuration is shown first, each root's recorded path as a
hint and flagged where it does not resolve on this machine, the retention
before it can delete anything, and only a confirmation makes it take effect.

**The preview.** `preview_adoption {destination_name, repository_id,
envelope}` takes the envelope adoption takes. It runs adoption's steps in
adoption's order up to the proof of the derived sealing key, so a wrong
passphrase or a foreign archive is refused at the preview, as it is at
adoption. It then reads the recorded shape and writes nothing to the state
directory. The shape can only be read through a catalogue, and adoption's
first write is its catalogue at the runtime's real path, so the preview
rebuilds one in a scratch directory and deletes it before it answers. The
answer, `adoption_preview`, carries:
- the set's id and name;
- each root's recorded path, with `resolves`;
- the schedule and rules;
- the set's own retention, which is what the set would delete by;
- the snapshot count and the newest snapshot;
- what adoption would say about this installation: already adopted,
  configured against a different archive, or a name already taken;
- a `confirmation`.

**The confirmation.** It is a SHA-256 digest over a length-prefixed canonical
encoding of what the preview showed, with the snapshot count and the newest
snapshot's id and time. It is not a secret and authorises nothing: the
envelope still does that. It names what was seen. The newest snapshot is in
it so that an archive written again between the preview and the
confirmation — the old machine still running, say — no longer matches, even
when its shape did not change.

**Adoption requires it.** `adopt_archive` gains `confirmation`.
- Without one, it is refused before any envelope is opened, naming the
  preview. The destination, id, presence and schedule refusals stay ahead
  of it, in the order they had.
- With one the archive no longer matches, it is refused as changed once the
  shape is re-read. The catalogue that re-read rebuilt is removed, unless a
  configured set already owns the repository; then it is that set's own.
- `set_name`, `roots` and `schedule` still override the recorded shape at
  confirmation. The digest is over what the archive recorded, so an
  override is a choice made while looking at the preview, not a change to
  what it showed. That is how a root is re-pointed: the confirmed path is
  the one captured from, under the recorded label the snapshots already
  carry.

§3's rule for a missing root stands: it is not refused at adoption. It is
no longer learned afterwards, though. The preview flags it; the person
re-points it at confirmation, or adopts as recorded and restores into it. A
backup of a set whose root is absent fails naming the root and publishes no
snapshot. The schedule is adopted as confirmed.

**Contract 1.42.** It is not purely additive, deliberately. A pre-1.42
client adopting in one call is refused by name rather than adopting a set
nobody was shown, because that one call is what FR-DR-009 ends. The CLI and
the console ship with the service. `KeyMaterialConfinementTests`' envelope
allowlist grows by decision to name `preview_adoption` (§5). The shape the
preview shows is sealed metadata that only the passphrase's derivation
reads, so it takes the envelope adoption takes.

**The surfaces.**
- The CLI's `adopt` prints the preview: each root on or not on this
  machine, the schedule, rules, history and the service's lines. Without
  `--confirm` it ends "Nothing was adopted" with exit 2; `--confirm` adopts.
- The console keeps one endpoint permitted a secret, `/api/adopt-archive`,
  in two phases. Without a confirmation it returns the preview, which the
  page shows with each folder's path editable; with one, it adopts. The page
  holds the passphrase from one phase to the other and drops it with the
  dialog, and each phase derives again.
- `eng/recovery-drill.sh` runs a preview-only `adopt` before adopting both
  archives with `--confirm`.

What the confirmation cannot prove is that the preview was read. The CLI
and the console show it before they can send the confirmation. A client
written against the contract could fetch one and confirm it unread. The
verb guarantees an adoption as shown, not as understood.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-09 | Accepted | Built end to end over six commits: the policy manifest's keys 10–12 (`Repository.Format/Manifests/PolicyManifest`, `Repository/SnapshotPublication`, `Agent/BackupRunner`); contract 1.30 and the service's `Agent/ServiceCommandHandler.Adoption.cs` with the writer-identity resume in `Application/LocalState`; the console's `/api/adopt-archive` through `Web/ConsoleRestoreGate`; the CLI's `discover` and `adopt` in `Cli/CliApplication` with the per-set restore grant in `Cli/OperationGateway`; the peer half over `Agent/PeerRetrievalClient`; `eng/recovery-drill.sh` step 8 green on the Release binaries, with `Hosts.Tests/DestinationAdoptionTests` and `Hosts.Tests/PeerAdoptionTests` as the in-process drills |
| 2026-09 | Accepted (amended) | Amendment 1: the set's own retention recorded as policy-manifest key 13 and re-declared on adoption, reported in the answer (FR-DR-006, contract 1.40); a destination's override still unrecorded (FR-DEST-006). `Repository.Format/Manifests/PolicyManifest`, `Agent/RecordedRetentionMapping`; `Hosts.Tests/DestinationAdoptionTests` |
| 2026-09 | Accepted (amended) | Amendment 2: a recovered configuration is previewed and takes effect only with the confirmation the preview answered (FR-DR-009, contract 1.42); an unconfirmed adoption is refused by name, and one the archive has moved on from is refused as changed. The preview and the check in `Agent/ServiceCommandHandler.Adoption.cs`; the CLI's `adopt --confirm` in `Cli/CliApplication`; the console's two phases in `Web/WebConsoleHost`; `eng/recovery-drill.sh` green on the Release binaries, with `Hosts.Tests/DestinationAdoptionTests`, `Hosts.Tests/PeerAdoptionTests`, `Web.Tests/AdoptionCeremonyTests` and `Web.DomTests/ConfigEditingDomTests` |
| 2026-10 | Amended (related) | [ADR-0091](0091-an-s3-compatible-destination.md) Amendment 1: an archive is discovered, previewed and adopted from an S3-compatible store by the same steps (§6), and the set it adopts is a staging set seeded with what a trimmed staging archive keeps, since a run never writes through a store (§3). `Agent/ServiceCommandHandler`; `Hosts.Tests/S3AdoptionTests`, `Web.Tests/AdoptionCeremonyTests` and `Web.DomTests/ConfigEditingDomTests` |
| 2026-10 | Amended (related) | [ADR-0093](0093-an-azure-blob-destination.md): an archive is discovered, previewed and adopted from an Azure Blob container by the same steps as from a bucket, into a staging set for the same reason (`Agent/ServiceCommandHandler`, `Hosts.Tests/AzureBlobAdoptionTests`) |
| 2026-10 | Amended (related) | [ADR-0094](0094-a-deleted-file-stays-restorable.md): Amendment 1's retention map gains rule 6, keep_deleted_days, recorded and re-declared with the set's own policy and said in the adoption report (FR-GC-014, contract 1.62). `Repository.Format/Manifests/PolicyManifest`, `Agent/RecordedRetentionMapping`, `Agent/ServiceCommandHandler.Adoption.cs`; `Repository.Tests/Format/ManifestCodecTests`, `Hosts.Tests/DestinationAdoptionTests` |
