# ADR-0058 — A direct-ship set ships to a peer over a session it holds open for the run

**Status:** Accepted
**Date:** 2026-09
**Requirements:** FR-DEST-013, FR-DEST-015, FR-REP-001, FR-VER-001
**Related:** [ADR-0034](0034-hub-and-spoke-destinations.md), [ADR-0041](0041-guided-restore-and-peer-retrieval.md), [ADR-0046](0046-direct-to-destination-publication.md), [ADR-0047](0047-backup-pool-and-priorities.md), [ADR-0055](0055-reclaim-authority.md), [ADR-0057](0057-resumable-object-transfer.md)

---

## Context

[ADR-0046](0046-direct-to-destination-publication.md) removed the local
staging copy: a set flagged `direct_ship` captures into its destinations and
keeps metadata alone on the machine being backed up. It shipped with one
stated tail, named in its own consequences and repeated in its last three
status rows — *peer destinations are not yet served by the sink*. Three lines
in `Agent/DestinationShipSink` enforced it, skipping any destination whose
kind was not `LocalPath` as `NotSupported`.

That tail cost more than it sounds. A destination that is skipped is a
destination that is not seeded, and a run with nothing seeded finds nowhere to
write and refuses. So a set whose only destination was a peer — a household
that keeps its backups at a friend's house and nowhere else, which is the
configuration the peer protocol was built for — could not capture at all. The
configuration boundary papered over it by forcing such a set to the staging
shape, which means keeping a whole second copy of the backup on the machine
the backup exists to survive.

The 2026-09 architecture review listed the peer write adapter among the items
genuinely still open under its R0. It is the last of them that direct-ship
itself depends on.

## Decision

### 1 The adapter is an object store over a live push session

`Agent/PeerShipStore` implements `IObjectStore` over one replication exchange
([peer-protocol 03](../../specifications/peer-protocol/03-replication.md)) held
open for the length of a run: the offer and the destination's inventory when
the run resolves its targets, one `ReplicationObject` and its chunks per put,
and the completion and its acknowledgement when the run closes its books.

The sink already fans out through `IObjectStore`, so this is the whole of the
integration: a peer becomes a shipment like any other, priority-ordered with
its siblings, dropped by the same rule, and recorded in the same ledger.

### 2 Live, because the alternative is the copy this record's parent removed

The obvious shape is to buffer the run's objects locally and push them
afterwards, which needs no session held open and reuses
`Agent/ReplicationInitiator` unchanged. It also reintroduces a local copy of
the backup — with a different name and a shorter life, but the same bytes on
the same disk — which is the thing ADR-0046 exists to remove. A set that ships
to a peer because it cannot afford a staging archive cannot afford a spool of
the same size either.

The cost is that a run holds an authenticated TLS session for its whole
duration, which for a large capture is hours. That is what the peer's own
listener is built to serve, and a session that dies is the destination's drop,
not the run's.

### 3 The inventory answers what is already there, before any read

`PutAsync` consults the inventory read at the session's start, plus whatever
this session has itself sent, and answers `AlreadyExists` without touching the
wire. This is the same diff the fan-out's push computes, arriving one object at
a time instead of all at once.

It is also what keeps the seeding probe free: a peer with a fresh replica holds
nothing, so nothing is asked of it beyond the objects themselves.

### 4 A ship session does not offer `partial-object-resume`

[ADR-0057](0057-resumable-object-transfer.md)'s resumption finishes an object
the source still holds. A direct-ship run holds none — its objects are sealed as
they are produced, and a run cut mid-blob seals a differently identified blob
the next time, because blob identity comes from the writer's sequence. A prefix
staged for this session could therefore never be finished.

Offering the feature anyway would have the destination keep those prefixes for
a week, charged against this peer's quota (ADR-0057 decision 6), waiting for a
second half that is never sent. So the ship session narrows what it offers, and
the destination stages nothing. The fan-out's own push is unaffected and still
resumes: it reads from a store that keeps its objects.

### 5 Reads travel a second session, and only when something asks

The replication exchange has no read in it. A peer shipment therefore answers
reads through `Agent/PeerRetrievalObjectStore` over a retrieval session
([07](../../specifications/peer-protocol/07-retrieval.md),
[ADR-0041](0041-guided-restore-and-peer-retrieval.md)) dialled the first time
bytes are actually wanted — and a key the opening inventory did not list is
answered absent without dialling at all, which is every probe a new
destination gets.

Listings go through retrieval rather than being synthesised from the inventory,
because a listing entry carries a length and an inventory page does not. A
listing whose lengths were all zero would be read by the copier and the sampler
as a replica full of empty objects, which is worse than answering nothing.

Outside a run the sink does not resolve peers at all. A peer shipment is a live
session rather than a directory, and dialling one per read would put a TLS
handshake behind a presence probe; a peer's replica is read back on the
restore-source path, which is what that path is for.

### 6 Every wire failure leaves the adapter as an `IOException`

The sink's drop rule — one destination's failure is that destination's, never
the run's, while a sibling remains — is written against the storage exceptions.
Rather than teach it a second vocabulary, the adapter translates: a refusal, a
severed connection, a session that desynchronised, all arrive at the sink as
the same kind of fault a full disk does, and are dropped, named and healed by
the same catch-up.

A peer that fails is also poisoned for the rest of the run. A half-written
frame leaves a stream nothing can be trusted to read, so later puts to that
destination fail immediately instead of desynchronising the exchange.

### 7 The acknowledgement is part of closing the books

