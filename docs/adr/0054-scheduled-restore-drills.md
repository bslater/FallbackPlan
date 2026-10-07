# ADR-0054 — Recovery is drilled on a cadence, and the drill says what it could not prove

**Status:** Accepted
**Date:** 2026-09
**Requirements:** FR-DRL-001, FR-DRL-002, FR-DRL-003 (FR-KIT-006 and FR-KIT-007 until [ADR-0060](0060-the-passphrase-is-the-recovery-credential.md) re-homed them), FR-VER-004, NFR-OPS-005
**Related:** [ADR-0013](0013-recovery-kit.md), [ADR-0034](0034-hub-and-spoke-destinations.md), [ADR-0041](0041-guided-restore-and-peer-retrieval.md), [ADR-0042](0042-write-only-repositories.md), [ADR-0046](0046-direct-to-destination-publication.md), [recovery drill](../../eng/recovery-drill.sh)

---

## Context

The product's first principle is that recovery is the product, and the
repository has taken that seriously in every place but one: nothing makes it
happen by itself. `eng/recovery-drill.sh` builds an installation, destroys the
machine and recovers it from the destination and the kit, and
`Hosts.Tests/RecoveryHostTests` runs the in-process half in CI. Both are real
and both are excellent. Neither is scheduled.

So "we could recover" has been, for the whole life of a running installation, a
claim about the last time somebody chose to check — and the person most likely
never to check is the person the product is for. The 2026-09 architecture
review's R11 put it plainly: a drill that is available is not a drill that
happened.

Verification does not close this. A possession challenge proves a destination
still holds authentic bytes, and since [ADR-0046](0046-direct-to-destination-publication.md)'s
direct-ship default it proves them by opening a record's AEAD tag at the
destination itself. That is a strong proof of *the bytes*. It says nothing
about the road from those bytes back to a file: whether the index plane
rebuilds, whether the catalogue projects, whether a manifest decodes, whether
segments assemble in the order the manifest says, whether the reassembly
hashes to what the capture recorded. Those are different mechanisms with
different failure modes, and a replica can pass every challenge ever put to it
while failing all of them.

## Decision

### 1 The service drills its own destinations, on a cadence

Every scheduler pass, after the transfers, each `(set, local-path
destination)` pair that is due a drill gets one: a bounded sample of files is
restored out of that destination's replica, and what happened is recorded on
the pair's row.

Thirty days by default, against the deep sweep's seven and the possession
challenge's six hours, because the three cost wildly different amounts and
watch for things that change at wildly different rates. A challenge samples a
handful of ranges. A sweep re-reads a replica in bounded segments. A drill
rebuilds a catalogue from the replica's index plane and writes real bytes to
disk — it is the most expensive scheduled thing in the service, and what it
watches for (a read path that has stopped working) changes far more slowly
than rot does.

