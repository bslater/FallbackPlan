# ADR-0028 — The service boundary: deployment topologies, process ownership, transport, and unlock

**Status:** Accepted (amended)
**Date:** 2026-08
**Requirements:** FR-SVC-001..008, NFR-SEC-009, NFR-OPS-005, NFR-OPS-006, NFR-PORT-004
**Related:** [architecture 00 §6.1](../architecture/00-overview.md#61-process-model), [architecture 04](../architecture/04-concurrency-and-publication.md), [architecture 10](../architecture/10-observability.md), [architecture 11](../architecture/11-solution-structure.md), [ADR-0010](0010-local-store-separation.md), [ADR-0027](0027-services-scheduling-status-telemetry.md), [threat model T-16/T-18](../threat-model.md)

---

## Context

FallbackPlan's committed shape is a headless engine with thin front ends —
`00-overview.md` §4.1 lists "command-line interface · local web administration ·
headless service operation" as first-release scope, and §6.1 describes Desktop as
"connecting to the local Agent" and Web as "hosted by the Agent". That is the
same shape the consumer prior art arrived at, and it is the shape the product needs.

It has never been designed. The whole of it is §6.1's eight-row table, carried
verbatim from the superseded original proposal, plus two cells in `11 §5` naming
ASP.NET Core and gRPC. `FallbackPlan.Api` appears in the project layout and in
ADR-0019's dependency tiers and is scheduled in no phase of the roadmap.

Meanwhile [ADR-0027](0027-services-scheduling-status-telemetry.md) built
something different: the CLI and the Agent as **peer hosts over a shared
`Application` library**, each opening the repository and the state directory
directly. The phase-1 plan's P2-H makes it explicit — the CLI `status` command
reads "the same derivation" rather than asking the Agent. Two architectures are
now written down and nothing says which governs.

This would be a tidiness problem if the peer-host model were merely inelegant.
It is not: **it is unsafe today, and its symptom is a security alarm.**

Writer identity is per state directory (`LocalState.WriterId`), and the state
directory defaults to one path per repository. Any two processes pointed at it —
an Agent on its poll loop and a user running `fallbackplan backup` — are
therefore *the same writer* by construction, drawing from one sequence space
that architecture 04 §2 requires to be monotonic and gapless. Nothing enforces
that across processes:

- `WriterSequence`'s guard is a `System.Threading.Lock` — an in-process monitor.
- Its constructor loads `sequence.txt` once and never re-reads it, so two
  instances hand out the same numbers.
- `FileSequenceStateStore.Save` writes through a fixed temp name, so two saves
  collide.

The damage escalates. Two writers on one counter collide on a blob spool path
(`BlobId` is derived from writer + counter). Worse, they lose a write intent
silently: `LocalFileSystemObjectStore.PutAsync` returns `AlreadyExists` for an
existing key *regardless of conditions*, while `JournalStore` and
`IndexPublisher` only treat `PreconditionFailed` as failure — so the second
process's intent is never written and it proceeds believing it is durable,
voiding the guarantee the entire publication order rests on (C4). Worst, the
second process reads the first's *live* pending sequences as crash leftovers and
publishes void deltas for numbers the first is still using — durable index-plane
damage carrying a valid signature.

And per 04 §2 and **T-18**, a duplicate or regressing sequence means *identity
cloning*: "a security alert rather than a log line". A user running two commands
at once would trip the alarm built to detect a stolen device key.

The local stores have no better story: the catalogue sets WAL but no
`busy_timeout` (a second writer gets `SQLITE_BUSY` immediately, with no retry
anywhere), holds a single unsynchronised `SqliteConnection`, and its rebuild path
calls process-global `ClearAllPools()` then deletes a database another process
may hold open; `state.json` and `jobs.json` are non-atomic whole-file rewrites
where the last writer wins.

None of this is a criticism of ADR-0027, which was correct for a world with one
process at a time. It is what changes when a service must run continuously while
a user still has a CLI.

Four installation shapes are required, and they decide the transport question
rather than following from it:

1. **Everything on one machine** — service plus a front end, the ordinary
   single-computer install.
2. **Service only** — headless, backing that machine up to a configured target,
   with no UI installed at all.
3. **A web front end managing several services** — one console, many machines.
4. **A client only** — talking to a service on *another* machine.

Shapes 3 and 4 make remote management a first-class topology rather than a later
addition, and they carry a consequence worth stating before the decision: a
console that administers other people's machines is a high-value target, and the
one thing it must never become is a way to read their files.

## Decision

### 1. Four topologies, one service and one contract

There is exactly one service implementation and one command contract; the
topologies differ only in what is installed and which transport is enabled.

| Topology | Service | Front end | Transport |
|---|---|---|---|
| All-in-one | local | local app or web | local binding only |
| Service only | local | none | local binding only (remote off) |
| Multi-instance console | one per managed machine | one web console, elsewhere | remote binding on each managed service |
| Client only | none locally | CLI or app | remote binding to a named service |

The **service is authoritative for its own machine and nothing else**. A console
in topology 3 is a client of *N* independent services; it is not a controller
that owns them, holds their keys, or brokers between them. Each service
continues to back its own machine up unattended whether or not the console is
running, whether or not it is reachable, and whether or not it still exists —
the property `00-overview.md` §6.1 already states as "the Agent must remain
fully functional without Desktop, Relay, Discovery, or any project-operated
service", now extended to the console.

Every topology is a composition of the same two questions — is a service
installed here, and is its remote binding enabled — so there is no topology-
specific code path and no "console build" of the service.

### 2. The service owns the local writer role exclusively

**While the service is running it is the sole holder of the repository writer
identity, the state directory, and the catalogue for a given repository.** Every
other local process is a **client**: it sends commands and receives results, and
never opens `sequence.txt`, `state.json`, `jobs.json`, `catalogue.db`, or the
spool.

This is chosen over making each shared store multi-process-safe. Cross-process
locking would have to be added to the sequence file, the catalogue, both JSON
stores and the spool directory *independently*, and each would have to be right
forever; single ownership makes the entire class of failure unrepresentable, and
leaves `WriterSequence`'s in-process lock correct as written.

The scope of the rule is deliberately narrow — **one local writer per
repository**, not one writer globally. Architecture 04 §1's direct-store mode,
in which many devices write one repository concurrently with no coordination,
is unchanged and remains the design's centre of gravity. §2's writer is a
*device*; this ADR says a device's writer role is held by exactly one *process*.

### 3. The CLI becomes a client, with an explicit direct mode

The CLI connects to the service when one is running. Every command that reads or
mutates repository or job state is served by the service.

When no service is running, the CLI may operate **in direct mode**, taking the
writer role itself for the duration of the command. Direct mode is not a
fallback that happens silently: it is entered only under the exclusion mechanism
of §3, and the CLI reports which mode it used, because "did my backup run
against the same state the Agent uses" is a question an operator must never have
to guess at.

`FallbackPlan.Recovery` is untouched and remains outside this rule entirely. It
speaks to no service, opens no state directory, and reads only the repository
plus a kit — the clean-machine premise of NFR-OPS-005 and the reason 11 §2 pins
its dependency closure. A recovery tool that needed a running service would not
be a recovery tool.

### 4. Exclusion is a lock on the state directory, not on the repository

A single **writer lock** in the state directory (an OS-level advisory file lock
held open for the lifetime of the writer role) admits exactly one holder. The
lock is on the *state directory* because that is what defines the writer
identity; the repository itself stays lock-free, preserving 04 §8's "no routine
operation requires a global exclusive lock".

- The service acquires it at startup and holds it until it stops.
- A CLI in direct mode acquires it for the command and releases it after.
- A client that cannot acquire it and finds no service to talk to **fails with a
  stated reason**, naming the holder. It never proceeds anyway.
- Because the lock is an open file handle rather than a recorded PID, the
  operating system releases it when the holder dies. Crash recovery therefore
  needs no stale-lock heuristic and no timeout — the failure mode that makes
  PID-file schemes unreliable.

### 5. Two transport bindings, one command contract

**The local binding is always present. The remote binding is off until enabled.**

**Local** — a **Unix domain socket** (POSIX) or **named pipe** (Windows), created
in a directory only the service account may write. Authentication is the
operating system's: filesystem permissions decide who may connect, and the
service reads peer credentials to identify the caller. No password, no token
file, no port. Topologies 1 and 2 use nothing else.

Loopback TCP is rejected *for the local binding*, on the prior art. One
widely-deployed desktop client authenticates to its own service with a token
file, and a stale or unreadable token is among its most familiar failure modes
— the UI insisting it cannot reach an engine that is running perfectly well.
Another listens on a fixed loopback port behind a server password, so any local
process may attempt to connect and the user must manage a credential to talk to
their own machine. Both are artefacts of using a network transport for a boundary that
is not a network; a socket with OS permissions has neither problem, because the
authentication is the same mechanism already protecting the state directory.

**Remote** — TLS over TCP, **mutually authenticated by device identity**,
disabled by default and enabled per service by an explicit administrative act
that names the interface it binds. Topologies 3 and 4 use it.

Remote clients are **paired, not passworded**, reusing the machinery
architecture 09 §3 already defines for peers rather than inventing a second
credential system:

- Both sides display the same short authentication string and a QR code, and both approve. (Written here as "words"; [peer-protocol 01 §2.3](../../specifications/peer-protocol/01-identity-and-pairing.md) settled it as six base32 characters, for the reasons recorded there.)
- The identity is **pinned on approval**; a changed identity is a hard failure
  requiring explicit re-approval, never a prompt that can be clicked through.
- Approval is revocable at the service, which is the party at risk.

This is the same reasoning that made pairing right for peers: neither side has
to hold a shared secret, phishing a password gains nothing, and revocation is
local to the machine being protected. A console in topology 3 therefore holds
*N* pairings, one per managed service, and each service can revoke it alone.

### 6. What crosses the boundary: control and status always, plaintext never by default

A remote client may **command and observe**. It may not, by default, **receive
file content**.

- Commands, results, status and progress events cross either binding freely.
- A restore commanded remotely writes its output **on the machine running the
  service**. The console is told what happened; it is not sent the files.
- Streaming restored content to a remote client is a separate, explicitly
  enabled capability with its own approval, because it converts a management
  console into a path by which every byte of every backup can leave the machine.
- Key material never crosses either binding in any direction, under any setting
  (§9).

Without this rule the console is a plaintext exfiltration point wearing an
administrative hat, and the property architecture 09 §3 is proud of — "a
destination cannot read source content" — would be undone from the front rather
than the back. A remote operator can therefore restore a colleague's laptop
without ever being able to read it, which is the distinction that makes
multi-machine administration safe to offer.

### 7. The command surface is versioned, and is not the peer protocol

The client↔service surface is a **distinct contract** from the peer replication
protocol of architecture 09, versioned independently. ADR-0003 already
anticipates this: repository encoding is canonical CBOR, while "wire protocols
are versioned independently and may use a different encoding".

The surface is specified in terms of **commands, results, and an event stream**,
not in terms of a transport binding:

- **Commands** are the operations a front end invokes: enumerate and modify
  backup sets, run or cancel a job, list snapshots, list a directory, plan and
  execute a restore, verify, export a kit, and report status.
- **Results** are explicit outcome types, never exceptions crossing the boundary
  (NFR-PORT-004).
- **Events** carry job progress. This is new capability, not a projection of
  something that exists: `IPublicationObserver` is nine payload-free callbacks
  serving the interruption harness, and `EngineDiagnostics` is job-anonymous by
  enforced policy (NFR-PRIV-002), so neither can feed a UI. Architecture 10 §3's
  job state machine is the vocabulary; ADR-0029 covers what the engine must emit
  to populate it.

A client and service at incompatible versions must **refuse to proceed with a
clear message naming both versions** — the failure users of a legacy service met as an
unexplained blank window. This matters more in topology 3 than anywhere else,
because a console will routinely meet services at several versions at once, and
must degrade per service rather than refusing to start.

### 8. Status aggregation preserves the never-merge rules

A console showing *N* machines must not become the place where the status
vocabulary is quietly flattened. Architecture 10 §1.2 and NFR-OPS-002 forbid
merging `captured` with `protected`, or `degraded` with `unrecoverable`; a
fleet view is exactly where the temptation to show one green tick per machine
becomes strongest.

Aggregation is therefore **derived and always decomposable**: a machine's
summary is computed from its per-set, per-destination detail, the detail
remains reachable, and a roll-up never invents a state the vocabulary does not
have. A console that cannot currently reach a service shows it as **stale, with
the age of the last contact** — never as healthy, and never as failed, because
neither is known.

### 9. Unlock: the service holds key material, released by the OS keystore

The service obtains the repository passphrase or wrapped key material from the
platform keystore — **DPAPI** (Windows), **Keychain** (macOS), **kernel keyring
or an equivalent** (Linux) — scoped to the service account, and unlocks itself
without a human present. This is what makes unattended scheduled backup possible
at all, and it replaces today's `--passphrase-env`, which requires an
environment variable to be set for the service's whole lifetime and is inherited
by every child process.

The consequence is stated plainly rather than buried: **an attacker who obtains
the service account obtains the backups.** That is a real reduction in secrecy
against a local attacker, accepted because the alternative — prompting a human
for every scheduled run — is not a backup product. It is recorded in the threat
model, not only here.

Bounded by three rules:

- Key material lives in the service process only. It never crosses the command
  boundary, in either direction, in any command or event.
- Clients never receive, and never need, the passphrase. A client that could ask
  the service for the key would have made the keystore pointless.
- Operations that re-derive the KEK from a user-supplied passphrase — key export
  above all — take it per invocation and never from the keystore, so possession
  of the running service is not sufficient to mint a recovery kit.

## Consequences

**Positive.** The multi-process hazards of the Context section stop being
reachable: one process holds the writer role, so duplicate sequence numbers,
colliding spool paths, silently lost intents, cross-process void deltas and the
spurious T-18 alarm all become unrepresentable rather than merely unlikely.
`WriterSequence`'s in-process lock becomes correct as written. The catalogue's
missing `busy_timeout` stops mattering for the shared case. Unattended scheduled
backup becomes possible without an environment variable holding a passphrase for
the life of the process. Front ends — CLI, desktop, web — are finally the same
kind of thing, so the status vocabulary ADR-0027 §4 built has somewhere to go.

All four topologies fall out of one service and one contract, so "manage a fleet"
and "back up this laptop" are the same product configured differently rather than
two codebases that drift.

**Negative.** The CLI gains a dependency on a running service for its normal
path, and direct mode is a second code path that must be tested. Two transport
bindings are more work than one, and the local binding is more platform-specific
than an HTTP listener while giving up HTTP's free tooling. Pairing is more
friction than a password for the person setting up a console, and that friction
lands on every managed machine. Keystore unlock weakens secrecy against a local
attacker who holds the service account. The service becomes a long-lived process
holding key material, which the current code is not written for: it re-derives
Argon2id every poll, keeps no state between passes, and holds the passphrase as
an unzeroable `string`. Enabling the remote binding creates a network attack
surface on a machine that previously had none, which is why it is off until
someone turns it on and says where it listens.

**Neutral.** Repository-level multi-writer semantics are unchanged — this
decision is about processes on one device, not devices on one repository.
`FallbackPlan.Recovery` is unaffected and speaks to no service in any topology.
The peer protocol is unaffected; the command contract is a separate surface that
happens to reuse its pairing machinery.

## Alternatives considered

**Make every local store multi-process-safe.** Add cross-process locking to the
sequence file, `busy_timeout` and serialisation to the catalogue, and atomic
append-with-lock to `state.json` and `jobs.json`. Rejected: it is four
independent mechanisms that must each stay correct forever, and it leaves the
writer *identity* shared, so the T-18 alarm remains reachable through any future
bug. Single ownership removes the class.

**Give each process its own writer identity.** Distinct writer IDs would make
concurrent local processes legal by the existing rules. Rejected: writer
identities are device-scoped and carry authorisation grants, journal chains and
rollback detection (04 §2, 03 §6); minting one per process would multiply
identities on every machine, inflate the index's per-writer chains, and make
"which writer wrote this" meaningless as an audit answer.

**Loopback TCP with a token file, for the local binding.** What both consumer
products in §5 chose. Rejected for the reasons given there — a credential to talk to your
own machine, a port any local process may reach, and a well-attested support
burden — none of which buys anything a socket does not provide locally. Note the
rejection is scoped: a network transport is right for the *remote* binding and
is adopted there.

**One transport for both, HTTP everywhere.** Simpler to build and to debug, and
it collapses the local case into the remote one. Rejected: it would make every
single-machine install — topologies 1 and 2, which will be most installs — carry
a listening port and a credential it has no use for, and it would make "is
remote management on?" a question about configuration rather than about which
binding exists.

**A shared secret or API token for remote clients.** Familiar, and easy to put
in a config file. Rejected: it puts a phishable, copyable credential on the
console that unlocks other people's machines, and revocation means rotating a
secret across every service. Pairing with pinned device identity is already
built for this exact trust shape (architecture 09 §3), is revocable at the
machine being protected, and gains nothing from being duplicated in a second
scheme.

**A console that holds repository keys and reads content centrally.** It would
make cross-machine search and restore-to-anywhere easy. Rejected: it would
concentrate every managed machine's plaintext in one place, which is the exact
property the repository design refuses to concede to a destination, a relay, or
a peer. Refusing it at the console too is what lets a fleet operator administer
machines they are not entitled to read.

**Keep the CLI as a peer host and document the hazard.** Rejected: the hazard's
first symptom is a security alarm, and a documented footgun in a backup product
is one a user finds during a restore.

**Client supplies the passphrase for every operation.** Strongest secrecy, and
it makes scheduled unattended backup impossible — which is the Agent's entire
purpose. Rejected as incompatible with the product.

**Unlock once per boot, held in memory.** No keystore dependency, but a reboot
silently stops backups until a human returns. Rejected: silent cessation of
backup is the failure users discover when they need a restore.

## Amendment (2026-08): what "or an equivalent" means per platform

§9 named "DPAPI (Windows), Keychain (macOS), kernel keyring or an equivalent
(Linux)". Implementation forced the third to be decided rather than left open,
so it is recorded here:

| Platform | Mechanism | Note |
|---|---|---|
| Windows | DPAPI (`CryptProtectData`), ciphertext in the state directory | Called through `crypt32` directly; the `ProtectedData` package would be a new dependency identity for nothing |
| macOS | A generic password item in the service account's keychain | Via the `SecKeychain*` functions — deprecated by Apple and working; the modern pair needs `CFDictionary` marshalling for no behavioural gain |
| Linux | An owner-only file in an owner-only directory | See below |

**The kernel keyring is not used as the durable store on Linux.** It does not
survive a reboot without something to re-provision it, and this ADR already
rejected "unlock once per boot, held in memory" because silent cessation of
backup is the failure users discover when they need a restore. `libsecret` needs
a D-Bus session a system service does not have. An owner-only file is weaker
than DPAPI and Keychain in exactly one way — the material is readable by anyone
who can read the file rather than only through an OS call — and identical in the
way that decides the threat model: **an attacker who obtains the service account
obtains the backups** either way, which is [T-19](../threat-model.md)'s accepted
residual rather than a new one. A TPM-sealed variant is the natural upgrade and
is not pretended at.

Two implementation rules that are part of the decision, not polish: the file is
created with its final mode rather than written and then tightened, because a
world-readable window is all an attacker needs; and material whose permissions
have drifted is **refused rather than read**, because permissions are the only
thing protecting it and carrying on would keep working while the property had
already been lost.

## Amendment (2026-08): one process, several archives — the writer rule is counted per archive

[ADR-0034](0034-hub-and-spoke-destinations.md) replaces the service's single
repository with one staging archive per backup set. Nothing in this ADR's
decision moves: there is still exactly one service process, one state
directory, one writer lock on it, and every other local process is still a
client. What changes is arithmetic. "One local writer per repository" was
written when the service held one repository; it now holds N, and the rule's
instances multiply with it — **the service holds N writer roles, one per set
archive, each with its own gapless sequence, all inside the one process the
lock already protects.** The hazard analysis of the Context section is
untouched, because the hazard was two *processes* sharing a sequence space, and
the lock that prevents it guards the state directory that now contains all N
sequences.

The CLI's direct mode and the recovery tool carry over unchanged: a repository
path is a repository path, whether it is a staging archive, a destination copy,
or the pre-0034 single archive.

## Implementation status (2026-08)

Built: the writer-role exclusion (§4), the local binding (§5), the command
contract and its versioning (§7), status aggregation (§8), and unlock (§9). The
CLI takes the writer role deliberately and says so; a second writer is refused
naming the holder.

Also built: restore, verify and check over the surface (§7). They had been
answered with a stated "read path, run it directly" refusal, which was honest
but left a console able to ask the service to make a backup and not to check
one. They run on the job queue's reader lane (ADR-0029 §4), so a read path never
takes the writer role and a restore runs alongside a scheduled backup rather
than behind it.

Also built now: the remote binding (§5). Once a terminal refusal that bound
nothing, it now binds a real TLS 1.3 socket on an interface an administrator
names — off by every default — and admits only a peer it holds a pairing grant
for, over the pairing and session machinery of architecture 09 §3
([ADR-0030](0030-peer-identity-and-pairing.md), now carried over the wire). A
paired console commands the service and receives results but, by default, no file
content (§6): a restore it commands writes on the service's machine, and the
console is told the counts and the path. The shipped CLI drives this over
`--connect <host:port> --fingerprint <fp> --state <dir>`: `backup`, `verify`,
`check`, `restore`, `snapshots`, `ls`, `status`, `sync` and `retention` route to the remote service,
the pinned service named by fingerprint because a grant holds a key and never an
address. This closes topologies 3 and 4 of §1.
What a paired console may additionally *do* — stream restored content (Q18),
carry a per-operator identity (Q19) — stays open by design.

This ADR stops at the service boundary and says nothing about how the operating
system launches and supervises the process. That is now decided in
[ADR-0033](0033-hosting-under-an-os-service-manager.md): the agent shuts down
cleanly on the stop signal a service manager sends, bridges the Windows SCM, and
generates the systemd/launchd/`sc.exe` registration — built on this ADR's §9
keystore unlock, which is what lets the boot-started service self-unlock.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | Written after the multi-process hazard was found while designing the service split |
| 2026-08 | Accepted (amended) | Implemented for the local binding; the Linux keystore question decided in the amendment above |
| 2026-08 | Accepted | Restore, verify and check served over the surface on the reader lane; the CLI routes them and backup to a running service |
| 2026-08 | Accepted | The remote binding (§5) built on ADR-0030's now-carried transport: a paired console reaches the service, an unpaired one is refused, and a remotely commanded restore writes on the service's machine — closing topologies 3 and 4 |
| 2026-08 | Accepted | How the OS hosts the process decided in [ADR-0033](0033-hosting-under-an-os-service-manager.md): clean shutdown on a manager's stop, the Windows SCM bridge, and generated systemd/launchd/`sc.exe` registration |
| 2026-08 | Accepted (amended) | One process, N staging archives: the writer rule is per archive, all roles held by the one locked service process ([ADR-0034](0034-hub-and-spoke-destinations.md)) |