`CompleteRunAsync` sends the completion, reads the acknowledgement, and holds
the two counts to agreement — the hard fault `Agent/ReplicationInitiator`
already makes of it. A destination that acknowledges fewer objects than it was
sent, without refusing, has either a bug or a desynchronised stream, and in
both cases the objects this run believes are there may not be. Such a peer is
recorded failed even though the run itself committed, because the ledger's
claim is about the replica and not about the capture.

### 8 A peer that holds the only copy is not claimed verified

This is the decision's uncomfortable half, and it is stated rather than
discovered later.

A destination challenge ([04](../../specifications/peer-protocol/04-verification.md))
is answered by the peer and judged against bytes this side reads for itself. A
direct-ship set whose only destination is this peer has no such bytes: the
sink can offer the metadata plane, which it keeps locally, and no content at
all. Sampling would then draw a population of small metadata objects, prove
them, and stamp the pair verified — reporting a proven replica while the part
a restore actually needs went unexamined.

That is exactly the emptiness the verification-independence work found in the
local-path path, arriving by a new road. So the pass challenges nothing in
that case, stamps nothing, and raises a durable notice saying that this
installation has no second copy to check the content against and does not
claim to have checked it. A set with a local-path sibling is unaffected: the
sink answers blob reads from the sibling, which is a genuinely independent
copy.

What would close it is the record-tag proof — opening sampled records read
back from the replica under the repository's own keys, which needs no second
copy — extended to reach a peer through the retrieval session. That is a
separate piece of work and is recorded as a gap rather than implied away.

### 9 A peer-only set still *defaults* to staging

The capability and the default are different questions, and letting them
collapse into one another would be the quiet regression in this record.

For a set whose only destination is a peer, the staging archive buys three
things direct-ship gives up: a capture that does not wait on the link, a
transfer that resumes after the link dies inside an object (decision 4), and —
the one that matters most — an independent copy to check the replica's content
against, without which decision 8 says no pass can honestly call it verified.
A new peer-only set therefore still defaults to staging, exactly as before.

What changes is that the configuration boundary no longer *forces* it. A
machine with no room for a second copy of its own backups can flag the set
direct-ship and have it work, which it could not before. That is a choice with
three stated costs, and it is the person's to make rather than a default to
impose on them.

### 10 A peer deletes nothing through the store

`DeleteAsync` answers not-found. A replica shrinks when its owner instructs it
to, over the retention exchange
([06](../../specifications/peer-protocol/06-retention.md)) and within the floor
the peer agreed to — never as a side effect of a store call the peer cannot
refuse per object.

Answering rather than throwing is the load-bearing part: the sink's sweep
deletes through every destination in turn, and a throw would take the sweep
down. The truthful answer leaves the object listed, re-condemned on the next
pass, and carried out by the instruction that is entitled to carry it.

## Consequences

**Positive**

- A set can keep its backups at a paired peer and nowhere else, without a
  staging copy on the machine it is protecting. The shape the peer protocol
  was built for is now the shape the default storage mode supports.
- Peer and local-path destinations mix in one set and advance independently,
  which is ADR-0046's fan-out promise with one of the fans being a peer.
- ADR-0046's last stated tail is discharged: the configuration boundary stops
  *forcing* peer-only sets into staging, and a machine with no room for a
  second copy can choose otherwise.

**Negative**

- A run holds an authenticated session per peer destination for its whole
  length, and the peer holds the matching listener connection. A capture that
  runs for hours is a connection open for hours.
- A peer-only direct-ship set cannot be challenge-verified, by decision 8. It
  restores, and the console says plainly that the content is unproven.
- Outside a run the sink cannot read a peer, so a catch-up copy that would
  source bytes from a peer has nothing to read. For a mixed set the local
  sibling answers; for a peer-only set there is nothing to catch up to.
- Reads during a run cost a second dialled session. It is opened lazily and
  most runs never open one.

## Alternatives considered

**Buffer the run, then push it.** Rejected in decision 2: it is a local copy of
the backup wearing a different name.

**One session per object.** A handshake, an offer and an inventory per object,
which for a capture with thousands of blobs is thousands of handshakes and
thousands of full inventories. The session is held open precisely so the
inventory is read once.

**Let the sink dial peers outside a run.** It would make a peer's replica
readable through the union everywhere, including for the catch-up copy and the
challenge — and the challenge would then compare a peer's replica against
itself, which is the vacuity decision 8 refuses to create. The readability is
real and wanted; the way to get it is the record-tag proof, not a read path
that quietly launders a replica into being its own witness.

**Carry writes on the retrieval session.** One session for both halves,
avoiding the second dial. The retrieval exchange is deliberately read-only —
its responder holds no write path and its grant role says so — and widening it
would put replication's authority behind a verb whose whole security argument
is that it has none.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-09 | Accepted | In response to the 2026-09 architecture review's R0, and the discharge of [ADR-0046](0046-direct-to-destination-publication.md)'s stated tail. Built: `Agent/PeerShipStore` is the adapter, `Agent/DestinationShipSink` admits peer destinations and closes their sessions when the run's books close, `Agent/BackupRunner` awaits that close, `Agent/FanOut` withholds the verification stamp from a pair with no independent copy, and `Agent/ServiceCommandHandler` stops refusing a direct-ship set for having a peer where a local path was demanded, while leaving the peer-only default at staging. Held by `Hosts.Tests/DirectShipPeerTests` and `Hosts.Tests/DirectShipTests` |
