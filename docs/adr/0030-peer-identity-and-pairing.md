# ADR-0030 — Peer identity and pairing: a transport keypair the repository knows nothing about

**Status:** Accepted (amended 2026-08) · Partly implemented — see [implementation status](../implementation-status.md#0030--the-socket-exists)
**Date:** 2026-08
**Requirements:** FR-REP-001, FR-REP-004, NFR-SEC-001, NFR-SEC-004, NFR-COMP-006, NFR-REL-007
**Related:** [ADR-0020](0020-ed25519-signing-key-semantics.md), [ADR-0028](0028-service-boundary-and-deployment-topologies.md), [architecture 09 §3](../architecture/09-replication-and-peers.md#3-pairing), [specification: peer protocol](../../specifications/peer-protocol/README.md)

---

## Context

Phase 2 promises backup to a friend's computer. [Architecture 09 §3](../architecture/09-replication-and-peers.md#3-pairing) states the properties that has to hold — both sides approve, identity is pinned, the destination sets its own terms, and neither party has to trust the other with anything — but names no mechanism. Nothing in `src/` implements any of it, and `specifications/peer-protocol/` did not exist.

It is also load-bearing for something that looks unrelated. [ADR-0028 §5](0028-service-boundary-and-deployment-topologies.md)'s remote binding — a console managing a service across a network — "validates and binds nothing", and the recorded reason is that pairing reuses this machinery. So the console cannot be built until peer identity exists, even though a console is not a peer.

The question this record settles is the one everything else hangs from: **what is a peer's identity, and how do two of them come to trust one another?**

### Why the repository's existing identities cannot answer it

The repository already has three identity-shaped things, and none of them works here.

**The signing key is repository-scoped.** [ADR-0020](0020-ed25519-signing-key-semantics.md) derives it from the master key, so it proves "a holder of the master key at generation *g* produced this" and nothing about which device did. That was the right call for repository state and it is settled. But a destination is *not* a repository member: the whole point of §3's last property is that it holds blobs it cannot decrypt. Authenticating a peer with the repository's signing key would require handing the master key to the friend whose disk you are borrowing, which inverts the entire arrangement.

**`device_id` and `writer_id` are claims, not credentials.** Both are 16 random bytes chosen freely by their holder ([Q13](../open-questions.md#q13--device-level-signature-attribution)). They identify; they authenticate nothing. A peer presenting a `device_id` has proved only that it can read one.

**The passphrase is the human's, and it never leaves the machine that typed it.** [NFR-SEC-009](../requirements/non-functional.md) confines unlocked key material to the service account, and ADR-0028 §9 keeps kit export local for exactly this reason.

So peer identity has to be a fourth thing, independent of repository membership — and that independence is a feature rather than a compromise. It is what lets a destination be someone who is not in your repository at all.

## Decision

### 1. A peer is a public key, held per device, unrelated to the repository

Each device generates a long-lived **peer keypair** on first use and stores the private half in durable local state. [NFR-REL-007](../requirements/non-functional.md) already names "device keypair, pairing grants, destination authorisations" as state that is *not rebuildable from the repository* and must be stored separately — this is the thing that requirement was describing, and it now exists.

The peer identity is the public key. Not a name, not a `device_id`, not a certificate naming either: **the key is the identity**, and everything else is a label shown to a human beside it.

Two consequences worth stating plainly:

- Losing durable local state loses the device's peer identity, and every pairing it held must be redone. That is the same blast radius [`LocalStateSeparationTests`](../../tests/FallbackPlan.Repository.Tests/EndToEnd/LocalStateSeparationTests.cs) already pins for `device_id`, and it is why this state is separated from the disposable catalogue.
- A device's peer identity survives repository re-keying, and a repository generation roll does not disturb pairings. They version independently because they are answering different questions.

### 2. Pairing is a short authentication string over an ephemeral exchange, confirmed by both humans

Pairing runs over a channel neither side yet trusts, so it is authenticated by the two people rather than by any key already shared:

1. each side contributes an ephemeral X25519 share and a nonce;
2. both derive the same secret, and from it a **short authentication string** — the "short authentication words and a QR code" of §3;
3. both humans compare and approve, on their own device;
4. on approval each side **pins** the other's long-lived peer public key, together with the terms of §3 and a human-chosen label.

The string is derived from both nonces and both long-lived public keys as well as the shared secret, so an attacker relaying between two sessions cannot make the strings match. Comparison is what defeats the relay; the ceremony exists to make the comparison happen.

**A changed identity is a hard failure.** §3 says "not a prompt that can be clicked through", and this record takes that literally: a pinned key that no longer matches ends the connection with a stated reason and requires the pairing to be deliberately removed and redone. There is no "trust this once", because that control is only ever used by people who do not know what it means.

### 3. The destination's terms are the destination's, and are re-approved when they change

Quota, storage path, schedule window and retention floor are set by the destination at pairing and travel with the grant. A source may request less and may not request more. When a destination changes its terms, the source is told at the next session and continues under the new ones; when they *narrow*, the source reports the set as degraded rather than silently failing to replicate.

This is a property of who owns the disk, not a negotiation. The person lending space decides how much.

### 4. The pinned key authenticates the channel

Sessions run over TLS 1.3. There is no certificate authority in this design and no name to validate — the pinned key *is* the expected identity, so a certificate is a container for a check that is already exact.

This was first written as TLS 1.3 with raw public keys ([RFC 7250](https://www.rfc-editor.org/rfc/rfc7250)) and X.509 prohibited. See **Amendment 1** — the mechanism changed, the guarantee did not.

Protocol feature negotiation happens after the handshake and is separate from repository format negotiation, because the two version independently ([ADR-0014](0014-format-versioning-and-stability.md), FR-REP-004, NFR-COMP-006).

### 5. The console reuses this, and its policy questions stay open

A console pairing with a service is the same ceremony with the same pinning, which is what unblocks ADR-0028 §5's remote binding.

What it does **not** settle is what a paired console may then *do*. [Q18](../open-questions.md#q18--streaming-restored-content-to-a-remote-client) (may restored content stream to a remote client) and [Q19](../open-questions.md#q19--console-identity-and-multi-operator-access) (whether an action is attributable to a person rather than to the console's device) are product questions about authority, not about identity, and this record deliberately answers neither. Pairing establishes *who is speaking*; those two decide *what they may ask for*.

## Consequences

- The remote binding becomes buildable. It was blocked on this and on nothing else.
- Durable local state grows a private key, which raises its value to an attacker and its cost when lost. It is confined to the service account like the rest of it (NFR-SEC-009).
- A destination needs no repository access, no key material, and no account. It runs a service, approves a pairing, and holds encrypted blobs it cannot read.
- Peer identity is not repository attribution, and this record does not make it so. A paired peer is authenticated as *a device*; what it writes into a repository is still attributed under [ADR-0020](0020-ed25519-signing-key-semantics.md)'s repository-scoped signature. [Q13](../open-questions.md#q13--device-level-signature-attribution) asks whether that should change, and this record neither answers it nor forecloses it — but it does mean the keypair a per-device signing scheme would need now exists, which makes that question cheaper to revisit than it was.

## Alternatives considered

**Derive peer identity from the master key.** Free, no new state, no new loss mode — and fatal: it can only authenticate repository members, so a destination would have to be given the keys to the data it stores. It also makes every device in a repository indistinguishable to a peer, which is precisely ADR-0020's known limitation.

**X.509 with a project-run certificate authority.** A familiar shape with real tooling. Rejected because it introduces an authority this design otherwise does not have, and because there is no name worth validating: pairing already fixes the exact key expected, so a chain adds a weaker check on top of an exact one.

**Trust on first use with no confirmation.** Cheap and common. Rejected because the threat it ignores — an attacker present at the moment of pairing — is the one moment an attacker most wants to be present, and because the recovery story ("your friend's key changed, click continue") is the failure mode §3 explicitly forbids.

**A shared secret typed on both machines.** Simple to explain, and no key exchange to get wrong. Rejected because a human-chosen shared secret becomes the weakest part of the system, and because it gives no way to pin an identity for later sessions — the second connection has the same problem as the first.

## Amendment 1 (2026-08) — authentication moves out of TLS

**§4's mechanism is not implementable on the reference platform, and is replaced. Its guarantee is unchanged.**

RFC 7250 raw public keys are not reachable from .NET: `SslStream`'s authentication surface is certificate-shaped throughout, with no way to supply or validate a bare key. The usual second choice — completing the handshake and binding the application protocol to a keying-material exporter ([RFC 5705](https://www.rfc-editor.org/rfc/rfc5705)) — is also unavailable, as `SslStream` exposes no exporter. Carrying the Ed25519 identity inside a self-signed certificate fails independently: .NET has no Ed25519 certificate support to build or read one with (which is why [ADR-0019 Amendment 2](0019-third-party-dependency-policy.md) vendors the primitive at all), and Schannel does not accept such certificates.

Replacing the platform TLS stack with OpenSSL, BoringSSL, Botan or Bouncy Castle would work and is refused. It means owning a TLS state machine, certificate parsing, native dependencies, cross-platform packaging and a vulnerability-response obligation — a tier-1 blast radius under ADR-0019 for a component whose entire purpose is to be boring.

**So the objective is restated.** It is not to implement RFC 7250; it is to preserve what RFC 7250 was chosen for. TLS becomes an encrypted, *unauthenticated* channel using a self-signed P-256 certificate generated per connection and discarded with it. No trust decision happens during the handshake. Authentication moves into the protocol: each side signs, with its permanent Ed25519 identity, a role-separated transcript binding both peer identities, both nonces, and the SHA-256 of both sides' TLS `SubjectPublicKeyInfo`. A man in the middle terminates TLS with its own certificates, so the transcript it would need to produce is not the one either genuine peer signed, and it holds neither private key. Both sides refuse.

The normative invariant:

> A session MUST NOT enter the authenticated state until the peer has demonstrated possession of the expected Ed25519 private key by signing a fresh, role-bound transcript that cryptographically binds that permanent identity to the current TLS connection.

Two things this buys beyond restoring the guarantee. Trust becomes a property of the protocol rather than of a platform TLS feature, so a future .NET that gains raw public keys or an exporter can strengthen the binding without changing what a session *means*. And the session's states become explicit — `Connected`, `Encrypted`, `Authenticated`, `Open` — because `Encrypted` is the state that looks finished and is not, and an implementation that mistakes a completed handshake for an authenticated peer has a stranger inside the protocol.

The cost is honest and small: two message types, one round trip that overlaps in both directions, and an ECDSA P-256 keypair per connection that authenticates nothing. The pairing ceremony is untouched — its short authentication string is computed over the X25519 agreement, so a man in the middle runs two different exchanges and the two humans read different strings, exactly as before.

Specified in [peer-protocol 02 §1–§3](../../specifications/peer-protocol/02-session.md#1-transport).

## Amendment 2 (2026-08) — the pairing lifecycle completes: roles on the wire, endings announced, terms enforced

[ADR-0034](0034-hub-and-spoke-destinations.md)'s hub-and-spoke shape asks three
things of the peering machinery that the first implementation deliberately
deferred, and this amendment commits to them. The identity model of §1–§2 and
the channel construction of Amendment 1 are untouched throughout.

**The role is negotiated in the ceremony, not assumed afterwards.** Pairing
today establishes *who* each side is; which way data flows is recorded locally
by each side, unauthenticated and uncoordinated — both shipped verbs simply
assume "they store for us". The direction of storage is part of what the two
humans are approving, so it joins the ceremony: the offer proposes a role, the
acceptance confirms or refuses it, and the bytes enter the authenticated
transcript so the two grants cannot disagree about which of them lends the
disk. This is a ceremony version change; pre-existing pairings do not carry a
negotiated role and must be redone, a break sanctioned pre-1.0 and stated
plainly by the refusing side.

**Either side may end the peering, and the ending is announced.** Revocation
was local-only by explicit design (peer-protocol 01 §3), which is right as the
*mechanism* — no protocol round-trip can be a precondition for withdrawing
consent — and incomplete as the *experience*: the other household discovers the
ending as unexplained refusals, or never. A best-effort **termination notice**
now travels when the ender can reach the peer (feature-gated, so an older peer
that cannot understand it is simply not sent it), and the long-idle refusal
reason `Revoked` — defined from the start and never sent — becomes the fallback
signal for the dialler that was unreachable when the ending happened. Both
paths land as a durable notice the user sees in `status` until acknowledged: a
spoke learns "this hub will stop sending; the data you hold for it is yours to
evict after the grace period", a hub learns "this destination is gone —
reconfigure the sets that counted on it". Eviction remains the storing side's
own act, on its own timetable.

**The terms of §3 stop being decorative.** Quota, window and retention floor
travel on the wire today and are enforced nowhere. The destination now refuses
storage past its granted quota with the refusal vocabulary the protocol already
reserves for exactly this, distinctly from disk-full and from transient failure
— the lender's disk is bounded by the lender, mid-transfer, not by the
borrower's good manners. And §3's narrowing rule gets its mechanism: terms in a
session hello narrower than the grant surface as a durable notice and a
degraded set, never as silent non-replication.

## Amendment 3 (2026-08) — TLS 1.2 accepted alongside 1.3

One supported platform's TLS stack cannot speak TLS 1.3 at all — its `SslStream` refuses the protocol outright — and negotiation needs both ends to overlap, so a build that offered only 1.3 would exclude that platform from peering with anybody, not merely from 1.3. Sessions now accept **TLS 1.2 or 1.3** on every platform ([spec 02 §1](../../specifications/peer-protocol/02-session.md#1-transport)): two 1.3-capable ends still land on 1.3 under RFC 8446's own downgrade protection, and nothing this decision guarantees derives from the version — identity was moved out of TLS by Amendment 1, and the channel-bound proof reads the exchanged certificates identically under both.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | Written with the peer-protocol specification's pairing and session documents; nothing implemented yet |
| 2026-08 | Amended | Amendment 1: RFC 7250 found unreachable on the reference platform; authentication moved from TLS into the protocol, guarantee preserved |
| 2026-08 | Accepted | The decision stands after Amendment 1 rebuilt its mechanism: a transport keypair the repository knows nothing about, proven by a channel-bound signature in the protocol rather than by TLS. It is now built and carried over a real TLS socket, including a man-in-the-middle test the construction defeats and a pairing ceremony performed by two real processes; a paired console reaches the service and an unpaired one is refused. The [implementation status](../implementation-status.md#0030--the-socket-exists) says what remains — peer replication itself (specs 03–05). |
| 2026-08 | Accepted (amended) | Amendment 2: the role joins the authenticated ceremony, endings produce notices with `Revoked` as the fallback signal, and the destination's terms are enforced at its own edge ([ADR-0034](0034-hub-and-spoke-destinations.md)). |
| 2026-08 | Accepted (amended) | Amendment 3: TLS 1.2 accepted alongside 1.3 — one supported platform's stack has no 1.3, and no guarantee here rests on the version. |
