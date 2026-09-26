# ADR-0035 — Destination fitness: admission, capacity, shortfall, and confirmation on a schedule

**Status:** Accepted
**Date:** 2026-08
**Requirements:** FR-DEST-001, FR-DEST-003, FR-DEST-004, FR-DEST-009, FR-DEST-010, FR-VER-002, FR-VER-003, FR-VER-004, FR-GC-009
**Related:** [ADR-0011](0011-commit-versus-replication-semantics.md), [ADR-0018](0018-replica-failure-domains.md), [ADR-0027](0027-services-scheduling-status-telemetry.md), [ADR-0029](0029-pipeline-and-service-concurrency.md), [ADR-0034](0034-hub-and-spoke-destinations.md), [architecture 09](../architecture/09-replication-and-peers.md), [peer-protocol 03](../../specifications/peer-protocol/03-replication.md), [peer-protocol 04](../../specifications/peer-protocol/04-verification.md), [peer-protocol 05](../../specifications/peer-protocol/05-quotas.md)

---

## Context

ADR-0034 settled *what* a destination holds and [ADR-0033](0033-hosting-under-an-os-service-manager.md)'s
service drives the fan-out that puts it there. Verification made the copy provable
([peer-protocol 04](../../specifications/peer-protocol/04-verification.md)), and the
staging trim was made to take proof rather than a claim. What none of that settled
is the question a household actually asks before it stops keeping a second copy by
hand: **is this destination one a backup can be built on?**

A survey of what the hub actually checked found the sixteen-range challenge was
very nearly the only assurance there was, and everything else the hub knew about
a destination it learned by trying to use it. Six gaps, each verified against the
code before this record was written:

1. **A destination that silently lost data was silently re-seeded.** The peer's
   declared inventory was read, used for two filters, and discarded. A destination
   emptied since the last success declares fewer keys, the source re-pushes them,
   the sync reports success, and nobody learns the destination sheds data. The
   local path dropped the same signal. Separately, the count the source declared
   sending and the count the destination acknowledged committing were never
   compared.
2. **Age was invisible.** The status derivation takes no clock by design, and the
   last-success timestamp — populated by both producers — was never read in it.
   Day 1 and day 400 read identically. Architecture 09 §4 already named bounds as
   an *example* policy nothing implemented.
3. **An uncomputable convergence filter was silently discarded.** Three different
   situations — no policy configured, the spoke lacking the retention feature, and
   a staging graph that would not walk — were spelled identically as a null filter,
   and all three took the whole-copy branch with nothing said. Only the third is a
   fault, and it is the one that leaves a spoke holding history it was told to drop.
4. **Nothing checked capacity.** `AvailableFreeSpace` had zero hits in the source
   tree. A peer's usage was destination-local and reached the source only inside
   refusal prose the protocol forbids parsing.
5. **Nothing probed a destination before the first full copy counted on it.**
   Configuration validation did no I/O and, beyond "not empty", no checking; a
   malformed peer endpoint surfaced at the first sync. Pairing ends at a grant,
   and the first sync *is* the full copy.
6. **Coverage never accumulated.** The sampler was a uniform reservoir, stateless
   across passes, so FR-VER-002's "weighted towards those longest unverified" was
   specified and unimplemented — sixteen of ten thousand objects, drawn afresh
   every pass, in expectation never reaching most of them, while the ledger
   recorded a verification stamp each time.

Underneath all six is one structural fact: **verification only ever ran inside a
sync, and a sync only ran when the archive moved on.** An idle set therefore froze
its own proof, and no signal existed that said so.

## Decision

### 1. Fitness is reported, not enforced at load

A defect in a destination's declaration — a relative path, a fingerprint that is
not one, an endpoint that is not `host:port` — is collected and reported wherever
the destination is reported. It is **not** a validation rule and never throws.

The reason is mechanical rather than stylistic. `ServiceRuntime.Configuration`
re-reads *and re-validates* `config.json` on every property access, several times
per scheduler pass, from the scheduler, the fan-out and the command handler. A
throw there would stop every set backing up and stop `status` answering — over one
mistyped character in one destination that some other set may not even use. Before
this decision a bad address degraded exactly one `(set, destination)` pair, at its
first sync. The blast radius stays exactly that; only the discovery moves earlier.

The checks are syntax only, with no name resolution, no disk and no dialling,
because they are read on that same hot path.

### 2. One verb, three depths

Asking about a destination is one verb with a depth flag, not a family of verbs:

| Depth | Reads | Answers |
|-------|-------|---------|
| `--probe` | nothing | Could this destination take a backup at all? |
| *(default)* | one bounded segment | Do the bytes in the next segment still match their seals? |
| `--full` | every stored object | Do all of them? |

The alternative considered and rejected was a separate `destination check` verb
beside `verify --destination`. Two verbs asking overlapping questions drift in
output shape and exit codes within an arc or two, and an operator then has to know
which to reach for. A depth flag cannot drift from itself.

