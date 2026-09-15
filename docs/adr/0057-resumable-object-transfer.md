# ADR-0057 — An interrupted object resumes where it stopped, and the source decides where that is

**Status:** Accepted
**Date:** 2026-09
**Requirements:** FR-REP-003, FR-DEST-003, NFR-PERF-016
**Related:** [ADR-0034](0034-hub-and-spoke-destinations.md), [ADR-0041](0041-guided-restore-and-peer-retrieval.md), [ADR-0046](0046-direct-to-destination-publication.md), [ADR-0056](0056-incremental-reconciliation.md)

---

## Context

[Peer-protocol 03 §5](../../specifications/peer-protocol/03-replication.md#5-resumption-and-atomicity)
made a good argument and then took it one step too far. The argument: objects
are immutable, a create-if-absent write is idempotent, the destination commits
whole or not at all — so re-running an interrupted exchange needs no
checkpoint at all, because the destination's next inventory already says what
it holds. The step too far was the granularity. Resumption at the *object*
was called sufficient, and the section closed with "an interrupted object is
re-sent whole … that is a later refinement".

For a small object it is not a refinement worth having. For the ones this
product actually moves it is: a sealed blob targets 64–128 MiB and may reach
512 MiB, and on a domestic uplink a quarter of an hour of transfer was being
discarded because a router rebooted. The larger the object, the likelier the
interruption and the dearer the restart — the two work against each other,
which is what makes the whole-object granularity worse than it sounds.

Three code sites enforced the old behaviour and none of them said so. The
destination spooled to `Guid.NewGuid()`, keyed to nothing; `Dispose` deleted
that file in a `finally`; the source's send loop opened `var offset = 0UL`
unconditionally. **Nothing in the test suite severed a transfer inside an
object**, so the behaviour was believed rather than held, and the first thing
this work needed was the cut itself.

## Decision

### 1 A negotiated feature, `partial-object-resume`

It gates both halves at once — the destination's declaration and the source's
resume offset — because either half alone is a protocol error to the other
side. An older destination reads a chunk at a non-zero offset as `malformed`,
which is *correct*: without the agreement there is nothing such an offset
could mean. An un-negotiated pair declares nothing, stages nothing between
sessions, and re-sends whole.

### 2 The destination declares, once, after its inventory

A new message, `ReplicationPartial` ([03 §3.3.1](../../specifications/peer-protocol/03-replication.md#331-replicationpartial)):
object key, bytes staged, and the SHA-256 of exactly those staged bytes. It is
sent even when it declares nothing, because a source expecting a frame that
never came would read the next message in its place.

A new message rather than keys on `ReplicationInventory` — which is what
§5 nominated. The inventory is paged at 4096 keys and "send it on the last
page" is an awkward rule to state and a worse one to implement; and a partial
is a different kind of fact. The inventory says what the replica holds. This
says what a scratch file holds: bytes that are in no store, answer no read,
and may be thrown away at any moment.

### 3 The digest is computed when declaring, not remembered

From the file, at the moment of declaring — one read of the staged prefix.
Remembering the digest from when the bytes arrived would be cheaper and would
miss the case the digest exists for: a prefix that rotted on disk between
sessions. It costs a read of bytes we are trying not to re-send, which is a
trade worth making by roughly the ratio of disk to uplink.

This is `Repository.Packing/SpoolCheckpoint`'s rule — *the resume point is
found by authenticating the bytes, never by trusting a durable watermark* —
applied to the one place in the product where the party holding the bytes
cannot authenticate them.

### 4 The source decides; the declaration is a claim

The destination holds no repository keys, and a store blob key is a keyed
rendering of an *identifier* rather than of the bytes
([02 §4.3](../../specifications/repository-format/02-identifiers.md#43-not-leaking-writer-identity)),
so a replica cannot check its own bytes against anything. The source can: it
reads its own first *n* bytes, hashes them, and compares. On a match it sends
the resume offset and streams the tail. On a mismatch it sends the object
whole and logs that it did.

So a peer cannot cause a source to skip bytes it has not proved it holds, and
the failure mode of every disagreement is the behaviour that existed before
this record. The check is possible at all only because objects are immutable:
the prefix the source hashes today is the prefix it sent yesterday.

### 5 The staged file is keyed, durable, and swept

Under `<state>/spool/replication/<repository>/<hash of key>.partial`, with a
`<hash>.meta` sidecar naming the object and the length it is being staged
towards. The hash is of the key because a store key may be a kilobyte long and
contain separators no file name may; the sidecar is what makes the file
nameable again.

It survives a cut session, goes when the object commits, and is swept when it
is stale (seven days), unpaired, empty, or superseded by an object that
arrived some other way — and at listener startup, which is where a process
kill, the one interruption no `finally` sees, is noticed. A prefix nobody can
name is deleted rather than kept: scratch that cannot be offered is only a
charge on disk.

### 6 Staged bytes count against the quota

[05 §3](../../specifications/peer-protocol/05-quotas.md) says a destination
refusing on quota must not spool any part of the object, which was true when
no part could outlive the session. Now it can, so the peer's usage includes
what it has staged, and a **resumed** object is admitted on its tail alone —
the staged bytes are already counted, and charging twice would refuse a peer
for bytes it already has. Without this a peer could park unbounded bytes
outside the ceiling it agreed to by starting transfers it never finishes.

### 7 Nothing about atomicity or verification changes

The destination still commits whole or not at all, still under create-if-absent.
Staged bytes live outside the replica store, in a directory the key grammar
cannot spell, so they answer no read, appear in no inventory, and cannot
satisfy a possession challenge. [04](../../specifications/peer-protocol/04-verification.md)'s
rule that a short replica is damage keeps its present meaning, because a
staged prefix is not a replica.

## Consequences

**Positive**

- An interrupted transfer costs its tail rather than the whole object, which
  is the difference between a large blob eventually arriving over a poor link
  and never arriving at all.
- The interruption itself is now exercised: a source store whose read stream
  dies after *n* bytes, which nothing in the fault-injection library could do
  before, since every decorator there stops at the object boundary.
- `PushOutcome` carries bytes as well as objects, so a pass can say what it
  saved. The object counts cannot: re-sending whole and sending a tail both
  commit exactly one object.

**Negative**

- A destination now keeps bytes between sessions that it previously discarded,
  and they are charged to the peer's quota, so a peer near its ceiling may be
  refused for an object it would previously have been admitted for. The
  alternative is worse: uncounted bytes are a ceiling that does not hold.
- One more file pair per interrupted object, one more sweep, and a startup
  scan of the spool root.
- The source pays a read and a hash of its own prefix for each resumed object.
  It is bounded by the staged length, and objects under one chunk (1 MiB) are
  never resumed because the hash would cost more than the bytes it saves.
- A resumed commit is assembled from two sessions' bytes. The prefix is
  verified by digest and the tail by the session's own integrity, so no part
  is unchecked — but the object is no longer the product of a single
  uninterrupted read, and that is a real change to how it came to be.

## Alternatives considered

**Trust the declared length.** Cheapest: the destination says how much it
holds and the source believes it. A destination that lied — or whose disk
rotted — would then have an object committed around bytes nobody checked, and
the error would surface days later as a failed challenge or a failed drill, if
at all. The whole value of resumption is bytes not sent; spending a local read
to know they were the right bytes is the smallest possible price for it.

**Range-challenge the prefix.** Reuse the HMAC possession challenge over a
random range inside the claimed prefix. Strictly stronger against a peer that
holds a digest but not the bytes, and it needs the responder's state machine
to accept challenges mid-payload plus a round trip per resumed object. The
threat it closes is narrow: a peer that can produce the digest of a prefix it
does not hold, in order to receive fewer bytes of its own replica. It can be
added later without changing the message, which is why the digest comes first.

**A whole-object digest on `ReplicationObject`.** Would let the destination
check an assembled object before committing. The prefix digest covers the
resumed part and the session covers the tail, so it would add a field nothing
yet needs — but it is the natural home for a replication receipt, and the
absence is recorded here rather than left to be rediscovered.

**Put the offsets on the inventory,** as §5 nominated. Rejected in decision 2.

**Resume by re-opening the store's own temp file.** `LocalFileSystemObjectStore`
already spools to `.fbp-tmp/<guid>` before its atomic rename, and that spool is
deliberately unaddressable — random names, a dot-prefixed directory no object
key can spell. Making it addressable to allow resumption would weaken an
invariant the store's security tests hold, for a mechanism that belongs to the
peer protocol rather than to storage.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-09 | Accepted | In response to the 2026-09 architecture review's R7. Built: `Protocol/PeerReplicationMessages.cs` carries `ReplicationPartial` and the resume offset, `Protocol/PeerSessionNegotiation` the feature, `Agent/PartialSpool` the staged prefix and its lifecycle, `Agent/ReplicationResponder` the declaration and the re-anchored offset check, `Agent/ReplicationInitiator` the verification and the ranged send. Held by `Hosts.Tests/PeerResumeTests` and `Protocol.Tests/ReplicationMessageTests` |
