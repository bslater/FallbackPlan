# ADR-0035 — Destination fitness: admission, capacity, shortfall, and confirmation on a schedule

**Status:** Accepted
**Date:** 2026-08
**Requirements:** FR-DEST-001, FR-DEST-003, FR-DEST-004, FR-DEST-009, FR-DEST-010, FR-VER-002, FR-VER-003, FR-VER-004, FR-VER-005, FR-VER-007, FR-VER-008, FR-GC-009
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

> **Amended 2026-09 ([Amendment 1](#amendment-1-2026-09--a-circuit-is-carried-to-its-end-and-damage-is-repaired)):** the cadence is
> between circuits, not segments. Built as written, the interval ran from the
> last segment, so a replica of ten thousand blobs was read once in about three
> years. A circuit that has begun is now carried on every pass until every
> stored blob has been read, and the interval rests from when it closed.

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

> **Amended 2026-09 ([Amendment 1](#amendment-1-2026-09--a-circuit-is-carried-to-its-end-and-damage-is-repaired)):** the reason given
> here no longer holds. The retrieval session
> ([ADR-0041](0041-guided-restore-and-peer-retrieval.md)) reads a peer's replica,
> and Amendment 1 reads one as a repair source. What keeps the sweep local is
> the cost: re-reading all of a peer's replica is a standing charge on somebody
> else's link, which needs a stated cadence and a bound of its own, as
> [ADR-0054 Amendment 3](0054-scheduled-restore-drills.md) gave drills.
>
> **Amended again 2026-09 ([Amendment 2](#amendment-2-2026-09--a-peer-is-swept-on-a-stated-cadence-and-what-is-found-there-is-held)):** a peer now has them. It is
> swept over the retrieval session on a cadence its source's operator states,
> and never without one.

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

> **Amended 2026-09 ([Amendment 1](#amendment-1-2026-09--a-circuit-is-carried-to-its-end-and-damage-is-repaired)):** the repair sync
> promised above repaired nothing. The copier counts any key that is present as
> held, and a local store never overwrites, so a damaged object stayed damaged
> and the next sync called the pair in sync over it. Amendment 1 builds the
> repair, and the sync that re-checks it. The last bullet stood, for the
> reason the blockquote at §4 gave, until
> [Amendment 2](#amendment-2-2026-09--a-peer-is-swept-on-a-stated-cadence-and-what-is-found-there-is-held)
> built peer-side deep verification on a stated cadence. What stays undone is
> repairing a peer.

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

## Amendment 1 (2026-09) — a circuit is carried to its end, and damage is repaired

§4's sweep was built, and three things around it did not hold.

- **The pace.** The scheduler made a pair due when the interval had passed since
  the last *segment*, and a segment is sixty-four blobs. The configuration said
  so in its own words — the interval "sets how often a segment runs, not how
  long a full circuit takes" — and nothing said what that meant. At the 64 MiB
  blob target a 640 GiB replica is ten thousand blobs, and a weekly interval
  read all of it once in about three years. "Every stored object has now been
  checked" was never said of an archive of any size.
- **The repair.** The finding's notice asked for the damaged objects to be
  re-copied, and the Consequences promised a repair sync delayed at most an
  hour. That sync copied nothing: the copier counts any key that is present as
  held, and a local store never overwrites
  ([specification 01 §4](../../specifications/repository-format/01-object-layout.md#4-object-immutability)).
- **The state.** Nothing in the fan-out or the status read the finding, so the
  retry sync recorded success and the pair read in sync while its notice said
  it was damaged.

**Decisions.**

1. **A circuit is carried to its end.** An open circuit — a cursor on the
   ledger — is due on every pass. The interval rests between the end of one
   circuit and the start of the next, measured from when the last one closed,
   and the seven-day default stands, now meaning a full re-read at most
   weekly. A segment stays bounded, because it holds the process's one
   transfer worker while it reads, and it is now bounded in bytes as well as
   in blobs: 4 GiB by default, and for a background segment of a destination
   with a transfer limit about a minute's worth at that rate — never less than
   one blob, or a blob larger than the budget would park the circuit on
   itself. A person's segment reads through no limit and is bounded by none.
2. **Damage is repaired from a copy proven sound first** (FR-VER-007). A copy is
   sound when its whole blob still hashes to its sealed digest *and* its
   envelope names the blob its key derives from: the check the sweep's length
   comparison stands in for, made directly, because the key-ID key is at hand.
   - Sources are tried nearest and cheapest first: a staging set's staging
     archive, the set's other local paths by priority, then its peers over the
     retrieval session, each dialled only when every nearer source has failed
     to serve and at most once.
   - The damaged destination is never a source, and neither is a direct-ship
     set's own read path, which answers from the highest-priority holder and
     may be the damaged one.
   - A source's copy is staged under the state directory's spool and proven
     there, so the bytes proven are the bytes installed and a peer's link is
     read once. Only then is the damaged object deleted and the copy put, and
     the copy is proven again where it landed before the repair is claimed.
   - A background repair reads each source through that source's own transfer
     limit.
   - Immutability holds. What is put is byte for byte what was written under
     the key, which is what the proof establishes; the key's content changed
     when the storage rotted, and the repair changes it back.
3. **With no sound copy, nothing is deleted.** A damaged blob's other records
   still restore, each authenticating on its own, so the object stays where it
   is, named on the notice and on the ledger.
4. **Known damage is held against the pair.** The ledger keeps the keys found
   damaged and not repaired (schema 6), written before the repair is attempted
   so that a repair cut short still leaves them.
   - While any are listed, a success recorded by any writer keeps the pair
     failed, says which objects, and does not reset the back-off. A direct-ship
     capture ships new blobs to a destination knowing nothing of the old ones,
     so its success is not evidence the damage has gone.
   - The local-path sync is the repair sync the Consequences promised. After
     its copy it re-reads the listed keys, repairs what it now can, resolves
     what is sound again or no longer owed, and records a failure rather than a
     success while any remain.
5. **A finding still fails the pair, and its notice stands until acknowledged.**
   FR-VER-005 is unchanged: the segment that finds damage records a failure even
   when every object was repaired, and the next sync re-checks and returns the
   pair to in sync. The notice says what was replaced and from where, what
   could not be and why, and to check the device. A clean circuit no longer
   withdraws it. With the repair immediate it would be withdrawn before anyone
   had read it, and a disk that altered a backup once is worth a person's
   attention — the shortfall notice's posture (§3).
6. **A blob that will not read stalls the circuit, under back-off.** Decision 1
   made an open circuit due on every pass, and a segment that met a blob it
   could not read recorded nothing: the next pass read the same run to the
   same blob, once a minute, for as long as it would not read, and nothing
   said so. A failed read is not a finding, because a disk gone from under the
   segment fails every read the same way one bad sector fails one.
   - The segment stops at the blob and keeps what it read before it, so the
     next attempt begins with that blob.
   - The stall is counted on the ledger (schema 6), and the next attempt waits
     the sync's back-off — the poll interval doubling, to an hour — instead of
     the next pass. A stalled segment that still read past where the last one
     stopped counts from one again.
   - Three stalls in a row raise a notice naming the destination, the blob and
     the error. It is a condition, not a finding, and is withdrawn once a
     segment reads past it.
   - A replica that is not there at all — a drive unplugged — is not a stall.
     Nothing was tried, fan-out says so, and the circuit resumes where it was.

The act is called **repair**. *Heal* already names the copy-back of a
destination's metadata to a rolled-back hub
([ADR-0062](0062-the-destination-is-the-rollback-witness.md)), and one word for
two directions of copy would say neither.

**What this amendment does not do.**

- **Repair a peer's replica.** The retrieval session is read-only and a push
  never overwrites, so replacing an object at a peer needs protocol work. A
  peer's damage is found by the read-back and the drill, and nothing repairs it
  yet.
- **Sweep a peer.** §4's stated reason is gone, as its blockquote says. What
  remains is the cost to somebody else's link, which needs a stated cadence and
  a bound; that is for the record that builds it.

  > **Amended 2026-09 ([Amendment 2](#amendment-2-2026-09--a-peer-is-swept-on-a-stated-cadence-and-what-is-found-there-is-held)):** built, on a stated cadence and
  > under a bound.
- **Keep content under the state directory.** The stage holds one blob at a
  time and is removed when the repair ends, so a direct-ship set still keeps no
  content there between repairs ([ADR-0046](0046-direct-to-destination-publication.md)).
- **Take a blob that will not read for damage.** Replacing it would need
  evidence, within the attempt, that the rest of the replica still reads.
  Without that, a failing disk's every blob would be condemned as altered in
  turn. Decision 6 says the stall instead, and repairing an unreadable blob is
  left to a record that can tell the two apart.

## Amendment 2 (2026-09) — a peer is swept on a stated cadence, and what is found there is held

Amendment 1 left the peer half named and unbuilt: a peer's replica could be
read over the retrieval session, and re-reading all of it was a cost to
somebody else's link that nobody had bounded. This bounds it (FR-VER-008).

**Decisions.**

1. **A peer is swept only on a cadence its source's operator states.** The
   destination's `deep_verify_interval_days` is the cadence, and for a peer an
   absent value means never. That is [ADR-0054 Amendment
   3](0054-scheduled-restore-drills.md)'s rule for drills, for the same reason:
   the bandwidth is somebody else's, and this service does not spend it by
   default. The console's form offered the field for every destination, and for
   a peer it did nothing; a peer whose declaration already states one is swept
   from the first pass after this change, because the value says what its
   operator asked for.

   > **Amended 2026-10 ([ADR-0091](0091-an-s3-compatible-destination.md) Amendment 1).**
   > An S3-compatible store keeps this rule for a reason of its own: every
   > read at a provider is a request it may charge for. Its segment reads the
   > peer's share, and a store that refuses is a stall waited out under the
   > back-off. What the sweep finds there is repaired, as at a local path,
   > because this side can write to a store.
2. **The read is the local path's read, over the wire.** Every blob is read
   back whole over the retrieval session and checked against the digest sealed
   into its own footer, with the length comparison against the source where the
   source still holds the key. The bytes crossing the wire are the proof: a
   digest the peer computed of its own copy would be a claim
   ([ADR-0058](0058-peer-write-adapter.md) §8). A circuit is carried on every
   pass, as Amendment 1 carries a local path's, and a blob that will not be
   read — the peer drops the session at it — stalls the circuit under
   Amendment 1's decision 6, as a bad sector does.
3. **Bounded to what the link can spare.** A background segment reads through
   the peer's transfer limit, about a minute's worth at it, or 256 MiB — four
   blobs at the default target — where the peer has none. It never reads less
   than one blob. A person's segment is not paced, and is bounded the same
   256 MiB, because it holds the set's gate, which the set's syncs wait on.
   - A peer last found unreachable is not dialled for its sweep, because a
     dial that fails holds the one transfer worker until it does.
   - A scheduled segment that does not reach its peer records it unreachable,
     with the message the fan-out would record. An in-sync pair with nothing
     owed syncs only when its challenge falls due, hours apart, so without
     this the sweep would dial a peer that has gone once a pass until then.
     The sync's back-off then decides when the peer is next tried, and the
     circuit keeps its cursor.
4. **What is found is held, and not repaired.** The keys go on the ledger and
   the pair fails, held failed through any success by Amendment 1's rule.
   Nothing here can write at a peer: the retrieval session reads, a push only
   creates, and a deletion there needs reclaim authority
   ([ADR-0055](0055-reclaim-authority.md), [ADR-0059](0059-session-bound-deletion-authority.md)),
   which a write-only service holds only under a grant. So the notice names the
   objects, the repository they sit under, and the one remedy there is: the
   peer's owner removes them, and the next push sends them again whole.
5. **Damage leaves the ledger when it has gone.** A push clears a damaged key
   the peer no longer declares holding, because it has been removed there and
   sent again if the peer is owed it. A segment clears a damaged key it has read
   again and found sound, for any destination. It clears only keys it actually
   read, so a key the segment never reached is never taken for a sound one.
6. **A peer that will not serve the session is not blamed.** Reached and
   refused is an incapacity, never a finding. The attempt is stamped, so the
   next waits the interval instead of following on the next pass, and a notice
   says that the cadence reads nothing and why. The notice resolves itself when
   a segment next reads. Unreached is not stamped: decision 3 records it as
   the fan-out would, and a reachable peer's next segment follows once a sync
   has found it again.
7. **On demand, any peer.** `verify-destination` reads a peer's replica, one
   segment or with `--full` the whole circuit, and needs no cadence: a person
   asking is consent for the read, as a restore is. A replica that could not be
   read is "not deeply verifiable now", with the reason. It is never counted as
   damage, and never as a pass, and a person's read records no outage. A
   segment that stops short at a peer with no cadence names the next
   `verify-destination` as what continues it, since nothing scheduled will.

**What this amendment does not do.**

- **Repair a peer.** Decision 4's remedy is the peer owner's to carry out.
  Replacing an object from here needs a write the protocol does not have, under
  an authority a write-only service does not hold unattended; that is a record
  of its own.
- **Offer a cheaper circuit.** The scoping offered one built from
  [ADR-0065](0065-merkle-commitment-and-chunk-possession.md)'s chunk challenge:
  one leaf a blob, a mebibyte instead of the blob. It is a sampled proof, which
  FR-VER-003 requires be reported apart from a whole read, so it needs its own
  ledger field and its own line in the status and the console. Named here, not
  built.
- **Report a circuit in status.** When a circuit last closed is on the ledger,
  and said by `verify-destination`, but not yet on the contract, the status
  matrix or the console, for a local path or a peer.

  > **Built 2026-09 ([Amendment 3](#amendment-3-2026-09--a-circuit-is-reported-where-the-status-is)):**
  > contract 1.46 carries each destination's deep sweep on its status row, and
  > the CLI and the console say it.

## Amendment 3 (2026-09) — a circuit is reported where the status is

Amendment 2 left the report owed. When a circuit last closed was on the ledger
and said by `verify-destination`, but not on the contract, the status matrix
or the console. So nothing a person looks at routinely could tell a replica
read back in full last night from one never read back at all (FR-VER-003).

**Decisions.**

1. **The status row carries the sweep, as one object (contract 1.46).**
   `deep_sweep` holds:
   - `circuit_closed_at`, when a circuit last closed: the one fact that
     supports "every stored object was read back and matched its seal";
   - `read_this_circuit` and `last_read_at`, for the circuit under way;
   - `stalls` and `stalled_on`, for a circuit stopped at a blob that will not
     read;
   - `interval_days`, the cadence the scheduler keeps.

   Each is the ledger's fact as it stands. Nothing is derived beside them.
2. **No sweep is not a sweep that has not run.** The object is null where no
   sweep exists: a reserved kind nothing reads back in full, a destination no
   longer declared, and a service older than 1.46. A client draws nothing for
   it. A sweep that has not run is an object with nothing closed, and the CLI
   prints `never` for it.
3. **Read only on request is not never.** A peer whose operator stated no
   cadence has a sweep with a null `interval_days`, because
   `verify-destination` reads it when a person asks (Amendment 2, decision 7).
   The CLI prints `manual`, and the console says it is read in full only when
   asked. Drawn as never, it would read as a sweep overdue.
4. **A stall is said first.** Whatever closed before it, a circuit stopped at
   a blob is the part of the row that needs a person. The CLI prints
   `STALLED`, and the console says how many times in a row, and whether at a
   blob or because the replica could not be read.
5. **Progress is never coverage.** What the circuit under way has read is a
   count of blobs, never a share of the replica: the sweep's cursor is a key,
   and nothing on the ledger counts what remains. Only a closed circuit
   supports a claim about every stored object.
6. **One rule for the cadence.** Which destinations are swept, and on what
   interval, is stated once (`ReplicaSweepJob.ScheduledIntervalDays`). The
   scheduler keeps it, `verify-destination` words its line by it, and the
   status reports it, so a row cannot promise a cadence the scheduler never
   keeps.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Accepted | Written after the arc it records was built, from six gaps each verified against the code rather than surmised; §1's alternative would have taken every backup set down over one typo, and is the reason the record exists in this shape |
| 2026-08 | Amended by later records | Fitness gained consequence it did not have here: for a direct-ship set ([ADR-0046](0046-direct-to-destination-publication.md) §3) the same defect/reachability/capacity findings scope the *run* — an unfit destination is excluded from the capture rather than merely degrading one pair, and with none fit the capture refuses. A zero already-held count is now also a legitimate state, not only a wiped replica: a pair owed its seed says so through the ledger's baseline facts ([ADR-0047](0047-backup-pool-and-priorities.md) §6, `needs_full`). The staging-trim licensing this record mentions applies to staging sets only; direct-ship reclaim runs through per-destination convergence under the same proof rule. |
| 2026-09 | Amended | [Amendment 1](#amendment-1-2026-09--a-circuit-is-carried-to-its-end-and-damage-is-repaired): a circuit is carried on every pass and the interval rests between circuits, and a segment is bounded in bytes as well as blobs (`Agent/Scheduler`, `Agent/ReplicaSweepJob`, `Repository/ReplicaSweep`). Damage the sweep finds at a local path is repaired from a copy proven sound first — the staging archive, another local path, or a peer over the retrieval session (`Repository/ReplicaRepair`, `Agent/ReplicaRepairer`) — and where none exists it is kept, named, and held against the pair by the ledger and the sync that re-checks it (`Application/DestinationSyncStore`, `Agent/FanOut`). The finding's notice now stands until acknowledged. A blob that will not read stalls the circuit under the sync's back-off rather than being met again every pass, and three stalls in a row are said. Held by `Hosts.Tests/DeepSweepTests`, `Repository.Tests/ReplicaRepairTests`, `Repository.Tests/ReplicaSweepTests`, `Hosts.Tests/BackgroundRateLimitTests` and `Application.Tests/DestinationSyncStoreTests` |
| 2026-09 | Amended | [Amendment 2](#amendment-2-2026-09--a-peer-is-swept-on-a-stated-cadence-and-what-is-found-there-is-held): a peer is swept over the retrieval session on a cadence its source's operator states and never without one, paced by its transfer limit and bounded per segment (`Agent/Scheduler`, `Agent/ReplicaSweepJob`); what it finds is held against the pair and named with the remedy the peer's owner can carry out, and cleared by the push that re-sends a removed object or by a segment that reads it sound (`Agent/FanOut`, `Application/DestinationSyncStore`, `Repository/ReplicaSweep`); a peer that will not serve the session is said to be unreadable and not redialled every pass, and one gone between syncs is recorded unreachable by the segment that meets it. Held by `Hosts.Tests/PeerDeepSweepTests`, `Hosts.Tests/DeepSweepTests` and `Web.Tests/ConsoleServiceSettingsScriptTests` |
| 2026-09 | Amended (related) | [ADR-0075](0075-a-restore-reads-around-damage.md) makes a restore a detector too: damage a restore of the set's own archive reads around is put on the destination's ledger row as the sweep's findings are, so the next sync repairs a local path's and holds a peer's. `Agent/ServiceCommandHandler`; `Hosts.Tests/RestoreReadAroundTests` |
| 2026-09 | Amended (related) | [ADR-0076](0076-damage-is-traced-to-what-needs-it.md) gives the findings a scope: the sweep's notice and verify-destination's line name the files and snapshots the damage reaches, a peer's finding says whether a copy here holds the objects sound, and a pair failed for its damage alone degrades only the snapshots that need it. `Agent/ReplicaSweepJob`, `Agent/FanOut` |
| 2026-09 | Amended | [Amendment 3](#amendment-3-2026-09--a-circuit-is-reported-where-the-status-is): each destination's status row carries its deep sweep — when a circuit last closed, how far the one under way has read, whether it has stopped, and the cadence the scheduler keeps — as contract 1.46's `deep_sweep` (`Api/Results`, `Agent/ServiceCommandHandler`), and the CLI (`Cli/CliApplication`) and the console say it. The cadence rule is stated once for the scheduler and the status (`Agent/ReplicaSweepJob`, `Agent/Scheduler`). Built tests first, with each rule's removal confirmed to turn its tests red: a stall checked after the close, and a reserved kind reported as swept. Held by `Hosts.Tests/DeepSweepTests`, `Hosts.Tests/PeerDeepSweepTests`, `Hosts.Tests/DeepSweepCadenceTests`, `Hosts.Tests/ClientModeTests`, `Cli.Tests/StatusSweepTokenTests`, `Api.Tests/ContractAdditiveFieldsTests`, `Web.Tests/StatusRelayNamesTests` and `Web.Tests/ConsoleDestinationCardTests` |
| 2026-10 | Amended (related) | [ADR-0091](0091-an-s3-compatible-destination.md) Amendment 1: an S3-compatible store is swept on Amendment 2's rule, only on a cadence its operator states and on the peer's segment share, and what the sweep finds there is repaired by delete and put, as Amendment 1 repairs a local path's; a store that does not answer is recorded unavailable, and one that refuses is a stall under the back-off. `Agent/ReplicaSweepJob`, `Agent/Scheduler`; `Hosts.Tests/S3DestinationTests`, `Hosts.Tests/DeepSweepCadenceTests` |
