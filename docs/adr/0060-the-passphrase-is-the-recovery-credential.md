# ADR-0060 — The passphrase is the recovery credential

**Status:** Accepted
**Date:** 2026-09
**Requirements:** FR-DRL-001, FR-WOR-002, NFR-OPS-005, NFR-SEC-009
**Related:** [ADR-0013](0013-recovery-kit.md) (superseded), [ADR-0014](0014-format-versioning-and-stability.md), [ADR-0042](0042-write-only-repositories.md), [ADR-0044](0044-first-run-setup.md), [ADR-0053](0053-peer-claim-and-configuration-recovery.md), [ADR-0054](0054-scheduled-restore-drills.md)
**Supersedes:** [ADR-0013](0013-recovery-kit.md)

---

## Context

The recovery kit was designed for a repository that stored a wrapped master
key ([ADR-0013](0013-recovery-kit.md)): the kit carried that key object, the
passphrase unwrapped it, and the two together were the two factors a recovery
needed. Format 1 is withdrawn
([ADR-0014 Amendment 1](0014-format-versioning-and-stability.md#amendment-1-2026-09--format-1-withdrawn-before-freeze)).
The one format left derives every key from
`root = Argon2id(passphrase, salt, params)` and seals content to an X25519
public key ([ADR-0042](0042-write-only-repositories.md)), and its kit had
already shrunk to match: no key object, only the KDF salt and parameters and
the sealing public key that proves a re-derivation
([ADR-0013](0013-recovery-kit.md)'s amendment).

Every one of those facts is in the archive's own descriptor, unencrypted by
design (`repository-format`, [01 §3.3](../../specifications/repository-format/01-object-layout.md)),
and the kit specification said so itself: its payload was a strict subset of
every descriptor's. `RepositoryLifecycle.ReadDescriptorAsync` takes only a
store, and `TryDeriveReadAuthority(descriptor, passphrase)` turns the two into
the full authority, content plane included. The standalone recovery tool
required a kit because its argument parser did. For a format-2 installation
**the kit was never a second factor**, and the threat model should stop
implying it was: a stolen kit yielded an address and a public key, and a
person holding the passphrase and the archive needed nothing else.

What the kit did uniquely carry was two things. A **destination list**, which
told a person where their backups were — and which the installation kit had
already dropped, because setup runs before any destination is declared. And a
**salt that survives the machine** for a peer replica, whose descriptor sits
behind the peer's attribution gate where a rebuilt machine cannot read it.
Slice 12B closed the second: the peer serves the KDF salts and costs behind its
claimable replicas to a paired claimant, and the claim needs the passphrase
and nothing else ([ADR-0053 Amendment 2](0053-peer-claim-and-configuration-recovery.md#amendment-2-2026-09--the-claim-takes-the-passphrase-and-nothing-else)).

The owner's decision, taken with the withdrawal of format 1 and not to be
re-litigated: the product is pre-release with no installed base, it carries
only the latest format, and **only the passphrase is needed**.

## Decision

### 1. Recovery is the passphrase and reach to an archive

A recovery needs exactly two things: the installation's passphrase, and a
path to any archive that installation wrote — a destination folder, a drive,
or a replica copied back from a peer. The archive's descriptor supplies the
salt, the parameters, the repository id and the verifier; the passphrase
supplies the root; derive-and-compare against the descriptor's sealing public
key is the whole check. `WriteOnlyDerivation.TryDeriveVerified` in
`Repository.Crypto` is that one gate, shared by the engine, the console and
the recovery tool — placed there because the recovery tool deliberately links
no engine, and the gate has to sit where all three can reach it.

The standalone tool is therefore `open | snapshots | restore --repo <path>
--passphrase-env <VAR>`. It has exactly two refusals: the passphrase does not
reproduce this archive's keys, and the path does not hold an archive at all.

### 2. There is no recovery kit

No kit file, no text form, no `key-export`, no `--kit` on the recovery tool,
no `--kit-output` on `setup`, no kit step or kit-rebuild flow in the console,
no `confirm_recovery_kit` verb, no `kit_required` state, no
`recovery-kit.confirmed`, no `installation-public.json` (its only purpose was
kit rebuild), no kit format in `Repository.Format`, no kit conformance
vectors, and no `specifications/recovery-kit`. A flag from the kit era —
`--kit`, `--kit-output` — is **refused by name with the remedy**, never
ignored, so a person following an old note learns the ceremony changed rather
than believing a kit was read or written.

### 3. First-run setup ends at the passphrase and the first account

`setup_state` is two-valued again: `setup_required` or `ready`, with the
account layer adding `users_required` as before. The console's wizard is
three steps — what this is, the passphrase, the account — and the headless
verb is `setup --passphrase-env --acknowledge-loss --user --password-env`.
Nothing is produced for a person to save, because nothing has to be.

### 4. The public parameters ride the contract

`describe_service` carries the installation's KDF salt and parameters and its
sealing public key (contract 1.28), so a client holding the passphrase can
derive a restore grant without holding an archive. ADR-0044's 2026-09
amendment argued the opposite — that these must stay a local file, because
together with the device id they would let a session assemble a kit. With no
kit to assemble the argument dissolves: every value is public by
construction, every archive's descriptor records the same facts, and holding
a running service is still not sufficient to derive any read authority.
NFR-SEC-009 is amended to say that rather than to name an artefact that no
longer exists.

### 5. Contract 1.29

A minor with removals, admitted under the pre-release rule: `confirm_recovery_kit`
is gone, `describe_service` drops `kit_status` and `kit_confirmed_at`, and
`setup_state` drops `kit_required`. The only clients are this repository's, a
client reads a missing `kit_status` exactly as it read one from a pre-1.15
service, and a client that still knows `kit_required` treats it as an
unfinished ceremony. A 2.0 would protect a client nobody has.

### 6. What is given up, and the follow-up it names

**A person must now remember two things: the passphrase, and where the
backups are.** The kit's destination list was the only recorded "where" for a
direct-ship set, and the installation kit had already stopped carrying it.
[ADR-0053 §4](0053-peer-claim-and-configuration-recovery.md) — the set's shape
travelling in the kit — closes as will-not-do: the set is re-declared after a
rebuild.

The flow the owner's recovery model implies — *add an existing destination →
discover its archives by descriptor → adopt each under its original
repository id with the passphrase* — does **not** exist yet.
`ProvisionWriteOnlySetCommand` adopts only from `<archives>/<setId>` and the
console mints a fresh set id. It is the named follow-up, not part of this
record.

**The "no archive yet" window** — setup done, the state directory lost before
the first backup — loses the salt for ever, and there is nothing to recover.
Run setup again. The kit was pointless there too: a kit for an installation
that has written nothing opens nothing.

**At a peer, a wrong passphrase reads as "nothing claimable"**, not "wrong
passphrase", because the peer must refuse a claim for nothing and a bad
signature identically ([ADR-0053 Amendment 2](0053-peer-claim-and-configuration-recovery.md#amendment-2-2026-09--the-claim-takes-the-passphrase-and-nothing-else)).
A person can only be told the latter with one of their own archives mounted.

## Consequences

**Positive**

- One credential. The user guards one passphrase and knows exactly what
  losing it means; there is no second artefact to keep apart from the first,
  to print, to confirm, to rebuild or to lose.
- The recovery tool's premise is now literally true: a clean machine, the
  passphrase, and reach to an archive. `eng/recovery-drill.sh` proves it
  against the Release binaries with the state directory destroyed and nothing
  written outside it beforehand.
- A ceremony with fewer steps. Setup is the passphrase and the owner account;
  the console has no kit step to go dead, and the headless verb has no path
  to get wrong.
- Less to specify and to keep honest: a format, a codec, a text form with
  per-line checks, conformance vectors, a spec directory, a contract verb, a
  setup state and a status field are gone rather than maintained for an
  artefact with no purpose.

**Negative**

- Where the backups are is no longer written down anywhere by the product. A
  person who forgets the destination has a passphrase that opens nothing they
  can find. The follow-up in §6 is what makes that recoverable from the
  destination's side.
- A rebuilt machine that never backed up before it was lost has nothing to
  recover, and cannot be told so until it tries.
- The wrong-passphrase diagnosis at a peer is weaker than it was with an
  archive in hand, stated in §6.

**Neutral**

- FR-KIT-001..005 are deleted rather than left unmet. FR-KIT-006 and
  FR-KIT-007 were about *drills* — manual, and on a cadence — and survive as
  FR-DRL-001 and FR-DRL-002 under a heading of their own.
- Event 3100 in the recovery tool now records the descriptor read rather
  than the kit read; 3101 and 3102 are unchanged.

## Alternatives considered

**Keep a kit as a destination list only.** It would carry the one fact the
product no longer records — where the backups are — and nothing else. A
printed page holding a folder path and a peer's address is a note, not a
format; the add-destination-then-adopt follow-up is the product-shaped answer
to the same need, and a note a person writes themselves does not need a
codec, a text form and a conformance suite.

**Keep the kit as a second factor.** For a format-2 installation it never was
one: its payload was public, every archive publishes the same facts, and the
threat model's T-19 gains nothing from it. Keeping an artefact so that the
documentation could go on calling it a factor would be keeping a fiction.

**Keep the QR half.** FR-KIT-003's QR rendering was the one part of the kit
never built, and it was the only part that would have made transcription
easier. Building it to then withdraw the kit would have been work for
nothing; withdrawing the kit makes the gap moot rather than closing it.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-09 | Accepted | Built as the last slice of the format-1 withdrawal. `Recovery/RecoverySession` opens from the passphrase and the descriptor through `Repository.Crypto/WriteOnlyDerivation`'s shared gate; the kit format, factory, codec, text form, vectors, fuzz seeds, specification, contract verb, setup state, status field, setup step, console endpoint and CLI export are deleted; contract 1.29; `eng/recovery-drill.sh` rewritten passphrase-only and green on the Release binaries; `Hosts.Tests/RecoveryHostTests` and `Repository.Tests/PassphraseDrillTests` are the in-process halves. Supersedes ADR-0013 |
