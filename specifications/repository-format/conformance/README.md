# Conformance suite

Test vectors for the [FallbackPlan repository format](../README.md).

---

## Running

```bash
# Regenerate the vectors
python3 generate.py

# Verify committed vectors match freshly computed ones (CI)
python3 generate.py --check
```

The generator depends on nothing but the Python standard library. That is deliberate: an implementer must be able to reproduce these values without installing anything and without trusting the reference implementation.

`generate.py` validates its own HKDF implementation against [RFC 5869](https://www.rfc-editor.org/rfc/rfc5869) test case 1 on every run. If that check fails the script exits non-zero and writes nothing, because every derived vector in the suite would be wrong.

## What this suite does and does not establish

**Read this before treating a passing run as evidence of anything.** A conformance suite that overstates its own authority is worse than a small one, because it stops people looking for the gaps.

| Vector group | Independently derived? | What a pass means |
|---|---|---|
| [`keys.json`](vectors/keys.json) | ✅ Yes | Your HKDF derivation and domain separation match the specification |
| [`identifiers.json`](vectors/identifiers.json) | ✅ Yes | Your content and object identifiers match |
| [`records.json`](vectors/records.json) | ✅ Yes | Your nonce and AAD construction match — record **and** footer AAD, as bytes |
| [`segmentation.json`](vectors/segmentation.json) | ✅ Yes | Your `fixed-v1` **and** `cdc-v1` boundaries match — the Rabin polynomial, tables, and boundary cases are computed here from the ADR-0023 rule |
| [`compression.json`](vectors/compression.json) | ✅ Yes | Your threshold decisions match |
| [`aes-gcm.json`](vectors/aes-gcm.json) | ❌ **No** — pinned constants, provenance per case | You use AES-256-GCM correctly, including AAD absorption over the format's real 55-byte AAD — see the note below |
| [`argon2id.json`](vectors/argon2id.json) | ❌ **No** — pinned from two agreeing implementations | Your Argon2id matches both reference implementations at the mandated minimum parameters |
| [`ed25519.json`](vectors/ed25519.json) | ✅ Yes | Your Ed25519 treats the derived 32 bytes as an RFC 8032 §5.1.5 **seed** ([ADR-0020](../../../docs/adr/0020-ed25519-signing-key-semantics.md)) and reproduces the RFC §7.1 vectors — computed by a pure-Python RFC 8032 implementation in the generator, gated by those published vectors on every run |
| [`write-only.json`](vectors/write-only.json) | ✅ Yes | Your format-v2 derivation tree ([03 §9](../03-keys.md#9-write-only-repositories-format-v2)) and sealed-content-key key agreement match — X25519 computed by a pure-Python RFC 7748 implementation in the generator, gated by the RFC's §5.2 and §6.1 vectors on every run; the pinned root stands in for Argon2id exactly as `argon2id.json` does, and the final AES-256-GCM seal follows `aes-gcm.json`'s posture |
| **AEAD record ciphertexts** | ❌ **Not present** | — |

"Independently derived" means computed here from published algorithms using standard-library primitives, with no input from the reference implementation. You can reproduce them in any language. Each file carries the flag as `independently_derived`, and `VectorFileTests` asserts the flag **per file against what the content actually is** — an earlier revision asserted `true` for every file, including one whose values the generator cannot compute, which was precisely the overstatement the flag exists to prevent.

The two ❌ groups exist because the generator's stdlib-only rule cuts both ways: AES-GCM and Argon2id cannot be computed from the Python standard library, so those values are pinned constants whose provenance is declared per case, and their correctness is re-verified against real implementations on every CI run (`CryptographicPrimitiveTests` against the platform `AesGcm`; `Argon2idCrossVerificationTests` against **both** Bodu and Konscious).

### The gap: AEAD record ciphertexts

There are no vectors asserting the exact ciphertext of an encrypted record. This is a real gap and it is stated rather than papered over.

Producing them requires an AES-GCM implementation, which the standard library does not provide. The options were:

1. **Generate them from the reference .NET implementation.** They would then be self-certifying — they would prove a future build matches today's build, and nothing about whether either matches the specification. A second implementer reproducing them would be reproducing our behaviour, not verifying our correctness.
2. **Add a third-party dependency to the generator.** That would make the vectors non-reproducible for anyone who cannot install it, undermining the reason the generator has no dependencies.
3. **Omit them and say so.**

Option 3 was chosen. What covers the gap instead:

- **`aes-gcm.json`** proves the primitive is used correctly, with the caveat in the next section.
- **`records.json`** pins the nonce and AAD construction exactly. Given a correct AES-GCM and the right nonce and AAD, the ciphertext follows.
- **The freeze-gate independent reader** is what actually validates the framing. A reader written from the specification alone, by an author who did not write the format, in a different language, is the only thing that proves the specification is unambiguous. Self-generated vectors catch regressions; they cannot catch a specification that is wrong in the same way the implementation is.

Option 1 remains available later as a *regression* suite, clearly labelled as such. It must never be presented as conformance evidence.

### A correction worth recording

An earlier revision of `nist-gcm.json` carried two cases and claimed both were NIST CAVP vectors. The second was written from memory rather than obtained, and it was wrong — `CryptographicPrimitiveTests` caught it the first time it ran against a real AES-GCM implementation.

It has been **removed rather than replaced with another remembered value**, and the file renamed to `aes-gcm.json` because the NIST claim could not be substantiated here.

The surviving case verifies against the platform implementation, so its correctness as an AES-256-GCM triple is established. Its provenance as a CAVP vector is asserted from memory and could not be re-fetched — `csrc.nist.gov` and `raw.githubusercontent.com` are both unreachable from the environment these vectors were generated in. The file marks this with `provenance_reverified: false`.

A second case was later added because the surviving CAVP case is empty-plaintext, empty-AAD — it proves nothing about AAD absorption, the one property the record format leans on (04 §4). The new case uses the format's **real construction** — the `keys.json` blob key, ordinal 47's nonce and its 55-byte AAD from `records.json` — and was computed **once with the platform `AesGcm` and pinned**. Its provenance field says exactly that: it is a *regression* vector proving a future implementation matches the platform over the format's exact inputs, not conformance evidence that either matches the specification. It was not remembered; it was computed, which is the difference that matters here.

**To expand this set properly:** fetch `gcmEncryptExtIV256` from the NIST CAVP archive and add cases from it. Do not add remembered values — that is what produced the defect.

This is recorded rather than quietly fixed because a conformance suite's value is entirely in whether its claims can be trusted, and a suite that has silently corrected a fabricated vector is indistinguishable from one that has not.

## What a conforming reader must demonstrate

Passing these vectors is necessary and not sufficient. A reader claiming conformance should also demonstrate:

1. **Refusal, not guessing.** Given a repository whose `required_features` contains an unknown identifier, it refuses and names the identifier. Given an unknown profile in an object it must interpret, it refuses.
2. **Deterministic CBOR enforcement.** It rejects non-canonical CBOR — indefinite lengths, non-shortest integers, unsorted or duplicate map keys — rather than accepting it leniently.
3. **Content verification after decryption.** It verifies that the decrypted plaintext hashes to the content identifier implied by the object identifier, not merely that the AEAD tag validates. A record can be perfectly authentic and still carry a false identifier ([04 §6](../04-record.md#6-reading-a-record)).
4. **Localised corruption.** A record with a bad tag affects only that record; every other record in the same blob remains readable.
5. **Bounds enforcement before allocation.** It validates lengths against [00 §8](../00-conventions.md#8-lengths-and-limits) before allocating.
6. **Footer-only recovery.** It can locate, decrypt and verify every record in a blob given the blob and the repository keys alone, with no index.
7. **Index precedence.** Given two entries for one object identifier, it honours the higher generation, and treats a supersession as ordered rather than commutative ([07 §3](../07-index.md#3-precedence)).
8. **Whole-file verification.** It verifies the reassembled file against `whole_file_hash`, and reports failure rather than emitting a partial file.

## Cross-implementation verification

One primitive this format needs has no platform implementation, so it comes from a third party and does not inherit the platform's audit posture ([03 §6.1](../03-keys.md#61-where-each-primitive-comes-from)). CI checks it against a second independent implementation:

| Primitive | Second implementation | Result |
|-----------|----------------------|--------|
| **Argon2id** | Konscious (test-only dependency, never shipped) | Bit-identical across the parameter range, including the mandated minimums — and both reproduce the committed [`argon2id.json`](vectors/argon2id.json) vector on every run. The two differ only in refusing versus accepting an empty password — an API-boundary policy difference, not an algorithmic one, and the reason [03 §2.1](../03-keys.md#21-the-passphrase-is-constrained-too-and-the-primitive-will-not-do-it-for-you) now exists. |

**XChaCha20-Poly1305 had no second implementation, and that is why it is no longer in the format.** The profile was withdrawn before the freeze rather than shipped unverified: an AEAD defect is discovered inside bytes the user already stored, and a format version can add a profile but cannot un-admit one that written repositories depend on ([Q12](../../../docs/open-questions.md#closed), [03 §6.1](../03-keys.md#61-where-each-primitive-comes-from)). The table above is the standard it failed to meet, not a list it is missing from.

Cross-verification is not an audit. It establishes that two people did not make the same mistake; it does not establish that either is correct. The external cryptographic review required before the first beta must cover Argon2id specifically.

## Fixtures

The vectors here cover algorithms and encodings. **Fixture repositories** — complete small repositories with known content — live in [`fixtures/`](fixtures/README.md). The first, `fixture-repository-v1`, is a complete deterministic phase-0 repository (descriptor, key object, blobs, standalone snapshot, index delta, journal) whose committed bytes are regenerated and compared on every test run: a diff under `fixtures/` is a format change and must be deliberate. Beside it, `fixture-repository-v2` is a committed write-only repository (ADR-0042): sealing is randomised by design so it is not byte-regenerated — instead every run re-proves its read contract (structure with the write bundle, `ContentSealed` without a grant, byte-identical restore with the derived authority).

The suite also carries the **recovery kit** ([specifications/recovery-kit](../../recovery-kit/README.md)): framing and text-form vectors in `vectors/recovery-kit.json`, and a committed kit for the fixture repository under `fixtures/fixture-repository-v1-kit/`.

Fixtures containing user data are never committed. Everything in this suite is synthetic and constant.

## Known gaps

Recorded so they are visible rather than discovered:

| Gap | Blocked on |
|-----|-----------|
| AEAD record ciphertext vectors | See above — deliberate. One platform-derived *regression* case now exists in `aes-gcm.json`, labelled as such; it is not conformance evidence |
| Confirmed-provenance AES-GCM known-answer tests | NIST CAVP archive unreachable from this environment |
| **XChaCha20-Poly1305 cross-verification** | No second implementation available to check against — unlike Argon2id, which is cross-verified on every CI run |
| Negative vectors (rejection cases) | Fixture territory — e.g. a non-power-of-two `fixed-v1` segment size ([09 §2.2](../09-segmentation.md#22-parameters)) MUST be rejected, but every committed case happens to use a conforming size, so a non-enforcing implementation passes today |
| Corruption-injected fixtures | `fixture-repository-v1` is the intact baseline; committed pre-corrupted variants (each damage class as frozen bytes) remain future work — today corruption is injected at test time by the F2 harness |
| Format upgrade fixtures | `fixture-repository-v2` freezes the write-only (ADR-0042) read contract; an upgrade path between formats does not exist by design — a repository is v1 or v2 from creation |

## Reporting a defect

If you cannot implement something from the specification, or these vectors disagree with a plain reading of it, that is a defect in the specification rather than in your reading. The specification is required to be implementable by someone who has never seen the reference implementation — please report it.
