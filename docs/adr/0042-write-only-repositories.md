# ADR-0042 — Write-only repositories: one passphrase, a sealed data plane, and a restore key that is derived, never stored

**Status:** Accepted
**Date:** 2026-08
**Requirements:** FR-WOR-001, FR-WOR-002, FR-WOR-003, FR-WOR-004, FR-WOR-005, NFR-SEC-009, NFR-SEC-010
**Related:** [ADR-0019](0019-third-party-dependency-policy.md), [ADR-0028](0028-service-boundary-and-deployment-topologies.md), [ADR-0030](0030-peer-identity-and-pairing.md), [ADR-0041](0041-guided-restore-and-peer-retrieval.md), [architecture 03](../architecture/03-crypto.md), [format spec 03](../../specifications/repository-format/03-keys.md)

---

## Context

Everything in a format-v1 repository derives from one symmetric master
key, and symmetric AEAD makes writing and reading the same capability:
whoever can add a backup can decrypt every backup ever taken. The
always-on service therefore holds, for the life of its process, the
power to read years of history — deleted files, superseded versions,
every device's data — and its startup passphrase typically sits in an
environment variable or the platform keystore. [T-19](../threat-model.md)
states the consequence plainly: an attacker who obtains the service
account obtains the backups.

The user's decision is to break that equation for repositories that opt
in: the hub becomes a machine that can **add to history but never read
file contents back**. One long passphrase governs the repository; it is
entered on exactly three occasions — **setup**, **adoption** of an
existing repository onto a new service instance, and **restore** — and
is otherwise nowhere: not on the service, not in an environment
variable, not wrapped in the store. Losing it loses the backup,
deliberately and irrecoverably, and setup says so behind an explicit
acknowledgement (architecture 03 §1 rule 6's posture). What a
compromised hub yields shrinks from "everything, forever" to "the
structure of what exists, plus the ability to keep writing".

This is **repository format v2**, opt-in at creation. v1 repositories
are untouched, remain fully supported, and never convert implicitly.

## Decision

1. **The passphrase is the key material's sole source — literal
   derivation, no stored key object.** A v2 repository has no `keys/`
   namespace and no KEK/wrap step. Creation draws a random salt,
   records it with the Argon2id parameters in the `repository-format`
   descriptor, and computes `root = Argon2id(passphrase, salt, params)`.
   Every repository key is an independent one-way HKDF-SHA256 domain of
   that root:
   - `fbp/seal/v2` → the X25519 **sealing keypair**. The public half is
     recorded in the descriptor; the private half is used and discarded.
   - `fbp/metadata/v2 ‖ generation`, `fbp/content-id/v2`,
     `fbp/key-id/v2`, `fbp/signing/v2 ‖ generation` — together the
     **write bundle**: everything the service needs to capture, browse,
     plan, deduplicate, trim, replicate and structurally verify, and
     nothing that opens file content.
   The descriptor's public-key copy doubles as the wrong-passphrase
   verifier: derive and compare, no decryption, no oracle beyond
   equality.
2. **File contents are sealed; structure stays symmetric.** A v2
   data-class blob draws a fresh random 32-byte **content key**; its
   records seal under that key with the v1 ordinal-nonce construction
   unchanged (architecture 03 §3 holds verbatim — a random key per blob
   satisfies its uniqueness argument trivially). The content key is
   sealed to the repository public key — X25519 ECDH with an ephemeral
   keypair, HKDF, AES-256-GCM — and the encapsulation rides the blob's
   cleartext envelope. The blob's **footer and record table move to the
   structure plane**: their blob key derives from the metadata class
   key, so a write-only holder can open every blob's structure — record
   tables, object identifiers, offsets — while the record payloads stay
   sealed. Metadata-class blobs and standalone records are unchanged
   from v1 mechanics.
3. **Spool resume survives sealing.** The in-flight blob's content key
   is carried in the spool checkpoint (state directory, owner-only,
   destroyed at seal), so resume-by-authenticating-the-tail still
   works. The exposure is bounded to the blob under construction on a
   machine that, being the source, holds the same bytes as plaintext
   files anyway.
4. **The service never holds the passphrase.** Derivation runs in the
   admin client process — the console host or the CLI, where the person
   typed it (ADR-0041's gate posture; ADR-0028 §9's rule). What reaches
   the service is a **sealed envelope**: the write bundle at
   setup/adoption, the private key at restore — sealed end-to-end to a
   service-held X25519 recipient keypair whose public half
   `describe_service` publishes, so not even the web console host relay
   sees the contents. Transport is the channels that already
   authenticate the operator: the state-directory-guarded local socket
   or pipe, and the pinned-TLS remote binding. The passphrase itself
   never transits anything.
5. **Restore grants ride the restore-source machinery.**
   `open_restore_source` accepts an optional sealed envelope; the
   unsealed private key lives only inside the source handle — swept
   after 30 idle minutes, closed explicitly, zeroed on disposal. Verbs
   against a v2 source without a grant do exactly what the write bundle
   allows (list, browse, plan structure) and name the grant when asked
   for content.
6. **NFR-SEC-009 is amended, not breached.** The wall's substance
   stands: no raw or unsealed key material on the command surface, in
   either direction, ever. The amendment admits precisely one shape —
   an envelope sealed to the service's published recipient key, as a
   hex string, on the two named verbs — and
   `KeyMaterialConfinementTests` enforces the amended rule as narrowly
   as it enforced the original.
7. **Write-only is honest about what it cannot do.** Verify levels that
   decrypt content, `verify --file`, and verify-on-reuse deduplication
   are impossible without the private key, so: v2 creation refuses the
   `repository` dedup trust domain with a stated reason (`device` is
   the default; `repository-unverified` keeps its explicit
   acknowledgement); structural verification (locator, footer,
   ciphertext digest) remains and level 2 becomes genuinely key-free;
   content-deep checks answer "needs a restore grant" rather than
   failing or silently passing. Forensic and catalogue rebuilds of
   structure work from the write bundle; only content needs a grant.
8. **The recovery kit carries no key material at all.** A v2 kit is
   repository id, format version, KDF salt and parameters, the public
   key, and destination descriptors — where the data is and how to
   derive, nothing that opens anything. `RecoverySession` opens a v2
   repository from kit plus passphrase by derivation alone. The
   passphrase is the single factor, and the kit's instructions say so.
9. **The write bundle is one-way.** Possession of the metadata key — or
   the entire write bundle, or the service's whole state directory —
   yields neither the passphrase, nor the root, nor the private key,
   nor any sibling key: every derived key is an independent HKDF-SHA256
   output of a root that is itself the output of a memory-hard KDF.
   The threat-model entry for stolen service state says exactly what
   the thief gets (structure readability, the power to keep writing,
   the dedup-confirmation side channel) and what they provably cannot
   get (content, passphrase, restore capability).
10. **Moving a repository to a new machine is an adoption, and it costs
    the passphrase once.** The write bundle is service-local state,
    deliberately absent from the repository and every replica. A v2
    archive attached to a fresh service — machine A to machine B, or a
    lost state directory — is unreadable, metadata included, until an
    admin re-enters the passphrase; the client re-derives the bundle,
    proves it against the descriptor's public key, and seals it to the
    new service. One provisioning verb serves both ceremonies: no
    descriptor → create; descriptor present → verify-and-adopt, with a
    mismatch refused as a wrong passphrase.
11. **There is no passphrase change for a v2 repository.** The
    passphrase is the root; changing it would change every derived key
    and orphan every sealed blob. Spec 03 §7 records this against v2
    explicitly, and the setup ceremony states it beside the loss
    acknowledgement. (The same edit corrects the v1 rotation row, which
    promised "keys/ only" while the salt lives in the descriptor —
    a fresh-salt passphrase change rewrites both.)
12. **Cryptography placement follows ADR-0019's logic.** X25519 becomes
    format-critical and joins Argon2id and XChaCha20-Poly1305 in
    `FallbackPlan.Repository.Crypto` (ADR-0019 Amendment 3); it must
    not leak into `Repository.Format`, whose recovery-tool closure
    stays minimal. ADR-0030 §1 is untouched: the *peer* identity
    keypair remains underived from any repository secret — the sealing
    keypair is a repository-plane key answering a different question,
    and the resemblance is noted here precisely so it is not
    re-litigated as a contradiction.

## Consequences

**Positive** — a compromised or stolen always-on hub can no longer read
history; the service starts with no passphrase anywhere in its
environment for v2 sets (an improvement over v1's posture even before
the sealing); the wrong-passphrase check becomes instant and
decryption-free; the recovery kit stops carrying wrapped key material;
and the one-secret model is honest — the user guards one passphrase and
knows exactly what losing it means.

**Negative** — verify-on-reuse deduplication is unavailable, so a
multi-writer v2 repository runs `device` (duplicated storage across
devices) or accepts `repository-unverified`'s risk knowingly;
content-deep verification requires a human with the passphrase to grant
it; passphrase rotation does not exist for v2; a forgotten passphrase
is unrecoverable by design; and the format grows a second major version
with everything that implies — specs, vectors, fixtures, and a
conformance surface twice the size.

**Neutral** — metadata (names, sizes, structure) remains readable to
the service, which is what keeps the product usable day to day and is
stated rather than hidden; the spool checkpoint briefly holds a content
key on a machine that holds the plaintext anyway; and v1 remains the
default until v2 has earned its miles.

## Alternatives considered

- **Wrap a random keypair under a passphrase KEK (keep rotation).**
  Identical UX, and passphrase changes stay cheap. Rejected by the
  user's explicit decision: the private key shall not be stored in any
  form, wrapped included; the passphrase is the key, and its loss is
  accepted as fatal. The wrapped design also keeps `keys/` objects as
  an attack surface the literal design simply does not have.
- **A second passphrase for restore, keeping v1's startup unlock.** Two
  secrets to manage, and the service still holds one of them
  perpetually. The single-passphrase design removes the standing secret
  from the service entirely.
- **Seal the metadata plane too.** Strictly stronger against a hub
  thief — and the product stops working: no browsing, no planning, no
  dedup bookkeeping, no trim, no structural verification, no wizard
  until a human arrives with the passphrase. The split down the
  content/structure line is the point of the design.
- **Ed25519→X25519 conversion of the existing peer keypair as the
  envelope recipient.** Saves one stored keypair — and couples
  repository provisioning to peer identity lifecycle (unpair, re-pair,
  identity loss), which ADR-0030 deliberately keeps independent.
- **In-browser derivation (WASM Argon2id).** The passphrase would never
  leave the tab — but the repository's memory-hard parameters can
  exceed what a tab will allocate, and the console host process is
  already local to the person typing (ADR-0041 settled this shape).

## Amendment (2026-08): setup provisions the installation, not the first set

Decision 10 above gives one provisioning verb serving create and adopt, and
both are addressed to a **named set**. That was the right shape for the
ceremony it was written for — an operator turning an existing set write-only,
or re-attaching a moved archive — and the wrong shape for the one that was
missing. A service on its first run has no sets, so there is nothing to
address, and the passphrase had no way in until the operator had already
built a destination and a set through a console they were never told they
needed.

[ADR-0044](0044-first-run-setup.md) closes that with an
**installation-level** credential. It rests on a property of the derivation
that was always there and never used: `WriteOnlyDerivation` takes no
repository identifier — `root = Argon2id(passphrase, salt, params)` and five
HKDF labels — so one `(passphrase, salt, params)` triple can stamp every
archive an installation ever creates. Setup mints one salt, derives once, and
the service stores the credential beside its per-set siblings; each set's
staging archive is then created from it on that set's first backup.

Nothing here is retracted. The per-set verb keeps decision 10's adopt
ceremony, which is the case where the salt is not ours to mint because the
archive's descriptor already fixed it. Decision 11 is reinforced rather than
weakened: one root for an installation makes "there is no passphrase change"
an installation-wide statement, and ADR-0044 refuses a second setup for
exactly that reason. The consequence worth naming, because it is now
structural rather than incidental, is the blast radius: the passphrase opens
every set on the machine. It always did — there is one passphrase — but a
reader of the derivation should not have to infer it.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Proposed | Written with the derivation, sealing, adoption and confinement-amendment decisions fixed by the user; build sequenced as crypto core → format v2 → repository layer → service and contract 1.12 → clients → docs |
| 2026-08 | Accepted | Built end to end: the derivation tree and content sealing in Repository.Crypto (conformance vectors cross-checked against an independent pure-python X25519), the v2 descriptor/envelope/footer re-key with sealed spool resume, derived lifecycle opens, contract 1.12's provisioning and grant ceremonies with the service's recipient keypair and per-set credential store, passphrase-free service start, the CLI's `init --write-only` and derived direct mode, the console's setup ceremony and v2 wizard gate — proven by service-level drills including the machine-migration adoption, a committed v2 conformance fixture, and a live Playwright walk from provisioning to byte-identical restore |
| 2026-08 | Amended | Decision 10's per-set provisioning is joined by an installation-level credential for first-run setup ([ADR-0044](0044-first-run-setup.md)); the per-set verb keeps the adopt ceremony, where the descriptor already fixes the salt |
| 2026-08 | Amended | This record's device-trust posture is generalised by [ADR-0046](0046-direct-to-destination-publication.md) §6: direct-ship sets run `device` trust on any format version — not because the private key is absent, as here, but because verify-on-reuse through the sink would pay a destination round trip per reuse — with the destination presence probe as the stale-catalogue guard in both cases. "Each set's staging archive is then created from it" reads "each set's repository" now that a direct-ship set's is a metadata store plus destinations; the derivation is indifferent to which. |