The probe is the depth that exists because the other two cannot speak before the
first sync: there are no stored bytes to re-read and no ledger row to read a
history from. A local path must exist, be a directory, and accept a write —
existence is not permission, and a directory that refuses writes fails every sync
while looking perfectly present. A peer must resolve to a grant and a dialable
address and then complete the handshake, **including the verification feature it
would be refused for lacking at a real sync**, so a probe cannot call viable what
the fan-out would turn away.

**A probe never records a success.** Reaching a destination is not syncing to it,
and a ledger row saying otherwise would move the staging-trim gate and the
scheduler's due-ness on the strength of a handshake. Failures *are* recorded,
because they are the same failures a sync would have found and belong in status
without one having to be run.

### 3. Shortfall is detected from what the destination already declares

A destination that has quietly lost data is caught by **collapse in what it says
it already holds**, not by reasoning about sequences.

The obvious signal — objects copied when nothing new was published — is unsound.
It false-positives on a widened keep-set, on a resumed partial sync (the failure
path writes no success, so the sequence is unchanged), on a peer that has just
gained the retention feature, and on an archive that has published nothing. Every
one of those leaves the already-held count *large*; only a wiped destination
reports it at zero. So the already-held count is the signal, and it needs no new
persisted field and no arithmetic.

For a local path the same question is asked of the replica root's existence,
checked before the fan-out creates it — which also catches a different drive
mounted at the same point, where the path is present and the archive under it is
somebody else's.

Separately and **not** bundled with it: a destination acknowledging fewer objects
than it was sent refuses the session outright. A spoke commits each object whole
or refuses, so an under-count without a refusal means a responder bug or a
desynchronised stream, and in both cases objects believed present may not be. A
soft warning there would be recorded beside a successful sync and learned to be
ignored while the ledger went on claiming coverage nobody has.

### 4. Confirmation runs on a schedule, on the transfer lane

A third phase of the scheduler pass re-reads a local-path replica's stored objects
and checks them against their seals — bounded per pass, resuming from a persisted
cursor, at a cadence that defaults to weekly and is overridable per destination.
This is the remedy for the structural fact above: it is the only thing in the
product that refreshes a proof without a backup having happened.

It runs on the **transfer lane**, not the reader lane. A reader-lane sweep would
read the replica while the fan-out is putting and deleting in it, manufacturing
failures that would set the pair failed and raise a notice about damage that never
existed. Transfer is correct by serialisation, at the cost of a single worker for
the whole process — which is why the sweep is bounded and resumable rather than
run to completion.

The sweep verifies at footer-and-digest **and additionally compares each swept
key's length against the source's.** That is not belt-and-braces: the blob reader
does not bind the store key to the envelope's blob id, so a valid sealed blob
stored under another blob's key passes the digest check entirely. The length
comparison is free from two listings the sweep already has, and is the only thing
that catches it.

Sweep progress is recorded in its own fields and deliberately **not** written into
the challenge coverage fields. A forty-of-forty segment written there would print
100% coverage while the sweep was a thousandth of the way round. Only a
circuit-completion stamp can honestly support "every stored object was confirmed
as of this moment".

**Local-path replicas only.** A peer replica has no readable object store this side
of the wire — only the range-challenge protocol — so a peer digest sweep needs the
session-establishment half of the push extracted first. Deferred and stated, not
silently omitted; the extraction that the admission probe required is its first
half.

### 5. Sampling coverage accumulates

The sync-time challenge rotates: the newest snapshot always, then the keys after
the last *passed* challenge's cursor, which lives in the sync ledger. The cursor
advances only on a pass that proved something, so a failed or empty pass re-asks
rather than walking past objects nobody answered for.

Three properties of the rotation are load-bearing:

- **It sorts its candidates.** Listing order carries no meaning in this system's
  store contract, so a cursor built on it would advance past keys it never sampled
  — and since the cursor only moves forward, those keys would never be challenged
  again.
- **It wraps within the same pass.** A rotation that had finished a lap and
  returned nothing would write no verification stamp, and the trim gate would read
  that as an unproven destination and stop reclaiming space.
- **A peer keeps part of its budget random.** It answers its own challenge, so a
  wholly predictable rotation tells it exactly which objects it can afford to
  lose. A local-path replica has nobody on the other side — the hub reads the
  bytes off its own disk — so it gives the whole budget to the rotation.

### 6. Capacity is a warning; the wire says it once, where it is already known

Where a quota bounds a peer destination, it reports its remaining headroom on the
**replication inventory frame**. Not in the terms, which are persisted in the grant
and compared for narrowing — a per-session number there would raise "your friend
reduced your space" on every sync. Not in the hello, which is too early: the
destination does not yet know which repository is coming, and computing usage means
walking every object it holds, a cost the periodic verification sessions would pay
for a number nobody reads. By the inventory the scope is known and
`quota − usage` is already sitting in a local.

An absent value means **not told** — no quota, or an older build — and must never
be read as no room. A destination with a quota and nothing left says zero, which is
a different statement.

