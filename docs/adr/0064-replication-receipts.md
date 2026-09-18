# ADR-0064 — Replication receipts

**Status:** Accepted
**Date:** 2026-09
**Requirements:** FR-DEST-004, FR-REP-005, FR-VER-001, FR-GC-008
**Related:** [ADR-0063](0063-deletion-receipts.md), [ADR-0057](0057-resumable-object-transfer.md), [ADR-0047](0047-backup-pool-and-priorities.md), [peer-protocol 03](../../specifications/peer-protocol/03-replication.md), [architecture 09 §1](../architecture/09-replication-and-peers.md#1-what-replication-moves), [threat model T-8](../threat-model.md#t-8-destination-withholding-data)

**Built:** `Protocol/ReplicationReceipt` (the statement, its signing encoding and the parse that is its exact inverse), `Protocol/PeerReplicationMessages.cs` (`ReplicationAck` keys 2–3), `Protocol/PeerReceiptFiles` (the one filing shared with the deletion receipts, each envelope naming its kind), `Protocol/ReplicationReceiptStore` (both parties' filing, the signature re-checked on every read), `Protocol/ReceiptReport` (the reader's rendering over both kinds), `Agent/ReplicationResponder` (the destination counts as it walks and receives, signs, files, then acks), `Agent/RemoteServiceListener` (the second store beside the first), `Agent/ReplicationInitiator` (`VerifyReplicationReceipt`, and the sent keys and owed bytes it holds a receipt against), `Agent/FanOut` (the commander files, raises the notice, and records the peer's completeness), `Agent/ServiceCommandHandler.Receipts.cs` (`list_receipts`, contract 1.33), `Agent/AgentHost` and `Cli/CliApplication` (`receipts --kind`), the console's Receipts card in the Web project's script; `Protocol.Tests/ReplicationMessageTests`, `Protocol.Tests/ReplicationReceiptStoreTests`, `Hosts.Tests/PeerReplicationTests`, `Hosts.Tests/PeerRetentionReplayTests`, `Hosts.Tests/ReplicationReceiptVerificationTests`, `Hosts.Tests/ReceiptsCommandTests`, `Cli.Tests/ReceiptsVerbValidationTests`, `Web.Tests/ConsoleReceiptsScriptTests`, `Web.Tests/CommandRelayTests`, `Api.Tests/ConfigurationContractTests`, `Api.Tests/ContractAdditiveFieldsTests`.

---

## Context

A push to a peer ended with a `ReplicationAck` carrying one `u64`: the
destination's count of what it committed. The commander checked it against
what it sent — fewer is a hard fault — and then dropped it. Nothing the
peer said was signed, nothing was kept, and the sync ledger's completeness
figures — the bytes a destination holds of what it is owed and when they
were counted, the figures the console's ring is drawn from (contract 1.24,
[ADR-0047](0047-backup-pool-and-priorities.md)) — were **never written for
a peer at all**. They are counted by the local-path copy, which lists both
sides; a source cannot cheaply list a peer's replica, so a peer's card read
"not counted yet" for ever while the same figure at a local path was
counted on every pass. Two records had named the artefact that would
close this: the 2026-09 review's "formal destination receipts", and
[ADR-0057](0057-resumable-object-transfer.md), which rejected a
whole-object digest on the transfer and called the absence "the natural
home for a replication receipt".

[ADR-0063](0063-deletion-receipts.md) built the artefact, the carrier and
the reader for the deletion side, and its three choices carry over
unchanged: the destination can sign only under its device key, the
acknowledgement inside the authenticated session is the one carrier the
commander already trusts, and the commander alone knows what it sent. What
a replication receipt adds to that design is one question the deletion
receipt never had to answer: **what may the ledger do with it?** A
signed count of what a peer committed is a statement the peer made, and
two rules this repository already holds say what such a statement must
not be promoted to. FR-VER-001: a destination is *never verified against
itself*. FR-GC-009: replication is judged on what the destination holds,
*never on a record of what was sent*. A receipt is neither a proof of
possession nor a record of sending; it is the peer's own attestation of
what its replica holds after the push, and the ledger may record exactly
that — a figure the peer attested — as long as every surface that shows it
keeps calling it that.

## Decision

1. **The receipt is signed under the destination's device key and rides
   the `ReplicationAck`**, keys 2 (the signed bytes) and 3 (the 64-byte
   signature), present together or not at all, `malformed` otherwise or
   when the receipt does not parse ([03 §3.4](../../specifications/peer-protocol/03-replication.md#34-replicationcomplete-and-replicationack)).
   No feature gates it: an absence can only mean less, which is
   [ADR-0063](0063-deletion-receipts.md)'s argument verbatim, and a peer
   that predates receipts acknowledges with the count alone and is
   treated exactly as every peer was before.

2. **What it attests.** The session the push arrived in, the repository,
   the commanding device's public key, when it was issued; the count of
   objects the push created and those keys in commit order, at most 4096
   listed with the count standing for the rest; and the replica's object
   and byte totals for the repository *after* the session — the inventory
   walk the destination already makes before the transfer, plus what this
   session committed, with no third walk. The encoding is fixed-width and
   label-separated under its own label, the parse is its exact inverse,
   and the receipt's own shape holds that the listed keys do not outnumber
   the count and the held figure is at least the count
   ([03 §3.5](../../specifications/peer-protocol/03-replication.md#35-the-replication-receipt)).
   A receipt is issued on **every** push, one that committed nothing
   included: a statement that nothing arrived is still a signed statement
   of what is held.

3. **The commander checks it whole, in a stated order, and names the
   failure.** The signature first, under the pinned identity of the peer
   this session was opened to. Then the session, so a recording of an
   earlier push is refused however well it verifies; the repository and
   the addressee; the count against the acknowledgement's; every listed
   key against the keys this commander sent this session; and the held
   figure, which can be no smaller than what the destination declared
   before the push plus what it committed during it — a smaller figure
   means something declared has gone since, and is not one to count on.
   The check runs before the exchange goes on to anything else, because
   every push has a receipt to expect and most pushes end there. A
   receipt's absence is a fact, not a fault.

4. **The ledger counts a peer only under a verified receipt, and calls
   the figure what it is.** On a verified receipt the fan-out pass
   records the pair's completeness as held equals owed — everything the
   source's policy owes the peer, summed from the push's own walk, after
   a push in which the peer acknowledged committing everything it lacked
   and attested what it holds. That is the first time the figures are
   written for a peer at all, and it is honest only because the receipt's
   committed count equals what was sent and the inventory declared the
   rest; the record does not call it possession. A peer that sends no
   receipt stays uncounted, as today. A **rejected** receipt raises
   `replication-receipt-invalid:<set>:<destination>`, never withdrawn by
   a later clean pass, files nothing and counts nothing; the objects the
   peer acknowledged committing stand, because refusing the session would
   change nothing at the destination.

5. **One filing, two kinds, one reader.** The envelope both receipt
   stores write gains a `kind`; a kind-less envelope written before this
   record reads as a deletion by where it sits. The deletion store keeps
   its surface over the shared core; the replication store sits beside it
   under `<state>/receipts/replications/<repository>/`; a file of one kind
   found among the other is reported as such, never read as the wrong
   statement. The `receipts` verb on both hosts lists both kinds
   interleaved newest first and narrows with `--kind`; the service answers
   `list_receipts` (contract 1.33) with rows of facts and the service's
   own verdict on each signature over the bytes on disk now, no path and
   no signed or key bytes crossing, to any signed-in role and any caller
   scope — it is an audit listing of what a peer already said under its
   own signature. The console's Maintenance view shows the newest fifty in
   three distinct states, fetched on entering the view and never by the
   pollers.

## Consequences

**Positive.**

- A peer's ring on the console fills from a figure the peer attested,
  where it had read "not counted yet" for ever; the caption still says
  which it is.
- What a push created and what the peer holds afterwards is on record at
  both ends under the peer's own signature, beside the deletion receipts,
  readable by one verb and one card.
- No new message type, no feature, no third walk of the replica, no quota
  change: two additive keys on a message both sides already exchange, and
  counts on walks the destination already makes.

**Negative.**

- The commander holds every key it sent this session in memory to check
  the listed keys against, and the destination holds up to 4096 committed
  keys; both are bounded by the inventory cap already on the wire.
- A concurrent eviction — a peering ended mid-session — can leave the held
  figure below what the inventory declared. The commander's check rejects
  that session's receipt and raises the notice; the next pass issues a
  fresh one. Rare, and stated rather than tolerated, because the
  alternative is a figure the ledger cannot trust.
- The completeness figure is the examined party's statement. It is held
  by the receipt's shape and the commander's arithmetic, not by a read;
  [threat model T-8](../threat-model.md#t-8-destination-withholding-data)
  says so, and possession stays with verification and the drill.

## Alternatives considered

**A whole-object digest on `ReplicationObject`.** The home
[ADR-0057](0057-resumable-object-transfer.md) named. Rejected here for
the same reason it was rejected there, now with the receipt in hand: a
per-object digest would let the destination check an assembled object,
which the session already covers, and it would attest nothing about what
the replica holds afterwards, which is the figure the ledger needed. The
receipt attests the session's outcome, not each object's transit.

**Counting a peer from the push alone.** The commander already knows what
it sent and what the inventory declared, so it could write held equals
owed without a receipt. Rejected: that is a record of what was sent, which
FR-GC-009 says replication is never judged on, and it would count a peer
complete on the strength of a bare count that nothing signed. The receipt
is what makes the figure the peer's statement rather than the source's
assumption.

**A receipt as a possession proof.** Have the destination hash what it
holds, or answer a challenge, inside the receipt. Rejected as
[ADR-0058](0058-peer-write-adapter.md) §8's amendment rejected the digest
challenge: a self-report of a digest is a claim, not a proof, and the
read-back and the drill remain the proofs. The receipt says what it says
and the records do not let it say more.

**A feature-gated receipt.** Rejected as in
[ADR-0063](0063-deletion-receipts.md): an absence can only mean less, so
there is nothing to negotiate and nothing an attacker gains by omitting it.

## What this does not do

- It does not verify anything. A destination is never verified against
  itself (FR-VER-001); the receipt is filed and counted, and the same
  pass's challenge or read-back proves possession as before.
- It does not change what a scheduled sync may delete, and it does not
  touch the deletion receipt's meaning: the two kinds share a filing and a
  reader and nothing else.
- It does not give a local-path destination a receipt. A local path is
  listed and counted directly by the pass; there is no second party to
  attest anything.
- A direct-ship set shipping to a peer through the write adapter
  ([ADR-0058](0058-peer-write-adapter.md)) receives the receipt with the
  run's acknowledgement and reads only its count; the sync pass over the
  same pair is what verifies, files and counts it. Filing from the run is
  a later change to the adapter, not to the receipt.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-09 | Accepted | The follow-up [ADR-0057](0057-resumable-object-transfer.md) named and [ADR-0063](0063-deletion-receipts.md)'s shape carried to the replication side. Built over four commits: the statement, the ack's keys, the shared filing and the store (`Protocol/ReplicationReceipt`, `Protocol/PeerReplicationMessages.cs`, `Protocol/PeerReceiptFiles`, `Protocol/ReplicationReceiptStore`, `Protocol/ReceiptReport`); the destination counting, signing, filing and acking (`Agent/ReplicationResponder`, `Agent/RemoteServiceListener`); the commander verifying, filing and counting the peer (`Agent/ReplicationInitiator`, `Agent/FanOut`); the readers — `receipts --kind` on both hosts, `list_receipts` at contract 1.33 (`Agent/ServiceCommandHandler.Receipts.cs`), the console's Receipts card. Held by `Protocol.Tests/ReplicationMessageTests`, `Protocol.Tests/ReplicationReceiptStoreTests`, `Hosts.Tests/PeerReplicationTests`, `Hosts.Tests/PeerRetentionReplayTests`, `Hosts.Tests/ReplicationReceiptVerificationTests`, `Hosts.Tests/ReceiptsCommandTests`, `Cli.Tests/ReceiptsVerbValidationTests`, `Web.Tests/ConsoleReceiptsScriptTests` and `Web.Tests/CommandRelayTests` |
