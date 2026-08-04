# FallbackPlan recovery kit — format v1

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
| 1 | u16 | `kit_format_version` — this document describes version **1** |
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

A parser MUST validate `body_length` against [00 §8](../repository-format/00-conventions.md#8-lengths-and-limits)'s pre-allocation discipline (a kit body over 64 KiB is invalid), MUST verify the checksum **before** parsing the body, and MUST verify that the framed `kit_version` equals body key 1. Checksum failure is reported as transcription or storage damage — distinct from "not a kit" (wrong magic) and from "unsupported version".

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

A **stale** kit — issued before a destination change — still performs steps 1–3; it may simply not know where every replica lives. Staleness is a freshness property, never a validity property ([ADR-0013](../../docs/adr/0013-recovery-kit.md)).

## 7 Conformance

- Vector group [`repository-format/conformance/vectors/recovery-kit.json`](../repository-format/conformance/vectors/recovery-kit.json): framing and text-form cases (round-trip, per-line check answers, refusal cases — bad checksum, bad line check, unknown key, oversize body, version mismatch).
- Fixture kit under [`repository-format/conformance/fixtures/`](../repository-format/conformance/fixtures/README.md): a committed kit for `fixture-repository-v1` that a conforming implementation parses and uses, with the fixture passphrase, to open and restore the fixture repository (FR-KIT-001).

Like every fixture, the committed kit regenerates byte-identically from fixed inputs; a diff is a deliberate format change.
