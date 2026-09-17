# ADR-0020 — Ed25519 signing key semantics: seed interpretation, repository scope

**Status:** Accepted
**Date:** 2026-08
**Requirements:** NFR-SEC-005, FR-SNP-001
**Related:** [ADR-0005](0005-aead-suite-and-nonce-construction.md), [`../../specifications/repository-format/03-keys.md` §4](../../specifications/repository-format/03-keys.md#4-derived-keys), [`../../specifications/repository-format/06-manifests.md` §6.1](../../specifications/repository-format/06-manifests.md#61-signature), [T-18](../threat-model.md#t-18-writer-identity-cloning), [Q13](../open-questions.md#q13--device-level-signature-attribution)

---

## Context

A specification review found that snapshot, index, and journal signatures were mandated but not implementable from the text. Three things were missing, and one of them was a contradiction rather than an omission:

1. **Seed or scalar?** Specification 03 derives the signing key as 32 bytes of HKDF output and says nothing about what those bytes *are* to Ed25519. RFC 8032 admits two readings — the private-key seed that is hashed and clamped internally, or a raw scalar — and they produce different signatures. Two implementers would disagree and both would be "following the spec".
2. **Where is the public key?** Readers were required to verify signatures, but no object in the namespace and no field in the key bundle carries a public key. A reader written from the specification alone had nothing to verify against.
3. **The contradiction.** Specification 06 told readers to verify against "the device's known public key" — but the signing key derives from the **shared master key**. Every repository member can derive it. A key every member holds cannot attribute anything to one device; the specification was promising an attribution the key hierarchy cannot deliver.

The third point is not a drafting slip to be worded away. It is a real design fork: either signatures stay master-derived and honest about what they prove, or the format grows per-device keypairs, an enrolment flow, and a public-key registry object with its own trust rules.

## Decision

### 1. The derived bytes are an RFC 8032 seed

> **Amended 2026-09.** With format 1 withdrawn ([ADR-0014 Amendment 1](0014-format-versioning-and-stability.md#amendment-1-2026-09--format-1-withdrawn-before-freeze)) the seed is
> `HKDF-Expand(signing_root, "fbp/signing-generation/v2" ‖ u32(g), 32)`,
> the signing root itself an HKDF domain of the passphrase-derived root
> ([03 §4](../../specifications/repository-format/03-keys.md#4-derived-keys)).
> The interpretation below — a seed, not a scalar — is unchanged, and so
> is everything §2 says about scope: "a holder of the master key" reads
> "a holder of the repository's write credential".

The 32 bytes of `HKDF-Expand(master_key, "fbp/signing/v1" ‖ u32(g), 32)` are the Ed25519 **private-key seed** of RFC 8032 §5.1.5 — the value that is SHA-512-expanded and clamped inside the algorithm. Not a pre-clamped scalar.

Chosen because it is the only interpretation mainstream APIs accept directly (`Ed25519.KeyPair.FromSeed` and equivalents), and because the scalar reading would force every implementation to perform clamping manually — an invitation to get it wrong in exactly the way that is hard to test.

### 2. Signatures are repository-scoped in format v1

A signature over a snapshot, index checkpoint, or journal record proves precisely: **this object was produced by a holder of the master key, at the recorded generation, and has not been altered since.** Nothing more.

It does not prove *which* member produced it. `device_id` and `writer_id` fields remain attribution **by claim** — they are authenticated as part of the signed content, so they cannot be altered after the fact, but a malicious member could have written any value into them before signing.

### 3. Readers derive the public key; nothing stores it

> **Narrowed 2026-09 by [ADR-0055](0055-reclaim-authority.md) §5.** "Any reader
> entitled to verify can derive it" is true of every reader *inside the key
> boundary*, and a peer destination is deliberately outside it — it holds
> ciphertext and no repository keys at all. A keyless destination asked to
> check a signature therefore needs a published public key, and one is
> recorded beside its attribution. The rule below is unchanged for the
> repository plane, where nothing stores a public key and nothing needs to.
> See the [amendment](#amendment-2026-09--a-keyless-destination-cannot-derive-anything).

Because the seed derives from the master key, any reader entitled to verify a signature can compute the keypair itself. The format therefore stores **no public key object**: no registry, no key-bundle field, no namespace entry. Verification is: derive seed for generation *g*, compute public key, verify.

This is the property that makes the whole resolution small. The missing-public-key-location defect is not fixed by adding a location — it is fixed by there being nothing to locate.

### 4. What the signature is still worth

Given (2), it is fair to ask what a repository-scoped signature defends at all. Three things:

- **Outsider substitution.** A store operator or network attacker without the master key cannot forge or substitute a snapshot ([T-3](../threat-model.md#t-3-object-substitution-and-splicing)). This is the primary threat the signature exists for, and it is fully delivered.
- **Generation binding.** A signature made at generation *g* fails verification under generation *g+1*'s key, which supports key-rotation reasoning.
- **Tamper evidence over claims.** The claimed `device_id` cannot be edited after signing, so a member can lie about origin at write time but nobody can re-attribute an existing snapshot later.

What it does not defend — one member impersonating another — is exactly [T-18](../threat-model.md#t-18-writer-identity-cloning)'s territory, where the existing mitigation is the writer-identity conflict alert, not cryptography.

## Amendment (2026-09) — a keyless destination cannot derive anything

Two of this record's conclusions were reasoned from the same premise — that
everyone who verifies a signature holds the master key — and that premise has
two exceptions this record did not foresee.

**§3's "nothing stores a public key" does not reach a peer.** A destination
holds ciphertext and no repository keys by design, so "derive it yourself" is
not available to it. Wherever a keyless party must check a repository
signature, the public half has to be published to it, and the place to publish
it is beside the attribution that already records the repository as this
peer's. [ADR-0053](0053-peer-claim-and-configuration-recovery.md) reached this
first for a claim key and [ADR-0055](0055-reclaim-authority.md) §5 for a
reclaim key; they share one carrier.

**§2's "produced by a holder of the master key" is now too coarse for one
object.** A tombstone's signature is an *authorisation to delete*
([specification 11 §3](../../specifications/repository-format/11-lifecycle-objects.md#3-tombstone)),
and a write-only service legitimately holds the signing key while
deliberately holding nothing else. ADR-0055 gives tombstones their own
derivation domain, so for a repository advertising `reclaim-authority` a
tombstone signature proves the narrower and more useful thing: *produced by a
holder of the reclaim key*. Publication signatures are unchanged and still
prove exactly what §2 says.

Neither changes §1's seed interpretation, which the reclaim key follows
verbatim — one clamping rule for both keys, because two would be a second
chance to get it wrong.

## Consequences

**Positive**

- Both implementer-blocking gaps close without a new object type, a new wire format, or a key-distribution mechanism.
- The specification stops promising device attribution it cannot deliver; what a signature proves is now stated exactly.
- Ed25519 signature conformance vectors become producible: seed derivation is already pinned by `keys.json`, and the seed interpretation is now fixed.

**Negative**

- No cryptographic device attribution in v1. A compromised member can sign anything as anyone. This is a real limit, accepted because the key hierarchy already implied it — the ADR makes it visible rather than introducing it.
- If per-device attribution is later wanted, it is a format extension (new keys in the snapshot map, a registry object, enrolment rules) — deferred, not precluded. Recorded as [Q13](../open-questions.md#q13--device-level-signature-attribution).

**Neutral**

- The signing key remains generational; rotation costs nothing beyond what ADR-0005's generation machinery already provides.

## Alternatives considered

**Raw-scalar interpretation.** Rejected. No mainstream API consumes it directly, manual clamping is an error magnet, and nothing is gained.

**Per-device signing keys with a public-key registry.** The full fix for attribution, and the honest long-term answer if multi-user repositories become central. Rejected **for v1**: it adds an object type, an enrolment/trust flow, revocation semantics, and a registry that itself needs integrity protection — a large surface serving a property no v1 requirement demands. Deferred to Q13.

**Drop signatures from v1 entirely.** Superficially attractive once (2) is understood — AEAD already authenticates everything against outsiders holding no keys. Rejected because AEAD tags do not travel with the *object graph*: a snapshot manifest's signature is the one artefact that binds the whole tree root, the parent chain, and the policy under a key an outsider cannot hold, and it is what makes rollback and substitution detectable at the snapshot level (NFR-SEC-005).

**Store the public key in the key bundle anyway.** Harmless but pointless — a reader that can open the bundle holds the master key and can derive it. Storing it would only create a field that can disagree with the derivation.

## Status history

| Date | Status | Note |
|------|--------|------|
| 2026-08 | Accepted | Seed interpretation and repository scope fixed for v1; device attribution deferred to Q13 |
| 2026-09 | Amended | The derivation moves to the format-2 signing root; the seed interpretation and the repository scope are unchanged. Format 1 withdrawn ([ADR-0014 Amendment 1](0014-format-versioning-and-stability.md#amendment-1-2026-09--format-1-withdrawn-before-freeze)) |
| 2026-09 | Accepted (amended) | §3 narrowed and §2 refined by [ADR-0055](0055-reclaim-authority.md): a keyless destination cannot derive a public key, so one is published beside its attribution; and a tombstone signs under its own reclaim domain, so its signature proves a narrower authority than a publication's. §1 unchanged |
