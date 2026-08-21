# FallbackPlan recovery kit — formats v1 and v2

**Normative.** Decision record: [ADR-0013](../../docs/adr/0013-recovery-kit.md) (contents and representations), [ADR-0026](../../docs/adr/0026-phase-1-capture-shapes.md) (phase-1 wave G2). Conventions — deterministic CBOR, base32 rendering, length limits — are inherited from [repository-format 00](../repository-format/00-conventions.md).

The kit is the artefact a user holds when everything else is gone: it must survive a printer, a filing cabinet, a decade, and a person typing it back in ([ADR-0013](../../docs/adr/0013-recovery-kit.md)). Together with the passphrase — which the kit **never** contains — it opens the repository on a clean machine.

---

## 1 What a kit is

One logical document with two representations carrying **identical content**:

- **machine form** — the framed binary of §3, stored as a file;
- **text form** — the transcribable rendering of §4, for printing.

A kit is **one factor**. It contains the wrapped master key and the public parameters needed to unwrap it, but unwrapping requires the passphrase. A kit MUST NOT contain the passphrase, any store credential, or any device private key; a parser encountering a kit that claims to is looking at a forgery or a defect, and MUST refuse it (FR-KIT-002).

## 2 Body

The body is one deterministic-CBOR map ([repository-format 00 §4](../repository-format/00-conventions.md#4-cbor-encoding)):

| Key | Type | Value |
|-----|------|-------|
| 1 | u16 | `kit_format_version` — **1** for the repository kits of this section; **2** for the installation kit of §2.2 |
| 2 | text | `minimum_tool_version` — lowest recovery-tool version able to process this kit, `major.minor.patch` |
| 3 | bytes[16] | `repository_id` |
| 4 | u16 | `repository_format_version` |
| 5 | bytes | `key_object` — the **verbatim `FBPKKEYS` key object** ([repository-format 03 §3](../repository-format/03-keys.md#3-the-key-object)), byte-identical to the repository's `/keys/<key-id>` object |
| 6 | map | `kdf_parameters` — `{1: memory_kib u32, 2: iterations u32, 3: parallelism u8, 4: salt bytes[16]}`, from the repository descriptor |
| 7 | array | `destinations` — array of maps `{1: kind text, 2: endpoint text, 3: container text, 4: prefix text}`; informational, says *where*, never *how to authenticate* |
| 8 | bytes[16] | `issuing_device_id` — public identity of the device that generated the kit |
| 9 | u64 | `issued_at` — Unix milliseconds |
| 10 | text | `instructions` — embedded step-by-step recovery instructions, plain text |

All ten keys are mandatory (`destinations` MAY be an empty array). Unknown keys MUST be rejected — a kit is small, security-relevant, and has no extension story other than a new `kit_format_version`.

Key 5 is deliberately the **unmodified key object**: the kit inherits the key object's own framing, AAD binding, and unwrap path, so a kit parser reuses the repository-format code and vectors instead of introducing a second wrapping construction. Recovering with a kit is exactly repository-open ([01 §6](../repository-format/01-object-layout.md#6-discovery-order)) with steps 1 and 3 satisfied from the kit instead of the store.

### 2.1 Write-only repositories (repository format v2)

A kit for a write-only repository ([ADR-0042](../../docs/adr/0042-write-only-repositories.md); [repository-format 03 §9](../repository-format/03-keys.md#9-write-only-repositories-format-v2)) carries **no key material at all** — not even wrapped, because no key object exists to carry. Its body is the same map with one added key:

| Key | Type | Value |
|-----|------|-------|
| 11 | bytes[32] | `sealing_public_key` — the repository's derived X25519 public key, byte-identical to descriptor key 9 |

The shape rules are mandatory in both directions and a parser MUST enforce them:

- when key 11 is **present** (an 11-key map): key 4 MUST be ≥ 2 and key 5 MUST be a **zero-length** byte string — a v2 kit that claims to carry a key object is a forgery or a defect, refused exactly as a passphrase-bearing kit is (§1);
- when key 11 is **absent** (the 10-key map of §2): key 5 MUST be a well-formed `FBPKKEYS` object as before.

Such a kit is purely *where the repository is and how to re-derive*: the KDF parameters (key 6) reproduce the Argon2id root from the passphrase, and key 11 is the verifier — derive, compare, no decryption. A stolen v2 kit yields strictly less than a stolen v1 kit: an address and a public key, with no wrapped key object to attack offline. The passphrase remains the one factor, and losing it loses the backup — the kit cannot soften that, by design.

### 2.2 Installation kits (kit format v2)

A **v2 kit describes an installation, not a repository** ([ADR-0044](../../docs/adr/0044-first-run-setup.md)). It exists because a write-only installation's keys do not come from any repository: `root = Argon2id(passphrase, salt, params)` and every key is an HKDF domain of that root ([03 §9](../repository-format/03-keys.md#9-write-only-repositories-format-v2)), with no repository identifier anywhere in the derivation. Everything a person needs to reconstruct their keys is therefore known the moment the passphrase is chosen — before any archive, set or destination exists — and that is when the kit is generated (FR-KIT-004).

Its body is a **six-key** deterministic-CBOR map:

| Key | Type | Value |
|-----|------|-------|
| 1 | u16 | `kit_format_version` = **2** |
| 2 | text | `minimum_tool_version` |
| 4 | u16 | `repository_format_version` — the profile every archive of this installation is written under; MUST be ≥ 2 |
| 6 | map | `kdf_parameters` — `{1: memory_kib u32, 2: iterations u32, 3: parallelism u8, 4: salt bytes[16]}`, the installation's |
| 8 | bytes[16] | `issuing_device_id` |
| 9 | u64 | `issued_at` |
| 10 | text | `instructions` |
| 11 | bytes[32] | `sealing_public_key` — the derived X25519 public key, the verifier |

Keys **3** (`repository_id`), **5** (`key_object`) and **7** (`destinations`) MUST be **absent**, and a parser MUST refuse a v2 body carrying any of them. The key numbering is deliberately left with holes rather than renumbered: a reader that mixes up the two versions then fails on an unknown key instead of silently reading a field as something it is not.

Each absence is a statement:

- **no repository id**, because one installation writes many archives and one kit opens all of them. The archive supplies its own identity — a recovering tool reads `repository-format` from the store it was pointed at (§6) — while the kit supplies the keys. A kit that named one archive would be a kit that could not open the others, which is not what a single passphrase means.
- **no key object**, for §2.1's reason: a write-only repository has none to carry.
- **no destinations**, because an installation kit is generated before any destination is declared, and a field that is empty in every real kit is a field that teaches a reader nothing. Where the archives live is operational knowledge the operator has; what only the kit can carry is how to re-derive.

A stolen v2 kit yields an Argon2id salt, public parameters and a public key. It names no repository, no location and no account, and it carries nothing to attack offline beyond what the archives themselves already publish in their descriptors.

**v1 kits are unaffected.** They remain valid, parse unchanged, and open the repository they name. A tool that supports v2 MUST still accept v1.

## 3 Machine form (framing)

```text
offset  size  field
------  ----  -----------------------------------------------
     0     8  magic            = "FBPKRKIT"
     8     2  kit_version      u16, big-endian — MUST equal body key 1
    10     2  reserved         u16, MUST be zero
    12     4  body_length      u32, big-endian
    16     N  body             deterministic CBOR (§2)
  16+N    32  checksum         SHA-256 over bytes [0, 16+N)
```

A parser MUST validate `body_length` against [00 §8](../repository-format/00-conventions.md#8-lengths-and-limits)'s pre-allocation discipline (a kit body over 64 KiB is invalid), MUST verify the checksum **before** parsing the body, and MUST verify that the framed `kit_version` equals body key 1. The framing is identical for both versions; only the body differs. Checksum failure is reported as transcription or storage damage — distinct from "not a kit" (wrong magic) and from "unsupported version".

## 4 Text form

The text form is a line-oriented rendering of the **entire framed binary** (§3):

```text
FALLBACKPLAN RECOVERY KIT v1
<instruction lines — free text, ignored by parsers>

01: xxxx xxxx xxxx xxxx xxxx xxxx xxxx xxxx xxxx xxxx xxxx xxxx cccc
02: xxxx xxxx xxxx xxxx xxxx xxxx xxxx xxxx xxxx xxxx xxxx xxxx cccc
...
END FALLBACKPLAN RECOVERY KIT
```

- The payload is the framed binary rendered as **lowercase unpadded base32** ([00 §6](../repository-format/00-conventions.md#6-object-identifiers-in-paths)), split into groups of 4 characters, **12 groups per line** (48 payload characters; the final line may be shorter).
- Each payload line is `NN: <groups> <check>` where `NN` is the 1-based, zero-padded, two-digit line number (three digits when a kit needs 100+ lines) and `<check>` is the first 4 characters of the lowercase base32 rendering of SHA-256(`NN` ‖ `":"` ‖ the line's payload characters with spaces removed), UTF-8.
- Parsers MUST ignore case, all whitespace within the payload, and everything outside the `BEGIN`/`END` markers other than payload lines; they MUST verify every per-line check and report failures **by line number** — the per-line check exists to localise a typo, the §3 checksum to guarantee the whole (FR-KIT-003).
- Writers MUST emit exactly the canonical layout above so that two kits for the same repository state diff cleanly.

## 5 QR form

The QR representation encodes the framed binary (§3) directly in **byte mode**, error-correction level **M**, smallest QR version that fits. It is a rendering of the machine form, not a third format; a decoder that yields the framed bytes proceeds per §3. (Phase 1 pins these parameters; rendering ships with the first UI that can print.)

## 6 Using a kit

1. Parse (§3/§4); verify checksum, version, and field constraints.
2. Derive the KEK from the passphrase and key 6's parameters ([03 §2](../repository-format/03-keys.md#2-key-encryption-key)).
3. Unwrap key 5 exactly as repository-open step 3 ([03 §3](../repository-format/03-keys.md#3-the-key-object)). A wrong passphrase and a tampered key object are indistinguishable, by design.
4. Reach the store named by key 7 (credentials come from the operator, never the kit), then proceed as an ordinary reader — including catalogue rebuild and forensic rebuild if the index plane is gone.

For a **write-only kit** (§2.1), steps 2–3 are replaced by the v2 derivation ([03 §9](../repository-format/03-keys.md#9-write-only-repositories-format-v2)): run Argon2id over the passphrase with key 6's parameters, expand the root into the full authority, and prove it by comparing the derived sealing public key against key 11 — equality is the verifier, and a mismatch is the wrong passphrase, refused before anything is read.

For an **installation kit** (§2.2) the sequence differs at both ends. There is no key 7 to name a store, so the operator supplies the archive; and there is no key 3, so the repository identity — which is AAD for every record and every sealed blob — is read from that archive's own `repository-format` descriptor ([01 §6](../repository-format/01-object-layout.md#6-discovery-order)) rather than from the kit. The derivation is §2.1's, and it is proved **twice**: the derived sealing public key MUST equal key 11 *and* the descriptor's key 9. Agreement means this kit and this archive belong to the same installation; a mismatch is refused before anything is read, and the two comparisons fail differently — against the kit it is the wrong passphrase, against the descriptor it is the wrong archive.

A **stale** kit — issued before a destination change — still performs steps 1–3; it may simply not know where every replica lives. Staleness is a freshness property, never a validity property ([ADR-0013](../../docs/adr/0013-recovery-kit.md)). An installation kit carries no destinations at all, so it cannot go stale in that sense.

## 7 Conformance

- Vector group [`repository-format/conformance/vectors/recovery-kit.json`](../repository-format/conformance/vectors/recovery-kit.json): framing and text-form cases (round-trip, per-line check answers, refusal cases — bad checksum, bad line check, unknown key, oversize body, version mismatch).
- Fixture kit under [`repository-format/conformance/fixtures/`](../repository-format/conformance/fixtures/README.md): a committed kit for `fixture-repository-v1` that a conforming implementation parses and uses, with the fixture passphrase, to open and restore the fixture repository (FR-KIT-001).
- A committed **v2 installation kit** beside it, generated from the same fixture passphrase and the v2 fixture's salt and parameters: it parses, its derivation reproduces the fixture's sealing public key, and it opens the v2 fixture repository whose identity it does not carry.

Like every fixture, the committed kit regenerates byte-identically from fixed inputs; a diff is a deliberate format change.
