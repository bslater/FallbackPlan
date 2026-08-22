# ADR-0045 — Client authentication: the channel says which process, a session says which person

**Status:** Proposed
**Date:** 2026-08
**Requirements:** FR-USR-001, FR-USR-002, FR-USR-003, FR-USR-004, FR-USR-005, FR-USR-006, NFR-SEC-012
**Related:** [ADR-0028](0028-service-boundary-and-deployment-topologies.md), [ADR-0036](0036-local-web-console.md), [ADR-0042](0042-write-only-repositories.md), [ADR-0044](0044-first-run-setup.md), [threat model](../threat-model.md), [architecture 00 §6.1](../architecture/00-overview.md#61-process-model)

---

## Context

The product cannot say **who** is using it.

The web console's authority is a bearer token minted per run and printed to
the terminal that started it. Everyone holding that URL is indistinguishably
"the operator": a restore is attributable to nobody, and no single person can
be revoked without restarting the service and redistributing the token to
everyone else. The CLI over the local socket is authenticated by the operating
system, which identifies a *uid* — a fact about a process, not about a person,
and one that several people can share on a machine with a shared account or a
shared administrator role.

That was a defensible place to stop while the product had one operator. It
stops being defensible as soon as a household or a small team shares an
installation, and it is already awkward for the audit story: FR-GOV and the
threat model both speak of who did what, and the answer today is always "the
operator".

**This is not a new door.** The channel checks do not change and no new
listener appears. What is added is person-identity *inside* a channel that is
already authenticated.

## Decision

### 1. Identity is a person, inside an already-authenticated channel

Three checks now stack, and they answer three different questions:

| Check | Question | Mechanism | Unchanged by this ADR |
|---|---|---|---|
| Local binding | which *process* may connect | OS filesystem permissions on the socket ([T-16](../threat-model.md)) | yes |
| Remote binding | which *device* may connect | pinned pairing ([T-20](../threat-model.md), FR-SVC-004) | yes |
| **Session** | which *person* is acting | this ADR | new |

A password is never sufficient on its own and never reaches a service that the
channel checks would have refused. Someone who learns a password still needs a
process the OS admits, or a device the service has pinned.

### 2. ADR-0028 §5 is amended, not reversed

[ADR-0028 §5](0028-service-boundary-and-deployment-topologies.md#5-two-transport-bindings-one-command-contract)
says of the local binding: *"No password, no token file, no port."* It rejects
loopback-TCP-plus-server-password on prior art — a user made to manage a
credential to talk to their own machine, and a token file whose staleness is a
familiar failure mode.

Both objections stand and this decision does not touch them. §5 answers
*which process may connect*, and the answer is still the operating system: no
port, no token file, no credential needed to reach the socket. This ADR
answers *which person is acting* once connected, which §5 never addressed
because there was nothing yet that could tell two people apart.

The distinction is load-bearing, so it is stated in the terms §5 used:

- **No new port.** The bindings are exactly the two §5 defines.
- **No token file.** A session lives in the service's memory and is never
  written down, which is the precise failure mode §5 rejected. See decision 5.
- **No credential to reach the machine.** The socket's permissions are still
  the whole of the connection check; the password authorises a *person* to act,
  not a *process* to connect.

### 3. Storage: the service's own state, never the metadata plane

`<state>/users.json`, owner-only, written through the existing atomic write,
beside the installation credential and the kit confirmation.

Not the repository metadata plane, and the reason is not stylistic: metadata
replicates to destinations. A credential file there would be copied to every
peer and every local-path replica by the ordinary fan-out, and the write-only
model already concedes metadata readability to whoever holds the service
account ([T-19](../threat-model.md)). Account credentials must not be part of
what a destination receives.

### 4. The primitive lives where Argon2id already lives

`PasswordHash` goes in `FallbackPlan.Repository.Crypto`, which is not where a
reader would first look for it.

`DependencyRuleTests.ThirdPartyCryptography_EveryAssemblyOffTheAllowlist_ReferencesNone`
confines `Bodu.Security.Cryptography` to an allowlist of exactly two assemblies
and says that adding a third "requires an argument". Rather than make that
argument to put a password hash in the Agent, the primitive goes where
Argon2id is already used and cross-verified against published vectors on every
CI run — the same derivation shape as `KekDerivation`, with a per-account salt
and its parameters encoded beside the hash — and the **Agent owns the store and
the policy**. That is the same split `KekDerivation` already has: the primitive
is crypto, the policy is not.

The cost is stated rather than hidden: the standalone recovery tool links a
class it never calls. That is a few kilobytes in the assembly
[NFR-PORT-001](../requirements/non-functional.md) keeps smallest, against
widening a deliberately narrow allowlist. The narrower allowlist is worth more.

**And a rule inherited from ADR-0043's amendment holds here too:** no message,
no result and no diagnostic in this product takes a password or a password
hash as a parameter. The safest way to honour "no secrets in logs" is to leave
no parameter one could be passed through.

### 5. The session rides the connection, and lives only in memory

A client authenticates once after connecting; the connection is authenticated
for its lifetime. Verification is memory-hard by design, so paying it per
command would be a self-inflicted denial of service —
[NFR-SEC-012](../requirements/non-functional.md) states that as a requirement
rather than leaving it as an implementation habit.

The session token is minted by the service, opaque to the client, carries an
idle timeout and an absolute expiry, and is **listable and revocable**. It is
held in the running process and **never written to disk**. Three things follow,
and all three are the point:

- There is no session file to steal, and no expiry sweep to get wrong.
- A restart logs everyone out — which is a blunt but comprehensible revocation
  story, and the one an operator expects from a service they just restarted.
- It does not re-create the copyable local credential ADR-0028 §5 rejected.

Mechanically this needs no field on `ServiceCommand` and no change to any
existing verb. `LocalServiceListener` today hands one shared service instance
to every connection's pump; both listeners take a **factory** instead, and each
accepted connection gets a decorator holding that connection's session. An
unauthenticated connection may call exactly two verbs: `describe_service`,
because a login screen has to render and needs to know whether setup is even
complete, and `login`.

### 6. The first account is the owner

Every account has equal rights over backup, restore and configuration. What the
owner has is a floor: **it cannot be deleted and cannot be locked out**, and
initially only the owner may create or remove accounts.

The role field exists from the first release even though there is only one
role's worth of behaviour, so that per-account rights later are a value change
rather than a file-format migration. A stored account written today reads
correctly when narrower roles exist tomorrow.

### 7. Throttle, never lock

Each consecutive failure for an account adds an increasing delay, capped at a
few seconds, and is logged at Warning with the account named. Nothing ever
locks.

That is a deliberate departure from the reflex. Argon2id already makes guessing
expensive per attempt; the delay makes sustained guessing hopeless. A lockout
would add almost nothing against an attacker and would hand anyone who knows a
username a way to deny a person access to their own backups by failing on
purpose. **For a backup product, being locked out of your own data at the
moment you need it is the worse outcome**, and it is worse by a wide margin: the
failure this product exists to survive and a lockout tend to arrive on the same
day.

Combined with decision 6 this is also what makes "the owner cannot be locked
out" cheap to honour — nobody can be locked out, so the owner's guarantee is a
consequence of the policy rather than a special case in it.

### 8. Two carve-outs, stated explicitly

Both are places where adding a password would be wrong, not places where it was
forgotten:

- **`fallbackplan-agent` *is* the service**, not a client of one. Scheduled
  backups run with nobody present and must keep doing so. A service that needed
  a person to log in before it would back up would not be a backup product.
- **`--direct` holds the installation passphrase**, which is a strictly
  stronger credential than any password: it derives the keys. Requiring a
  password as well would be asking for a weaker secret in addition to a
  stronger one.

Scripts that drive the service as a client name a password by environment
variable (`--password-env`), never on a command line, matching the rule the
passphrase already follows.

## Consequences

- Every client gains a login step, including the local CLI. That is a real cost
  in convenience, paid so that a restore has a name against it.
- Restarting the service logs everyone out. Deliberate, per decision 5.
- The contract goes to **1.16**. An older client meeting a service that requires
  a login is refused by name on its first command, through the existing
  refusal path, rather than hanging.
- The recovery tool links a password-hash class it never calls (decision 4).
- Nothing in the threat model's channel entries changes; T-16 and T-20 gain a
  sentence each recording that a channel check now admits a *process* or a
  *device* rather than a person, and which check does the rest.

## Alternatives considered

**A shared service password, as prior art does.** Rejected for the reason
ADR-0028 §5 already rejected it, and for the reason this ADR exists: a shared
secret cannot attribute an action to a person or revoke one person, which is
the entire problem.

**OS user identity as the person identity.** The local socket already reads
peer credentials, so a uid is available for free. Rejected because it does not
generalise: a console reaching a service over the remote binding has no uid on
that machine, a shared administrator account is several people, and the product
would then have two different identity models depending on which binding a
client arrived on.

**Sessions persisted to disk, surviving a restart.** Convenient, and rejected:
it re-creates exactly the stale-token-file failure ADR-0028 §5 names as prior
art's most familiar problem, and adds an expiry sweep and a file worth stealing.
The cost — logging everyone out on restart — is comprehensible in a way a stale
token is not.

**Per-command authentication.** Simpler to reason about and rejected on cost:
a memory-hard derivation per command would make a directory listing take as long
as a login by design.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | Written with the storage location, client scope, session mechanism, owner rule, failure policy and session lifetime all fixed by the user: service state directory, console and CLI both, a service-minted token, first user is the owner, throttle but never lock, and sessions that live only in the process. Build sequenced as the primitive → the store and its policy → contract 1.16 with the per-connection gate → setup captures the first account → the clients → drills |
