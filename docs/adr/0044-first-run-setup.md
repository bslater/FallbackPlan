# ADR-0044 — First-run setup: an installation is initialised the moment it has a passphrase

**Status:** Proposed
**Date:** 2026-08
**Requirements:** FR-SVC-011, NFR-SEC-011
**Related:** [ADR-0028](0028-service-boundary-and-deployment-topologies.md), [ADR-0036](0036-local-web-console.md), [ADR-0041](0041-guided-restore-and-peer-retrieval.md), [ADR-0042](0042-write-only-repositories.md), [architecture 03](../architecture/03-crypto.md), [format spec 03](../../specifications/repository-format/03-keys.md)

---

## Context

A fresh install has no notion of being uninitialised, and nothing asks.
`ServiceRuntime.StartAsync` takes a `Passphrase?`; since ADR-0042 a null
passphrase is a legitimate way to run, because a provisioned write-only set
opens with its stored credential and needs no passphrase at all. So start-up
mints a device identity, mints an envelope recipient key, reads a
`config.json` that is not there, finds zero sets, and comes up clean. Nothing
is wrong yet, and nothing is right either.

The consequence arrives later and per set, at first archive touch:

> Set 'documents' is not provisioned write-only and this service started
> without a passphrase; start with one, or provision the set (ADR-0042).

That refusal is accurate, and it names two remedies. What it cannot do is
arrive at a useful time — it fires on a scheduler poll, minutes or hours
after the install, once per set, forever, and the operator who reads it has
no flow to follow. The only existing door to a provisioned set is the
per-set **Write-only…** dialog inside the Configuration view, which presumes
a declared destination, a saved set, a running service, and an operator who
already knows the dialog exists.

