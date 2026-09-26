# ADR-0063 — Deletion receipts

**Status:** Accepted
**Date:** 2026-09
**Requirements:** FR-GC-007, FR-GC-008
**Related:** [ADR-0055](0055-reclaim-authority.md), [ADR-0059](0059-session-bound-deletion-authority.md), [ADR-0034](0034-hub-and-spoke-destinations.md), [peer-protocol 06](../../specifications/peer-protocol/06-retention.md), [architecture 07 §5](../architecture/07-retention-and-gc.md#5-destructive-change-safeguards), [threat model T-6](../threat-model.md#t-6-deletion-by-compromised-store-credentials)

**Built:** `Protocol/DeletionReceipt` (the statement, its signing encoding and the parse that is its exact inverse), `Protocol/PeerReplicationMessages.cs` (`RetentionAck` keys 2–3), `Protocol/DeletionReceiptStore` (both parties' filing, and the signature re-checked on every read), `Protocol/DeletionReceiptReport` (the reader's rendering, shared by both hosts), `Agent/ReplicationResponder` (the destination signs, files, then acks), `Agent/RemoteServiceListener` (the issuer: the real session identifier, the device key, the store), `Agent/ReplicationInitiator` (`VerifyReceipt` and the page digests it holds a receipt against), `Agent/FanOut` (the commander files, raises the notice, reports), `Agent/AgentHost` and `Cli/CliApplication` (the `receipts` verb, `--limit` since the amendment); `Protocol/ReceiptRetentionPolicy` (the three numbers) and `Protocol/PeerReceiptFiles` (the sweep over names, and the read bounded before anything is opened), `Agent/ServiceRuntime` (the start-up sweep of every repository of both kinds); `Protocol.Tests/ReplicationMessageTests`, `Protocol.Tests/DeletionReceiptStoreTests`, `Hosts.Tests/PeerRetentionReplayTests`, `Hosts.Tests/DeletionReceiptVerificationTests`, `Retention.Tests/PeerRetentionTests`, `Hosts.Tests/AgentPairingVerbsTests`, `Cli.Tests/ReceiptsVerbValidationTests`, `Protocol.Tests/ReceiptSweepTests`.

---

## Context

`FR-GC-008` promises that retention reduction and bulk deletion "shall
produce signed audit records". The repository plane has had one since
[ADR-0055](0055-reclaim-authority.md): the tombstone, signed under the
reclaim key, naming the condemned object, the reason, the writer and the
eligible generation. The peer plane had none. A destination that acted on a
`RetentionOffer` answered with a `RetentionAck` carrying one `u64` — the
count it deleted — and kept nothing, and the commander discarded even the
count once the ledger row was written. The gap was recorded in the same
words in six places: the requirement's "not yet met" clause, threat T-6's
residual, proof row 69, [architecture 07 §5](../architecture/07-retention-and-gc.md#5-destructive-change-safeguards),
and the register's notes on 0055 and 0059, which said what the artefact
would have to be: *signed under the destination's own device key, since it
holds no repository keys — a different artefact with its own lifetime and
its own reader.*

That description is the whole design problem. A destination cannot sign
under any repository key, so its statement can only be as good as its
device identity — which is exactly the identity the commander already
pinned at pairing and already authenticated at the start of the session
the instruction rode in ([02 §3](../../specifications/peer-protocol/02-session.md)).
The receipt therefore has one natural carrier (the acknowledgement of the
instruction it answers, inside the session that authenticated its signer),
one natural signer (the device key), and one natural check (the commander,
which alone knows what it sent). Everything below follows from choosing
those three.

Two things a receipt is **not**, said before the decisions so they are not
read into them. It is not a proof of possession: it attests what the
destination *says* it deleted, and whether the destination still holds what
it did not delete is verification's job ([04](../../specifications/peer-protocol/04-verification.md),
FR-VER-001). And it is not authorisation: the reclaim signature over the
instruction ([ADR-0055](0055-reclaim-authority.md) §5, [ADR-0059](0059-session-bound-deletion-authority.md))
is what licenses the deletion, and a receipt licenses nothing.

## Decision

1. **The receipt is signed under the destination's device key.** It is the
   destination's statement, and the device key is the only key the
   destination holds that the commander can check — pinned at pairing,
   proved at the session's start. A statement under any other key would be
   one the destination cannot make; a statement under no key would be a
   count with more fields.

2. **It rides the `RetentionAck`, in the same exchange.** Keys 2 (the
   signed bytes) and 3 (the 64-byte signature) on the message that already
   answers the instruction ([06 §4.2](../../specifications/peer-protocol/06-retention.md#42-retentionack)).
   The same session is the only place the commander already trusts the
   signer's identity, so no second channel, no second authentication and no
   retrieval read is needed to believe it. The keys are additive and ungated:
   a destination that predates receipts sends neither, a commander that
   predates them skips both, and an absence that can only mean less needs
   no feature ([02 §6](../../specifications/peer-protocol/02-session.md#6-feature-negotiation)).
   Present together or not at all; one without the other, or a receipt that
   does not parse, is `malformed`.

3. **What it commits to, and what it deliberately does not.** The statement
   names the session the instruction arrived in — the real session
   identifier ([02 §3.5](../../specifications/peer-protocol/02-session.md)),
   whatever the pages' signatures were bound to, because which session an
   instruction arrived in is a fact about the session and not about how it
   was signed; the repository; the commanding device's public key; when it
   was issued; the retention floor in force; the reclaim public key the
   pages were verified against, or that none was held; the SHA-256 of each
   page's signed bytes in the order accepted; the count of keys removed;
   the removed keys themselves, at most 4096, the count and the page digests
   standing for the rest; and how many named keys the destination never
   held. Its encoding is fixed-width and label-separated
   ([06 §4.3](../../specifications/peer-protocol/06-retention.md#43-the-deletion-receipt),
   [00 §4](../../specifications/peer-protocol/00-conventions.md#4-domain-separation)),
   and the parse is its exact inverse — trailing bytes are refused, because
   a reader must never show as attested something the signature does not
   cover. It carries no reasons, no object ids and no manifests, for the
   reason [06 §5](../../specifications/peer-protocol/06-retention.md#5-what-this-does-not-carry)
   gives: the store keys are the only names both sides share.

4. **The commander checks it whole, in a stated order, and names the
   failure.** The signature first, under the pinned identity of the peer
   this session was opened to — nothing else is worth reading until it is
   the peer's. Then the session, so a recording of another exchange is
   refused however well it verifies. Then the repository and the addressee.
   Then the pages, digest for digest in order, so the receipt attests the
   instruction actually sent and not a rearrangement of it. Then the count
   against the acknowledgement's, and every listed key against the drop
   list this commander composed. Each failure is a different lie and is
   reported as such. A receipt's absence is not a failure: the peer is
   older, and "no receipt" is a fact the report states, not a fault.

5. **Both parties file their own copy, and neither depends on the
   other's.** The destination files before it acks; the commander files
   what it verified. One immutable file per receipt under
   `<state>/receipts/deletions/<repository>/`, written atomically, in an
   envelope whose only contribution is bookkeeping — role, filing time, the
   commander's set and destination names — because every fact a reader
   shows comes from the signed bytes after the signature is checked again
   against the recorded signer. A file edited after filing reads as
   unverified, not as something the peer attested. A destination that
   cannot write its copy still acks with the receipt: the deletion has
   happened, and a receipt this side cannot keep is still one the commander
   can (logged, not fatal).

6. **A rejected receipt is a notice, never a refusal.** By the time it
   arrives the deletion has been done; refusing the session would change
   nothing at the destination and would hide the one fact worth a human's
   attention. The commander files nothing, raises
   `deletion-receipt-invalid:<set>:<destination>` — never withdrawn by a
   later clean pass, because a peer that misattested once deserves a look —
   and says so in the granted run's report.

7. **The reader is file-direct.** `receipts --state <dir> [--set] [--repository] [--json]`
   on the agent and the CLI alike, rendered by one shared routine so the two
   print the same thing from the same bytes. It never goes through the
   service: the store is append-only, the verb only reads, and the receipts
   must be readable when the service is not running. A mistyped state
   directory is refused by path rather than answered "no deletion receipts",
   because that answer is the one this verb must never give by accident.

## Consequences

**Positive.**

- FR-GC-008's audit half holds on the peer plane: what a granted run
  destroyed at a peer is on record at both ends, under the peer's own
  signature, bound to the session and the pages, and not only as a count
  somebody once acknowledged.
- The commander's report stops being "converged under the grant" and
  becomes a countable statement — how many objects, which receipt file — or
  a named reason why no receipt could be filed.
- No new message type, no feature, no retrieval change, no quota
  accounting: two additive keys on a message both sides already exchange,
  and a store both hosts can already reach.

**Negative.**

- A receipt is at most 4096 listed keys and 4096 page digests; an
  instruction longer than that is refused before anything is deleted, which
  is a new bound on the wire (16.7 million keys per instruction) that
  nothing approaches.
- Two crash windows on the destination, both harmless and both stated: a
  crash between filing and acking leaves the destination with a receipt the
  commander never saw, and the commander's ledger row records a failed
  exchange it retries; a crash between deleting and filing loses that
  receipt, the deletion is idempotent, and the next instruction produces
  one.
- The receipt attests what the destination *says*. A destination that
  deletes and lies about the keys is caught by the commander's subset check
  only if it lies upward; one that quietly keeps what it reported deleted is
  caught by nothing here, and need not be — that is a destination holding
  more, and possession of what remains is verification's to prove.

## Alternatives considered

**Receipts as replica objects, read back over retrieval.** The destination
would write each receipt into the replica under a reserved prefix and the
commander would fetch it later. Rejected: a namespace the copier, the
convergence, the quota and the heal would all have to learn, counted against
the owner's quota, and — since the responder's deny-list does not cover it —
deletable by the source's own next instruction. The acknowledgement is
already in hand, already authenticated, and already the answer to the
question the receipt attests.

**A receipt per page.** Finer, and it would let the commander act on an
instruction page by page. Rejected because the spoke must not act page by
page ([06 §4.1](../../specifications/peer-protocol/06-retention.md#41-retentionoffer)):
the floor check is over the whole instruction, so the deletion is one event
and gets one statement, with the pages committed to inside it.

**A feature-gated receipt.** Negotiate `deletion-receipt` and require one
when the feature is in force. Rejected for the reason [ADR-0059](0059-session-bound-deletion-authority.md)
gives about gates the examined party may decline: a receipt's absence can
only mean less, so there is nothing to negotiate and nothing an attacker
gains by omitting it that they did not already have by being an older
build. The commander states "no receipt" instead.

**Signing under a repository-derived key held for the purpose.** The
destination would be given a per-repository signing key at attribution.
Rejected: it would give a keyless destination a repository key for the
first time, for a statement whose only reader already trusts the device
key, and it would make the receipt's meaning depend on a key rotation
the destination cannot follow.

## What this does not do

- A destination that deletes on its own — evicting a replica whose peering
  ended, or acting on its operator's own decision — writes no receipt,
  because nothing instructed it. The receipt is a record of *instructed*
  deletion, which is what FR-GC-008 is about.
- Nothing here changes what a scheduled sync may do: it still holds no
  authority to delete ([ADR-0055 Amendment 2](0055-reclaim-authority.md#amendment-2-2026-09--a-write-only-sets-peers-converge-under-the-grant)),
  so it instructs nothing and receives nothing.
- ~~The console does not yet show receipts. The verb is the reader; a card
  is a later slice's, and the `--json` output is what it would read.~~

  > **Amended 2026-09 ([ADR-0064](0064-replication-receipts.md)).** The
  > console shows them: a Receipts card on the Maintenance view over
  > `list_receipts` (contract 1.33), which answers both kinds as facts
  > with the service's own verdict on each signature. The filing this
  > record decided is now the one both kinds share, each envelope naming
  > its kind; a kind-less envelope filed under this record still reads as
  > a deletion.

## Amendment (2026-09) — a receipt is kept for a stated time, and the rule is three numbers

This record gave the receipt a filing and gave the pile no end. One is written
per instruction at **both** ends and nothing removed one, which is a
disk-filling bug wearing an audit trail's clothes — the phrase
[ADR-0057](0057-resumable-object-transfer.md) used about the replication spool
before giving it a sweep. A deletion receipt is kept under a rule of three
numbers, and no fewer:

| | |
|---|---|
| `MinimumRetained` | 8 — the newest, kept whatever their age |
| `MaximumRetained` | 4096 per repository — nothing survives past it, however new |
| `RetainedDays` | 365 — between the two, older than this and it goes |

**No single number is honest.** A count alone discards a year of history from a
pair that exchanges often. An age alone leaves a pair that has gone quiet — a
set deleted, a peering ended, a machine that was away — with nothing recent at
all, which is precisely when its last receipts matter most. The minimum is
therefore what keeps a bound from ever emptying an audit trail, and it is the
clause to read before concluding that a bounded trail is a hole in one.

**A deletion receipt is kept longer than a replication receipt**
([ADR-0064](0064-replication-receipts.md): 8 / 1024 / 90 days). Each one attests
a distinct irreversible act and is FR-GC-008's audit record; a replication
receipt attests what a peer holds *now* and is superseded by the next push's
statement of the same thing. That difference is the difference between an audit
trail and a log, and it is why the two rules are not one.

**The sweep reads names and nothing else.** A receipt's file name has always
begun with its issue time zero-padded to twenty digits, so ordering and ageing
are an ordinal sort over a directory listing with no file opened, no JSON
parsed and no signature checked. Three consequences, each worth having:

- A pile that has grown unreadable is still bounded — a receipt whose bytes are
  corrupt ages out by the same rule as a sound one, rather than being immortal
  for being unparseable.
- A state directory that was copied or restored still ages correctly. A copy
  stamps every file with its own time; age taken from the filesystem would make
  the whole pile look new and quietly stop the bound applying.
- A file whose name is not a receipt's is left alone, because it is not this
  sweep's to delete.

The name earns that without being promoted. It orders and it bounds; it never
describes. Every fact a reader is shown still comes from the signed bytes after
the signature is checked, which is this record's own rule and is untouched.

**It runs in two places, for two different failures.** Filing applies the rule
to the repository directory it has just written into, so a live pair stays
bounded between restarts — the weakness a start-up-only sweep has, which a
service running for months never reaches. `ServiceRuntime.StartAsync` sweeps
every repository of both kinds, which is what reaches a pair that has *stopped*
filing and a pile inherited from a build that had no bound at all. A failure at
either site is logged and swallowed: housekeeping must never fail a push or
refuse to let a service start.

**And the reader is priced by the question.** `list_receipts` took a limit and
applied it after reading and verifying everything, so the console's card
verified every receipt ever filed to show fifty. The limit now reaches the file
names, and only the chosen few are opened; `total` (contract 1.35) says how
many are on file so a client can show what share it is rendering. One
consequence is worth stating, because it is a property and not an accident: a
bounded window over *names* cannot be escaped by a receipt becoming unreadable,
which a window over what still parses could — an unreadable file has no issue
time to sort by and fell to the bottom, so tampering with a receipt used to
remove its row from every bounded view of the audit trail.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-09 | Accepted | Closes FR-GC-008's audit half on the peer plane, recorded as not met since [ADR-0055](0055-reclaim-authority.md) and restated by [ADR-0059](0059-session-bound-deletion-authority.md). Built over four commits: the statement, the ack's keys and the store (`Protocol/DeletionReceipt`, `Protocol/PeerReplicationMessages.cs`, `Protocol/DeletionReceiptStore`); the destination signing, filing and acking (`Agent/ReplicationResponder`, `Agent/RemoteServiceListener`); the commander verifying, filing and reporting (`Agent/ReplicationInitiator`, `Agent/FanOut`); the `receipts` verb on both hosts (`Agent/AgentHost`, `Cli/CliApplication`, `Protocol/DeletionReceiptReport`). Held by `Protocol.Tests/ReplicationMessageTests`, `Protocol.Tests/DeletionReceiptStoreTests`, `Hosts.Tests/PeerRetentionReplayTests`, `Hosts.Tests/DeletionReceiptVerificationTests`, `Retention.Tests/PeerRetentionTests`, `Hosts.Tests/AgentPairingVerbsTests` and `Cli.Tests/ReceiptsVerbValidationTests` |
| 2026-09 | Accepted | Amended by [ADR-0064](0064-replication-receipts.md): the filing is shared with the replication receipts (`Protocol/PeerReceiptFiles`, each envelope naming its kind), the `receipts` verb lists both kinds and narrows with `--kind`, and the console's Receipts card over `list_receipts` closes the "not yet" this record stated |
| 2026-09 | Accepted | Amended: a receipt is kept under a stated retention rule (NFR-OPS-008) — the newest 8 whatever their age, at most 4096 per repository, 365 days between the two — swept from file names alone at filing and at service start, and longer than a replication receipt's because it attests a distinct irreversible act rather than a fact the next push supersedes. The reader is bounded by the same names, so a listing costs what was asked for and a receipt tampered with cannot drop out of a bounded window by becoming unreadable (`Protocol/ReceiptRetentionPolicy`, `Protocol/PeerReceiptFiles`, `Agent/ServiceRuntime`; `Protocol.Tests/ReceiptSweepTests`, `Protocol.Tests/DeletionReceiptStoreTests`) |
