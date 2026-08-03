# 00 — Conventions

**Normative.** Applies to every other document in this specification.

---

## 1 Notation

| Notation | Meaning |
|----------|---------|
| `‖` | Concatenation of byte strings, with no separator and no length prefix unless stated |
| `u8`, `u16`, `u32`, `u64` | Unsigned integers of that width, **big-endian** |
| `bytes[n]` | Exactly *n* bytes |
| `0x…` | Hexadecimal literal |
| *italic* | A value defined elsewhere in this specification |

All multi-byte integers in framing structures are **big-endian**. This applies to blob envelopes, record headers, and footers. Integers *inside* CBOR objects follow CBOR's own rules (§4).

Byte offsets are zero-based and measured from the start of the enclosing object unless stated otherwise.

## 2 Sizes and units

`KiB` = 1024 bytes, `MiB` = 1024² bytes, `GiB` = 1024³ bytes. This specification never uses `KB`, `MB` or `GB`.

## 3 Profiles

Several behaviours are selected by a **profile** — a small integer naming a fixed, versioned set of parameters. Profiles exist so that a repository can contain objects written under different settings without ambiguity, and so that new options can be added without a format break.

| Profile family | Values | Defined in |
|----------------|--------|------------|
| Segmentation | `fixed-v1`, `cdc-v1` | [09](09-segmentation.md) |
| Compression | `none`, `zstd-v1` | [10](10-compression.md) |
| Encryption | `aes-256-gcm-v1`, `xchacha20-poly1305-v1` | [04](04-record.md) |
| Content hash | `sha-256-v1` | [02](02-identifiers.md) |

A profile identifier is a `u16`. Values `0x0000`–`0x7FFF` are reserved for this specification; `0x8000`–`0xFFFF` are available for private use and MUST NOT appear in a repository intended to be portable.

A reader encountering an unknown profile in an object it must interpret **MUST refuse the object** and report the unknown profile value. It MUST NOT guess.

## 4 CBOR encoding

All metadata objects — manifests, index deltas, checkpoints, journal records, and the repository descriptor — are encoded as **CBOR** ([RFC 8949](https://www.rfc-editor.org/rfc/rfc8949)) in **deterministic encoding** form, per RFC 8949 §4.2.1.

Deterministic encoding is required because object identifiers are derived from encoded bytes (§[02](02-identifiers.md)). Two implementations that encode the same logical object differently would produce different identifiers for the same object, and deduplication, verification, and this entire conformance suite would break.

### 4.1 Deterministic encoding rules

A conforming writer MUST:

1. encode integers, lengths, and tags in the **shortest form** that represents the value;
2. use **definite-length** encoding for all arrays, maps, byte strings, and text strings — indefinite-length encoding MUST NOT appear anywhere;
3. sort map keys by their **encoded bytes**, lexicographically, treating the bytes as unsigned;
4. never emit duplicate map keys;
5. never emit floating-point values — this format has no use for them, and their canonical form is a recurring source of cross-language disagreement;
6. never emit CBOR tags except those explicitly defined in this specification.

A conforming reader MUST **reject** any CBOR object that violates rules 1–6, rather than accepting it leniently. A lenient decoder silently permits two encodings of the same object, which reintroduces exactly the ambiguity deterministic encoding exists to remove.

### 4.2 Map keys

Map keys in this specification are **unsigned integers**, not strings. Integer keys are smaller, sort unambiguously, and avoid text-encoding questions entirely.

Each object's document assigns its key numbers. Key numbers are stable: once assigned in a released format version, a key number MUST NOT be reused for a different meaning.

### 4.3 Unknown fields

A reader encountering an unknown map key:

- in an object whose **feature set** it fully supports, MUST ignore the key and MUST preserve it byte-for-byte if it re-emits the object;
- in an object requiring a feature it does not support, MUST refuse the object per §5.

Writers MUST NOT rely on readers preserving unknown fields for correctness.

## 5 Versioning and feature negotiation

A repository advertises a **format version** and a **feature set**, both recorded in the repository descriptor ([01](01-object-layout.md)).

The format version is a `u16`. It changes only when the framing structures in §[04](04-record.md) or §[05](05-blob.md) change incompatibly.

The feature set is a list of feature identifiers, each marked **required** or **optional**:

- A reader MUST refuse the repository if any **required** feature is one it does not implement.
- A reader MAY proceed if it does not implement an **optional** feature, provided it does not attempt to interpret objects that depend on it.

Advertising a feature set rather than a single version number is deliberate: a reader that supports most of a version has a way to say so, instead of having to refuse everything or claim everything.

## 6 Object identifiers in paths

Identifiers appearing in store paths are rendered as **lowercase base32 without padding** ([RFC 4648](https://www.rfc-editor.org/rfc/rfc4648) §6, alphabet `abcdefghijklmnopqrstuvwxyz234567`).

Base32 rather than base64 because store key namespaces are frequently case-insensitive — object stores, some filesystems, and several S3-compatible implementations — and a case-insensitive collision between two distinct identifiers would be a silent data-loss bug. Base32 lowercase is unambiguous under case folding.

Identifiers are **not** hexadecimal, which would cost 25% more key length for no benefit.

## 7 Time

Timestamps are **`u64` milliseconds since the Unix epoch (1970-01-01T00:00:00Z)**, UTC, with no leap-second adjustment.

No correctness property in this format depends on timestamps being accurate or on two machines agreeing about them. Timestamps are recorded for retention policy, diagnostics, and display. Ordering that matters for correctness uses generation numbers and per-writer journal sequences instead. → [`04-concurrency-and-publication.md` §7](../../docs/architecture/04-concurrency-and-publication.md#7-time-and-clock-skew)

## 8 Lengths and limits

Every length field has a stated maximum. A reader MUST enforce these limits **before** allocating memory based on a length read from an object, and MUST refuse an object exceeding them.

This is not a performance concern. A parser that allocates from an unvalidated length read from an untrusted store is the standard shape of a denial-of-service or heap-corruption bug, and repository objects are supplied by parties this format explicitly does not trust. → [`threat-model.md` T-7](../../docs/threat-model.md#t-7-malicious-or-malformed-protocol-input)

| Limit | Value |
|-------|-------|
| Maximum record stored length | 64 MiB |
| Maximum records per blob | 65 536 |
| Maximum blob size | 512 MiB |
| Maximum metadata object size | 16 MiB |
| Maximum repository descriptor CBOR body | 65 536 bytes ([01 §3.1](01-object-layout.md#31-framing)) |
| Maximum key object CBOR bundle | 4 096 bytes ([03 §3](03-keys.md#3-the-key-object)) |
| Maximum segment references per file-version manifest | 1 048 576 |
| Maximum path component length | 1 024 bytes |
| Maximum path depth | 512 components |

An object exceeding a limit is not merely rejected — it is reported as a **damage finding**, because a conforming writer cannot produce one.

## 9 Reserved and zero values

Fields marked *reserved* MUST be written as zero and MUST be ignored on read. A reader MUST NOT refuse an object because a reserved field is non-zero, and MUST NOT assign meaning to it.

## 10 Error handling posture

Throughout this specification, the required behaviour on encountering something unexpected is to **refuse and report**, never to guess, repair, or continue with partial data.

Repair is a separate, explicitly invoked operation. A reader's job is to determine what is true about the repository, not to change it. → [`02-repository-format.md` §8.3](../../docs/architecture/02-repository-format.md#83-rebuild-never-repairs)

---

**Next:** [01 — Object layout](01-object-layout.md)