Two requirements have assumed a first-run ceremony since the beginning and
have never had one to attach to: **FR-KIT-004** ("Kit generation shall occur
during first-run setup and require explicit confirmation that it has been
saved before setup completes") and **FR-SNP-007** ("First-run warns when the
only destination shares a failure domain with the source"). §16.1 of the
original proposal lists nine steps. None of them was built, because there
was no ceremony to build them into.

This decision builds the first step and the one every other step needs: an
installation is set up the moment it has a passphrase, the service knows
whether it does, and the client that connects captures it.

## Decision

### 1. Setup establishes a write-only installation (format 2)

The passphrase captured at first run is the root of a **format 2**
repository per ADR-0042: every key derives from it, nothing derived from it
is stored, content seals to a public key, and the service can add to history
and browse structure but can never read file contents back.

This is the model that makes the sentence the ceremony must say literally
true — lose the passphrase and the backup is unrecoverable, with no reset
and no export — and it is the model whose steady state needs no passphrase
in the service's environment at all. Choosing format 1 here would have meant
the opposite: to run unattended, the service would need the passphrase in
its platform keystore, and getting it there from a setup ceremony would mean
inventing a way to hand a secret to the service, which
[NFR-SEC-009](../requirements/non-functional.md) exists to forbid.

Format 1 repositories are untouched and remain fully supported. An
installation whose sets predate setup keeps opening them through the
passphrase path; nothing is migrated, because write-only is chosen at
creation.

### 2. The credential is per installation, not per set

`provision_write_only_set` provisions **one named set**, and a first run has
no sets. Rather than force the ceremony to create a set it was not asked to
create, setup provisions the installation.

This is available because the derivation was never per-set.
`WriteOnlyDerivation` computes `root = Argon2id(passphrase, salt, params)`
and expands it through five HKDF-SHA256 labels; no repository identifier
enters it anywhere. One `(passphrase, salt, params)` triple therefore yields
one `RepositoryWriteCredential` capable of stamping any number of archives.

So setup mints one installation-wide 16-byte salt, derives once, and the
service stores the resulting credential — with its salt and KDF parameters,
which each new archive's descriptor needs — at
`<state>/write-credentials/installation.bin`. Every set's staging archive is
then **created from that credential on the set's first backup**, in place of
the silent format-1 `CreateAsync` fallthrough that happens today.

`ServiceRuntime.ArchiveForAsync` gains one arm, and the ladder reads:

> per-set credential → **installation credential** → passphrase → refuse

The per-set verb is untouched and keeps its own job: **adoption** of a moved
archive, where the salt is not ours to mint because it must come from that
archive's existing descriptor.

What follows from one root across an installation, stated rather than
discovered: one passphrase opens every set on the machine, which was always
the intent — ADR-0042's whole framing is *one* long passphrase; content-ids
become comparable across sets, which is harmless and mildly useful; and
repository ids remain independently random per archive, so the archives are
still distinct repositories in every way the format cares about.

### 3. Setup captures the passphrase and stops

The ceremony is: state the consequence, take an explicit acknowledgement,
capture the passphrase twice, derive, seal, provision. It does not create a
destination, a backup set, or a recovery kit, and it does not run a backup.
Those stay where they are, in the Configuration view and the CLI.

This is a deliberate narrowing of §16.1's nine steps, and it leaves
**FR-KIT-004 and FR-SNP-007 unmet**. They are recorded as unmet in
[implementation status](../implementation-status.md) rather than left to
look satisfied by a ceremony that exists but skips them. The reason to
narrow is that the passphrase is the only step the others depend on and the
only one whose absence produces a service that cannot work; a set with no
kit is a risk, a set with no passphrase is a stopped product.

### 4. The passphrase never crosses the contract; a sealed envelope does

Setup reuses ADR-0042's ceremony exactly. The client derives in its own
process, seals the write bundle to the service's published X25519 recipient
key, and sends opaque hex. The service opens the envelope with a private key
only its state directory holds. Argon2id runs where the person typing is.

`Api.Tests/KeyMaterialConfinementTests` pins the members named `Envelope` to
an explicit list of commands by name; `provision_installation` joins that
list deliberately, as a third named exception, rather than the test being
loosened to a pattern. A list that grows by decision is still a fence; a
pattern that admits anything is not.

### 5. Setup is local-only, and happens once

A paired remote console may not initialise a service. Remote callers are
already withheld file content by default (ADR-0028 §6) and are read-only for
diagnostics; initialising an installation — choosing the one secret that can
never be changed — is further inside that line, not outside it. Refusing it
needs something the code does not have: `AgentHost` builds **one**
`ServiceCommandHandler` and hands it to both listeners, and
`RemoteBindingState` says only whether the remote binding is on, not whether
this caller came in over it. So a **caller scope** is introduced — the
listener that owns the session presents `Local` or `Remote`, and the
handler is decorated per session.

A second `provision_installation` is refused rather than obeyed. There is no
passphrase change for a v2 repository (ADR-0042 §11), so a second setup
could only mean one of two things — a mistake, or an attempt to strand every
archive already written under the first root — and both deserve the same
answer.

### 6. A minimum length **and** a strength estimate, at the setup boundary only

This settles [open question Q14](../open-questions.md), which has stood
undecided since specification 03 §2.1 asked for a minimum and named no
number. Q14 poses the choice as "length alone versus a strength estimate,
and what the recovery story says to a user whose old passphrase no longer
meets the bar". Both halves are answered here.

`Passphrase.RecommendedMinimumLength = 12` — declared today and enforced
nowhere — becomes the floor, and above it a small documented estimate scores
length, character-class variety, and penalties for a single repeated
character, an unbroken run of one class, and short repeated cycles. Four
bands: `TooShort` and `Weak` are refused, `Fair` and `Strong` pass, and
every finding is a plain sentence the console shows live while typing.

**It is enforced at the setup boundary only.** `Passphrase.Create` keeps
refusing exactly the empty string and nothing else. That is the answer to
Q14's second half: no existing repository becomes unopenable because the
policy tightened, and no restore is refused for a passphrase that was
acceptable when it was chosen. A rule applied where secrets are *created*
costs nothing; the same rule applied where they are *used* would lock people
out of their own backups, which for a product whose one job is not losing
data would be an unusually direct failure.

The estimate is deliberately modest and the ADR says what it is not: it is
not zxcvbn, it consults no dictionary or breach corpus, and it cannot tell a
strong passphrase from a famous quotation of the same shape. It is a floor
that catches the obviously bad, and calling it a floor is the honest
description. The reason not to take a dependency is ADR-0019's tiering — a
password-strength corpus is a large operational dependency for a
setup-screen hint — and the reason not to claim more is that a strength
meter that overstates its reach is worse than none, because it converts a
guess into reassurance.

### 7. The service reports whether it is set up

`describe_service` gains an optional trailing `SetupState` — `"ready"` or
`"setup_required"` — the same additive-minor shape the archives root and the
grant recipient already use. It is derived from the installation credential
being held **or** any set holding a per-set credential, so an installation
provisioned the old way is never told to set itself up again.

The console already calls `describe_service` on every status refresh, so it
learns this for free and shows the setup ceremony in place of its normal
views, in the shape of the token gate it already has. Blocking navigation is
not paternalism: there is nothing behind the gate that works.

### 8. Two clients, one code path

The local console and an agent CLI verb (`fallbackplan-agent setup`) both
run the ceremony, and both do it by deriving locally, sealing to the
service's recipient key, and sending `provision_installation`. The CLI verb
exists for headless installs with no browser; it is built on the same
one-shot `ServiceVerbAsync` helper as `retention` and `sync`, so it inherits
their state-directory-lock behaviour — refused, with the running service
named, rather than fighting it — and it requires `--acknowledge-loss`, using
the same sentence `init --write-only` already uses.

## Consequences

**Good** — the reported failure becomes impossible to reach silently: a
service with no passphrase says so on the first `describe_service`, and the
first client to connect resolves it. Unattended backup then works from the
installation's first minute with no secret in the service's environment.
Every set created afterwards is write-only by default rather than by an
operator remembering a dialog. Q14 is answered. And the caller-scope
decorator that setup needs is the one the pending diagnostics work needs
too, so it is built once.

**Bad** — the contract grows a third verb permitted to carry an envelope,
and the confinement test's allowlist grows with it. One derivation root
serves a whole installation, which is a coarser blast radius than one per
set: an attacker who obtains the passphrase obtains every set on the
machine. That was already true — it is one passphrase — but it is now true
structurally rather than incidentally, and it is written down here rather
than discovered by whoever next reads the derivation. We also now own a
hand-rolled strength estimator, with all the obligation to defend its
judgements that implies.

**Neutral** — FR-KIT-004 and FR-SNP-007 stay unmet, and the ceremony that
was supposed to host them now exists, which makes the gap easier to close
next time and easier to notice until then. Format 1 installs are unaffected
and unmigrated.

## Alternatives considered

- **Provision per set, prompting at each set's creation.** No new state, no
  new verb, and the existing ceremony untouched. Rejected because it asks
  for the master passphrase repeatedly — training the exact habit a
  never-changeable secret must not train — and because it leaves the window
  between "service installed" and "first set created" in precisely the
  broken state that prompted this work.
- **Have the service mint the salt and derive from a passphrase sent over
  the contract.** By far the smallest change. Rejected outright: it would
  put the passphrase in the service's process and on the wire, which
  NFR-SEC-009 forbids and which the sealed-envelope machinery exists
  specifically to avoid.
- **Store the passphrase in the platform keystore at setup (format 1).**
  Restores stay cheap and local, and the service can read content for
  verification. Rejected with the model choice in §1: it reintroduces the
  standing secret ADR-0042 removed, and T-19 — an attacker who obtains the
  service account obtains the backups — comes back with it.
- **Build the full nine-step §16.1 ceremony now.** It is the right eventual
  shape and it is what FR-KIT-004 and FR-SNP-007 assume. Deferred rather
  than rejected: the passphrase step is separable, unblocks a broken
  install today, and the remaining steps mostly re-render surfaces that
  already work.
- **Enforce the minimum inside `Passphrase.Create`.** One rule in one
  place, impossible to bypass. Rejected because `Passphrase.Create` is on
  the *restore* path as well as the create path, and a policy tightened
  later would refuse to open repositories that were made legitimately —
  turning a hardening change into data loss.
- **Take a real strength library (zxcvbn or similar).** Better judgements
  than anything hand-rolled. Rejected on ADR-0019 grounds for a
  setup-screen hint, and because the honest floor described in §6 catches
  what a floor is for; the ADR states the limitation rather than importing
  a corpus to hide it.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | Written with the model, scope, client set and strength policy fixed by the user: write-only format 2, passphrase-only ceremony, local console plus an agent verb, and a length floor with a strength estimate. Build sequenced as strength assessment → installation credential and the archive ladder → contract 1.13 with caller scope → console ceremony → agent verb and sweep |
