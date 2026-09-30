# ADR-0077 — Observed clock skew is read from a peer's receipt and recorded per snapshot

**Status:** Accepted
**Date:** 2026-09
**Requirements:** NFR-TIME-002, NFR-TIME-001
**Related:** [ADR-0009](0009-garbage-collection-safety.md) Amendment 7 (the other half of NFR-TIME-002), [ADR-0064](0064-replication-receipts.md) (the receipt the reading is taken from), [ADR-0011](0011-commit-versus-replication-semantics.md) (why a manifest does not wait for a peer), [ADR-0010](0010-local-store-separation.md) Amendment 4 (the rebuild at open), [specification 06 §6](../../specifications/repository-format/06-manifests.md#6-snapshot-manifest) key 14, [peer-protocol 03 §3.5](../../specifications/peer-protocol/03-replication.md#35-the-replication-receipt), [architecture 04 §7](../architecture/04-concurrency-and-publication.md#7-time-and-clock-skew)

**Built:**
- The reading: `Application/ClockObservation`, taken on both hub paths by
  `Agent/ReplicationInitiator` (a sync) and `Agent/PeerShipStore` (a
  direct-ship run), and stamped by `Agent/ReplicationResponder` on the peer's
  side.
- Where it waits: the ledger, schema 8 (`Application/DestinationSyncStore`),
  written by `Agent/FanOut` and `Agent/DestinationShipSink`.
- What a capture records: `Agent/BackupRunner` chooses the reading and
  `Repository/SnapshotPublication` signs it into the manifest as key 14.
- Where it is read back: catalogue schema v9
  (`Repository.Catalogue/CatalogueSchema`, `Repository.Catalogue/Catalogue`),
  filled by the capture, `Repository/CatalogueProjector` and
  `Repository.Catalogue/Forensic/ForensicRebuilder`; contract 1.47 on
  `Agent/ServiceCommandHandler`'s snapshot rows; the `snapshots` token in
  `Cli/CliApplication` and the standalone recovery tool's listing in
  `Recovery/RecoveryHost`, both rendered by `Domain/Status/ObservedClockSkewText`;
  the console's snapshot row in `wwwroot/app.js`.
- The tests: `Application.Tests/ObservedClockSkewTests`,
  `Repository.Tests/ObservedClockSkewTests`,
  `Hosts.Tests/ObservedClockSkewServiceTests`,
  `Api.Tests/ContractAdditiveFieldsTests`,
  `Cli.Tests/SnapshotClockTokenTests`,
  `Web.Tests/ConsoleSnapshotClockScriptTests` and
  `Web.DomTests/ConsoleViewsDomTests`.

---

## Context

NFR-TIME-002 asks for two things. The first, a configured skew margin on the
one grace a clock decides, is [ADR-0009 Amendment 7](0009-garbage-collection-safety.md#amendment-7-2026-09--the-skew-margin-configured).
The second is that observed skew is recorded in snapshot manifests and can be
queried per snapshot. Architecture 04 §7 says why: so that "a device with a
badly wrong clock is diagnosable after the fact".

No correctness property depends on a clock (specification 00 §7). A wrong
clock still does harm, though:

- every capture time it stamps is wrong, and retention reads capture times;
- a write intent's expiry compares the collector's clock with the writer's
  stamp, which the margin exists to absorb;
- a person reading "last backup 3 hours ago" is reading this machine's clock.

After the fact, nothing could say whether a machine's clock had been wrong.
The format has had the field for this since phase 0 — key 14,
`observed_clock_skew_ms`, a signed integer inside the signed prefix, "absent
if no reference was available". Nothing wrote it, and nothing said which way
its sign ran.

**What a reference could be.** This installation talks to few machines, and
only one routine exchange carries another machine's clock:

- **A replication receipt** (peer-protocol 03 §3.5,
  [ADR-0064](0064-replication-receipts.md)). The peer signs `issued_at`, its
  own clock in Unix milliseconds. It issues the receipt after the last commit
  and before the acknowledgement, on every push.
- **A deletion receipt** ([ADR-0063](0063-deletion-receipts.md)) carries
  `issued_at` too. Its exchange brackets the peer's deletions as well, which
  can take far longer than a push's last step.
- **The session handshake** carries no time.
- **A session's TLS certificate** is made for one connection, and this
  implementation dates it a day either side of its own clock. Nothing binds
  those dates. The session proof covers only the certificate's key (peer-protocol
  02 §1), and the window is a convention another implementation need not
  share. A reading taken from it would rest on a number nobody signed.
- **An object store's `Last-Modified`** is not read, and there is no cloud
  provider yet.

## Decisions

### 1. The reference is a peer's receipt, bracketed by this clock

On both hub paths — a sync pass and a direct-ship run — the hub reads its own
clock twice:

- just before it writes `ReplicationComplete`;
- just after the `ReplicationAck` arrives.

The peer stamps `issued_at` between the two. The reading is:

- **skew** = `issued_at` − the midpoint of the two readings;
- **round trip** = the second reading − the first. Wherever the peer stamped
  inside the bracket, the skew is out by at most half of it.

Two rules decide what counts as a reading:

- **Only a verified receipt.** A receipt that fails the checks of 03 §3.5 is
  not filed and not acted on. A stamp nobody's signature stands behind is
  anyone's number.
- **Not a bracket that ends before it begins.** If this clock ran backwards
  across the exchange, what the bracket measures is the step, so there is no
  reading.

### 2. The sign: the peer's clock minus this one's

A positive value means this clock was behind its peer. Added to this machine's
timestamps, the value gives the time the peer would have said.

The words never say "slow" or "fast". Two clocks disagreeing do not say which
of them is wrong. The CLI and the console name the direction relative to the
peer: behind it, or ahead of it.

### 3. It waits on the ledger, one reading per pair

A snapshot's manifest is signed before its own run meets the peer:

- a staging set syncs after the capture is published;
- a direct-ship run completes its shipment after publication.

Making publication wait for a peer is exactly what
[ADR-0011](0011-commit-versus-replication-semantics.md) forbids. So the reading
waits for the set's next capture. It waits on the destination's ledger row,
schema 8:

- `clock_skew_ms`;
- `clock_observed_at` — this machine's clock when the exchange ended;
- `clock_round_trip_ms`.

Each verified receipt replaces the pair's previous reading and nothing else on
the row: a reading is not a sync, and says nothing about one. The ledger keeps
only the latest reading, because the manifests are the history — one reading
per snapshot, signed.

A schema-7 ledger loads with no reading on any row.

### 4. What a capture records

When a capture begins, it takes its set's destinations' readings and records
the freshest one that is:

- **no older than a day**, by this clock;
- **not dated in this clock's future.**

Between two readings of the same moment, it records the one with the tighter
bracket.

**Why a day.** A clock can be set right, or wrong, between a reading and a
capture. A stale reading recorded as though it were current points the
diagnosis at the wrong moment. A set on a daily schedule that syncs every run
is inside a day of its previous exchange whenever it runs on time. A set that
has gone quiet longer records nothing, which is true: nothing recent was
compared.

**Why not the future.** A reading dated after now means this clock was set
back since the reading. The reading then describes a clock this one no longer
is.

**Absent is not zero.** With no reading that qualifies, key 14 is absent.
That covers:

- a set whose destinations are all local paths, or whose peers send no
  receipts;
- a set that has not yet met its peer;
- a CLI direct-mode backup;
- an archive written by `archive` or by an import.

Every surface reads an absent key as "nothing to compare with", never as
"in step". Those are different findings about a clock.

### 5. Queryable per snapshot

- **Catalogue schema v9** adds a nullable `snapshots.observed_clock_skew_ms`.
  Every route that fills a catalogue writes it from the manifest: the capture,
  the projection a rebuild runs, and the forensic rebuild. So the reading
  survives the loss of every catalogue, and of every index object. The first
  open after the upgrade rebuilds each set's catalogue ([ADR-0010 Amendment 4](0010-local-store-separation.md#amendment-4-2026-09--a-catalogue-discarded-is-rebuilt-before-it-is-read)).
- **Contract 1.47** adds `observed_clock_skew_ms` to each snapshot of
  `list_snapshots` and `open_restore_source`. Null means no reading, or a
  service older than 1.47.

**The capture's own row now writes the capture time a rebuild does.** The
live row wrote `captured_at` as the capture's start, while both rebuilds read
the manifest's completion stamp. So a snapshot's time moved the first time its
catalogue was rebuilt. This change found that while giving the live row the
manifest's skew. Both now write the manifest's completion stamp and the
manifest's consistency method, so the capture's row and a rebuilt one agree.

### 6. What is said

**The `snapshots` verb** appends a token to each line that has a reading:

| Reading | Token |
|---------|-------|
| Under two seconds either way | `clock:in-step` |
| Under five minutes | `clock:4m12s-behind`, `clock:1m30s-ahead` |
| Five minutes or more | `clock:3h-BEHIND`, `clock:1d2h-AHEAD` |

The span is given in its two largest units. From five minutes the direction is
capitalised, as the verb's other alarm tokens are. A line with no reading has
no token.

**The console** says the same in the snapshot row's capture cell: "clock in
step with its peer", or "clock 4 min 12 s behind its peer". From five minutes
it is drawn as a warning. A row with no reading draws nothing.

**Why two seconds.** Under that, the reading's own error — half a round trip,
plus the peer's time spent issuing and filing the receipt — is the same size
as the thing measured. Nothing a person would do differs.

**Why five minutes.** That is where timestamps from this machine stop being
fit to compare with another's for anything a person reads in minutes. It was
also the old fixed intent margin that Amendment 7 replaced.

### 7. A diagnostic, never an input

Nothing reads the value to decide anything. It does not correct a clock,
refuse a peer, shift a retention window or move a grace. That is specification
00 §7's rule for every timestamp in the format, and the erratum to 06 §6 key
14 says so for this one.

## Consequences

**Positive**

- A machine whose clock was badly wrong can be found after the fact, snapshot
  by snapshot. The evidence comes from the repository alone: the reading is
  signed into the manifest, and a forensic rebuild recovers it.
- Each reading carries its own uncertainty on the ledger, so a reading from a
  slow link is not mistaken for a precise one.
- A set with a peer destination needs no configuration, and the peer needs no
  new protocol. Every peer that sends receipts already sends the stamp.

**Negative**

- The reading needs a peer. A set whose destinations are all local paths
  records none, and so will a cloud destination until its provider reads a
  time reference.
- The reading describes the clock before the capture, by up to a day.
- Ledger schema 8 and catalogue schema v9. The first open after the upgrade
  rebuilds each set's catalogue.

**Neutral**

- Contract 1.47 is additive. A pre-1.47 service sends no reading, which reads
  as nothing to report.

**Limits, stated**

- **Only disagreement is measured.** The peer's clock may be the wrong one,
  and two clocks wrong together read as in step.
- **A clock stepped after the reading is not seen.** A capture records the
  reading taken before the step as though nothing had moved, unless the clock
  was set back past the reading, which then stops being recorded.
- **The standalone recovery tool does not print the reading.** The manifest
  carries it and a rebuilt catalogue holds it, so the listing can show it
  later without any format change.

  > **Printed since 2026-09.** The recovery tool's `snapshots` listing ends a
  > line with the same token the CLI prints, read from the manifest alone. One
  > routine renders it for both, in Domain, because the recovery tool's
  > dependency closure reaches Domain and never the CLI (architecture 11 §2).

## What this does not do

- **It does not flag an implausible capture time.** Architecture 04 §7 also
  says a snapshot whose recorded time is implausible beside its neighbours is
  flagged rather than silently expired. That is retention's, and still unbuilt.
- **It does not change how filed receipts age.** Receipt retention ages a
  peer's receipt by its `issued_at` against this clock, so a peer whose clock
  is wrong has its receipts aged early or late here. The floor of newest
  receipts is kept whatever their age. That is a record's lifetime, not a GC
  outcome, and it is unchanged.

## Alternatives considered

**A time field in the session handshake.** Every session would read the clock,
including one that pushes nothing. But it needs a protocol change, and the
receipt already carries a signed stamp on every push. Rejected while the
receipt serves.

**Sign the manifest after the peer answers, so it records its own run's
reading.** This couples publication to a peer's availability, which ADR-0011
forbids. A capture must commit whether or not its peer is reachable.

**Ask a time server.** That is an outbound dependency the product otherwise
never has. A default install dials nothing but the destinations a person
configured.

**Correct timestamps by the skew.** A disagreement does not say which clock is
wrong. Correcting by it would make a timestamp depend on a peer's clock, which
specification 00 §7 rules out.

**Record 0 when there is no reading.** That conflates "in step" with "not
known", which are the two findings a person diagnosing a clock most needs
kept apart.

**Keep every reading on the ledger.** A history of readings would duplicate
what the manifests already are: one signed reading per snapshot.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-09 | Accepted | Built tests first (the reading, the choice a capture makes, the ledger's schema 8, the three routes that fill a catalogue, the contract, the CLI token, the console row, and both hub paths end to end against a peer whose receipt clock runs three hours ahead). Removing each piece of wiring was confirmed to turn its tests red: a sync that does not keep its reading, a direct-ship run that does not keep its reading, a capture that does not record the reading, and a capture's own row that writes the start time as the capture time |
| 2026-09 | Accepted (amended) | The standalone recovery tool prints the reading. Its `snapshots` listing ends a line with the CLI's token, which one routine in Domain now renders for both (`Hosts.Tests/ObservedClockSkewServiceTests`). A listing that dropped the reading was confirmed to turn the test red |
