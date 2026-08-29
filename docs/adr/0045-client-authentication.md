# ADR-0045 — Client authentication: the channel says which process, a session says which person

**Status:** Accepted
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

> **Amended 2026-08 ([ADR-0049](0049-service-lifecycle-hygiene.md)):**
> restarting the service joined account management as the owner's second
> exclusive privilege — a restart interrupts every run and signs everybody
> out, which is not an operator's call to make.

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

## Amendment (2026-08): what the build changed about §5

Two things in §5 were wrong in a way only implementing them showed.

**"The session rides the connection" cannot be taken literally.** The web
console opens a fresh transport connection for every request it relays, so a
session welded to one connection would sign an operator out between clicking
two buttons. The decision's own mechanism is what survives: the service mints a
token, holds it in memory, and a connection *presents* it. A third verb,
`resume_session`, does the presenting, and it joins `describe_service` and
`login` on the unauthenticated surface. Nothing else about §5 changes — no
field on `ServiceCommand`, no existing verb touched, and still nothing written
to disk by the service.

**Enforcement engages only once an installation has accounts.** Refusing every
verb without a session, applied literally, bricks every existing installation
on upgrade: there is no `users.json`, so there is no way in and no way to create
the first account. So an installation with no accounts behaves exactly as it did
before, and the gap is *surfaced* rather than left silent —
`describe_service` reports `users_required` once setup is otherwise finished.
New installations never see that state, because setup now captures the first
account (§6, phase E).

**The browser holds the console's session, not the console.** A console process
that cached one token would make every action attributable to whoever signed in
first, which is this ADR's problem moved one hop rather than solved. The token
travels on an `X-FallbackPlan-Session` header per request, separate from the
console's own bearer token, which answers the different question of whether that
browser may talk to that console at all.

## Amendment (2026-08): the CLI caches a token, and why that is not §5's token file

The service writes no session down. A *client* has to, or every command would be
a login, and a command line is not a place to type a password once per verb. So
`fallbackplan login` caches the token owner-only at `<state>/session.json`.

That looks exactly like the thing [ADR-0028](0028-service-boundary-and-deployment-topologies.md) §5
rejected, and the difference is worth being precise about. §5's objection was to
a credential needed to **reach** the service: a stale one made a running service
unreachable, with no way for the user to tell what was wrong. This token reaches
nothing — the socket's permissions are still the whole of the connection check —
and when it is stale the answer is a sentence naming `fallbackplan login`.

## Amendment (2026-08): six members are carved out of NFR-SEC-009 by name

`Api.Tests/KeyMaterialConfinementTests` bans the fragments "password" and
"token" from every contract member, because a *passphrase* must never cross the
surface and those are the words somebody would reach for. Six new members trip
it, and the ban is not relaxed: `LoginCommand.Password`,
`CreateUserCommand.Password`, `ChangePasswordCommand.CurrentPassword` and
`.NewPassword`, `ResumeSessionCommand.Token` and `SessionResult.Token` are named
one by one, in the same shape ADR-0042 used for sealed envelopes.

The argument for each is the same: NFR-SEC-009 is about **key material** —
things that derive a repository key, open an archive, or mint access. A person's
password is none of those, and it must cross because there is no other way to
prove who is acting. A second test asserts every carved-out name still exists
and is still a string, so the list cannot silently become a hole a future field
falls into.


## Amendment (2026-08): a lapsed session is answered once, not retried forever

A day of real service log showed what §5 left unsaid: what a client must *do*
when the session it holds lapses. A machine slept through the eight-hour idle
timeout; on wake, the console page kept its dead token, kept its pollers, and
kept its `EventSource` — 581 doomed `resume_session` presentations and a fresh
progress watch every two seconds, for sixteen minutes, with the sign-in screen
already on display. Four rules close that loop, none changing the contract:

**The console ends an exchange at a refused resume.** The refusal names the fix
("log in again") where the command's own refusal, sent blind afterwards, would
not — and forwarding the command doubled the traffic of an already-failing
loop. `Refused` specifically: a pre-1.16 service answers `resume_session`
itself with `InvalidArgument`, and that stays the shrug it always was.

**The event stream presents the session too.** `EventSource` cannot set a
header, so the session rides the query as the console's own token already does
(ADR-0036 §4). Without it every watch on an installation with accounts is
anonymous, and the gate's empty answer plus the browser's redial is the
two-second loop above. A refused stream session is answered honestly: a named
`session` event the page reacts to, a thirty-second retry hint for a page that
does not, then the stream ends.

**The page forgets a dead token.** On a "not current" refusal it drops the
stored session, closes the stream, and stands the sign-in screen up; the data
pollers pause behind that screen, and a thirty-second `describe_service`
heartbeat — answerable without a session — is what keeps the page noticing the
service while nobody is signed in.

**The gate says why, in its own log.** The whole incident was reconstructed
from generic "answered ServiceError" listener lines, because the gate refused
silently. A refused resume, a successful resume, and a command refused for
want of a session now log by name (3755–3757; the token, like the password,
has no parameter to ride in).

## Amendment (2026-08): the password policy gains composition, and the ceremony creates the owner

The password floor rises from eight to ten, and the passphrase's composition
rules join it: at least one uppercase letter, two digits, and one special
character. The policy lives in `Domain/Configuration/PasswordPolicy` and is
enforced at the one chokepoint every creation path crosses —
`UserStore.Create` and `ChangePasswordAsync` — so the console's bootstrap
`create_user`, the headless setup verb's `--password-env`, and any future
caller answer identically. Like the passphrase policy it applies where a
password is *chosen*, never where one is presented: a login verifies against
the stored hash whatever rules were in force when the password was set, so
tightening strands nobody outside their account. The original rationale for
the floor sitting below the passphrase's stands — a password is changeable
and throttled where a passphrase is neither — the number is just no longer
eight.

The console's first account also moved: it is now the setup ceremony's final
step (ADR-0044's second amendment) rather than a bare sign-in gate the
operator lands on afterwards, with the account policy rendered live as a
checklist and a rule the store cannot check — the password must not be the
installation passphrase — enforced client-side by hash comparison, since the
service never holds the passphrase to compare. The sign-in gate's
create-first-account mode remains for a service that reaches
`users_required` outside the ceremony.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | Written with the storage location, client scope, session mechanism, owner rule, failure policy and session lifetime all fixed by the user: service state directory, console and CLI both, a service-minted token, first user is the owner, throttle but never lock, and sessions that live only in the process. Build sequenced as the primitive → the store and its policy → contract 1.16 with the per-connection gate → setup captures the first account → the clients → drills |
| 2026-08 | Accepted | Built end to end: the password primitive in Repository.Crypto, the account store with the owner rules and a throttle that never locks, contract 1.16's seven verbs behind a per-connection decorator, setup capturing the first account, and the CLI and console signing in. Three amendments above record where implementation corrected the decision — the session is presented by a connection rather than owned by one, enforcement engages only once an installation has accounts, and the browser rather than the console holds a viewer's session |
| 2026-08 | Amended | A lapsed session is answered once: the console short-circuits a refused resume, the event stream presents (and reports) the session, the page forgets a dead token and pauses its pollers behind sign-in, and the gate logs its refusals (3755–3757). Driven by a real wedge — 581 doomed resumes and a watch every two seconds across sixteen minutes of service log |
| 2026-08 | Amended | The password policy gains composition — a floor of ten with an uppercase letter, two digits and a special character, enforced in `UserStore` where every creation path converges and never at login — and the first account becomes the setup ceremony's final step, with a hash-compared must-not-be-the-passphrase rule the service itself cannot check |