The source warns below a tenth of the loan and withdraws the warning above it. A
warning and not a refusal: the existing boundary stop already refuses the exact
object that would cross the line, with exact numbers, at the exact moment, and
preserves everything copied before it. Refusing the session early would discard
that partial progress to say something vaguer, sooner. What was missing was only
that nobody heard about it beforehand.

For a local path, a copy does not start when the destination volume is under a
64 MiB floor. Filling a volume to zero harms the machine and not merely this
backup: the journal stops, temp files fail, and on the source's own volume the next
capture cannot stage at all. It is recorded **unavailable** rather than failed —
freeing space makes the next pass simply succeed, so nothing there needs a human's
decision, only room. A platform that will not report free space reads as room: this
guard exists to stop a disk filling and must never itself be why a healthy
destination stops receiving backups.

### 7. Age warns; it does not move the state

A proof past its bound — seven days for a local path, thirty for a peer, from
architecture 09 §4 — is named in the warnings and leaves `ProtectionState`
untouched. An old proof is still a proof, and demoting a set over its age would say
data is at risk when what is true is that nobody has looked lately.

This was recorded against the author's own initial recommendation, which was to
degrade. The argument that changed it: a state-only dashboard stays green over an
unproven destination either way, so the warning text has to carry its own weight
regardless — and a state that means two different things is worse than a warning
that means one.

Three narrowings keep it worth reading. It fires only where nothing else is already
complaining, because an unproven, sequence-stale or knowingly-unprovable
destination each earns its own warning naming that situation. It is checked for
**every** destination, not only those the protection question reaches — a local
path usually sits inside the source's failure domain and can never earn
`protected`, but its proof is what licenses the staging trim, so an overdue proof
there quietly stops space coming back. And a stamp ahead of the clock withholds the
age rather than reporting zero, because zero reads as "verified today", which is the
one answer certainly wrong.

### 8. Transient conditions are warnings; findings are notices

The two channels are now separated by rule rather than by habit. A condition that
is recomputed every time status is derived — staleness, address defects, domain
residue — is a **warning**, and therefore self-clearing. A finding that must
survive being ignored — a destination that lost data, a peering ended while this
hub was away — is a **notice**, and persists until acknowledged.

The notice store gained the ability to *resolve* an entry for this rule to be
usable at all: without it every transient condition became a permanent nag. It also
now refreshes a re-raised notice's message, which it had documented and not done —
so a notice carrying numbers no longer shows the first observation forever.

## Consequences

- A destination that quietly deletes objects is named on the next sync rather than
  silently re-seeded, and the finding survives being ignored.
- A destination unproven past its bound is named in status without the protection
  state becoming ambiguous.
- A convergence filter that could not be computed says so instead of quietly taking
  a whole copy.
- A typo'd endpoint is reported before anything counts on it, and a new destination
  can be probed before the first full copy does.
- A peer push that is about to run out of room says so a pass early, and a local
  copy that would fill the volume does not start.
- A replica's stored bytes are re-confirmed against their seals on a schedule, and
  sync-time sampling coverage provably accumulates instead of re-asking the same
  questions forever.
- The transfer lane carries more work: fan-out and the deep sweep share one worker
  for the whole process. The sweep is bounded and resumable precisely so this stays
  a delay to confirmation rather than to replication.
- A failed sweep segment records a sync failure and therefore takes the back-off
  with it. That back-off is capped at an hour, so a repair sync is delayed at most
  that — stated here rather than discovered later.
- Peer-side deep verification remains undone. It is the one piece of the
  fitness picture this record does not deliver, and it is named in §4 rather than
  left to be noticed.

## Alternatives considered

**Degrade on staleness rather than warn.** Rejected in §7, against the author's
first instinct.

**A ledger row for a never-attempted destination.** Offered and declined: the
fan-out returns silently when no archive exists, so such a destination is absent
from the status matrix entirely. The probe covers most of what the row would have,
on demand. Recorded so it is a decision rather than an oversight.

**Headroom in the hello or in the terms.** Both rejected in §6, for different
reasons — one costs an O(all objects) walk on every session, the other fires a
false narrowing notice on every sync.

**Sequence reasoning for shortfall detection.** Rejected in §3 with its four
false positives enumerated.

**Address validation at configuration load.** Rejected in §1. This is the one that
would have been actively harmful: it would have taken every backup set down over a
single typo.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Accepted | Written after the arc it records was built, from six gaps each verified against the code rather than surmised; §1's alternative would have taken every backup set down over one typo, and is the reason the record exists in this shape |
| 2026-08 | Amended by later records | Fitness gained consequence it did not have here: for a direct-ship set ([ADR-0046](0046-direct-to-destination-publication.md) §3) the same defect/reachability/capacity findings scope the *run* — an unfit destination is excluded from the capture rather than merely degrading one pair, and with none fit the capture refuses. A zero already-held count is now also a legitimate state, not only a wiped replica: a pair owed its seed says so through the ledger's baseline facts ([ADR-0047](0047-backup-pool-and-priorities.md) §6, `needs_full`). The staging-trim licensing this record mentions applies to staging sets only; direct-ship reclaim runs through per-destination convergence under the same proof rule. |
