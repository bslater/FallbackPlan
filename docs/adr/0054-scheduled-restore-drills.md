# ADR-0054 — Recovery is drilled on a cadence, and the drill says what it could not prove

**Status:** Accepted
**Date:** 2026-09
**Requirements:** FR-KIT-006, FR-KIT-007, FR-VER-004, NFR-OPS-005
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

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-09 | Accepted | In response to the 2026-09 architecture review's R11. Built: `Agent/RecoveryDrillJob` drills through the guided-restore verbs, `Agent/Scheduler` decides when, `Application/DestinationSyncStore` carries the answer, and contract 1.25 puts it on the status matrix. The scheduled drill deliberately proves less than [the operator drill](../../eng/recovery-drill.sh), and §5 says what |
| 2026-09 | Amended | [Amendment 1](#amendment-1--an-interrupted-drill-is-not-a-failed-drill-2026-09): an interrupted drill states nothing. `Agent/RecoveryDrillJob` translates a cancelled command answer back into a cancellation, `Agent/AgentPass` waits for the drill phase, and `Agent/JobScheduler` refuses work once stopped instead of posting to disposed semaphores |
| 2026-09 | Amended | [Amendment 2](#amendment-2--a-drill-on-a-write-only-set-proves-the-road-as-far-as-the-sealed-content-2026-09): on a write-only set the scheduled drill proves the road back as far as the sealed content and states that limit as a pass, never as a failure. `Agent/RecoveryDrillJob` recognises a sealed-only refusal and confirms the plan finds every segment; `Application/DestinationSyncStore` and contract 1.27 carry `drill_limit`; `Hosts.Tests/RecoveryDrillTests` runs on a set-up installation |