> **Amended (2026-09):** a drill that did not complete is not left for the
> whole interval: it is due again on a back-off from an hour, never later
> than the interval — see [Amendment 4](#amendment-4--a-drill-that-did-not-complete-says-so-2026-09).

> **Amended (2026-10):** and whenever a person asks, outside the cadence —
> see [Amendment 6](#amendment-6--a-person-can-drill-now-2026-10).

### 2 The replica is opened the way a stranger would open it

The drill runs through the ordinary guided-restore verbs — `open_restore_source`
against the destination, `list_directory`, `run_restore` — rather than a
private implementation beside them. Those verbs already open a local-path
replica as [ADR-0041](0041-guided-restore-and-peer-retrieval.md) specified: a
fresh store over the replica root, a fresh repository open, and a throwaway
catalogue rebuilt from that replica's own index plane. No staging archive, no
live catalogue, no metadata store.

Going through the real verbs is the decision, not an implementation
convenience. **What the drill exercises has to be what a person would use**, or
the two drift and the drill starts certifying a path nobody travels. It also
means the drill inherits every refusal and every containment rule those verbs
already carry, rather than growing its own weaker copies.

The consequence worth stating: the drill is **not** a queued job. The verbs it
calls queue themselves on the reader lane, which has one worker, so a drill
holding that worker while waiting on them would wait for ever.

### 3 Three answers, never two

A pair is in one of three states and every surface keeps them apart:

- **Never drilled** — no stamp. Not a failure and not a pass.
- **Drilled and passed** — a stamp, no reason.
- **Drilled and failed** — a stamp **and** the drill's own words.

The first and third both mean *this destination has not been shown to work*;
only the third means something is wrong. A surface that folds "never" into
either of the others turns an unexercised destination into a reassuring one,
which is the specific failure this record exists to prevent. It is the same
rule `measured_at` carries for the completion figures (contract 1.24), for the
same reason.

> **Amended (2026-09): a pass may carry a stated limit.** On a write-only
> set the service cannot read content, so its drill passes *as far as the
> sealed content* and says so beside the stamp — still the second answer,
> with a qualifier a surface must show and must not render as the third.
> See [Amendment 2](#amendment-2--a-drill-on-a-write-only-set-proves-the-road-as-far-as-the-sealed-content-2026-09).

### 4 A failed drill is not a failed sync

The outcome lands on its own fields and leaves the sync state alone. A
destination can hold every byte it was sent, prove possession of them under
challenge, and still fail to restore — and calling that a sync failure would
blame the copy that worked and back off the transfers that are fine.

It raises a durable notice instead, which is the loudest thing the product has
to say: a recovery that would not work has to be told to somebody *before* they
need it rather than during.

> **Amended (2026-09):** "a failed attempt is a try" means an attempt that
> reached the replica. A drill cut short because the service is stopping
> reached nothing and states nothing — see
> [Amendment 1](#amendment-1--an-interrupted-drill-is-not-a-failed-drill-2026-09).

The drill stamp moves on failure as well as success, which is the opposite of
how the verification stamps behave. A verification stamp answers "when were
bytes last proven", so a failure must leave the last true answer standing. A
drill stamp answers "when did we last try to recover", and a failed attempt is
a try — leaving yesterday's success on the row would report a destination as
recently drilled when the most recent drill said it cannot be restored.

### 5 What the automated drill does not prove

Stated here so a green row is never read as more than it is. The in-process
drill does **not** exercise:

- **The kit file's own parse.** The service holds the passphrase and derives
  directly; nothing reads a `.bin` or transcribes a printable page.

  > **Amended 2026-09.** Moot: there is no kit to parse
  > ([ADR-0060](0060-the-passphrase-is-the-recovery-credential.md)). What the operator drill still proves beyond this one
  > is the next two bullets — the tool's closure and the machine's absence.
- **The standalone recovery tool's dependency closure.** The drill runs inside
  the service, which has every assembly. Whether `FallbackPlan.Recovery` still
  restores on a machine with nothing on it is a build-graph property held by
  `ArchitectureTests/DependencyRuleTests` and by the script.
- **The machine actually being gone.** A drill cannot delete the state
  directory it is running out of.

`eng/recovery-drill.sh` remains the only proof of all three and is not
superseded by this record. The two answer different questions: the script asks
"does recovery work at all", once, thoroughly, when a person runs it; the
scheduled drill asks "does *this* destination still restore", repeatedly,
without anybody remembering to ask.

> **Amended (2026-09): on a write-only set, add the content plane.** The
> service holds no content key ([ADR-0042 §7](0042-write-only-repositories.md)),
> so the scheduled drill does not read a byte of content there; it proves
> the road back as far as the sealed content and states that limit. The
> content drill is the manual one, with the passphrase — see
> [Amendment 2](#amendment-2--a-drill-on-a-write-only-set-proves-the-road-as-far-as-the-sealed-content-2026-09).

### 6 Peers are not drilled

Only local-path destinations. A peer's replica is behind the wire, and
restoring from one costs a retrieval session and the peer's bandwidth on a
cadence the peer never agreed to. The possession challenge already proves a
peer holds the bytes; what a drill adds needs the whole read path across the
protocol, which is peer-protocol work rather than a cadence. Stated rather
than silently skipped, so the gap is visible on the
[proof obligations](../proof-obligations.md) rather than implied by an absence.

> **Amended 2026-09.** A peer is drilled — on a cadence the source's
> operator writes down for it, never by default, and under a byte cap. The
> read path across the protocol turned out to exist already. See
> [Amendment 3](#amendment-3--a-peer-is-drilled-on-a-stated-cadence-and-under-a-byte-cap-2026-09).

> **Amended (2026-10):** a person may also drill a peer with no stated
> cadence, once, by asking, under the same caps — see
> [Amendment 6](#amendment-6--a-person-can-drill-now-2026-10).

### 7 Sampling

A bounded number of files (three), chosen by descending the newest snapshot at
random rather than by enumerating it. Two reasons, both load-bearing: a drill
must cost the same on a snapshot of eleven files as on one of eleven million,
and a fixed choice is one a damaged replica could survive for ever — the same
reasoning that makes the possession challenge draw its record at random.

The **newest** snapshot rather than a random one, because it is the one a
person would reach for, and because an older snapshot's segments may
legitimately have been trimmed from a destination under its own retention
(FR-GC-010) — a drill that sampled those would report damage about a policy
working correctly.

## Consequences

**Positive**

- "We could recover" stops being a claim about somebody's memory and becomes a
  dated fact per destination, with an age that visibly goes stale.
- A read-path regression is found by the product rather than by the person it
  happens to, at the moment they can least afford it.
- The drill and the guided restore cannot diverge, because they are the same
  code path.

**Negative**

- The heaviest scheduled job in the service now exists. It is rare and it is
  bounded, but a drill on a large replica rebuilds a catalogue, and that is a
  real cost on a real disk.
- A green drill row can be over-read as "recovery is proved", which §5 exists
  to prevent and which the surfaces have to keep saying.
- One more durable field group per destination, and one more thing a status
  surface must render in three states rather than two.

## Alternatives considered

**Let verification stand in for a drill.** Cheapest, and it proves the wrong
thing: a challenge and a restore fail independently, and only the second is
what the first principle claims.

**Schedule `eng/recovery-drill.sh` itself.** It proves strictly more — the kit
parse, the tool's closure, the machine's absence — and it cannot run inside a
service: it builds installations, writes outside the repository and destroys a
machine. Kept as the operator's drill, unchanged.

**Restore the whole snapshot.** Proves the most per drill and scales with the
archive rather than with the sample, which would make the drill unaffordable
on exactly the installations that most need one.

**Record only a pass/fail boolean.** Half the size and loses the distinction
the whole record turns on: never drilled is not a pass and is not a failure.

## Amendment 1 — an interrupted drill is not a failed drill (2026-09)

§3's three answers need a fourth thing said about them, and it is a thing about
silence rather than a fourth state: a drill that is interrupted records
**nothing at all** — no stamp, no reason, no notice — and the row keeps
whatever the last completed drill said.

> **Amended (2026-09):** the silence is for the service stopping and for
> nothing else. A cancellation or a disposed object met while the service
> runs is a drill that did not complete, and it says so — see
> [Amendment 4](#amendment-4--a-drill-that-did-not-complete-says-so-2026-09).

The case is ordinary rather than exotic. A drill's commands run on the
service's job queue, and stopping the service cancels them; the handler answers
a cancelled command as a refusal like any other, so a drill that read that
refusal as an answer about the replica wrote "a restore drill could not bring
back a file" — the loudest notice this product can raise — every time the
service stopped while a drill was in flight. §4 is right that a failed attempt
is a try; a cancelled one is not an attempt at all, and the distinction is the
same one §3 draws between never-drilled and failed.

Two consequences follow, and both are now built:

- A cancelled command answer (`ServiceErrorReason.Cancelled`) is translated
  back into the cancellation it was, rather than being folded into the drill's
  failure text with everything else a command can refuse for.
- A one-shot pass waits for its drill phase before returning
  (`Agent/AgentPass`), because returning earlier tore the runtime down
  underneath a drill still issuing commands — which is how the false notice was
  found, in a state directory that would not delete because something the
  caller had stopped waiting for was still writing to it.

## Amendment 2 — a drill on a write-only set proves the road as far as the sealed content (2026-09)

This record was written against a service that holds the keys, and every
installation setup produces does not: a write-only set's replica seals its
content to a key the service never holds ([ADR-0042 §7](0042-write-only-repositories.md)),
so the drill of §1, run as written, restored nothing there — every sampled
file read `ContentSealed`, and §4 turned that into a recovery failure and the
loudest notice the product has, on every drill, on every write-only set, for a
key the service was never meant to hold. Found by moving
`Hosts.Tests/RecoveryDrillTests` onto a set-up installation (slice 12A), where
the clean drills went red with that message.

**Decision.** Unattended, a drill on a write-only set proves what the service
can prove without the passphrase, and states what it cannot:

- It runs the same verbs (§2). Opening the replica proves the descriptor
  verifies and the index plane rebuilds; sampling proves the catalogue
  projects and the manifests decode; the restore reaches every segment
  record of each sampled file — located in a footer table authenticated
  under the metadata key — and stops at the sealed content. When every
  failure the engine reports is `ContentSealed` and nothing else, and a
  restore plan for the same file names no missing object, that is the road
  back proved as far as it can be from inside the service.
- The outcome is a **pass with a stated limit**: the stamp moves, the files
  proved that far are counted, no bytes are counted as restored, the failure
  stays null, and the limit rides beside it on the ledger and the status
  matrix (`drill_limit`, contract 1.27). No notice: the limit is on the row,
  and a notice every thirty days about a key the service is not meant to
  hold would be noise wearing an alarm's clothes.
- Damage is still damage. A replica whose data blobs are rotted fails the
  footer authentication before the content question arises, and a missing
  segment fails the plan: both record a failure and raise the notice exactly
  as before. The limit is reserved for the one case where the only thing
  between the drill and the bytes is the passphrase.
- The **content drill** on a write-only set is the manual one: the recovery
  tool with the passphrase, `eng/recovery-drill.sh` and
  `Hosts.Tests/RecoveryHostTests`. §5's list gains that item for the
  write-only shape, and the proof obligations say the scheduled drill proves
  the content plane only where the service holds the key.

§3's three answers stand. A pass with a limit is the second answer with a
qualifier, not a fourth state: a surface shows the limit, and must not fold it
into "could not restore" — which it is not — or into a plain pass, which
overstates by exactly what the limit says.

## Amendment 3 — a peer is drilled on a stated cadence and under a byte cap (2026-09)

§6 excluded peers on consent, not on mechanism, and the mechanism was
already there: the drill opens its source by destination name through the
same verb a person's restore uses, which dispatches to the peer over the
retrieval session ([peer-protocol 07](../../specifications/peer-protocol/07-retrieval.md)),
and on a write-only set it takes [Amendment 2](#amendment-2--a-drill-on-a-write-only-set-proves-the-road-as-far-as-the-sealed-content-2026-09)'s
sealed-limit path — a plan over the retrieval store — without ever needing
the passphrase. The one thing keeping a peer out was the kind filter in the
scheduler. Proof row 91 was the table's only Unproved row for it.

**Decision.** The consent question is answered on the source side:

- A local path keeps its default cadence. A **peer drills only on a cadence
  the source's operator writes down** for that destination
  (`drill_interval_days`); absent means never. The peer agreed to serve
  restores when it granted retrieval, and a drill is a small restore — but a
  cadence is a standing cost on somebody else's link, and this service does
  not put one there by default. Zero is still refused for both kinds; for a
  peer the absence is the decision, and the configuration's own words say
  so.
- The bytes one drill may pull from a peer are **capped**: a file over
  64 MiB is not chosen, and once the chosen files reach 128 MiB no more
  are — deliberately below three times the per-file cap, so the total is a
  number that can bite. A file the cap excludes is left where it is and the
  random descent tries again; the drill states how many files it left, as a
  limit beside the sealed-content limit where both apply, never as a
  failure. A snapshot whose every reached file is over the cap records a
  stamp, no files, no failure and the limit: the replica opened and listed
  over the wire, and what was not read is named. A local path is not
  capped. The deep-verify sweep is untouched and remains local-path only.
- Everything else is §1–§4 unchanged: the ledger fields, the three answers,
  the `drill-failed` notice on damage, and Amendment 1's silence on
  interruption. Rot at the peer fails the drill exactly as it does at a
  local path.

§6's heading stands as written — the default is still that a peer is not
drilled — with the amendment's blockquote beside it.

## Amendment 4 — a drill that did not complete says so (2026-09)

Amendment 1 gave the drill its silence for the case it named: the service
stopping while a drill was in flight. The build drew the line by exception
type instead. Any cancellation and any disposed object ended a drill in
silence, whatever caused it, and FR-DRL-002 wrote that down as "the service
stopping, or anything else that cancels its commands". The second half let a
fault inside a running service pass for a shutdown. The SQLite pool race
([ADR-0010 Amendment 3](0010-local-store-separation.md#amendment-3-2026-09--the-catalogues-connections-are-not-pooled))
could dispose a connection under a running drill, and it is the likeliest
reading of a drill in CI that left no trace. Silence also moved no stamp, so a pair past its interval stayed due, and a
fault that lasted re-ran the drill at every pass, a minute apart by default
and over a peer's link as readily as a local disk, with nothing said each
time.

**Decision.**

- **Silence is for a real stop.** A drill states nothing when the pass that
  ran it has been cancelled, or when the service has begun stopping: its
  runtime's disposal has started, or its queue has stopped taking work. Then
  nothing it met on the way out is evidence. That goes for a cancellation, a
  disposed object, and a failure it had already reached, because the store
  or the source it was reading may be going away underneath it.
- **Anything else is a drill that did not complete.** While the service
  runs, whatever ends a drill without an answer about the replica is
  recorded as a failed drill in its own words, naming the step when a
  command came back cancelled, and it raises the `drill-failed` notice. §4
  holds: it was a try, so the stamp moves.
- **It is retried on a back-off, not at the interval.** The ledger counts
  drills in a row that did not complete. The pair is due again an hour after
  the first, then after two, four and so on, never later than its interval,
  a peer's stated cadence included. The next drill that completes, passing
  or failing, ends the count. There is no drill-now verb, so without this a
  fault that passed in a minute would leave the notice standing for a
  month; with it, the fault is cleared within the hour, and one that lasts
  backs off instead of drilling every pass. A drill that completed and found
  damage still waits its full interval: drilling a broken replica every hour
  would repeat the notice, not add evidence.

  > **Amended (2026-10):** that holds while the replica is the one the drill
  > answered about. Once a sync has succeeded since the drill, with no damage
  > the deep sweep recorded still standing there, the failure is checked
  > again on this back-off — see
  > [Amendment 5](#amendment-5--a-set-with-no-snapshot-is-not-drilled-and-a-failed-drill-is-checked-again-once-its-replica-has-synced-2026-10).

  > **Amended (2026-10):** there is a drill-now verb since
  > [Amendment 6](#amendment-6--a-person-can-drill-now-2026-10). The back-off stays: it is what
  > clears a passing fault on an installation nobody is watching.
- **A listing answered as cancelled is not an empty folder.** Read as one,
  it ended every descent of the sample, and the drill blamed the snapshot
  for having nothing it could sample, both at a stop and while the service
  ran.

`Agent/RecoveryDrillJob` decides silence in one place, from the stopping
state `Agent/ServiceRuntime` now reports. `Application/DestinationSyncStore`
carries the count, in ledger schema 5, and `Agent/Scheduler` reads it.
`Hosts.Tests/RecoveryDrillTests` puts each fault between the drill and the
service it talks to, so a fault is all that differs from a clean drill.

## Amendment 5 — a set with no snapshot is not drilled, and a failed drill is checked again once its replica has synced (2026-10)

A set's archive exists from the moment its first backup starts, and stays
when that backup ends before it commits. The scheduler pass took the
archive's existence for something to carry. A pass during that window copied
an archive that held no snapshot and recorded the copy as a success, with a
baseline and a possession proof, so the destination read in sync. The drill
that followed found no replica of the set and raised `drill-failed`. Its
notice stood for the pair's whole interval, thirty days by default, because
there is no drill-now verb, and the sync that soon carried the first
snapshot changed nothing about that.

> **Amended (2026-10):** a person can now clear such a notice at once by
> drilling the pair ([Amendment 6](#amendment-6--a-person-can-drill-now-2026-10)). The rule below is
> still what clears it with nobody asking.

**Decision.**

- **A set with no snapshot gives a destination nothing to carry.** The pass
  copies, sweeps and drills a set only when its archive's catalogue lists a
  snapshot of it. The archive answers rather than the job journal, because
  an adopted set's archive is older than its journal. An archive that will
  not open is answered yes, so the work that opens it next meets the fault
  and reports it as before. A pass cancelled, or a service stopping, while it
  asks is answered no, and winds down without a word as Amendments 1 and 4
  require. What the status says of such a pair is
  [ADR-0050 Amendment 2](0050-completed-run-record-and-drill-down.md#amendment-2-2026-10--a-set-with-no-snapshot-yet-gives-its-destinations-nothing-to-hold)'s.
- **A failed drill is checked again once its replica has synced.** A drill
  that completed and failed answered about the replica it found. Once a sync
  has succeeded since it, that replica may have been put right, so the pair
  is due again on Amendment 4's back-off: an hour after the drill, then two,
  four and so on while the failures go on, never later than the interval.
  The ledger counts failed drills in a row, in schema 10. A drill that
  passes ends the count. One that did not complete answered nothing, so it
  neither adds to the count nor ends it.
- **Not while the damage it may have found still stands.** A sync in the
  drill's own pass ran before the drill, so the drill saw what it copied. A
  sync that went on around damage the deep sweep recorded and could not
  replace has put nothing right. Either way the answer stands for the
  interval, as §4 had it: drilling a known-broken replica every hour would
  repeat the notice, not add evidence.

A false notice an older service raised this way clears within the hour of
the next sync. One a real fault raised clears soon after the fault is put
right, rather than a month later.

`Agent/ServiceRuntime` answers whether a set holds a snapshot, and
`Agent/Scheduler` asks it before each phase. `Agent/RecoveryDrillJob` holds
the wait in one function, and `Application/DestinationSyncStore` carries the
count. `Hosts.Tests/RecoveryDrillTests` creates a set's archive the way a
first backup does and runs a pass over it, for a direct-ship set and a
staging one. It also arranges the false notice and the sync that clears it,
and pins the wait as a function of the ledger row.

## Amendment 6 — a person can drill now (2026-10)

Amendments 4 and 5 each worked around the same gap: "there is no drill-now
verb". A fault that passed in a minute, or a false notice an older service
raised, stood until the schedule came round again, and the back-offs those
amendments added shortened the wait without letting anyone end it. The owner
asked for a drill a person can run.

**Decision.**

- **A person runs a pair's drill now** (FR-DRL-003): `run_drill` on the
  command contract (1.58), **Run restore drill** on each destination row in
  the console, `fallbackplan drill` and `fallbackplan-agent drill`, each
  naming a set and a destination or leaving either out for every one. It is
  the drill the schedule runs: the same replica opened the same way, the
  same caps for a peer, recorded on the pair's row, and the `drill-failed`
  notice raised or cleared exactly as §4 says. It moves the stamp, so the
  schedule's next drill of the pair is an interval after it.
- **What a person may drill.** Anything the schedule would, and a peer with
  no stated cadence. §6 and Amendment 3 kept peers off a default cadence
  because a cadence is a standing cost on somebody else's link; one drill a
  person chose is not. Not a pair with nothing there to restore: a set with
  no snapshot yet, as Amendment 5 has it, a destination nothing has been
  copied to, one no longer declared, or a kind nothing serves. Those are
  said and counted apart, and nothing is recorded against them, because
  drilling would record a failure about an absence that is correct.
- **One drill of a pair at a time.** A drill asked for while the pair is
  being drilled, by the schedule or by a person, joins the drill under way
  and is told its answer. A second drill would rebuild a second catalogue
  from the same replica, and both would record, so the counts of drills in
  a row would move twice for one state of it. The pair is let go before the
  drill is answered, so whoever is told it has finished may ask for the
  next at once.
- **The drill is the service's.** It runs on the service's lifetime, not
  the asker's, as an on-demand sync does. A person who stops waiting is
  answered cancelled, and the drill finishes and is recorded. Only the
  service stopping cuts it short, and then it states nothing, as Amendments
  1 and 4 require, and the ask is answered cancelled rather than as a pass.
- **The answer counts what an exit code needs.** A line per pair, and the
  number of drills that could not restore and of pairs not drilled. A pair
  not drilled proved nothing, so the CLI exits non-zero unless every pair
  asked about was drilled and restored.
- **The back-offs stay.** Amendments 4 and 5 shortened the wait because
  nobody might ask, and on an installation nobody watches nobody does.

`Agent/DrillFlights` holds the drills under way, and `Agent/RecoveryDrillJob`
runs every drill through it, the schedule's included, and runs a person's on
the service's lifetime. `Agent/ServiceCommandHandler` answers the command,
`Agent/AgentHost` and `Cli/CliApplication` give it a verb, and the console
gives each destination row a button. `Hosts.Tests/DrillNowTests` drills a
pair the pass drilled moments ago, clears a failed drill's notice, holds the
schedule's drill at its first step while an ask joins it, and lets the
person who asked give up while the drill finishes.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-09 | Amended | [Amendment 3](#amendment-3--a-peer-is-drilled-on-a-stated-cadence-and-under-a-byte-cap-2026-09): a peer is drilled on a cadence the source's operator states, never by default, and under a byte cap. `Agent/Scheduler` admits a peer only with an explicit interval; `Agent/RecoveryDrillJob` samples under a budget and states what it left; `Application/DestinationConfiguration` says what absence means per kind. `Hosts.Tests/PeerRecoveryDrillTests` drills over the retrieval session, fails on rot at the peer, and holds both caps |
| 2026-09 | Accepted | In response to the 2026-09 architecture review's R11. Built: `Agent/RecoveryDrillJob` drills through the guided-restore verbs, `Agent/Scheduler` decides when, `Application/DestinationSyncStore` carries the answer, and contract 1.25 puts it on the status matrix. The scheduled drill deliberately proves less than [the operator drill](../../eng/recovery-drill.sh), and §5 says what |
| 2026-09 | Amended | [Amendment 1](#amendment-1--an-interrupted-drill-is-not-a-failed-drill-2026-09): an interrupted drill states nothing. `Agent/RecoveryDrillJob` translates a cancelled command answer back into a cancellation, `Agent/AgentPass` waits for the drill phase, and `Agent/JobScheduler` refuses work once stopped instead of posting to disposed semaphores |
| 2026-09 | Amended | [Amendment 2](#amendment-2--a-drill-on-a-write-only-set-proves-the-road-as-far-as-the-sealed-content-2026-09): on a write-only set the scheduled drill proves the road back as far as the sealed content and states that limit as a pass, never as a failure. `Agent/RecoveryDrillJob` recognises a sealed-only refusal and confirms the plan finds every segment; `Application/DestinationSyncStore` and contract 1.27 carry `drill_limit`; `Hosts.Tests/RecoveryDrillTests` runs on a set-up installation |
| 2026-09 | Amended (kit withdrawn) | The requirements this record carries are FR-DRL-001/002, the drills re-homed from FR-KIT-006/007 by [ADR-0060](0060-the-passphrase-is-the-recovery-credential.md); §5's first "does not exercise" bullet is moot because there is no kit file, and `eng/recovery-drill.sh` is rewritten passphrase-only |
| 2026-09 | Amended | [Amendment 4](#amendment-4--a-drill-that-did-not-complete-says-so-2026-09): a drill states nothing only when the service is stopping or its pass is cancelled; any other ending is a drill that did not complete, recorded as a failure and retried on a back-off from an hour, never later than the interval. `Agent/RecoveryDrillJob` decides silence from the stopping state of `Agent/ServiceRuntime`; `Application/DestinationSyncStore` counts the drills that did not complete; `Agent/Scheduler` backs off; `Hosts.Tests/RecoveryDrillTests` puts each fault between the drill and the service |
| 2026-10 | Amended | [Amendment 5](#amendment-5--a-set-with-no-snapshot-is-not-drilled-and-a-failed-drill-is-checked-again-once-its-replica-has-synced-2026-10): the pass copies, sweeps and drills only a set whose archive holds a snapshot, and a failed drill is checked again on the back-off once a sync has succeeded since it with no damage standing there. `Agent/ServiceRuntime` and `Agent/Scheduler` gate the phases; `Agent/RecoveryDrillJob` holds the wait; `Application/DestinationSyncStore` counts failed drills in schema 10; `Hosts.Tests/RecoveryDrillTests` runs a pass over an archive with no snapshot and clears a false notice |
| 2026-10 | Amended | [Amendment 6](#amendment-6--a-person-can-drill-now-2026-10): a person runs a pair's drill now, outside its cadence, from the console, the CLI and the service's own command line, contract 1.58's `run_drill`. A peer with no stated cadence may be drilled by asking; a pair with nothing there to restore is said and not drilled; an ask joins the pair's drill under way; the drill runs on the service's lifetime. `Agent/DrillFlights` holds the drills under way; `Agent/RecoveryDrillJob` runs every drill through it; `Agent/ServiceCommandHandler` answers the command; `Hosts.Tests/DrillNowTests` pins it |
